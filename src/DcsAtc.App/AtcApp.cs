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
}

/// <summary>
/// The ATC app: follows the mission through the bridge, gives every player group an "ATC" F10 menu and
/// runs ground control.
/// </summary>
public sealed class AtcApp
{
    private static readonly string[] GroundMenu = ["ATC", "Ground"];
    private static readonly string[] TowerMenu = ["ATC", "Tower"];
    private static readonly string[] FlightMenu = ["ATC", "Flight"];
    private static readonly string[] DebugMenu = ["ATC", "Debug"];

    private readonly BridgeClient _client;
    private readonly AppOptions _options;
    private readonly AirfieldCatalog _airfields;
    private readonly FlightRegistry _flights;
    private readonly GroundControl _ground;
    private readonly TowerControl _tower;
    private readonly Dictionary<string, UnitState> _units = [];
    private readonly HashSet<int> _menuGroups = [];
    private DateTime _lastStatus = DateTime.MinValue;

    public AtcApp(BridgeClient client, AppOptions options)
    {
        _client = client;
        _options = options;
        _airfields = new AirfieldCatalog(client, options.DcsDirectory, options.DataDirectory, Log);
        _flights = new FlightRegistry(client, Log);
        _ground = new GroundControl(client, _airfields, _flights, Log);
        _tower = new TowerControl(client, _airfields, _ground, _flights, Log);
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
            await _airfields.GetAsync(airbase, ct);
            if (!_options.KeepStockAtc) silence.Add(airbase.Name);
        }
        foreach (var name in silence)
            Log($"{name}: stock ATC silenced: {await _client.SetRadioSilentAsync(name, true, ct)}");
    }

    private async Task OnUnitsAsync(UnitsMessage snapshot, CancellationToken ct)
    {
        _units.Clear();
        foreach (var u in snapshot.Units) _units[u.Name] = u;

        foreach (var u in snapshot.Units.Where(u => u.IsPlayer && u.GroupId is not null))
        {
            int groupId = u.GroupId!.Value;
            if (!_menuGroups.Add(groupId)) continue;
            await _client.AddMenuAsync(groupId, GroundMenu, "Request taxi", "ground.taxi", ct);
            await _client.AddMenuAsync(groupId, GroundMenu, "Request taxi to parking", "ground.parking", ct);
            await _client.AddMenuAsync(groupId, GroundMenu, "Cancel taxi", "ground.cancel", ct);
            await _client.AddMenuAsync(groupId, GroundMenu, "Say again", "ground.repeat", ct);
            await _client.AddMenuAsync(groupId, TowerMenu, "Ready for departure", "tower.ready", ct);
            await _client.AddMenuAsync(groupId, TowerMenu, "Request landing", "tower.landing", ct);
            await _client.AddMenuAsync(groupId, TowerMenu, "Report final", "tower.final", ct);
            await _client.AddMenuAsync(groupId, TowerMenu, "Say again", "tower.repeat", ct);
            await _client.AddMenuAsync(groupId, FlightMenu, "Flight of 2", "flight.2", ct);
            await _client.AddMenuAsync(groupId, FlightMenu, "Flight of 3", "flight.3", ct);
            await _client.AddMenuAsync(groupId, FlightMenu, "Flight of 4", "flight.4", ct);
            await _client.AddMenuAsync(groupId, FlightMenu, "Single ship", "flight.single", ct);
            await _client.AddMenuAsync(groupId, DebugMenu, "Where am I?", "debug.where", ct);
            Log($"ATC menu added for {u.Player} ({u.Group})");
        }

        await _flights.OnUnitsAsync(_units, ct);
        await _ground.OnUnitsAsync(_units, ct);
        await _tower.OnUnitsAsync(_units, ct);

        if (DateTime.UtcNow - _lastStatus >= _options.StatusInterval)
        {
            _lastStatus = DateTime.UtcNow;
            foreach (var p in snapshot.Units.Where(u => u.IsPlayer))
                Log($"    {p.Player,-14} {p.Type,-12} {Phraseology.Callsign(p),-12} hdg {p.Hdg:000}  gs {p.GroundSpeed * 1.94384,4:F0} kt  {p.Agl * 3.28084,6:F0} ft AGL  {(p.InAir ? "air" : "ground")}" +
                    (_ground.Clearances.Values.FirstOrDefault(c => c.UnitName == p.Name) is { } c ? $"  [Ground: {c.Phase} to {c.Departure?.End.Name ?? "parking"}]" : "") +
                    (_tower.Flights.Values.FirstOrDefault(f => f.UnitName == p.Name) is { } t ? $"  [Tower: {t.Phase} runway {t.End.Name}]" : ""));
        }
    }

    private async Task OnMenuAsync(MenuSelectedMessage m, CancellationToken ct)
    {
        var unit = _units.Values.FirstOrDefault(u => u.GroupId == m.GroupId && u.IsPlayer) ?? _units.Values.FirstOrDefault(u => u.GroupId == m.GroupId);
        Log($"menu '{m.Id}' from group {m.GroupId} ({unit?.Player ?? "?"})");
        if (unit is null) return;
        switch (m.Id)
        {
            case "ground.taxi":
                await _ground.RequestTaxiAsync(unit, _units, ct);
                break;
            case "ground.parking":
                await _ground.RequestTaxiToParkingAsync(unit, _units, ct);
                break;
            case "tower.ready":
                await _tower.ReadyForDepartureAsync(unit, _units, ct);
                break;
            case "tower.landing":
                await _tower.RequestLandingAsync(unit, _units, ct);
                break;
            case "tower.final":
                await _tower.ReportFinalAsync(unit, _units, ct);
                break;
            case "tower.repeat":
                await _tower.SayAgainAsync(unit, _units, ct);
                break;
            case "flight.2" or "flight.3" or "flight.4":
                await _flights.DeclareAsync(unit, m.Id[^1] - '0', _units, ct);
                break;
            case "flight.single":
                await _flights.LeaveAsync(unit, _units, ct);
                break;
            case "ground.cancel":
                await _ground.CancelTaxiAsync(unit, _units, ct);
                break;
            case "ground.repeat":
                await _ground.SayAgainAsync(unit, _units, ct);
                break;
            case "debug.where":
                await _client.ShowTextAsync(m.GroupId, DescribePosition(unit), 15, ct: ct);
                break;
        }
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
        _menuGroups.Clear();
        _flights.Reset();
        _ground.Reset();
        _tower.Reset();
    }

    private static void Log(string text) => Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {text}");
}
