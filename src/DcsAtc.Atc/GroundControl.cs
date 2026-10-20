using DcsAtc.Airfields;
using DcsAtc.Airfields.Taxi;
using DcsAtc.Bridge;

namespace DcsAtc.Atc;

public enum TaxiPhase
{
    /// <summary>Cleared and taxiing.</summary>
    Taxiing,
    /// <summary>At the runway holding point, waiting for Tower.</summary>
    HoldingShort,
}

/// <summary>A taxi clearance for one aircraft or a flight: to a runway holding point, or to parking.</summary>
public sealed class TaxiClearance
{
    public required Party Party { get; init; }
    public required int Coalition { get; init; }
    public required GroundAirfield Airfield { get; init; }
    public required TaxiRoute Route { get; init; }
    public required string Text { get; init; }
    /// <summary>Set for taxi clearances to a runway.</summary>
    public DepartureRoute? Departure { get; init; }
    /// <summary>Set for taxi clearances to parking (the lead's spot).</summary>
    public ParkingSpot? Spot { get; init; }
    public TaxiPhase Phase { get; set; } = TaxiPhase.Taxiing;
    /// <summary>A helicopter: air-taxis, may hover, is held to the height instead of the speed.</summary>
    public bool Helicopter { get; init; }
    internal int OffRouteSamples { get; set; }
    internal DateTime LastHeightWarning { get; set; } = DateTime.MinValue;
    internal DateTime LastOffRouteWarning { get; set; } = DateTime.MinValue;
    internal Dictionary<string, DateTime> LastSpeedWarning { get; } = [];

    /// <summary>Giving way to traffic: held (at a point of the route, or where it is) until the traffic has passed.</summary>
    public TrafficHold? TrafficHold { get; internal set; }
    internal (string Other, TrafficKind Kind, int Seen) PendingTraffic { get; set; }
    internal Dictionary<string, DateTime> LastFollow { get; } = [];
    internal DateTime LastHoldReminder { get; set; } = DateTime.MinValue;
    /// <summary>Traffic this flight was last released from, and when: not held for it again right away.</summary>
    internal (string Other, DateTime At) LastPassed { get; set; }

    public int Key => Party.Key;
    public string Callsign => Party.Callsign;
    /// <summary>The unit whose position drives the clearance: the aircraft itself, or the flight lead.</summary>
    public string UnitName => Party.LeadUnit;
    public string DrawingKey => $"taxi-{Key}";
    public string HoldDrawingKey => $"hold-{Key}";
}

/// <summary>
/// A flight giving way: <see cref="Unit"/> (the member that met the traffic) stops at <see cref="Along"/> metres along
/// the route (where it was, for "hold position") until <see cref="OtherUnit"/> has passed.
/// </summary>
public sealed record TrafficHold(string Unit, string OtherUnit, TrafficKind Kind, double Along, DateTime Since, string Text);

/// <summary>
/// Start up approved, before taxi (optional; how the flight departs is asked for at the holding point). Kept until the
/// flight takes off, so Ground can say it again and the menu no longer offers it.
/// </summary>
public sealed record StartupApproval(Party Party, int Coalition, GroundAirfield Airfield, string Text, bool Helicopter = false)
{
    public int Key => Party.Key;
    /// <summary>The flight lead (or the aircraft itself).</summary>
    public string UnitName => Party.LeadUnit;
}

/// <summary>
/// Ground control: taxi clearances to the active runway's holding point and to parking (route computed
/// on the DCS taxi network, named from chart labels, drawn on the F10 map) and monitoring while taxiing.
/// A declared flight gets one clearance; the lead's position drives it. Everything said to a pilot at an
/// airfield is also transmitted on its Ground frequency when a radio is given.
/// </summary>
public sealed class GroundControl(IDcsCommands dcs, AirfieldCatalog airfields, FlightRegistry flights, Action<string>? log = null, Func<DateTime>? clock = null, IAtcRadio? radio = null)
{
    /// <summary>Further than this from the cleared route counts as off route (metres).</summary>
    public const double OffRouteDistance = 35;

    /// <summary>Taxi speed limit (m/s, 30 knots).</summary>
    public const double TaxiSpeedLimit = 15.4;

    /// <summary>Within this distance of the route's end the aircraft has arrived (metres).</summary>
    public const double Arrived = 30;

    public static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(30);
    private static readonly double[] DepartureColor = [1, 0, 1, 1];
    private static readonly double[] ParkingColor = [0, 0.8, 1, 1];
    private static readonly double[] HoldColor = [1, 0.15, 0.15, 1];

    /// <summary>Ground tells a flight to give way when the conflict is at most this far ahead (seconds).</summary>
    public const double TrafficWithin = 30;

    /// <summary>A conflict is acted on once seen in this many unit updates in a row (predictions of turning aircraft wobble).</summary>
    public const int TrafficConfirmations = 2;

    /// <summary>Held at least this long before "continue taxi"; released after <see cref="MaxTrafficHold"/> whatever happens.</summary>
    public static readonly TimeSpan MinTrafficHold = TimeSpan.FromSeconds(5), MaxTrafficHold = TimeSpan.FromMinutes(3);

    /// <summary>"Follow the F-16 ahead" at most this often for the same pair.</summary>
    public static readonly TimeSpan FollowInterval = TimeSpan.FromMinutes(2);

    /// <summary>"Hold position" repeated at most this often to a flight rolling past its hold.</summary>
    public static readonly TimeSpan HoldReminderInterval = TimeSpan.FromSeconds(15);

    /// <summary>After "continue taxi", not held again for the same traffic within this time (predictions wobble).</summary>
    public static readonly TimeSpan PassedQuiet = TimeSpan.FromSeconds(15);

    private readonly Dictionary<int, TaxiClearance> _clearances = [];
    private readonly Dictionary<int, StartupApproval> _startups = [];
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);

    /// <summary>Clearances by party key (the lead's group id).</summary>
    public IReadOnlyDictionary<int, TaxiClearance> Clearances => _clearances;

    /// <summary>The clearance of the unit's party (its own or its flight's), if any.</summary>
    public TaxiClearance? ClearanceFor(UnitState unit, IReadOnlyDictionary<string, UnitState> units) =>
        _clearances.GetValueOrDefault(flights.PartyFor(unit, units).Key);

    /// <summary>Startup approvals by party key, until takeoff.</summary>
    public IReadOnlyDictionary<int, StartupApproval> Startups => _startups;

    /// <summary>The startup approval of the unit's party, if any.</summary>
    public StartupApproval? StartupFor(UnitState unit, IReadOnlyDictionary<string, UnitState> units) =>
        _startups.GetValueOrDefault(flights.PartyFor(unit, units).Key);

    /// <summary>"Viper 1-1, Al Dhafra Ground, start up approved, wind 310 degrees, 10 knots, QNH 1013, altimeter 29.92."</summary>
    public async Task RequestStartupAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, GroundAirfield? at = null, CancellationToken ct = default)
    {
        if (await OnGroundAtAirfieldAsync(unit, units, at, ct) is not { } who) return;
        var (party, lead, airfield) = who;
        var weather = await dcs.GetAirbaseWeatherAsync(airfield.Airbase.Name, ct);
        string text = Phraseology.StartupApproved(party.Callsign, airfield.Facility, weather, lead.Type);
        _startups[party.Key] = new StartupApproval(party, lead.Coal, airfield, text, lead.IsHelicopter);
        await SayAsync(party, airfield, text, 20, ct);
        log?.Invoke($"{party.Callsign}: {text}");
    }

    /// <summary>Taxi to the holding point of the active runway.</summary>
    public async Task RequestTaxiAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, GroundAirfield? at = null, CancellationToken ct = default)
    {
        if (await OnGroundAtAirfieldAsync(unit, units, at, ct) is not { } who) return;
        var (party, lead, airfield) = who;

        var weather = await dcs.GetAirbaseWeatherAsync(airfield.Airbase.Name, ct);
        var ends = airfield.Layout.ActiveEnds(weather?.WindFrom ?? 0, weather?.WindSpeed ?? 0);
        var position = new Vec2(lead.X, lead.Z);
        var departure = ends
            .Select(e => airfield.Layout.RouteToRunway(position, e.End.Name))
            .OfType<DepartureRoute>()
            .MinBy(d => d.Route.Length);
        if (departure is null)
        {
            await SayAsync(party, airfield, Phraseology.UnableToRoute(party.Callsign, airfield.Facility), 15, ct);
            return;
        }

        var via = airfield.Layout.Via(departure.Route);
        string text = Phraseology.TaxiClearance(party.Callsign, airfield.Facility, departure.End, via, weather, lead.Type, airTaxi: lead.IsHelicopter);
        await StartAsync(new TaxiClearance
        {
            Party = party,
            Coalition = lead.Coal,
            Airfield = airfield,
            Route = departure.Route,
            Text = text,
            Departure = departure,
            Helicopter = lead.IsHelicopter,
        }, DepartureColor, $"HOLD SHORT {departure.End.Name}", ct);
    }

    /// <summary>Taxi to a free parking spot (after landing). A flight is sent to the lead's spot's area.</summary>
    public async Task RequestTaxiToParkingAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, GroundAirfield? at = null, CancellationToken ct = default)
    {
        if (await OnGroundAtAirfieldAsync(unit, units, at, ct) is not { } who) return;
        var (party, lead, airfield) = who;

        var others = units.Values.Where(u => !party.Units.Contains(u.Name) && !u.InAir).Select(u => new Vec2(u.X, u.Z));
        var parking = airfield.Layout.RouteToParking(new Vec2(lead.X, lead.Z), lead.IsHelicopter, others);
        if (parking is not { } found)
        {
            await SayAsync(party, airfield, Phraseology.NoFreeParking(party.Callsign, airfield.Facility), 15, ct);
            return;
        }
        var (spot, route) = found;

        string area = airfield.Layout.AreaNear(spot.Position) ?? "parking";
        var via = airfield.Layout.Via(route);
        string text = Phraseology.TaxiToParking(party.Callsign, airfield.Facility, area, via, airTaxi: lead.IsHelicopter);
        await StartAsync(new TaxiClearance
        {
            Party = party,
            Coalition = lead.Coal,
            Airfield = airfield,
            Route = route,
            Text = text,
            Spot = spot,
            Helicopter = lead.IsHelicopter,
        }, ParkingColor, area.ToUpperInvariant(), ct);
    }

    public async Task CancelTaxiAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (ClearanceFor(unit, units) is not { } c) return;
        _clearances.Remove(c.Key);
        await ClearDrawingsAsync(c, ct);
        await SayAsync(c.Party, c.Airfield, Phraseology.TaxiCancelled(c.Callsign, c.Airfield.Facility), 15, ct);
    }

    /// <summary>Ends a clearance without a message (e.g. when Tower takes over).</summary>
    public async Task ReleaseAsync(int key, CancellationToken ct = default)
    {
        if (_clearances.Remove(key, out var c)) await ClearDrawingsAsync(c, ct);
    }

    /// <summary>The route on the map, and the hold line if the flight was giving way.</summary>
    private async Task ClearDrawingsAsync(TaxiClearance c, CancellationToken ct)
    {
        await dcs.ClearDrawingAsync(c.DrawingKey, ct);
        if (c.TrafficHold is not null)
        {
            c.TrafficHold = null;
            await dcs.ClearDrawingAsync(c.HoldDrawingKey, ct);
        }
    }

    /// <summary>Repeats the taxi clearance (while giving way to traffic, that instruction), or before taxi the startup approval.</summary>
    public async Task SayAgainAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (unit.GroupId is not int g) return;
        if (ClearanceFor(unit, units) is { } c)
        {
            string text = c.TrafficHold?.Text ?? c.Text;
            await dcs.ShowTextAsync(g, text, 30, ct: ct);
            Transmit(c.Airfield, c.Coalition, text);
        }
        else if (StartupFor(unit, units) is { } s)
        {
            await dcs.ShowTextAsync(g, s.Text, 30, ct: ct);
            Transmit(s.Airfield, s.Coalition, s.Text);
        }
    }

    /// <summary>Checks every taxiing flight against its clearance.</summary>
    public async Task OnUnitsAsync(IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        // A startup approval lasts until the flight is airborne (or gone); a helicopter's until it leaves the field.
        foreach (var s in _startups.Values.Where(s => !units.TryGetValue(s.UnitName, out var lead) || !Helicopters.OnField(lead)).ToList()) _startups.Remove(s.Key);

        foreach (var c in _clearances.Values.ToList())
        {
            if (!units.TryGetValue(c.UnitName, out var unit) || !Helicopters.OnField(unit))
            {
                await ReleaseAsync(c.Key, ct);
                continue;
            }
            if (c.Phase != TaxiPhase.Taxiing) continue;

            var position = new Vec2(unit.X, unit.Z);
            var route = c.Route.Geometry;
            if (position.DistanceTo(route[^1]) <= Arrived)
            {
                await ArriveAsync(c, ct);
                continue;
            }

            var now = _now();
            if (Polyline.Project(route, position).Distance > (c.Helicopter ? Helicopters.OffRouteDistance : OffRouteDistance))
            {
                c.OffRouteSamples++;
                if (c.OffRouteSamples >= 3 && now - c.LastOffRouteWarning >= WarningInterval)
                {
                    c.LastOffRouteWarning = now;
                    await SayAsync(c.Party, c.Airfield, Phraseology.OffRoute(c.Callsign, c.Airfield.Facility), 15, ct);
                }
            }
            else
            {
                c.OffRouteSamples = 0;
            }

            // Helicopters air-taxi at the speed they like, but low.
            if (c.Helicopter)
            {
                if (unit.Agl > Helicopters.AirTaxiWarning && now - c.LastHeightWarning >= WarningInterval)
                {
                    c.LastHeightWarning = now;
                    await SayAsync(c.Party, c.Airfield, Phraseology.RemainLow(c.Callsign, c.Airfield.Facility), 10, ct);
                }
                continue;
            }

            // Speed is every member's own business.
            foreach (var name in c.Party.Units)
            {
                if (!units.TryGetValue(name, out var member) || member.InAir || member.GroundSpeed <= TaxiSpeedLimit || member.GroupId is not int g) continue;
                if (now - c.LastSpeedWarning.GetValueOrDefault(name, DateTime.MinValue) < WarningInterval) continue;
                c.LastSpeedWarning[name] = now;
                string slow = Phraseology.ReduceTaxiSpeed(Phraseology.Callsign(member), c.Airfield.Facility);
                await dcs.ShowTextAsync(g, slow, 10, ct: ct);
                Transmit(c.Airfield, member.Coal, slow);
            }
        }

        await TrafficAsync(units, ct);
    }

    /// <summary>
    /// Traffic on the ground (M7): every taxiing jet flight is checked against everything else moving or standing on
    /// its airfield's taxiways. A flight that gives way is told to hold (short of the other's taxiway, before a shared
    /// stretch, or where it is) and to continue once the traffic has passed; one catching up is told to follow.
    /// </summary>
    private async Task TrafficAsync(IReadOnlyDictionary<string, UnitState> units, CancellationToken ct)
    {
        var now = _now();
        foreach (var atField in _clearances.Values.Where(c => !c.Helicopter).GroupBy(c => c.Airfield).ToList())
        {
            var taxiing = atField.Where(c => c.Phase == TaxiPhase.Taxiing).ToList();
            if (taxiing.Count == 0) continue;
            var movers = Movers(atField.Key, atField.ToList(), units);
            foreach (var c in taxiing)
            {
                if (!_clearances.ContainsKey(c.Key)) continue;
                var own = movers.Where(m => m.Clearance?.Key == c.Key).ToList();
                // Not traffic: the flight itself, and AI wingmen in its players' DCS groups.
                var others = movers.Where(m => m.Clearance?.Key != c.Key && !(m.Group is int g && c.Party.Groups.Contains(g))).ToList();
                if (own.Count == 0) continue;
                if (c.TrafficHold is { } hold) await KeepHoldAsync(c, hold, own, others, units, now, ct);
                else await LookOutAsync(c, own, others, now, ct);
            }
        }
    }

    /// <summary>The aircraft on an airfield's taxiways: its cleared flights along their routes, everyone else straight ahead.</summary>
    private static List<GroundMover> Movers(GroundAirfield airfield, IReadOnlyList<TaxiClearance> clearances, IReadOnlyDictionary<string, UnitState> units)
    {
        static Vec2 DirectionOf(UnitState u) => u.GroundSpeed > 0.5 ? new Vec2(u.Vx, u.Vz).Normalized : Vec2.FromHeading(u.Hdg);
        static double Reach(double speed) => speed * GroundTraffic.Horizon + GroundTraffic.Separation + 50;

        var movers = new List<GroundMover>();
        var cleared = new HashSet<string>();
        foreach (var c in clearances)
        {
            foreach (var name in c.Party.Units)
            {
                if (!units.TryGetValue(name, out var u) || u.InAir) continue;
                cleared.Add(name);
                var p = new Vec2(u.X, u.Z);
                // Holding short at the runway: standing there; taxiing (held or not): along the route.
                IReadOnlyList<Vec2> path = c.Phase == TaxiPhase.Taxiing ? GroundTraffic.RouteAhead(c.Route.Geometry, p, Reach(Math.Max(u.GroundSpeed, GroundTraffic.ResumeSpeed))) : [p];
                movers.Add(new GroundMover(name, Phraseology.SpokenType(u.Type), p, DirectionOf(u), u.GroundSpeed, path, c, u.GroupId));
            }
        }
        foreach (var u in units.Values)
        {
            if (u.InAir || u.Cat != "plane" || cleared.Contains(u.Name)) continue;
            var p = new Vec2(u.X, u.Z);
            if (!airfield.Layout.Covers(p)) continue;
            var dir = DirectionOf(u);
            IReadOnlyList<Vec2> path = u.GroundSpeed >= GroundTraffic.Moving ? GroundTraffic.NetworkAhead(airfield.Layout.Graph, p, dir, Reach(u.GroundSpeed)) : [p];
            movers.Add(new GroundMover(u.Name, Phraseology.SpokenType(u.Type), p, dir, u.GroundSpeed, path, null, u.GroupId));
        }
        return movers;
    }

    /// <summary>The most urgent conflict where this flight gives way, acted on once confirmed.</summary>
    private async Task LookOutAsync(TaxiClearance c, List<GroundMover> own, List<GroundMover> others, DateTime now, CancellationToken ct)
    {
        (TrafficConflict Conflict, GroundMover Yields, GroundMover Other)? worst = null;
        foreach (var m in own)
            foreach (var o in others.Where(o => !(o.UnitName == c.LastPassed.Other && now - c.LastPassed.At < PassedQuiet)))
                if (GroundTraffic.FirstConflict(m, o) is { } conflict && conflict.Time <= TrafficWithin
                    && GroundTraffic.WhoGivesWay(conflict) is { } w && w.Yields.Clearance?.Key == c.Key
                    && (worst is null || conflict.Time < worst.Value.Conflict.Time))
                    worst = (conflict, w.Yields, w.Other);
        if (worst is not { } found)
        {
            c.PendingTraffic = default;
            return;
        }
        var pending = c.PendingTraffic;
        int seen = pending.Other == found.Other.UnitName && pending.Kind == found.Conflict.Kind ? pending.Seen + 1 : 1;
        c.PendingTraffic = (found.Other.UnitName, found.Conflict.Kind, seen);
        if (seen < TrafficConfirmations) return;

        var plan = GroundTraffic.Plan(found.Conflict, found.Yields, found.Other, c.Airfield.Layout);
        string facility = c.Airfield.Facility;
        if (plan.Kind == TrafficKind.Behind)
        {
            if (now - c.LastFollow.GetValueOrDefault(plan.Other.UnitName, DateTime.MinValue) < FollowInterval) return;
            c.LastFollow[plan.Other.UnitName] = now;
            await SayTrafficAsync(c, Phraseology.FollowTraffic(c.Callsign, facility, plan.Other.Type), plan, ct);
            return;
        }

        string where = plan.Kind == TrafficKind.HeadOn
            ? "opposite direction" + (plan.OtherTaxiway is { } t ? $" on {t}" : "")
            : (plan.Other.Position - plan.Yields.Position).Dot(plan.Yields.Direction.Left) > 0 ? "from the left" : "from the right";
        string text = Phraseology.GiveWay(c.Callsign, facility, plan.HoldNow, plan.HoldShortOf, plan.Other.Type, where);
        double along = Polyline.Project(c.Route.Geometry, plan.Yields.Position).Along + Math.Max(0, plan.HoldAlong);
        c.TrafficHold = new TrafficHold(plan.Yields.UnitName, plan.Other.UnitName, plan.Kind, along, now, text);
        c.PendingTraffic = default;
        await SayTrafficAsync(c, text, plan, ct);
        if (!plan.HoldNow)
        {
            var across = plan.Yields.DirectionAt(plan.HoldAlong / Math.Max(plan.Yields.Speed, 0.1)).Left * 15;
            Vec2 from = plan.HoldPoint - across, to = plan.HoldPoint + across;
            await dcs.DrawRouteAsync(c.HoldDrawingKey, c.Coalition, [(from.X, from.Z), (to.X, to.Z)], HoldColor,
                plan.HoldShortOf is { } name ? $"HOLD SHORT {name}" : "HOLD", ct);
        }
    }

    /// <summary>A held flight: "continue taxi" once nothing it gives way to is left ahead, "hold position" if it rolls on.</summary>
    private async Task KeepHoldAsync(TaxiClearance c, TrafficHold hold, List<GroundMover> own, List<GroundMover> others, IReadOnlyDictionary<string, UnitState> units, DateTime now, CancellationToken ct)
    {
        string facility = c.Airfield.Facility;
        bool blocked = own.Any(m =>
        {
            var resumed = m with { Speed = Math.Max(m.Speed, GroundTraffic.ResumeSpeed) };
            return others.Any(o => GroundTraffic.FirstConflict(resumed, o) is { Kind: not TrafficKind.Behind } conflict && conflict.Time <= TrafficWithin
                && GroundTraffic.WhoGivesWay(conflict) is { } w && w.Yields.Clearance?.Key == c.Key);
        });
        if ((!blocked && now - hold.Since >= MinTrafficHold) || now - hold.Since >= MaxTrafficHold)
        {
            c.TrafficHold = null;
            c.LastPassed = (hold.OtherUnit, now);
            await dcs.ClearDrawingAsync(c.HoldDrawingKey, ct);
            await SayAsync(c.Party, c.Airfield, Phraseology.ContinueTaxi(c.Callsign, facility), 10, ct);
            log?.Invoke($"{c.Callsign}: continue taxi (held {(now - hold.Since).TotalSeconds:F0} s for {hold.OtherUnit})");
            return;
        }

        // Rolling on past the hold.
        if (units.TryGetValue(hold.Unit, out var unit) && unit.GroundSpeed > 2 && now - c.LastHoldReminder >= HoldReminderInterval
            && Polyline.Project(c.Route.Geometry, new Vec2(unit.X, unit.Z)).Along > hold.Along + 25)
        {
            c.LastHoldReminder = now;
            await SayAsync(c.Party, c.Airfield, Phraseology.HoldPositionNow(c.Callsign, facility), 10, ct);
        }
    }

    private async Task SayTrafficAsync(TaxiClearance c, string text, TrafficPlan plan, CancellationToken ct)
    {
        await SayAsync(c.Party, c.Airfield, text, 15, ct);
        log?.Invoke($"{c.Callsign}: {text} ({plan.Kind}, {plan.Other.UnitName}, {plan.Time:F0} s)");
    }

    public async Task OnEventAsync(DcsEventMessage e, CancellationToken ct = default)
    {
        if (e.Event is not ("takeoff" or "runway_takeoff" or "crash" or "dead" or "unit_lost" or "player_leave_unit")) return;
        // A helicopter lifting into a hover is not departing: its clearance ends by height (OnUnitsAsync).
        bool takeoff = e.Event is "takeoff" or "runway_takeoff";
        foreach (var s in _startups.Values.Where(s => s.UnitName == e.Unit && !(takeoff && s.Helicopter)).ToList()) _startups.Remove(s.Key);
        var c = _clearances.Values.FirstOrDefault(x => x.UnitName == e.Unit && !(takeoff && x.Helicopter));
        if (c is not null) await ReleaseAsync(c.Key, ct);
    }

    /// <summary>The mission ended: forget all clearances (their drawings went with the mission).</summary>
    public void Reset()
    {
        _clearances.Clear();
        _startups.Clear();
    }

    private async Task ArriveAsync(TaxiClearance c, CancellationToken ct)
    {
        await ClearDrawingsAsync(c, ct);
        if (c.Departure is { } d)
        {
            c.Phase = TaxiPhase.HoldingShort;
            await SayAsync(c.Party, c.Airfield, Phraseology.HoldShortContactTower(c.Callsign, c.Airfield.Facility, d.End, c.Airfield.Frequencies.Own("Tower")), 20, ct);
        }
        else
        {
            _clearances.Remove(c.Key);
            await SayAsync(c.Party, c.Airfield, Phraseology.ParkingReached(c.Callsign, c.Airfield.Facility), 10, ct);
        }
    }

    private async Task StartAsync(TaxiClearance clearance, double[] color, string label, CancellationToken ct)
    {
        _clearances[clearance.Key] = clearance;
        await SayAsync(clearance.Party, clearance.Airfield, clearance.Text, 30, ct);
        var line = Polyline.Simplify(clearance.Route.Geometry, 3).Select(p => (p.X, p.Z));
        await dcs.DrawRouteAsync(clearance.DrawingKey, clearance.Coalition, line, color, label, ct);
        log?.Invoke($"{clearance.Callsign}: {clearance.Text} ({clearance.Route.Length:F0} m)");
    }

    /// <summary>Text to every member; on the radio too when it comes from an airfield's Ground.</summary>
    private async Task SayAsync(Party party, GroundAirfield? airfield, string text, int seconds, CancellationToken ct)
    {
        foreach (var g in party.Groups) await dcs.ShowTextAsync(g, text, seconds, ct: ct);
        Transmit(airfield, party.Coalition, text);
    }

    private void Transmit(GroundAirfield? airfield, int coalition, string text)
    {
        if (radio is not null && airfield?.Call("Ground", coalition, text) is { } call) radio.Transmit(call);
    }

    /// <summary>The unit's party, its lead and the airfield if the lead is on the ground at an airfield we control; tells it otherwise.</summary>
    private async Task<(Party Party, UnitState Lead, GroundAirfield Airfield)?> OnGroundAtAirfieldAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, GroundAirfield? at, CancellationToken ct)
    {
        if (unit.GroupId is null) return null;
        var party = flights.PartyFor(unit, units);
        var lead = units.GetValueOrDefault(party.LeadUnit) ?? unit;
        if (!Helicopters.OnField(lead))
        {
            await SayAsync(party, null, Phraseology.GroundWhileAirborne(party.Callsign), 10, ct);
            return null;
        }
        var position = new Vec2(lead.X, lead.Z);
        var airfield = at is null ? await airfields.AtAsync(position, ct) : at.Layout.Covers(position) ? at : null;
        if (airfield is null)
        {
            await SayAsync(party, null, Phraseology.NoGroundControl(party.Callsign), 10, ct);
            return null;
        }
        return (party, lead, airfield);
    }
}
