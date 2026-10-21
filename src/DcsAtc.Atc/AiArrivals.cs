using DcsAtc.Airfields;
using DcsAtc.Airfields.Taxi;
using DcsAtc.Bridge;

namespace DcsAtc.Atc;

public enum AiArrivalPhase
{
    /// <summary>Let go straight in: DCS flies its own route to the airfield.</summary>
    Inbound,
    /// <summary>Orbiting in the holding stack, waiting for its turn.</summary>
    Holding,
    /// <summary>Out of the hold, flying final (DCS lands it).</summary>
    Cleared,
}

/// <summary>An AI group arriving at an airfield we control.</summary>
public sealed class AiArrival
{
    public required string Group { get; init; }
    public required GroundAirfield Airfield { get; init; }
    public required int Coalition { get; init; }
    /// <summary>The user flag that ends its hold (arrival_clear).</summary>
    public required string Flag { get; init; }
    public string Callsign { get; set; } = "";
    public string UnitType { get; set; } = "";
    public AiArrivalPhase Phase { get; set; }
    /// <summary>The runway and end it is expected to land on (from the wind).</summary>
    public Runway? Runway { get; set; }
    public RunwayEnd? End { get; set; }
    public double HoldAltitude { get; set; }
    public DateTime Since { get; set; }
    public DateTime HeldSince { get; set; }
    public DateTime? ClearedAt { get; set; }
    /// <summary>Why it holds, as last logged.</summary>
    public string? HeldFor { get; set; }
    public bool LandingCalled { get; set; }
}

/// <summary>
/// AI arriving at an airfield on the air, players first. An AI group is an arrival when the route of its mission file
/// lands at that airfield and it comes back within <see cref="DetectRange"/> NM after having been farther out than
/// <see cref="AwayRange"/> (or was spawned in the air): groups spawned by scripts, whose routes we cannot see, stay
/// plain traffic. If a player is landing, on approach close in, or about to take off, the AI is held: given a new route
/// to the shared holding stack over the TACAN (or the field), where it orbits until its flag is set, then to final and
/// onto the airfield (as MOOSE FLIGHTCONTROL does). One AI on approach at a time; an AI never holds longer than
/// <see cref="MaxHold"/> (fuel). DCS flies the approach and the landing; Approach and Tower talk to it (no readbacks).
/// </summary>
public sealed class AiArrivals(IDcsCommands dcs, AirfieldCatalog airfields, TowerControl tower, ApproachControl approach, HoldingStacks stacks,
    Action<string>? log = null, Func<DateTime>? clock = null, IAtcRadio? radio = null)
{
    /// <summary>An arrival is taken over this close to its airfield (NM).</summary>
    public const double DetectRange = 30;
    /// <summary>…after having been farther out than this (NM): a departure climbing out is not an arrival.</summary>
    public const double AwayRange = 40;
    /// <summary>A player inbound (with Tower, or in the air near the field without) closer than this holds AI arrivals (NM).</summary>
    public const double PlayerRange = 15;
    /// <summary>Tower clears an AI to land this far out (NM).</summary>
    public const double LandingCallRange = 8;
    /// <summary>The AI ahead on approach must be this close to the field before the next leaves the hold (NM).</summary>
    public const double SpacingRange = 4;
    public static readonly TimeSpan MaxHold = TimeSpan.FromMinutes(10);
    /// <summary>An arrival that has not landed after this long is forgotten.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    private const double Nm = 1852, Ft = 0.3048, Kt = 0.514444;
    private const double HoldSpeed = 250 * Kt, FinalSpeed = 170 * Kt;
    /// <summary>The second point of the race track, from the fix along the landing direction (NM).</summary>
    private const double HoldLeg = 5;

    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly Dictionary<string, AiArrival> _arrivals = [];
    private readonly Dictionary<string, Seen> _seen = [];
    private bool _unsupported;
    private int _flags;

    private sealed class Seen
    {
        public bool FirstInAir;
        public double MaxDistance;
        public bool Asked;
        public int? Destination;
    }

    /// <summary>AI arrivals by group name.</summary>
    public IReadOnlyDictionary<string, AiArrival> Arrivals => _arrivals;

    /// <summary>Whether AI arrivals at an airfield are worth handling for a coalition (airbase id, coalition): a player is near. The app sets it.</summary>
    public Func<int, int, bool> OnAir { get; set; } = (_, _) => true;

    public void OnEvent(DcsEventMessage e)
    {
        if (e.Event == "land" && e.Group is { } group && _arrivals.TryGetValue(group, out var a))
        {
            log?.Invoke($"AI {a.Callsign}: landed at {a.Airfield.Facility}");
            Forget(a);
        }
    }

    public async Task OnUnitsAsync(IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (_unsupported) return;
        try
        {
            foreach (var members in units.Values.Where(u => !u.IsPlayer && !u.IsHelicopter && u.Group is not null && u.Cat == "plane")
                         .GroupBy(u => u.Group!).Select(g => g.OrderBy(u => u.Id).ToList()))
            {
                await SeeAsync(members, units, ct);
            }
            await ReleaseAsync(units, ct);
            await WatchAsync(units, ct);
        }
        catch (BridgeCommandException ex) when (IsUnknownCommand(ex))
        {
            // A bridge from before AI arrivals: arriving AI is left to DCS, as before.
            _unsupported = true;
            log?.Invoke($"AI arrivals off: the DCS bridge does not support them ({ex.Message}); update the bridge (Install page)");
        }
    }

    /// <summary>The mission ended: everything went with it.</summary>
    public void Reset()
    {
        foreach (var a in _arrivals.Values) stacks.Leave(Occupant(a.Group));
        _arrivals.Clear();
        _seen.Clear();
        _unsupported = false;
    }

    private static bool IsUnknownCommand(BridgeCommandException ex) => ex.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase);

    private static string Occupant(string group) => $"ai:{group}";

    // A group of AI planes: remember where it has been; take it over when it comes back to the airfield it lands at.
    private async Task SeeAsync(List<UnitState> members, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct)
    {
        var lead = members[0];
        string group = lead.Group!;
        if (!_seen.TryGetValue(group, out var seen)) _seen[group] = seen = new Seen { FirstInAir = lead.InAir };
        if (_arrivals.ContainsKey(group) || !lead.InAir) return;
        if (!seen.Asked)
        {
            seen.Asked = true;
            var destination = await dcs.GetGroupDestinationAsync(group, ct);
            seen.Destination = destination.Id;
        }
        if (seen.Destination is not int id || airfields.Airbases.FirstOrDefault(a => a.Id == id && a.Category == 0) is not { } airbase) return;
        var position = new Vec2(lead.X, lead.Z);
        double miles = position.DistanceTo(new Vec2(airbase.X, airbase.Z)) / Nm;
        seen.MaxDistance = Math.Max(seen.MaxDistance, miles);
        if (miles > DetectRange || !(seen.FirstInAir || seen.MaxDistance > AwayRange) || !OnAir(id, lead.Coal)) return;
        if (await airfields.GetAsync(airbase, ct) is not { } airfield) return;
        await ArriveAsync(group, lead, airfield, units, ct);
    }

    private async Task ArriveAsync(string group, UnitState lead, GroundAirfield airfield, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct)
    {
        var weather = await dcs.GetAirbaseWeatherAsync(airfield.Airbase.Name, ct);
        var position = new Vec2(lead.X, lead.Z);
        var a = new AiArrival
        {
            Group = group, Airfield = airfield, Coalition = lead.Coal, Flag = $"DigiTowerArrival{++_flags}",
            Callsign = AiTraffic.CallsignOf(group, units), UnitType = lead.Type, Since = _now(),
        };
        var ends = airfield.Layout.ActiveEnds(weather?.WindFrom ?? 0, weather?.WindSpeed ?? 0).ToList();
        // As Approach picks for players: the parallel whose centre line is nearest.
        if (ends.Count > 0) (a.Runway, a.End) = ends.MinBy(e => e.Runway.FromCenterline(position));
        _arrivals[group] = a;
        radio?.Prepare(Phraseology.CallsignSamples(a.Callsign), soon: true);
        radio?.PreparePilot(Phraseology.CallsignSamples(a.Callsign), soon: true);
        string where = ApproachControl.PositionOf(airfield, position);

        if (HoldReason(a, units) is not { } reason || a.End is null)
        {
            a.Phase = AiArrivalPhase.Inbound;
            if (a.End is not null)
                Say(a, "Approach", Phraseology.AiStraightIn(a.Callsign, airfield.Facility, where, a.End),
                    Phraseology.AiInbound(airfield.Facility, a.Callsign, where), Phraseology.AiStraightInReadback(a.Callsign, a.End));
            log?.Invoke($"AI {a.Callsign} ({a.UnitType}) inbound to {airfield.Facility}, {where}: straight in");
            return;
        }
        var p = approach.ProceduresFor(airfield);
        a.HoldAltitude = stacks.Take(airfield.Airbase.Id, Occupant(group), p.HoldingAltitude, p.HoldingStep);
        try
        {
            await dcs.HoldArrivalAsync(group, a.Flag, airfield.Airbase.Id, HoldingFixFor(airfield, a.End, a.HoldAltitude),
                FinalFor(a.End, p.FinalDistance, p.InterceptAltitude), LandingPointFor(a.End, airfield.Airbase.Y), ct);
        }
        catch (BridgeCommandException ex) when (!IsUnknownCommand(ex))
        {
            stacks.Leave(Occupant(group));
            a.Phase = AiArrivalPhase.Inbound;
            log?.Invoke($"AI {a.Callsign}: cannot be held ({ex.Message}), left to DCS");
            return;
        }
        a.Phase = AiArrivalPhase.Holding;
        a.HeldSince = _now();
        a.HeldFor = reason;
        int minutes = 5 * stacks.At(airfield.Airbase.Id).Count;
        bool tacan = airfield.Radio.Tacan is not null;
        Say(a, "Approach", Phraseology.Hold(a.Callsign, airfield.Facility, where, tacan, a.HoldAltitude, null, a.UnitType, minutes),
            Phraseology.AiInbound(airfield.Facility, a.Callsign, where), Phraseology.AiHoldReadback(a.Callsign, tacan, a.HoldAltitude));
        log?.Invoke($"AI {a.Callsign} ({a.UnitType}) inbound to {airfield.Facility}, {where}: holding at {a.HoldAltitude:0} ft, {reason}");
    }

    // Holders leave the stack in order, lowest first, one at a time per airfield, when no player needs the runway (or
    // after MaxHold).
    private async Task ReleaseAsync(IReadOnlyDictionary<string, UnitState> units, CancellationToken ct)
    {
        foreach (var field in _arrivals.Values.Where(a => a.Phase == AiArrivalPhase.Holding).GroupBy(a => a.Airfield.Airbase.Id).ToList())
        {
            var next = field.OrderBy(a => a.HoldAltitude).First();
            bool overdue = _now() - next.HeldSince >= MaxHold;
            if (HoldReason(next, units) is { } reason && !overdue)
            {
                if (reason != next.HeldFor) log?.Invoke($"AI {next.Callsign}: holding, {reason}");
                next.HeldFor = reason;
                continue;
            }
            await dcs.ClearArrivalAsync(next.Flag, ct);
            stacks.Leave(Occupant(next.Group));
            next.Phase = AiArrivalPhase.Cleared;
            next.ClearedAt = _now();
            var p = approach.ProceduresFor(next.Airfield);
            Say(next, "Approach", Phraseology.AiLeaveHold(next.Callsign, next.End!, p.InterceptAltitude),
                readback: Phraseology.AiLeaveHoldReadback(next.Callsign, next.End!, p.InterceptAltitude));
            log?.Invoke($"AI {next.Callsign}: leaves the hold after {(_now() - next.HeldSince).TotalSeconds:F0} s{(overdue ? " (held long enough)" : "")} (had waited: {next.HeldFor})");
        }
    }

    // Tower clears it once established on final (not while a player is on that runway or landing on it); forgotten once
    // down or gone.
    private async Task WatchAsync(IReadOnlyDictionary<string, UnitState> units, CancellationToken ct)
    {
        foreach (var a in _arrivals.Values.ToList())
        {
            var members = AiTraffic.MembersOf(a.Group, units);
            if (members.Count == 0 || _now() - a.Since > Timeout)
            {
                Forget(a);
                continue;
            }
            var lead = members[0];
            if (!lead.InAir)
            {
                log?.Invoke($"AI {a.Callsign}: on the ground at {a.Airfield.Facility}");
                Forget(a);
                continue;
            }
            if (a.Phase == AiArrivalPhase.Holding || a.LandingCalled || a.End is null || a.Runway is null) continue;
            // Established on final, not just near the field (the hold is over it).
            if (TowerControl.DistanceOnFinal(a.Runway, a.End, lead) is not double metres || metres > LandingCallRange * Nm) continue;
            a.LandingCalled = true;
            var runway = a.Runway;
            bool playerOnIt = units.Values.Any(u => u.IsPlayer && !u.InAir && runway.IsOnRunway(new Vec2(u.X, u.Z)))
                || tower.Flights.Values.Any(f => f.UsesRunway && f.Runway == runway && f.Phase is TowerPhase.ClearedToLand or TowerPhase.Landed);
            if (playerOnIt) continue;
            var weather = await dcs.GetAirbaseWeatherAsync(a.Airfield.Airbase.Name, ct);
            Say(a, "Tower", Phraseology.ClearedToLand(a.Callsign, a.End, weather),
                Phraseology.AiOnFinal(a.Airfield.Facility, a.Callsign, a.End), Phraseology.AiLandingReadback(a.Callsign, a.End));
        }
    }

    /// <summary>
    /// Why an AI arrival must wait now, or null: players first (landing, on approach or inbound close in, on the runway,
    /// about to take off), then one AI on approach at a time.
    /// </summary>
    private string? HoldReason(AiArrival a, IReadOnlyDictionary<string, UnitState> units)
    {
        int id = a.Airfield.Airbase.Id;
        var field = new Vec2(a.Airfield.Airbase.X, a.Airfield.Airbase.Z);
        double MilesOf(UnitState u) => new Vec2(u.X, u.Z).DistanceTo(field) / Nm;
        bool Near(string unitName) => units.TryGetValue(unitName, out var u) && MilesOf(u) < PlayerRange;

        foreach (var f in tower.Flights.Values.Where(f => f.Airfield.Airbase.Id == id && f.UsesRunway))
        {
            string? doing = f.Phase switch
            {
                TowerPhase.WaitingForDeparture => "ready for departure",
                TowerPhase.LineUpAndWait => "lined up",
                TowerPhase.ClearedForTakeoff => "taking off",
                TowerPhase.Departed when Near(f.UnitName) => "departing",
                TowerPhase.ClearedToLand => "landing",
                TowerPhase.Landed => "on the runway after landing",
                TowerPhase.Inbound when Near(f.UnitName) => "arriving",
                _ => null,
            };
            if (doing is not null) return $"{f.Callsign} {doing}";
        }
        foreach (var f in approach.Flights.Values.Where(f => f.IsArrival && f.Airfield.Airbase.Id == id))
            return $"{f.Callsign} on approach";
        foreach (var u in units.Values.Where(u => u.IsPlayer))
        {
            if (!u.InAir && a.Airfield.Layout.Runways.Any(r => r.IsOnRunway(new Vec2(u.X, u.Z)))) return $"{Phraseology.Callsign(u)} on the runway";
            // A player low and close without talking to us: maybe landing, maybe in the pattern.
            if (u.InAir && !u.IsHelicopter && MilesOf(u) < 10 && u.Agl < 3000 * Ft) return $"{Phraseology.Callsign(u)} near the field";
        }
        // One AI on approach at a time: the one ahead must be close in first.
        foreach (var other in _arrivals.Values.Where(o => o != a && o.Airfield.Airbase.Id == id && o.Phase != AiArrivalPhase.Holding))
        {
            if (AiTraffic.MembersOf(other.Group, units).FirstOrDefault() is { InAir: true } lead && MilesOf(lead) > SpacingRange)
                return $"{other.Callsign} on approach";
        }
        return null;
    }

    private void Forget(AiArrival a)
    {
        stacks.Leave(Occupant(a.Group));
        _arrivals.Remove(a.Group);
    }

    /// <summary>The race track: from the fix (the TACAN, else the field) along the landing direction.</summary>
    internal static HoldingFix HoldingFixFor(GroundAirfield airfield, RunwayEnd end, double feet)
    {
        var fix = airfield.Radio.Tacan?.Position ?? new Vec2(airfield.Airbase.X, airfield.Airbase.Z);
        var leg = fix + Vec2.FromHeading(end.TrueHeading) * (HoldLeg * Nm);
        return new HoldingFix(fix.X, fix.Z, feet * Ft, HoldSpeed, leg.X, leg.Z);
    }

    /// <summary>The point on final it flies to from the hold: the approach's intercept point and altitude.</summary>
    internal static (double X, double Z, double Alt, double Speed) FinalFor(RunwayEnd end, double miles, double feet)
    {
        var p = end.Threshold - Vec2.FromHeading(end.TrueHeading) * (miles * Nm);
        return (p.X, p.Z, feet * Ft, FinalSpeed);
    }

    /// <summary>
    /// The landing waypoint: 5 NM out on final at the 3° glide-path altitude above <paramref name="elevation"/> (m). As MOOSE
    /// does, and measured: a landing waypoint at the field makes DCS fly a wide circuit first, one at altitude 0 makes the
    /// AI dive to a few hundred feet miles out.
    /// </summary>
    internal static (double X, double Z, double Alt) LandingPointFor(RunwayEnd end, double elevation)
    {
        const double miles = 5;
        var p = end.Threshold - Vec2.FromHeading(end.TrueHeading) * (miles * Nm);
        return (p.X, p.Z, elevation + miles * Nm * Math.Tan(3 * Math.PI / 180));
    }

    /// <summary>A controller's call to an AI arrival, with what its pilot says before and after (in the AI pilots' voice, if any).</summary>
    private void Say(AiArrival a, string role, string text, string? pilotCall = null, string? readback = null)
    {
        // A Mayday silences the frequency: the AI is still worked, without a word.
        if (tower.Silenced(a.Airfield.Airbase.Id))
        {
            log?.Invoke($"AI {a.Callsign}: {text} (not said: Mayday on the frequency)");
            return;
        }
        if (radio is not null && a.Airfield.Call(role, a.Coalition, text) is { } call)
            radio.Transmit(call with { PilotCall = pilotCall, PilotReadback = readback, ToAi = true });
        if (pilotCall is not null) log?.Invoke($"AI {a.Callsign} (pilot): {pilotCall}");
        log?.Invoke($"AI {a.Callsign}: {text}");
        if (readback is not null) log?.Invoke($"AI {a.Callsign} (pilot): {readback}");
    }
}
