using DcsAtc.Airfields;
using DcsAtc.Airfields.Taxi;
using DcsAtc.Bridge;

namespace DcsAtc.Atc;

public enum AiDeparturePhase
{
    /// <summary>Held at its parking spots: spawned again uncontrolled (engines off), waiting for its turn.</summary>
    Held,
    /// <summary>Going: DCS taxis it to the runway and takes off.</summary>
    Taxiing,
}

/// <summary>An AI group departing from an airfield we control.</summary>
public sealed class AiDeparture
{
    public required string Group { get; init; }
    public required GroundAirfield Airfield { get; init; }
    public required int Coalition { get; init; }
    public AiDeparturePhase Phase { get; set; }
    public DateTime HeldSince { get; set; }
    public DateTime GoingSince { get; set; }
    public string Callsign { get; set; } = "";
    public bool TaxiCalled { get; set; }
    public bool TakeoffCalled { get; set; }
    /// <summary>Why it was held, as last logged.</summary>
    public string? HeldFor { get; set; }
}

/// <summary>
/// AI departures at airfields we control, players first. DCS reports every AI departure's engine start while it is
/// still at its parking spot (measured: 6 s before it rolls when spawned hot, about two minutes when started cold).
/// If it may go then, it is left alone; otherwise it is held (spawned again uncontrolled, engines off, on its spots)
/// and released with DCS's Start command when it may: never while a player is lined up, taking off, landing or
/// arriving within <see cref="ArrivalRange"/> NM; for at most <see cref="MaxWaitForPlayers"/> while a player taxis
/// out or waits at the holding point; at most <see cref="MaxTaxiing"/> AI groups going at once. Ground calls it as it
/// starts to roll, Tower clears it at the runway; DCS steers it (its taxi route, and its runway from the wind, as ours).
/// An object in front of an AI is not used: measured, an AI stopped by one never moves again.
/// </summary>
public sealed class AiTraffic(IDcsCommands dcs, AirfieldCatalog airfields, GroundControl ground, TowerControl tower, ApproachControl approach,
    Action<string>? log = null, Func<DateTime>? clock = null, IAtcRadio? radio = null)
{
    public const int MaxTaxiing = 2;
    public static readonly TimeSpan MaxWaitForPlayers = TimeSpan.FromMinutes(5);

    /// <summary>A player arriving closer than this (NM) holds AI departures.</summary>
    public const double ArrivalRange = 8;

    /// <summary>Closer than this to a runway's centre line a going AI has reached the runway (metres; holding points are 90 m out).</summary>
    public const double AtRunway = 100;

    /// <summary>
    /// An AI going for this long without getting airborne no longer counts (measured: two minutes from Start to rolling,
    /// several more to taxi and queue).
    /// </summary>
    public static readonly TimeSpan GoingTimeout = TimeSpan.FromMinutes(20);

    // An engine start whose aircraft does not appear in a unit snapshot within this time is not followed.
    private static readonly TimeSpan DecisionWindow = TimeSpan.FromSeconds(10);
    private const double Nm = 1852;
    private const double Rolling = 1;

    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly Dictionary<string, AiDeparture> _departures = [];
    // Engine starts of AI at our airfields, waiting for the aircraft in a unit snapshot: unit -> (group, airbase, when).
    private readonly Dictionary<string, (string Group, string Airbase, DateTime Seen)> _starts = [];
    private bool _unsupported;

    /// <summary>AI departures by group name.</summary>
    public IReadOnlyDictionary<string, AiDeparture> Departures => _departures;

    public void OnEvent(DcsEventMessage e)
    {
        if (_unsupported || e.Unit is not { } name || e.Group is not { } group || !string.IsNullOrEmpty(e.Player)) return;
        switch (e.Event)
        {
            // Every departure starts its engines at its parking spot first; a released group starts them again (ignored).
            case "engine_startup" when !_departures.ContainsKey(group) && e.Place is { } place && airfields.LabelsFor(place) is not null:
                _starts[name] = (group, place, _now());
                break;
            case "takeoff" or "runway_takeoff" when _departures.TryGetValue(group, out var d):
                _departures.Remove(d.Group);
                log?.Invoke($"AI {d.Callsign}: airborne");
                break;
        }
    }

    public async Task OnUnitsAsync(IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (_unsupported) return;
        try
        {
            await DecideAsync(units, ct);
            foreach (var d in _departures.Values.Where(d => d.Phase == AiDeparturePhase.Taxiing).ToList())
            {
                var members = MembersOf(d.Group, units);
                if (members.Count == 0) _departures.Remove(d.Group);
                else await WatchAsync(d, members, units, ct);
            }
            await ReleaseHeldAsync(units, ct);
        }
        catch (BridgeCommandException ex) when (IsUnknownCommand(ex))
        {
            // A bridge from before AI traffic: AI is left to DCS, as before.
            _unsupported = true;
            log?.Invoke($"AI traffic off: the DCS bridge does not support it ({ex.Message}); update it with bridge/install.ps1");
        }
    }

    /// <summary>The mission ended: everything went with it.</summary>
    public void Reset()
    {
        _departures.Clear();
        _starts.Clear();
        _unsupported = false;
    }

    private static bool IsUnknownCommand(BridgeCommandException ex) => ex.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase);

    // An AI started its engines at one of our parking spots: let it go, or hold it.
    private async Task DecideAsync(IReadOnlyDictionary<string, UnitState> units, CancellationToken ct)
    {
        foreach (var (name, start) in _starts.ToList())
        {
            if (_now() - start.Seen > DecisionWindow || _departures.ContainsKey(start.Group))
            {
                _starts.Remove(name);
                continue;
            }
            if (!units.TryGetValue(name, out var unit)) continue;
            _starts.Remove(name);
            if (unit.IsPlayer || unit.IsHelicopter || unit.InAir || unit.GroundSpeed > Rolling) continue;
            var airbase = airfields.Airbases.FirstOrDefault(a => a.Name == start.Airbase);
            if (airbase is null || await airfields.GetAsync(airbase, ct) is not { } airfield) continue;
            if (!airfield.Layout.Parking.Any(p => p.Position.DistanceTo(new Vec2(unit.X, unit.Z)) < AtcMenu.ParkedDistance)) continue;

            var d = new AiDeparture { Group = start.Group, Airfield = airfield, Coalition = unit.Coal, Callsign = CallsignOf(start.Group, units) };
            _departures[d.Group] = d;
            radio?.Prepare(Phraseology.CallsignSamples(d.Callsign), soon: true);
            if (HoldReason(d, units, null) is not { } hold)
            {
                Go(d);
                log?.Invoke($"AI {d.Callsign} ({unit.Type}) at {airfield.Facility}: engines running, may go");
                continue;
            }
            try
            {
                await dcs.HoldGroupAsync(d.Group, ct);
                d.Phase = AiDeparturePhase.Held;
                d.HeldSince = _now();
                d.HeldFor = hold;
                log?.Invoke($"AI {d.Callsign} ({unit.Type}) at {airfield.Facility}: held, {hold}");
            }
            catch (BridgeCommandException ex) when (!IsUnknownCommand(ex))
            {
                // Not in the mission file (spawned by a script): cannot be held, DCS keeps it.
                Go(d);
                log?.Invoke($"AI {d.Callsign}: cannot be held ({ex.Message}), left to DCS");
            }
        }
    }

    private void Go(AiDeparture d)
    {
        d.Phase = AiDeparturePhase.Taxiing;
        d.GoingSince = _now();
    }

    private async Task ReleaseHeldAsync(IReadOnlyDictionary<string, UnitState> units, CancellationToken ct)
    {
        foreach (var d in _departures.Values.Where(d => d.Phase == AiDeparturePhase.Held).OrderBy(d => d.HeldSince).ToList())
        {
            if (HoldReason(d, units, d.HeldSince) is { } hold)
            {
                if (hold != d.HeldFor) log?.Invoke($"AI {d.Callsign}: held, {hold}");
                d.HeldFor = hold;
                continue;
            }
            await dcs.ReleaseGroupAsync(d.Group, ct);
            Go(d);
            log?.Invoke($"AI {d.Callsign}: released after {(_now() - d.HeldSince).TotalSeconds:F0} s (had waited: {d.HeldFor})");
        }
    }

    /// <summary>
    /// Why the AI must wait now, or null: players first, then a limit on AI going at once. Players taxiing out or waiting
    /// at the holding point only hold it for <see cref="MaxWaitForPlayers"/> after <paramref name="heldSince"/>.
    /// </summary>
    private string? HoldReason(AiDeparture d, IReadOnlyDictionary<string, UnitState> units, DateTime? heldSince)
    {
        int id = d.Airfield.Airbase.Id;
        var field = new Vec2(d.Airfield.Airbase.X, d.Airfield.Airbase.Z);
        bool Near(string unitName) => units.TryGetValue(unitName, out var u) && new Vec2(u.X, u.Z).DistanceTo(field) < ArrivalRange * Nm;

        int going = _departures.Values.Count(x => x != d && x.Phase == AiDeparturePhase.Taxiing && x.Airfield.Airbase.Id == id);
        if (going >= MaxTaxiing) return $"{going} AI taxiing";

        // Players on the runway, or about to use it: always.
        foreach (var f in tower.Flights.Values.Where(f => f.Airfield.Airbase.Id == id))
        {
            string? doing = f.Phase switch
            {
                TowerPhase.LineUpAndWait => "lined up",
                TowerPhase.ClearedForTakeoff => "taking off",
                TowerPhase.Departed => "departing",
                TowerPhase.ClearedToLand => "landing",
                TowerPhase.Landed => "on the runway after landing",
                TowerPhase.Inbound when Near(f.UnitName) => "arriving",
                _ => null,
            };
            if (doing is not null) return $"{f.Callsign} {doing}";
        }
        foreach (var a in approach.Flights.Values.Where(a => a.IsArrival && a.Airfield.Airbase.Id == id))
        {
            if (Near(a.Party.LeadUnit)) return $"{a.Party.Callsign} on approach";
        }
        foreach (var u in units.Values.Where(u => u.IsPlayer && !u.InAir))
        {
            if (d.Airfield.Layout.Runways.Any(r => r.IsOnRunway(new Vec2(u.X, u.Z)))) return $"{Phraseology.Callsign(u)} on the runway";
        }

        // Players taxiing out or waiting at the holding point go first, but the AI does not wait forever.
        if (heldSince is null || _now() - heldSince < MaxWaitForPlayers)
        {
            if (ground.Clearances.Values.FirstOrDefault(c => c.Airfield.Airbase.Id == id && c.Departure is not null) is { } taxi)
                return $"{taxi.Callsign} {(taxi.Phase == TaxiPhase.HoldingShort ? "holding short" : "taxiing out")}";
            if (tower.Flights.Values.FirstOrDefault(f => f.Airfield.Airbase.Id == id && f.Phase == TowerPhase.WaitingForDeparture) is { } ready)
                return $"{ready.Callsign} ready for departure";
        }
        return null;
    }

    // A going AI: Ground calls it as it starts to roll, Tower clears it at the runway; forgotten once airborne (or stuck).
    private async Task WatchAsync(AiDeparture d, List<UnitState> members, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct)
    {
        var lead = members[0];
        if (lead.InAir || _now() - d.GoingSince > GoingTimeout)
        {
            _departures.Remove(d.Group);
            return;
        }
        var position = new Vec2(lead.X, lead.Z);
        if (!d.TaxiCalled && lead.GroundSpeed > Rolling)
        {
            d.TaxiCalled = true;
            var weather = await dcs.GetAirbaseWeatherAsync(d.Airfield.Airbase.Name, ct);
            if (EndFor(d.Airfield, weather, position) is { } end) Say(d, "Ground", Phraseology.AiTaxi(d.Callsign, d.Airfield.Facility, end));
        }
        if (d.TakeoffCalled) return;
        var runway = d.Airfield.Layout.Runways.Where(r => r.DistanceFrom(position) < AtRunway).MinBy(r => r.DistanceFrom(position));
        if (runway is null) return;
        d.TakeoffCalled = true;
        // Not while a player is on that runway or landing on it: the AI goes anyway, but ATC does not clear it.
        bool playerOnIt = units.Values.Any(u => u.IsPlayer && !u.InAir && runway.IsOnRunway(new Vec2(u.X, u.Z)))
            || tower.Flights.Values.Any(f => f.Runway == runway && f.Phase is TowerPhase.ClearedToLand or TowerPhase.Landed);
        if (playerOnIt) return;
        var w = await dcs.GetAirbaseWeatherAsync(d.Airfield.Airbase.Name, ct);
        if (EndFor(d.Airfield, w, position, runway) is { } takeoff) Say(d, "Tower", Phraseology.ClearedForTakeoff(d.Callsign, takeoff, w));
    }

    /// <summary>The runway end in use (from the wind) of the parallel runway nearest <paramref name="position"/> (or of <paramref name="only"/>).</summary>
    private static RunwayEnd? EndFor(GroundAirfield airfield, AirbaseWeather? weather, Vec2 position, Runway? only = null)
    {
        var ends = airfield.Layout.ActiveEnds(weather?.WindFrom ?? 0, weather?.WindSpeed ?? 0).Where(e => only is null || e.Runway == only).ToList();
        return ends.Count == 0 ? null : ends.MinBy(e => e.Runway.DistanceFrom(position)).End;
    }

    private void Say(AiDeparture d, string role, string text)
    {
        if (radio is not null && d.Airfield.Call(role, d.Coalition, text) is { } call) radio.Transmit(call);
        log?.Invoke($"AI {d.Callsign}: {text}");
    }

    /// <summary>The group's aircraft, lead (lowest id) first.</summary>
    private static List<UnitState> MembersOf(string group, IReadOnlyDictionary<string, UnitState> units) =>
        units.Values.Where(u => u.Group == group && !u.IsPlayer).OrderBy(u => u.Id).ToList();

    /// <summary>"Enfield 2-1", or for a group of several "Enfield 2 flight".</summary>
    private static string CallsignOf(string group, IReadOnlyDictionary<string, UnitState> units)
    {
        var members = MembersOf(group, units);
        if (members.Count == 0) return group;
        string callsign = Phraseology.Callsign(members[0]);
        return members.Count > 1 && FlightRegistry.Split(callsign) is { } split ? $"{split.Prefix} flight" : callsign;
    }
}
