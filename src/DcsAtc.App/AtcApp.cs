using System.Text.Json;
using DcsAtc.Airfields;
using DcsAtc.Atc;
using DcsAtc.Bridge;

namespace DcsAtc.App;

public sealed record AppOptions
{
    public string? DcsDirectory { get; init; }
    public string? DataDirectory { get; init; }
    public IReadOnlyList<string> DumpAirbases { get; init; } = [];
    public IReadOnlyList<string> SilenceAirbases { get; init; } = [];
    /// <summary>Keep the stock DCS ATC at airbases we have chart data for (it is silenced by default).</summary>
    public bool KeepStockAtc { get; init; }
    public string DumpDirectory { get; init; } = Path.Combine("data", "dcs");
    public TimeSpan StatusInterval { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Where controllers speak (SRS); null: text messages only.</summary>
    public IAtcRadio? Radio { get; init; }
}

/// <summary>
/// The ATC app: follows the mission through the bridge, gives every player group an "ATC" F10 menu and
/// runs ground control and tower, in text and (with a radio) on SRS.
/// </summary>
public sealed class AtcApp
{
    private readonly BridgeClient _client;
    private readonly AppOptions _options;
    private readonly AirfieldCatalog _airfields;
    private readonly FlightRegistry _flights;
    private readonly GroundControl _ground;
    private readonly TowerControl _tower;
    private readonly ApproachControl _approach;
    private readonly AtisService _atis;
    private readonly AtcMenu _menu;
    private readonly Dictionary<string, UnitState> _units = [];
    /// <summary>The F10 menu each player group has now (entry ids), so it is only rebuilt when it changes.</summary>
    private readonly Dictionary<int, IReadOnlyList<string>> _menus = [];
    private DateTime _lastStatus = DateTime.MinValue;

    public AtcApp(BridgeClient client, AppOptions options)
    {
        _client = client;
        _options = options;
        _airfields = new AirfieldCatalog(client, options.DcsDirectory, options.DataDirectory, Log);
        _flights = new FlightRegistry(client, Log);
        _ground = new GroundControl(client, _airfields, _flights, Log, radio: options.Radio);
        _tower = new TowerControl(client, _airfields, _ground, _flights, Log, radio: options.Radio);
        _approach = new ApproachControl(client, _airfields, _tower, _flights, Log, radio: options.Radio);
        _atis = new AtisService(client, _airfields, options.Radio, Log);
        _menu = new AtcMenu(_airfields, _ground, _tower, _approach, _flights);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await foreach (var message in _client.Messages.ReadAllAsync(ct))
        {
            try
            {
                await HandleAsync(message, ct);
            }
            catch (Exception ex) when (ex is BridgeCommandException or InvalidOperationException or IOException or TimeoutException or InvalidDataException)
            {
                Log($"error: {ex.Message}");
            }
        }
    }

    private async Task HandleAsync(BridgeMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case ConnectionChangedMessage { Connected: true }:
                Log("connected to DCS");
                break;
            case ConnectionChangedMessage c:
                Log($"disconnected from DCS ({c.Reason})");
                ResetMission();
                break;
            case HelloMessage h:
                Log($"bridge protocol {h.Protocol}, mission '{h.Mission}', running: {h.MissionRunning}, multiplayer: {h.Multiplayer}");
                if (h.Error is not null) Log($"bridge error: {h.Error}");
                if (h.MissionRunning) await OnMissionRunningAsync(h.Info, ct);
                break;
            case MissionStartedMessage m:
                Log($"mission started: '{m.Mission}'");
                await OnMissionRunningAsync(m.Info, ct);
                break;
            case MissionStoppedMessage:
                Log("mission stopped");
                ResetMission();
                break;
            case UnitsMessage u:
                await OnUnitsAsync(u, ct);
                break;
            case DcsEventMessage e:
                if (e.Player is not null || e.Event is "takeoff" or "land")
                    Log($"event {e.Event,-16} {e.Unit} ({e.UnitType}){(e.Player is null ? "" : $" player {e.Player}")}{(e.Place is null ? "" : $" at {e.Place}")}");
                await _ground.OnEventAsync(e, ct);
                await _tower.OnEventAsync(e, ct);
                await _approach.OnEventAsync(e, ct);
                break;
            case MenuSelectedMessage m:
                await OnMenuAsync(m, ct);
                break;
            case BridgeErrorMessage e:
                Log($"bridge error: {e.Error}");
                break;
        }
    }

    private async Task OnMissionRunningAsync(MissionInfo? info, CancellationToken ct)
    {
        ResetMission();
        Log($"theatre {info?.Theatre ?? "?"}, mission date {info?.Date?.Year}-{info?.Date?.Month:00}-{info?.Date?.Day:00}");
        var airbases = await _client.GetAirbasesAsync(ct);
        _airfields.SetMission(info?.Theatre, airbases);
        Log($"{airbases.Count} airbases in this mission");

        foreach (var name in _options.DumpAirbases)
        {
            var dump = await _client.GetAirbaseAsync(name, ct);
            if (dump is null) continue;
            Directory.CreateDirectory(_options.DumpDirectory);
            string path = Path.Combine(_options.DumpDirectory, $"{name}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }), ct);
            Log($"{name}: {dump.Runways.Count} runways, {dump.Parking.Count} parking spots -> {path}");
        }

        // Airbases we have chart data for get our ground control; the stock ATC there is silenced.
        var silence = new HashSet<string>(_options.SilenceAirbases);
        foreach (var airbase in airbases.Where(a => a.Category == 0 && _airfields.LabelsFor(a.Name) is not null))
        {
            var airfield = await _airfields.GetAsync(airbase, ct);
            if (!_options.KeepStockAtc) silence.Add(airbase.Name);
            if (airfield is not null && _options.Radio is { } radio)
            {
                // Everything the controllers say here, with today's weather, so calls only stitch cached clips.
                var layout = airfield.Layout;
                var weather = await _client.GetAirbaseWeatherAsync(airbase.Name, ct);
                radio.Prepare(Phraseology.Samples(airfield.Facility, layout.Runways,
                    layout.Labels?.Taxiways.Select(t => t.Name).Distinct() ?? [], layout.Labels?.Areas.Select(a => a.Name) ?? [],
                    layout.Labels?.Frequencies, weather, layout.Labels?.Departure));
                if (ApproachControl.Serves(layout.Labels))
                    radio.Prepare(Phraseology.ApproachSamples(airfield.Facility, layout.Runways, airfield.Radio, layout.Labels?.Frequencies, weather, ApproachControl.ProceduresOf(airfield)));
                if (layout.Labels?.Frequencies?.Atis is not null)
                    radio.Prepare(Phraseology.AtisSamples(airfield.Facility, layout.Runways, weather));
            }
        }
        foreach (var name in silence)
            Log($"{name}: stock ATC silenced: {await _client.SetRadioSilentAsync(name, true, ct)}");
        await _atis.UpdateAsync(ct);
    }

    private async Task OnUnitsAsync(UnitsMessage snapshot, CancellationToken ct)
    {
        _units.Clear();
        foreach (var u in snapshot.Units) _units[u.Name] = u;

        foreach (var u in PlayerGroups())
        {
            if (_menus.ContainsKey(u.GroupId!.Value)) continue;
            Log($"ATC menu for {u.Player} ({u.Group})");
            _options.Radio?.Prepare(Phraseology.CallsignSamples(Phraseology.Callsign(u)), soon: true);
        }

        await _flights.OnUnitsAsync(_units, ct);
        await _ground.OnUnitsAsync(_units, ct);
        await _tower.OnUnitsAsync(_units, ct);
        await _approach.OnUnitsAsync(_units, ct);
        await _atis.OnUnitsAsync(ct);
        foreach (var u in PlayerGroups()) await UpdateMenuAsync(u, ct);

        if (DateTime.UtcNow - _lastStatus >= _options.StatusInterval)
        {
            _lastStatus = DateTime.UtcNow;
            foreach (var p in snapshot.Units.Where(u => u.IsPlayer))
                Log($"    {p.Player,-14} {p.Type,-12} {Phraseology.Callsign(p),-12} hdg {p.Hdg:000}  gs {p.GroundSpeed * 1.94384,4:F0} kt  {p.Agl * 3.28084,6:F0} ft AGL  {(p.InAir ? "air" : "ground")}" +
                    (_ground.Clearances.Values.FirstOrDefault(c => c.UnitName == p.Name) is { } c ? $"  [Ground: {c.Phase} to {c.Departure?.End.Name ?? "parking"}]" : "") +
                    (_tower.Flights.Values.FirstOrDefault(f => f.UnitName == p.Name) is { } t ? $"  [Tower: {t.Phase} runway {t.End.Name}]" : "") +
                    (_approach.Flights.Values.FirstOrDefault(f => f.UnitName == p.Name) is { } a
                        ? $"  [Approach: {a.Phase}{(a.IsArrival ? $" {a.Type} {a.End.Name} {a.Leg} hdg {a.Heading:000} alt {a.Altitude:0}" : "")}]" : ""));
        }
    }

    private async Task OnMenuAsync(MenuSelectedMessage m, CancellationToken ct)
    {
        var unit = _units.Values.FirstOrDefault(u => u.GroupId == m.GroupId && u.IsPlayer) ?? _units.Values.FirstOrDefault(u => u.GroupId == m.GroupId);
        Log($"menu '{m.Id}' from group {m.GroupId} ({unit?.Player ?? "?"})");
        if (unit is null) return;
        // Airfield calls carry the airfield ("ab4:ground.taxi"): the controllers work that one, not the nearest.
        var (airbaseId, action) = AtcMenu.ParseId(m.Id);
        var at = airbaseId is int id && _airfields.Airbases.FirstOrDefault(a => a.Id == id) is { } airbase ? await _airfields.GetAsync(airbase, ct) : null;
        switch (action)
        {
            case "ground.taxi":
                await _ground.RequestTaxiAsync(unit, _units, at, ct);
                break;
            case "ground.parking":
                await _ground.RequestTaxiToParkingAsync(unit, _units, at, ct);
                break;
            case "ground.cancel":
                await _ground.CancelTaxiAsync(unit, _units, ct);
                break;
            case "ground.repeat":
                await _ground.SayAgainAsync(unit, _units, ct);
                break;
            case var ready when AtcMenu.IntentOf(ready) is { } intent:
                await _tower.ReadyForDepartureAsync(unit, _units, intent, at, ct);
                break;
            case "tower.landing":
                await _tower.RequestLandingAsync(unit, _units, at, ct);
                break;
            case "tower.final":
                await _tower.ReportFinalAsync(unit, _units, ct);
                break;
            case "tower.repeat":
                await _tower.SayAgainAsync(unit, _units, ct);
                break;
            case "tower.initial":
                await _tower.ReportInitialAsync(unit, _units, at, ct);
                break;
            case var request when AtcMenu.ApproachOf(request) is { } type:
                await _approach.RequestApproachAsync(unit, _units, type, at, ct);
                break;
            case "approach.checkin":
                await _approach.CheckInAsync(unit, _units, at, ct);
                break;
            case "approach.field":
                await _approach.FieldInSightAsync(unit, _units, ct);
                break;
            case "approach.cancel":
                await _approach.CancelAsync(unit, _units, ct);
                break;
            case "approach.repeat":
                await _approach.SayAgainAsync(unit, _units, ct);
                break;
            case "atis" when at is not null:
                await _client.ShowTextAsync(m.GroupId, await _atis.TextAsync(at, ct), 30, ct: ct);
                break;
            case "frequencies" when at is not null:
                await _client.ShowTextAsync(m.GroupId, Phraseology.FrequencyList(at), 20, ct: ct);
                break;
            case "flight.2" or "flight.3" or "flight.4":
                await _flights.DeclareAsync(unit, action[^1] - '0', _units, ct);
                break;
            case "flight.single":
                await _flights.LeaveAsync(unit, _units, ct);
                break;
            case "debug.where":
                await _client.ShowTextAsync(m.GroupId, DescribePosition(unit), 15, ct: ct);
                break;
        }
        // What the pilot can do next changed: show it now rather than on the next update. A flight's call changes its members' menus too.
        foreach (var u in PlayerGroups()) await UpdateMenuAsync(u, ct);
    }

    /// <summary>One player unit per group with players (the menu is per group).</summary>
    private IEnumerable<UnitState> PlayerGroups() =>
        _units.Values.Where(u => u.IsPlayer && u.GroupId is not null).DistinctBy(u => u.GroupId).ToList();

    /// <summary>Rebuilds a group's ATC menu when the calls it can make have changed (rebuilt whole, so the order stays the same).</summary>
    private async Task UpdateMenuAsync(UnitState unit, CancellationToken ct)
    {
        int groupId = unit.GroupId!.Value;
        var entries = await _menu.ForAsync(unit, _units, ct);
        var ids = entries.Select(e => e.Id).ToList();
        if (_menus.TryGetValue(groupId, out var current) && current.SequenceEqual(ids)) return;
        await _client.ClearMenusAsync(groupId, ct);
        foreach (var e in entries) await _client.AddMenuAsync(groupId, e.Path, e.Label, e.Id, ct);
        _menus[groupId] = ids;
    }

    private string DescribePosition(UnitState u)
    {
        string where = "no airbase known";
        var position = new Vec2(u.X, u.Z);
        var nearest = _airfields.Airbases.Where(a => a.Category == 0).MinBy(a => new Vec2(a.X, a.Z).DistanceTo(position));
        if (nearest is not null)
        {
            var offset = position - new Vec2(nearest.X, nearest.Z);
            where = $"{offset.Length / 1852:F1} NM, bearing {offset.Heading:000}° from {nearest.Name}";
        }
        return $"DCS-ATC: {Phraseology.Callsign(u)}, {where}. Heading {u.Hdg:000}°, ground speed {u.GroundSpeed * 1.94384:F0} kt, " +
               $"{u.Agl * 3.28084:F0} ft AGL, {(u.InAir ? "airborne" : "on the ground")}. Position x {u.X:F0}, z {u.Z:F0}.";
    }

    private void ResetMission()
    {
        _units.Clear();
        _menus.Clear();
        _flights.Reset();
        _ground.Reset();
        _tower.Reset();
        _approach.Reset();
        _atis.Reset();
    }

    private static void Log(string text) => Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {text}");
}
