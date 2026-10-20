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

    public int Key => Party.Key;
    public string Callsign => Party.Callsign;
    /// <summary>The unit whose position drives the clearance: the aircraft itself, or the flight lead.</summary>
    public string UnitName => Party.LeadUnit;
    public string DrawingKey => $"taxi-{Key}";
}

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
        await dcs.ClearDrawingAsync(c.DrawingKey, ct);
        await SayAsync(c.Party, c.Airfield, Phraseology.TaxiCancelled(c.Callsign, c.Airfield.Facility), 15, ct);
    }

    /// <summary>Ends a clearance without a message (e.g. when Tower takes over).</summary>
    public async Task ReleaseAsync(int key, CancellationToken ct = default)
    {
        if (_clearances.Remove(key, out var c)) await dcs.ClearDrawingAsync(c.DrawingKey, ct);
    }

    /// <summary>Repeats the taxi clearance, or before taxi the startup approval.</summary>
    public async Task SayAgainAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (unit.GroupId is not int g) return;
        if (ClearanceFor(unit, units) is { } c)
        {
            await dcs.ShowTextAsync(g, c.Text, 30, ct: ct);
            Transmit(c.Airfield, c.Coalition, c.Text);
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
        await dcs.ClearDrawingAsync(c.DrawingKey, ct);
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
