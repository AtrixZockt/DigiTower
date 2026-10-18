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
    internal int OffRouteSamples { get; set; }
    internal DateTime LastOffRouteWarning { get; set; } = DateTime.MinValue;
    internal Dictionary<string, DateTime> LastSpeedWarning { get; } = [];

    public int Key => Party.Key;
    public string Callsign => Party.Callsign;
    /// <summary>The unit whose position drives the clearance: the aircraft itself, or the flight lead.</summary>
    public string UnitName => Party.LeadUnit;
    public string DrawingKey => $"taxi-{Key}";
}

/// <summary>
/// Ground control: taxi clearances to the active runway's holding point and to parking (route computed
/// on the DCS taxi network, named from chart labels, drawn on the F10 map) and monitoring while taxiing.
/// A declared flight gets one clearance; the lead's position drives it.
/// </summary>
public sealed class GroundControl(IDcsCommands dcs, AirfieldCatalog airfields, FlightRegistry flights, Action<string>? log = null, Func<DateTime>? clock = null)
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
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);

    /// <summary>Clearances by party key (the lead's group id).</summary>
    public IReadOnlyDictionary<int, TaxiClearance> Clearances => _clearances;

    /// <summary>The clearance of the unit's party (its own or its flight's), if any.</summary>
    public TaxiClearance? ClearanceFor(UnitState unit, IReadOnlyDictionary<string, UnitState> units) =>
        _clearances.GetValueOrDefault(flights.PartyFor(unit, units).Key);

    /// <summary>Taxi to the holding point of the active runway.</summary>
    public async Task RequestTaxiAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (await OnGroundAtAirfieldAsync(unit, units, ct) is not { } who) return;
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
            await SayAsync(party, $"{party.Callsign}, {airfield.Facility} Ground, unable to find a taxi route from your position.", 15, ct);
            return;
        }

        var via = airfield.Layout.Via(departure.Route);
        string text = Phraseology.TaxiClearance(party.Callsign, airfield.Facility, departure.End, via, weather, lead.Type);
        await StartAsync(new TaxiClearance
        {
            Party = party,
            Coalition = lead.Coal,
            Airfield = airfield,
            Route = departure.Route,
            Text = text,
            Departure = departure,
        }, DepartureColor, $"HOLD SHORT {departure.End.Name}", ct);
    }

    /// <summary>Taxi to a free parking spot (after landing). A flight is sent to the lead's spot's area.</summary>
    public async Task RequestTaxiToParkingAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (await OnGroundAtAirfieldAsync(unit, units, ct) is not { } who) return;
        var (party, lead, airfield) = who;

        var others = units.Values.Where(u => !party.Units.Contains(u.Name) && !u.InAir).Select(u => new Vec2(u.X, u.Z));
        var parking = airfield.Layout.RouteToParking(new Vec2(lead.X, lead.Z), lead.IsHelicopter, others);
        if (parking is not { } found)
        {
            await SayAsync(party, $"{party.Callsign}, {airfield.Facility} Ground, no free parking found, hold position.", 15, ct);
            return;
        }
        var (spot, route) = found;

        string area = airfield.Layout.AreaNear(spot.Position) ?? "parking";
        var via = airfield.Layout.Via(route);
        string text = Phraseology.TaxiToParking(party.Callsign, airfield.Facility, area, via);
        await StartAsync(new TaxiClearance
        {
            Party = party,
            Coalition = lead.Coal,
            Airfield = airfield,
            Route = route,
            Text = text,
            Spot = spot,
        }, ParkingColor, area.ToUpperInvariant(), ct);
    }

    public async Task CancelTaxiAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (ClearanceFor(unit, units) is not { } c) return;
        _clearances.Remove(c.Key);
        await dcs.ClearDrawingAsync(c.DrawingKey, ct);
        await SayAsync(c.Party, $"{c.Callsign}, {c.Airfield.Facility} Ground, taxi clearance cancelled, hold position.", 15, ct);
    }

    /// <summary>Ends a clearance without a message (e.g. when Tower takes over).</summary>
    public async Task ReleaseAsync(int key, CancellationToken ct = default)
    {
        if (_clearances.Remove(key, out var c)) await dcs.ClearDrawingAsync(c.DrawingKey, ct);
    }

    public async Task SayAgainAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (ClearanceFor(unit, units) is { } c && unit.GroupId is int g)
            await dcs.ShowTextAsync(g, c.Text, 30, ct: ct);
    }

    /// <summary>Checks every taxiing flight against its clearance.</summary>
    public async Task OnUnitsAsync(IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        foreach (var c in _clearances.Values.ToList())
        {
            if (!units.TryGetValue(c.UnitName, out var unit) || unit.InAir)
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
            if (Polyline.Project(route, position).Distance > OffRouteDistance)
            {
                c.OffRouteSamples++;
                if (c.OffRouteSamples >= 3 && now - c.LastOffRouteWarning >= WarningInterval)
                {
                    c.LastOffRouteWarning = now;
                    await SayAsync(c.Party, $"{c.Callsign}, {c.Airfield.Facility} Ground, you are off your cleared taxi route. Check route and hold position if unsure.", 15, ct);
                }
            }
            else
            {
                c.OffRouteSamples = 0;
            }

            // Speed is every member's own business.
            foreach (var name in c.Party.Units)
            {
                if (!units.TryGetValue(name, out var member) || member.InAir || member.GroundSpeed <= TaxiSpeedLimit || member.GroupId is not int g) continue;
                if (now - c.LastSpeedWarning.GetValueOrDefault(name, DateTime.MinValue) < WarningInterval) continue;
                c.LastSpeedWarning[name] = now;
                await dcs.ShowTextAsync(g, $"{Phraseology.Callsign(member)}, {c.Airfield.Facility} Ground, reduce taxi speed.", 10, ct: ct);
            }
        }
    }

    public async Task OnEventAsync(DcsEventMessage e, CancellationToken ct = default)
    {
        if (e.Event is not ("takeoff" or "runway_takeoff" or "crash" or "dead" or "unit_lost" or "player_leave_unit")) return;
        var c = _clearances.Values.FirstOrDefault(x => x.UnitName == e.Unit);
        if (c is not null) await ReleaseAsync(c.Key, ct);
    }

    /// <summary>The mission ended: forget all clearances (their drawings went with the mission).</summary>
    public void Reset() => _clearances.Clear();

    private async Task ArriveAsync(TaxiClearance c, CancellationToken ct)
    {
        await dcs.ClearDrawingAsync(c.DrawingKey, ct);
        if (c.Departure is { } d)
        {
            c.Phase = TaxiPhase.HoldingShort;
            string tower = Phraseology.Frequency(c.Airfield.Layout.Labels?.Frequencies?.Tower) is { } f ? $" on {f}" : "";
            await SayAsync(c.Party, $"{c.Callsign}, {c.Airfield.Facility} Ground, hold short runway {d.End.Name}, contact Tower{tower} when ready for departure.", 20, ct);
        }
        else
        {
            _clearances.Remove(c.Key);
            await SayAsync(c.Party, $"{c.Callsign}, {c.Airfield.Facility} Ground, parking reached.", 10, ct);
        }
    }

    private async Task StartAsync(TaxiClearance clearance, double[] color, string label, CancellationToken ct)
    {
        _clearances[clearance.Key] = clearance;
        await SayAsync(clearance.Party, clearance.Text, 30, ct);
        var line = Polyline.Simplify(clearance.Route.Geometry, 3).Select(p => (p.X, p.Z));
        await dcs.DrawRouteAsync(clearance.DrawingKey, clearance.Coalition, line, color, label, ct);
        log?.Invoke($"{clearance.Callsign}: {clearance.Text} ({clearance.Route.Length:F0} m)");
    }

    private async Task SayAsync(Party party, string text, int seconds, CancellationToken ct)
    {
        foreach (var g in party.Groups) await dcs.ShowTextAsync(g, text, seconds, ct: ct);
    }

    /// <summary>The unit's party, its lead and the airfield if the lead is on the ground at an airfield we control; tells it otherwise.</summary>
    private async Task<(Party Party, UnitState Lead, GroundAirfield Airfield)?> OnGroundAtAirfieldAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct)
    {
        if (unit.GroupId is null) return null;
        var party = flights.PartyFor(unit, units);
        var lead = units.GetValueOrDefault(party.LeadUnit) ?? unit;
        if (lead.InAir)
        {
            await SayAsync(party, $"{party.Callsign}, you are airborne. Ground control is for aircraft on the ground.", 10, ct);
            return null;
        }
        var airfield = await airfields.NearestAsync(new Vec2(lead.X, lead.Z), ct: ct);
        if (airfield is null)
        {
            await SayAsync(party, $"{party.Callsign}, no ground control available at your position.", 10, ct);
            return null;
        }
        return (party, lead, airfield);
    }
}
