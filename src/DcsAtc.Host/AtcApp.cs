using System.Text.Json;
using DcsAtc.Airfields;
using DcsAtc.Atc;
using DcsAtc.Bridge;
using DcsAtc.Recognition;

namespace DcsAtc.Host;

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
    /// <summary>Hold AI departures at parking and release them in turn (players first).</summary>
    public bool ManageAi { get; init; } = true;
    /// <summary>Mishearings to correct before a voice request is read ("one way" → "runway").</summary>
    public IReadOnlyDictionary<string, string>? VoiceAliases { get; init; }
    /// <summary>Correct wrong readbacks ("negative, I say again, runway 31R").</summary>
    public bool CheckReadbacks { get; init; } = true;
    /// <summary>Pilots may declare a Mayday or Pan Pan (voice, F10 → ATC → Emergency).</summary>
    public bool Emergencies { get; init; } = true;
    /// <summary>Approach's procedures where an airfield's labels file has none.</summary>
    public ApproachProcedures? ApproachDefaults { get; init; }
    /// <summary>Where everything is logged; null: nowhere.</summary>
    public AtcLog? Log { get; init; }
}

/// <summary>
/// The ATC app: follows the mission through the bridge, gives every player group an "ATC" F10 menu and
/// runs ground control and tower, in text and (with a radio) on SRS.
/// </summary>
public sealed partial class AtcApp
{
    private readonly BridgeClient _client;
    private readonly AppOptions _options;
    private readonly AirfieldCatalog _airfields;
    private readonly FlightRegistry _flights;
    private readonly GroundControl _ground;
    private readonly TowerControl _tower;
    private readonly ApproachControl _approach;
    private readonly AtisService _atis;
    private readonly AiTraffic _ai;
    private readonly AiArrivals _arrivals;
    private readonly FarpControl _farps;
    private readonly CallLog? _calls;
    private readonly IAtcRadio? _radio;
    private readonly AtcLog _log;

    /// <summary>A readback is checked against the instructions of the last minute.</summary>
    public static readonly TimeSpan ReadbackWindow = TimeSpan.FromMinutes(1);
    // The server may turn AI handling off in its bridge config (hello.manageAi).
    private bool _serverAllowsAi = true;
    private readonly AtcMenu _menu;
    private readonly Dictionary<string, UnitState> _units = [];
    /// <summary>The F10 menu each player group has now (entry ids), so it is only rebuilt when it changes.</summary>
    private readonly Dictionary<int, IReadOnlyList<string>> _menus = [];
    private readonly PilotCallReader _reader;
    // DCS messages and voice requests are handled one at a time.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastStatus = DateTime.MinValue;
    // Airfields on the air (listening, ATIS broadcasting) for a coalition: those a player of it came near (airbase id, coalition).
    private readonly HashSet<(int Id, int Coalition)> _onAir = [];
    // Airfields whose phrases are prepared (airbase id).
    private readonly HashSet<int> _prepared = [];

    /// <summary>Connected to the DCS bridge.</summary>
    public bool Connected { get; private set; }
    /// <summary>The running mission and its theatre; null between missions.</summary>
    public string? Mission { get; private set; }
    public string? Theatre { get; private set; }
    /// <summary>The server's map drawing setting (null: an older bridge does not say).</summary>
    public bool? ServerMapDrawings { get; private set; }

    /// <summary>
    /// Raised on the app's own loop after every message handled and every voice request: the moment to take a
    /// <see cref="Snapshot"/> (nothing changes while a handler runs).
    /// </summary>
    public event Action? Changed;

    /// <summary>An airfield goes on the air (voice requests heard, ATIS broadcast, phrases prepared) when a player comes this close (NM).</summary>
    public const double OnAirRange = 60;
    private const double Nm = 1852;

    public AtcApp(BridgeClient client, AppOptions options)
    {
        _client = client;
        _options = options;
        _log = options.Log ?? new AtcLog();
        // Every transmission goes through the call log, so readbacks can be checked against it.
        _calls = options.Radio is null ? null : new CallLog(options.Radio);
        _radio = _calls;
        _airfields = new AirfieldCatalog(client, options.DcsDirectory, options.DataDirectory, _log.For(LogCategory.App));
        _flights = new FlightRegistry(client, _log.For(LogCategory.App));
        _ground = new GroundControl(client, _airfields, _flights, _log.For(LogCategory.Ground), radio: _radio);
        _tower = new TowerControl(client, _airfields, _ground, _flights, _log.For(LogCategory.Tower), radio: _radio);
        var stacks = new HoldingStacks();
        _approach = new ApproachControl(client, _airfields, _tower, _flights, _log.For(LogCategory.Approach), radio: _radio, defaults: options.ApproachDefaults, stacks: stacks);
        _atis = new AtisService(client, _airfields, _radio, _log.For(LogCategory.Atis), onAir: a => _onAir.Any(k => k.Id == a.Id));
        _ai = new AiTraffic(client, _airfields, _ground, _tower, _approach, _log.For(LogCategory.Ai), radio: _radio);
        _arrivals = new AiArrivals(client, _airfields, _tower, _approach, stacks, _log.For(LogCategory.Ai), radio: _radio)
        {
            OnAir = PlayerNear,
        };
        _farps = new FarpControl(client, _flights, _log.For(LogCategory.Farp), _radio);
        _menu = new AtcMenu(_airfields, _ground, _tower, _approach, _flights, _farps, options.Emergencies);
        _reader = new PilotCallReader(options.VoiceAliases, options.Emergencies);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await foreach (var message in _client.Messages.ReadAllAsync(ct))
        {
            await _gate.WaitAsync(ct);
            try
            {
                await HandleAsync(message, ct);
            }
            catch (Exception ex) when (ex is BridgeCommandException or InvalidOperationException or IOException or TimeoutException or InvalidDataException)
            {
                Log(LogCategory.App, $"error: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                RaiseChanged();
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// A pilot's transmission to a station, as text (a voice request): ATC acts as if the matching F10 entry had been
    /// picked. A request for another controller of the airfield is sent there ("contact Approach on 257.0"); a call
    /// that is not understood gets "say again" if it was aimed at ATC; readbacks and chatter get no answer.
    /// </summary>
    /// <param name="facility">The airfield whose radio heard it ("Al Dhafra").</param>
    /// <param name="mhz">The frequency it was said on: a position's own frequency, or one all positions share.</param>
    /// <param name="sender">The SRS client name (the player name) and its DCS unit id, from SRS.</param>
    public async Task OnVoiceAsync(string facility, double? mhz, string? sender, uint? unitId, string text, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await VoiceAsync(facility, mhz, sender, unitId, text, ct);
            RaiseChanged();
        }
        catch (Exception ex) when (ex is BridgeCommandException or InvalidOperationException or IOException or TimeoutException or InvalidDataException)
        {
            Log(LogCategory.VoiceIn, $"voice: {ex.Message}", LogLevel.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task VoiceAsync(string facility, double? mhz, string? sender, uint? unitId, string text, CancellationToken ct)
    {
        var unit = _units.Values.FirstOrDefault(u => unitId is > 0 && u.Id == unitId && u.IsPlayer)
            ?? _units.Values.FirstOrDefault(u => u.IsPlayer && u.Player == sender);
        if (unit?.GroupId is null)
        {
            Log(LogCategory.VoiceIn, $"voice: {sender ?? "?"} is not in an aircraft, not answered");
            return;
        }
        // Stations sharing a frequency (a few DCS airfields; every Retribution FARP): the one nearest the pilot answers.
        if (mhz is double heardOn && NearestOn(heardOn, unit) is { } nearest && nearest != facility) return;
        if (_farps.Sites.FirstOrDefault(s => s.Name == facility) is { } site)
        {
            await FarpVoiceAsync(site, unit, text, ct);
            return;
        }
        var airbase = _airfields.Airbases.FirstOrDefault(a => a.Category == 0 && Phraseology.Facility(a.Name) == facility);
        if (airbase is null || await _airfields.GetAsync(airbase, ct) is not { } airfield) return;

        // On a position's own frequency that position was called; on a shared one, the one named, else the one that
        // fits where the aircraft is (the request itself decides below).
        var stations = airfield.Frequencies;
        string? called = mhz is double m ? stations.RoleOn(m) : null;
        var party = _flights.PartyFor(unit, _units);
        var reading = _reader.Read(text, called is null ? facility : $"{facility} {called}");
        string role = called ?? NamedRole(reading.Words) ?? DefaultRole(unit, airfield);
        string station = $"{facility} {role}";
        if (reading.Call is not { } call)
        {
            if (reading.Readback)
            {
                await ReadbackAsync(party, facility, reading.Words, ct);
                return;
            }
            if (!reading.AimedAtAtc)
            {
                Log(LogCategory.VoiceIn, $"voice: {party.Callsign} to {station}: not a call to ATC, no answer");
                return;
            }
            Log(LogCategory.VoiceIn, $"voice: {party.Callsign} to {station}: not understood");
            await ReplyAsync(party, airfield, role, unit.Coal, Phraseology.SayAgainPlease(party.Callsign, station), ct);
            return;
        }

        // An emergency: the station called answers and stays with the flight, whatever it is.
        if (call.Intent is PilotIntent.Mayday or PilotIntent.PanPan or PilotIntent.CancelEmergency)
        {
            Log(LogCategory.VoiceIn, $"voice: {party.Callsign} to {station}: {call.Intent}");
            await EmergencyAsync(unit, call.Intent, airfield, role, ct);
            foreach (var u in PlayerGroups()) await UpdateMenuAsync(u, ct);
            return;
        }

        // "Inbound" or "request landing" to Approach asks for an approach: Approach brings arrivals to Tower.
        if (role == "Approach" && call.Intent == PilotIntent.RequestLanding) call = call with { Intent = PilotIntent.RequestApproach };
        string owner = call.Intent switch
        {
            PilotIntent.RequestTaxi or PilotIntent.TaxiToParking or PilotIntent.CancelTaxi or PilotIntent.RequestStartup => "Ground",
            PilotIntent.ReadyForDeparture or PilotIntent.RequestLanding or PilotIntent.ReportFinal or PilotIntent.ReportInitial => "Tower",
            PilotIntent.RequestApproach or PilotIntent.FieldInSight or PilotIntent.CancelApproach => "Approach",
            _ => role,
        };
        Log(LogCategory.VoiceIn, $"voice: {party.Callsign} to {station}: {call.Intent}{(call.Option is { } o ? $" ({o})" : "")}, match {call.Match:P0}");
        // Asked on another position's own frequency: sent to the one that handles it. On a shared frequency that one answers.
        var (answering, sendTo) = stations.Route(mhz, role, owner);
        if (sendTo is double other)
        {
            await ReplyAsync(party, airfield, role, unit.Coal, Phraseology.ContactStation(party.Callsign, station, owner, other), ct);
            return;
        }
        role = answering;
        station = $"{facility} {role}";
        switch (call.Intent)
        {
            case PilotIntent.RadioCheck:
                await ReplyAsync(party, airfield, role, unit.Coal, Phraseology.ReadYouFive(party.Callsign, station), ct);
                return;
        }
        if (ActionFor(call, role, airfield) is not { } action)
        {
            await ReplyAsync(party, airfield, role, unit.Coal, Phraseology.SayAgainPlease(party.Callsign, station), ct);
            return;
        }
        await DispatchAsync(unit, action, airfield, ct);
        foreach (var u in PlayerGroups()) await UpdateMenuAsync(u, ct);
    }

    /// <summary>The F10 action for a voice request to <paramref name="role"/>; the details not said get defaults.</summary>
    private static string? ActionFor(PilotCall call, string role, GroundAirfield airfield) => call.Intent switch
    {
        PilotIntent.RequestStartup => "ground.startup",
        PilotIntent.RequestTaxi => "ground.taxi",
        PilotIntent.TaxiToParking => "ground.parking",
        PilotIntent.CancelTaxi => "ground.cancel",
        PilotIntent.SayAgain => $"{role.ToLowerInvariant()}.repeat",
        // Ready for departure without a direction: own navigation.
        PilotIntent.ReadyForDeparture => $"tower.ready.{call.Option ?? "own"}",
        PilotIntent.RequestLanding => "tower.landing",
        PilotIntent.CheckIn => role switch { "Tower" => "tower.landing", "Approach" => "approach.checkin", _ => null },
        PilotIntent.ReportFinal => "tower.final",
        PilotIntent.ReportInitial => "tower.initial",
        // An approach without a type, or one the airfield does not have: ILS, else TACAN, else visual.
        PilotIntent.RequestApproach => $"approach.{call.Option switch
        {
            "ils" when airfield.Radio.HasIls => "ils",
            "tacan" when airfield.Radio.Tacan is not null => "tacan",
            "visual" or "overhead" => call.Option,
            _ => airfield.Radio.HasIls ? "ils" : airfield.Radio.Tacan is not null ? "tacan" : "visual",
        }}",
        PilotIntent.FieldInSight => "approach.field",
        PilotIntent.CancelApproach => "approach.cancel",
        _ => null,
    };

    /// <summary>
    /// A readback: a value different from what ATC said in its latest instruction (runway, heading, altitude, frequency,
    /// QNH) is said again by the same station, "Hangman 1-2, negative, I say again, runway 31R"; missing or misheard
    /// parts are let go, and a correct readback gets no answer.
    /// </summary>
    private async Task ReadbackAsync(Party party, string facility, IReadOnlyList<string> words, CancellationToken ct)
    {
        if (!_options.CheckReadbacks)
        {
            Log(LogCategory.VoiceIn, $"voice: {party.Callsign} to {facility}: readback (not checked)");
            return;
        }
        if (_calls?.Correction(party.Callsign, facility, words, ReadbackWindow) is not { } correction)
        {
            Log(LogCategory.VoiceIn, $"voice: {party.Callsign} to {facility}: readback, no answer");
            return;
        }
        Log(LogCategory.VoiceIn, $"voice: {party.Callsign} to {facility}: readback differs");
        foreach (var group in party.Groups) await _client.ShowTextAsync(group, correction.Text, 15, ct: ct);
        _radio!.Transmit(correction);
        Log(LogCategory.VoiceIn, $"{party.Callsign}: {correction.Text}");
    }

    /// <summary>Of the airfields and FARP sites working this frequency, the one nearest the unit.</summary>
    private string? NearestOn(double mhz, UnitState unit)
    {
        var p = new Vec2(unit.X, unit.Z);
        return _airfields.AirbasesOn(mhz).Select(a => (Facility: Phraseology.Facility(a.Name), At: new Vec2(a.X, a.Z)))
            .Concat(_farps.Sites.Where(s => s.Frequencies.Has(mhz)).Select(s => (Facility: s.Name, At: s.Center)))
            .OrderBy(c => c.At.DistanceTo(p)).Select(c => c.Facility).FirstOrDefault();
    }

    /// <summary>A voice request to a FARP site: start up, departure, landing, say again, radio check.</summary>
    private async Task FarpVoiceAsync(FarpSite site, UnitState unit, string text, CancellationToken ct)
    {
        var party = _flights.PartyFor(unit, _units);
        var reading = _reader.Read(text, site.Name);
        if (reading.Call is not { } call)
        {
            if (reading.Readback)
            {
                await ReadbackAsync(party, site.Name, reading.Words, ct);
                return;
            }
            if (!reading.AimedAtAtc)
            {
                Log(LogCategory.VoiceIn, $"voice: {party.Callsign} to {site.Name}: not a call to ATC, no answer");
                return;
            }
            Log(LogCategory.VoiceIn, $"voice: {party.Callsign} to {site.Name}: not understood");
            await FarpReplyAsync(party, site, unit.Coal, Phraseology.SayAgainPlease(party.Callsign, site.Name), ct);
            return;
        }
        Log(LogCategory.VoiceIn, $"voice: {party.Callsign} to {site.Name}: {call.Intent}{(call.Option is { } o ? $" ({o})" : "")}, match {call.Match:P0}");
        switch (call.Intent)
        {
            case PilotIntent.RequestStartup:
                await _farps.RequestStartupAsync(unit, _units, site, ct);
                break;
            case PilotIntent.ReadyForDeparture:
                await _farps.RequestDepartureAsync(unit, _units, AtcMenu.IntentOf($"tower.ready.{call.Option ?? "own"}") ?? DepartureIntent.OwnNavigation, site, ct);
                break;
            case PilotIntent.RequestLanding or PilotIntent.CheckIn:
                await _farps.RequestLandingAsync(unit, _units, site, ct);
                break;
            case PilotIntent.SayAgain:
                await _farps.SayAgainAsync(unit, _units, ct);
                break;
            case PilotIntent.RadioCheck:
                await FarpReplyAsync(party, site, unit.Coal, Phraseology.ReadYouFive(party.Callsign, site.Name), ct);
                break;
            default:
                await FarpReplyAsync(party, site, unit.Coal, Phraseology.SayAgainPlease(party.Callsign, site.Name), ct);
                break;
        }
        foreach (var u in PlayerGroups()) await UpdateMenuAsync(u, ct);
    }

    private async Task FarpReplyAsync(Party party, FarpSite site, int coalition, string text, CancellationToken ct)
    {
        foreach (var group in party.Groups) await _client.ShowTextAsync(group, text, 10, ct: ct);
        if (_radio is { } radio && site.Call(coalition, text) is { } call) radio.Transmit(call);
        Log(LogCategory.Farp, $"{party.Callsign}: {text}");
    }

    private async Task FarpMenuAsync(UnitState unit, int groupId, string action, FarpSite site, CancellationToken ct)
    {
        switch (action)
        {
            case "farp.startup":
                await _farps.RequestStartupAsync(unit, _units, site, ct);
                break;
            case "farp.landing":
                await _farps.RequestLandingAsync(unit, _units, site, ct);
                break;
            case "farp.repeat":
                await _farps.SayAgainAsync(unit, _units, ct);
                break;
            case "farp.frequencies":
                await _client.ShowTextAsync(groupId, $"{site.Name}: {FarpFrequency(site)}, {site.Pads.Count} pads.", 20, ct: ct);
                break;
            default:
                if (AtcMenu.FarpIntentOf(action) is { } intent) await _farps.RequestDepartureAsync(unit, _units, intent, site, ct);
                break;
        }
    }

    private static string FarpFrequency(FarpSite site) =>
        site.Frequencies.Shared.Count > 0 ? string.Join(" / ", site.Frequencies.Shared.Select(f => Phraseology.Frequency(f))) : "no frequency, text messages only";

    /// <summary>The position a pilot named ("…, Al Minhad Tower, …"), if any.</summary>
    private static string? NamedRole(IReadOnlyList<string> words) =>
        words.Contains("ground") ? "Ground" : words.Contains("tower") ? "Tower" : words.Contains("approach") || words.Contains("radar") ? "Approach" : null;

    /// <summary>The position that works an aircraft where it is: Ground on the ground, Tower close to the field, else Approach.</summary>
    private static string DefaultRole(UnitState unit, GroundAirfield airfield)
    {
        if (!unit.InAir) return "Ground";
        double nm = Math.Sqrt(Math.Pow(airfield.Airbase.X - unit.X, 2) + Math.Pow(airfield.Airbase.Z - unit.Z, 2)) / Nm;
        return nm < 10 || !airfield.Frequencies.Serves("Approach") ? "Tower" : "Approach";
    }

    /// <summary>A station's own answer to a voice request: as text to the flight, and on its frequencies.</summary>
    private async Task ReplyAsync(Party party, GroundAirfield airfield, string role, int coalition, string text, CancellationToken ct)
    {
        foreach (var group in party.Groups) await _client.ShowTextAsync(group, text, 10, ct: ct);
        if (_radio is { } radio && airfield.Call(role, coalition, text) is { } call) radio.Transmit(call);
        Log(RoleCategory(role), $"{party.Callsign}: {text}");
    }

    private async Task HandleAsync(BridgeMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case ConnectionChangedMessage { Connected: true }:
                Connected = true;
                Log(LogCategory.Bridge, "connected to DCS");
                break;
            case ConnectionChangedMessage c:
                Connected = false;
                Mission = null;
                Theatre = null;
                Log(LogCategory.Bridge, $"disconnected from DCS ({c.Reason})", LogLevel.Warning);
                ResetMission();
                break;
            case HelloMessage h:
                Log(LogCategory.Bridge, $"bridge protocol {h.Protocol}, mission '{h.Mission}', running: {h.MissionRunning}, multiplayer: {h.Multiplayer}");
                if (h.Error is not null) Log(LogCategory.Bridge, $"bridge error: {h.Error}", LogLevel.Error);
                _serverAllowsAi = h.ManageAi != false;
                ServerMapDrawings = h.MapDrawings;
                Mission = h.MissionRunning ? h.Mission : null;
                if (_options.ManageAi && !_serverAllowsAi) Log(LogCategory.Ai, "AI traffic off: turned off on this server (manageAi = false in Scripts\\DigiTower\\DigiTowerConfig.lua)", LogLevel.Warning);
                if (h.MapDrawings == false) Log(LogCategory.Bridge, "F10 map drawings off on this server (mapDrawings = false in Scripts\\DigiTower\\DigiTowerConfig.lua)", LogLevel.Warning);
                if (h.MissionRunning) await OnMissionRunningAsync(h.Info, ct);
                break;
            case MissionStartedMessage m:
                Mission = m.Mission;
                Log(LogCategory.Bridge, $"mission started: '{m.Mission}'");
                await OnMissionRunningAsync(m.Info, ct);
                break;
            case MissionStoppedMessage:
                Mission = null;
                Theatre = null;
                Log(LogCategory.Bridge, "mission stopped");
                ResetMission();
                break;
            case UnitsMessage u:
                await OnUnitsAsync(u, ct);
                break;
            case DcsEventMessage e:
                if (e.Player is not null || e.Event is "takeoff" or "land")
                    Log(LogCategory.Bridge, $"event {e.Event,-16} {e.Unit} ({e.UnitType}){(e.Player is null ? "" : $" player {e.Player}")}{(e.Place is null ? "" : $" at {e.Place}")}");
                await _ground.OnEventAsync(e, ct);
                await _tower.OnEventAsync(e, ct);
                await _approach.OnEventAsync(e, ct);
                await _farps.OnEventAsync(e, ct);
                if (ManagingAi)
                {
                    _ai.OnEvent(e);
                    _arrivals.OnEvent(e);
                }
                break;
            case MenuSelectedMessage m:
                await OnMenuAsync(m, ct);
                break;
            case BridgeErrorMessage e:
                Log(LogCategory.Bridge, $"bridge error: {e.Error}", LogLevel.Error);
                break;
        }
    }

    private async Task OnMissionRunningAsync(MissionInfo? info, CancellationToken ct)
    {
        ResetMission();
        Theatre = info?.Theatre;
        Log(LogCategory.Bridge, $"theatre {info?.Theatre ?? "?"}, mission date {info?.Date?.Year}-{info?.Date?.Month:00}-{info?.Date?.Day:00}");
        var airbases = await _client.GetAirbasesAsync(ct);
        IReadOnlyList<AirbaseRadios> radios = [];
        try
        {
            radios = await _client.GetAirbaseRadiosAsync(ct);
        }
        catch (BridgeCommandException ex)
        {
            Log(LogCategory.Bridge, $"ATC frequencies from DCS not available ({ex.Message}): voice only where chart data has frequencies. Update the bridge.", LogLevel.Warning);
        }
        _airfields.SetMission(info?.Theatre, airbases, radios);
        int withRadio = airbases.Count(a => _airfields.StationsFor(a).All.Count > 0);
        Log(LogCategory.Bridge, $"{airbases.Count} airbases in this mission, {withRadio} with ATC frequencies");
        IReadOnlyList<FarpInfo> farps = [];
        try
        {
            farps = await _client.GetFarpsAsync(ct);
        }
        catch (BridgeCommandException ex)
        {
            Log(LogCategory.Bridge, $"FARPs not available ({ex.Message}): update the bridge.", LogLevel.Warning);
        }
        _farps.SetSites(FarpSite.Build(farps));
        foreach (var site in _farps.Sites) Log(LogCategory.Farp, $"FARP site {site.Name}: {site.Pads.Count} pads, {FarpFrequency(site)}");

        foreach (var name in _options.DumpAirbases)
        {
            var dump = await _client.GetAirbaseAsync(name, ct);
            if (dump is null) continue;
            Directory.CreateDirectory(_options.DumpDirectory);
            string path = Path.Combine(_options.DumpDirectory, $"{name}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }), ct);
            Log(LogCategory.App, $"{name}: {dump.Runways.Count} runways, {dump.Parking.Count} parking spots -> {path}");
        }

        // The stock ATC is silenced where we talk on its frequencies, and at airfields with chart data.
        var silence = new HashSet<string>(_options.SilenceAirbases);
        if (!_options.KeepStockAtc)
        {
            foreach (var airbase in airbases.Where(a => a.Category == 0
                         && (_airfields.LabelsFor(a.Name) is not null || (_options.Radio is not null && _airfields.StationsFor(a).All.Count > 0))))
                silence.Add(airbase.Name);
            if (_options.Radio is not null)
                foreach (var site in _farps.Sites.Where(s => s.Frequencies.All.Count > 0)) silence.UnionWith(site.Farps);
        }
        foreach (var name in silence)
            Log(LogCategory.Bridge, $"{name}: stock ATC silenced: {await _client.SetRadioSilentAsync(name, true, ct)}");
        await _atis.UpdateAsync(ct);
    }

    private async Task OnUnitsAsync(UnitsMessage snapshot, CancellationToken ct)
    {
        _units.Clear();
        foreach (var u in snapshot.Units) _units[u.Name] = u;

        foreach (var u in PlayerGroups())
        {
            if (_menus.ContainsKey(u.GroupId!.Value)) continue;
            Log(LogCategory.Bridge, $"ATC menu for {u.Player} ({u.Group})");
            _radio?.Prepare(Phraseology.CallsignSamples(Phraseology.Callsign(u)), soon: true);
        }
        await OnAirAsync(ct);

        await _flights.OnUnitsAsync(_units, ct);
        await _ground.OnUnitsAsync(_units, ct);
        await _tower.OnUnitsAsync(_units, ct);
        await _approach.OnUnitsAsync(_units, ct);
        await _farps.OnUnitsAsync(_units, ct);
        if (ManagingAi)
        {
            await _ai.OnUnitsAsync(_units, ct);
            await _arrivals.OnUnitsAsync(_units, ct);
        }
        await _atis.OnUnitsAsync(ct);
        foreach (var u in PlayerGroups()) await UpdateMenuAsync(u, ct);

        if (DateTime.UtcNow - _lastStatus >= _options.StatusInterval)
        {
            _lastStatus = DateTime.UtcNow;
            foreach (var p in snapshot.Units.Where(u => u.IsPlayer))
                Log(LogCategory.App, $"    {p.Player,-14} {p.Type,-12} {Phraseology.Callsign(p),-12} hdg {p.Hdg:000}  gs {p.GroundSpeed * 1.94384,4:F0} kt  {p.Agl * 3.28084,6:F0} ft AGL  {(p.InAir ? "air" : "ground")}" +
                    (_ground.Clearances.Values.FirstOrDefault(c => c.UnitName == p.Name) is { } c ? $"  [Ground: {c.Phase} to {c.Departure?.End.Name ?? "parking"}]" : "") +
                    (_tower.Flights.Values.FirstOrDefault(f => f.UnitName == p.Name) is { } t ? $"  [Tower: {t.Phase} {(t.Spot is { } spot ? $"spot {spot.Index}" : t.FromPresentPosition ? "from present position" : $"runway {t.End.Name}")}]" : "") +
                    (_approach.Flights.Values.FirstOrDefault(f => f.UnitName == p.Name) is { } a
                        ? $"  [Approach: {a.Phase}{(a.IsArrival ? $" {a.Type} {a.End.Name} {a.Leg} hdg {a.Heading:000} alt {a.Altitude:0}" : "")}]" : "") +
                    (_farps.Flights.Values.FirstOrDefault(f => f.UnitName == p.Name) is { } fa
                        ? $"  [{fa.Site.Name}: {fa.Phase}{(fa.Pad is { } pad ? $" pad {pad.Number}" : "")}]" : ""), LogLevel.Debug);
        }
    }

    private async Task OnMenuAsync(MenuSelectedMessage m, CancellationToken ct)
    {
        var unit = _units.Values.FirstOrDefault(u => u.GroupId == m.GroupId && u.IsPlayer) ?? _units.Values.FirstOrDefault(u => u.GroupId == m.GroupId);
        Log(LogCategory.Bridge, $"menu '{m.Id}' from group {m.GroupId} ({unit?.Player ?? "?"})");
        if (unit is null) return;
        // Airfield calls carry the airfield ("ab4:ground.taxi"): the controllers work that one, not the nearest.
        var (airbaseId, action) = AtcMenu.ParseId(m.Id);
        if (action.StartsWith("farp.", StringComparison.Ordinal))
        {
            if (airbaseId is int siteId && _farps.Site(siteId) is { } site) await FarpMenuAsync(unit, m.GroupId, action, site, ct);
            foreach (var u in PlayerGroups()) await UpdateMenuAsync(u, ct);
            return;
        }
        var at = airbaseId is int id && _airfields.Airbases.FirstOrDefault(a => a.Id == id) is { } airbase ? await _airfields.GetAsync(airbase, ct) : null;
        switch (action)
        {
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
            case "debug.pin":
                await PrivatePinAsync(unit, m.GroupId, ct);
                break;
            case "debug.unpin":
                await _client.ClearDrawingAsync(PinKey(m.GroupId), ct);
                await _client.ShowTextAsync(m.GroupId, "Private pin removed.", 10, ct: ct);
                break;
            default:
                await DispatchAsync(unit, action, at, ct);
                break;
        }
        // What the pilot can do next changed: show it now rather than on the next update. A flight's call changes its members' menus too.
        foreach (var u in PlayerGroups()) await UpdateMenuAsync(u, ct);
    }

    private static string PinKey(int groupId) => $"pin-{groupId}";

    /// <summary>
    /// Multiplayer test: a pin at the player's position meant only for their group (DCS markToGroup). The others say
    /// whether they see it on their F10 map; DCS can draw lines and text only for a whole side.
    /// </summary>
    private async Task PrivatePinAsync(UnitState unit, int groupId, CancellationToken ct)
    {
        string who = Phraseology.Callsign(unit);
        try
        {
            await _client.MarkGroupAsync(PinKey(groupId), groupId, unit.X, unit.Z, $"Only {who} should see this", ct);
            await _client.ShowTextAsync(groupId, $"Private pin placed at your position, for {who} only (group {groupId}). Ask the others whether they see it on their F10 map.", 20, ct: ct);
            Log(LogCategory.Bridge, $"private pin for {who} (group {groupId}) placed");
        }
        catch (BridgeCommandException ex)
        {
            await _client.ShowTextAsync(groupId, $"Private pin not placed: {ex.Message}. Update the bridge with bridge/install.ps1.", 15, ct: ct);
        }
    }

    /// <summary>An airfield call to Ground, Tower or Approach, picked in the F10 menu or said on the radio.</summary>
    private async Task DispatchAsync(UnitState unit, string action, GroundAirfield? at, CancellationToken ct)
    {
        switch (action)
        {
            case "ground.startup":
                await _ground.RequestStartupAsync(unit, _units, at, ct);
                break;
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
            case "emergency.mayday":
                await EmergencyAsync(unit, PilotIntent.Mayday, null, null, ct);
                break;
            case "emergency.panpan":
                await EmergencyAsync(unit, PilotIntent.PanPan, null, null, ct);
                break;
            case "emergency.cancel":
                await EmergencyAsync(unit, PilotIntent.CancelEmergency, null, null, ct);
                break;
        }
    }

    /// <summary>
    /// A Mayday, Pan Pan or its cancellation, said to <paramref name="station"/>'s <paramref name="role"/> or picked in the
    /// F10 menu (then the station working the flight, else the nearest airfield's: Tower within 10 NM, else Approach).
    /// Tower takes the flight over; Approach lets go of it.
    /// </summary>
    private async Task EmergencyAsync(UnitState unit, PilotIntent intent, GroundAirfield? station, string? role, CancellationToken ct)
    {
        if (!_options.Emergencies) return;
        if (intent == PilotIntent.CancelEmergency)
        {
            await _tower.CancelEmergencyAsync(unit, _units, ct);
            return;
        }
        var party = _flights.PartyFor(unit, _units);
        if (station is null)
        {
            if (_tower.Flights.GetValueOrDefault(party.Key) is { } t) (station, role) = t.Station ?? (t.Airfield, "Tower");
            else if (_approach.Flights.GetValueOrDefault(party.Key) is { } a) (station, role) = (a.Airfield, "Approach");
            else if (await _airfields.NearestAsync(new Vec2(unit.X, unit.Z), TowerControl.EmergencyRange, b => b.Coalition == 0 || b.Coalition == unit.Coal, ct) is { } nearest)
                (station, role) = (nearest, DefaultRole(unit, nearest));
        }
        var kind = intent == PilotIntent.Mayday ? EmergencyKind.Mayday : EmergencyKind.PanPan;
        if (await _tower.DeclareEmergencyAsync(unit, _units, kind, station, role, ct)) _approach.Release(party.Key);
    }

    /// <summary>
    /// Whether a player of <paramref name="coalition"/> is within <see cref="OnAirRange"/> of an airbase: where AI arrivals
    /// are worth handling (with or without voice; the controllers talk by text too).
    /// </summary>
    private bool PlayerNear(int airbaseId, int coalition) =>
        _airfields.Airbases.FirstOrDefault(a => a.Id == airbaseId) is { } airbase
        && _units.Values.Any(u => u.IsPlayer && u.Coal == coalition && Math.Sqrt(Math.Pow(airbase.X - u.X, 2) + Math.Pow(airbase.Z - u.Z, 2)) <= OnAirRange * Nm);

    /// <summary>
    /// Puts airfields with frequencies on the air as players come within <see cref="OnAirRange"/>: their controllers
    /// listen for that coalition (voice requests), their ATIS broadcasts, and everything they say is prepared with
    /// today's weather, so calls only stitch cached clips. Airfields stay on the air until the mission ends.
    /// </summary>
    private async Task OnAirAsync(CancellationToken ct)
    {
        if (_radio is not { } radio) return;
        bool atis = false;
        foreach (var unit in _units.Values.Where(u => u.IsPlayer && u.Coal is 1 or 2))
        {
            foreach (var airbase in _airfields.Airbases.Where(a => a.Category == 0))
            {
                var stations = _airfields.StationsFor(airbase);
                if (stations.All.Count == 0 || _onAir.Contains((airbase.Id, unit.Coal))) continue;
                if (Math.Sqrt(Math.Pow(airbase.X - unit.X, 2) + Math.Pow(airbase.Z - unit.Z, 2)) > OnAirRange * Nm) continue;
                _onAir.Add((airbase.Id, unit.Coal));
                atis |= stations.Atis.Count > 0;
                Log(LogCategory.Srs, $"{airbase.Name} on the air for {(unit.Coal == 1 ? "red" : "blue")}: {stations.Describe()}");
                radio.Listen(Phraseology.Facility(airbase.Name), stations, unit.Coal);
                if (_prepared.Add(airbase.Id) && await _airfields.GetAsync(airbase, ct) is { } airfield) await PrepareAsync(airfield, radio, ct);
            }
            foreach (var site in _farps.Sites)
            {
                if (site.Frequencies.All.Count == 0 || _onAir.Contains((site.Id, unit.Coal))) continue;
                if (site.Center.DistanceTo(new Vec2(unit.X, unit.Z)) > OnAirRange * Nm) continue;
                _onAir.Add((site.Id, unit.Coal));
                Log(LogCategory.Srs, $"{site.Name} (FARP) on the air for {(unit.Coal == 1 ? "red" : "blue")}: {FarpFrequency(site)}");
                radio.Listen(site.Name, site.Frequencies, unit.Coal);
                if (_prepared.Add(site.Id)) radio.Prepare(Phraseology.FarpSamples(site, await _client.GetAirbaseWeatherAsync(site.Farps[0], ct)));
            }
        }
        if (atis) await _atis.UpdateAsync(ct);
    }

    private async Task PrepareAsync(GroundAirfield airfield, IAtcRadio radio, CancellationToken ct)
    {
        var layout = airfield.Layout;
        var weather = await _client.GetAirbaseWeatherAsync(airfield.Airbase.Name, ct);
        radio.Prepare(Phraseology.Samples(airfield.Facility, layout.Runways,
            layout.Labels?.Taxiways.Select(t => t.Name).Distinct() ?? [], layout.Labels?.Areas.Select(a => a.Name) ?? [],
            airfield.Frequencies, weather, layout.Labels?.Departure));
        if (ApproachControl.Serves(airfield.Frequencies))
            radio.Prepare(Phraseology.ApproachSamples(airfield.Facility, layout.Runways, airfield.Radio, airfield.Frequencies, weather, _approach.ProceduresFor(airfield)));
        if (airfield.Frequencies.Atis.Count > 0)
            radio.Prepare(Phraseology.AtisSamples(airfield.Facility, layout.Runways, weather));
        if (ManagingAi)
        {
            var p = _approach.ProceduresFor(airfield);
            radio.PreparePilot(Phraseology.AiPilotSamples(airfield.Facility, layout.Runways.SelectMany(r => new[] { r.EndA, r.EndB }),
                Enumerable.Range(0, 6).Select(i => p.HoldingAltitude + i * p.HoldingStep), p.InterceptAltitude));
        }
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
        return $"DigiTower: {Phraseology.Callsign(u)}, {where}. Heading {u.Hdg:000}°, ground speed {u.GroundSpeed * 1.94384:F0} kt, " +
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
        _ai.Reset();
        _arrivals.Reset();
        _farps.Reset();
        _onAir.Clear();
        _prepared.Clear();
    }

    /// <summary>AI departures are handled: wanted here and allowed by the server.</summary>
    public bool ManagingAi => _options.ManageAi && _serverAllowsAi;

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException)
        {
            // A listener's bug must not stop the ATC.
            Log(LogCategory.App, $"status update failed: {ex.Message}", LogLevel.Warning);
        }
    }

    private void Log(LogCategory category, string text, LogLevel level = LogLevel.Info) => _log.Write(category, text, level);

    /// <summary>The log category of a position's transmissions.</summary>
    private static LogCategory RoleCategory(string role) => role switch
    {
        "Ground" => LogCategory.Ground,
        "Tower" => LogCategory.Tower,
        "Approach" => LogCategory.Approach,
        "ATIS" => LogCategory.Atis,
        _ => LogCategory.App,
    };
}
