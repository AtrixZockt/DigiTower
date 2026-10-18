using DcsAtc.Airfields;
using DcsAtc.Airfields.Taxi;
using DcsAtc.Bridge;

namespace DcsAtc.Atc;

public enum TowerPhase
{
    /// <summary>Ready for departure at the holding point, not yet cleared.</summary>
    WaitingForDeparture,
    LineUpAndWait,
    ClearedForTakeoff,
    /// <summary>Airborne, still with Tower until clear of the field.</summary>
    Departed,
    /// <summary>Has asked to land, not yet cleared.</summary>
    Inbound,
    ClearedToLand,
    /// <summary>On the runway after landing, until it has vacated.</summary>
    Landed,
}

/// <summary>An aircraft or declared flight Tower is working: departing from or landing on one runway.</summary>
public sealed class TowerFlight
{
    public required Party Party { get; init; }
    public required string UnitType { get; init; }
    public required GroundAirfield Airfield { get; init; }
    public required Runway Runway { get; init; }
    public required RunwayEnd End { get; init; }
    public TowerPhase Phase { get; set; }
    /// <summary>When the flight entered its current phase (orders departures and arrivals).</summary>
    public DateTime Since { get; set; }
    public string LastText { get; set; } = "";
    public bool ReportedFinal { get; set; }
    /// <summary>The last "hold" / "continue" reason given, so it is not repeated every second.</summary>
    internal string? LastReason { get; set; }

    public int Key => Party.Key;
    public string Callsign => Party.Callsign;
    /// <summary>The unit whose position drives the clearance: the aircraft itself, or the flight lead.</summary>
    public string UnitName => Party.LeadUnit;
}

/// <summary>
/// Tower: departure clearances (line up and wait, cleared for takeoff), landing clearances (cleared to
/// land, go around) and the hand-offs to Approach and Ground. Every aircraft in the mission counts as
/// traffic, AI included: one aircraft on a runway at a time, departures wait for landing traffic.
/// Parallel runways are independent. A declared flight is one client: its own members are never traffic to it.
/// </summary>
public sealed class TowerControl(IDcsCommands dcs, AirfieldCatalog airfields, GroundControl ground, FlightRegistry flights, Action<string>? log = null, Func<DateTime>? clock = null)
{
    private const double NauticalMile = 1852;

    /// <summary>How far out an aircraft counts as on final (metres).</summary>
    public const double FinalLength = 10 * NauticalMile;

    /// <summary>Landing traffic closer than this keeps departures holding short (metres from the threshold).</summary>
    public const double DepartureBlockingFinal = 5 * NauticalMile;

    /// <summary>Landing clearance is given from this distance (metres from the threshold), or when final is reported.</summary>
    public const double LandingClearanceRange = 6 * NauticalMile;

    /// <summary>Within this distance an aircraft cleared to land is sent around if the runway becomes blocked.</summary>
    public const double GoAroundRange = 1 * NauticalMile;

    /// <summary>Tower takes landing requests up to this distance from the airfield (metres).</summary>
    public const double InboundRange = 30 * NauticalMile;

    /// <summary>A departure is handed to Approach once this far from the runway or this high (metres).</summary>
    public const double HandoffDistance = 4000, HandoffHeight = 900;

    private readonly Dictionary<int, TowerFlight> _flights = [];
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);

    /// <summary>Tower clients by party key (the lead's group id).</summary>
    public IReadOnlyDictionary<int, TowerFlight> Flights => _flights;

    /// <summary>"Ready for departure" at a holding point.</summary>
    public async Task ReadyForDepartureAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (unit.GroupId is null) return;
        var party = flights.PartyFor(unit, units);
        string callsign = party.Callsign;
        var lead = units.GetValueOrDefault(party.LeadUnit) ?? unit;
        if (lead.InAir)
        {
            await SayAsync(party, $"{callsign}, you are already airborne.", ct);
            return;
        }

        var position = new Vec2(lead.X, lead.Z);
        GroundAirfield? airfield;
        Runway? runway;
        RunwayEnd? end;
        if (ground.ClearanceFor(unit, units) is { Departure: { } departure } taxi)
        {
            (airfield, runway, end) = (taxi.Airfield, departure.Runway, departure.End);
        }
        else
        {
            airfield = await airfields.NearestAsync(position, ct: ct);
            if (airfield is null)
            {
                await SayAsync(party, $"{callsign}, no tower available at your position.", ct);
                return;
            }
            // Not cleared by Ground: the nearest runway, in the direction of the wind.
            runway = airfield.Layout.Runways.MinBy(r => r.DistanceFrom(position));
            var weather = await dcs.GetAirbaseWeatherAsync(airfield.Airbase.Name, ct);
            var active = airfield.Layout.ActiveEnds(weather?.WindFrom ?? 0, weather?.WindSpeed ?? 0);
            end = active.FirstOrDefault(a => a.Runway == runway).End ?? runway?.EndA;
        }
        if (runway is null || end is null) return;

        if (runway.DistanceFrom(position) > 400)
        {
            await SayAsync(party, $"{callsign}, {airfield.Facility} Tower, you are not at a holding point for runway {end.Name}. Request taxi from Ground.", ct);
            return;
        }

        await ground.ReleaseAsync(party.Key, ct);
        var flight = Track(party, lead, airfield, runway, end, TowerPhase.WaitingForDeparture);
        await EvaluateAsync(flight, units, ct, requested: true);
    }

    /// <summary>"Request landing": airborne within <see cref="InboundRange"/> of an airfield.</summary>
    public async Task RequestLandingAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (unit.GroupId is null) return;
        var party = flights.PartyFor(unit, units);
        string callsign = party.Callsign;
        var lead = units.GetValueOrDefault(party.LeadUnit) ?? unit;
        if (!lead.InAir)
        {
            await SayAsync(party, $"{callsign}, you are on the ground.", ct);
            return;
        }
        var position = new Vec2(lead.X, lead.Z);
        var airfield = await airfields.NearestAsync(position, InboundRange, ct);
        if (airfield is null)
        {
            await SayAsync(party, $"{callsign}, no tower within {InboundRange / NauticalMile:F0} miles.", ct);
            return;
        }

        // The landing runway: of the ends in use, the one whose extended centerline the aircraft is closest to.
        var weather = await dcs.GetAirbaseWeatherAsync(airfield.Airbase.Name, ct);
        var (runway, end) = airfield.Layout.ActiveEnds(weather?.WindFrom ?? 0, weather?.WindSpeed ?? 0)
            .MinBy(e => e.Runway.FromCenterline(position));
        if (runway is null) return;

        var flight = Track(party, lead, airfield, runway, end, TowerPhase.Inbound);
        string info = weather is null ? "" : $", {Phraseology.Wind(weather)}, {Phraseology.Altimeter(weather, lead.Type)}";
        await SayAsync(flight, $"{callsign}, {airfield.Facility} Tower, runway {end.Name}{info}, report final.", ct);
    }

    /// <summary>"Final": the pilot reports being on final; landing clearance follows as soon as the runway allows.</summary>
    public async Task ReportFinalAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (unit.GroupId is not int groupId) return;
        var party = flights.PartyFor(unit, units);
        if (!_flights.TryGetValue(party.Key, out var flight) || flight.Phase != TowerPhase.Inbound)
        {
            await dcs.ShowTextAsync(groupId, $"{party.Callsign}, request landing first.", 10, ct: ct);
            return;
        }
        flight.ReportedFinal = true;
        flight.LastReason = null;
        await EvaluateAsync(flight, units, ct, requested: true);
    }

    public async Task SayAgainAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (unit.GroupId is int g && _flights.TryGetValue(flights.PartyFor(unit, units).Key, out var flight) && flight.LastText != "")
            await dcs.ShowTextAsync(g, flight.LastText, 20, ct: ct);
    }

    public async Task OnUnitsAsync(IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        // Departures first in the order they became ready, so the earliest is cleared first.
        foreach (var flight in _flights.Values.OrderBy(f => f.Since).ToList())
            await EvaluateAsync(flight, units, ct, requested: false);
    }

    public Task OnEventAsync(DcsEventMessage e, CancellationToken ct = default)
    {
        if (e.Event is "crash" or "dead" or "unit_lost" or "player_leave_unit" or "pilot_dead" or "ejection")
        {
            var flight = _flights.Values.FirstOrDefault(f => f.UnitName == e.Unit);
            if (flight is not null) _flights.Remove(flight.Key);
        }
        return Task.CompletedTask;
    }

    public void Reset() => _flights.Clear();

    private TowerFlight Track(Party party, UnitState lead, GroundAirfield airfield, Runway runway, RunwayEnd end, TowerPhase phase)
    {
        var flight = new TowerFlight
        {
            Party = party,
            UnitType = lead.Type,
            Airfield = airfield,
            Runway = runway,
            End = end,
            Phase = phase,
            Since = _now(),
        };
        _flights[party.Key] = flight;
        return flight;
    }

    private async Task EvaluateAsync(TowerFlight f, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct, bool requested)
    {
        if (!units.TryGetValue(f.UnitName, out var unit))
        {
            _flights.Remove(f.Key);
            return;
        }
        var position = new Vec2(unit.X, unit.Z);

        switch (f.Phase)
        {
            case TowerPhase.WaitingForDeparture or TowerPhase.LineUpAndWait:
            {
                if (unit.InAir)
                {
                    Enter(f, TowerPhase.Departed);
                    break;
                }
                var (reason, departingAhead) = DepartureBlocker(f, units);
                if (reason is null)
                {
                    var weather = await dcs.GetAirbaseWeatherAsync(f.Airfield.Airbase.Name, ct);
                    Enter(f, TowerPhase.ClearedForTakeoff);
                    await SayAsync(f, Phraseology.ClearedForTakeoff(f.Callsign, f.End, weather), ct);
                }
                else if (f.Phase == TowerPhase.WaitingForDeparture && departingAhead && !LandingTrafficWithin(f, units, LandingClearanceRange))
                {
                    Enter(f, TowerPhase.LineUpAndWait);
                    await SayAsync(f, $"{f.Callsign}, runway {f.End.Name}, line up and wait.", ct);
                }
                else if (f.Phase == TowerPhase.WaitingForDeparture && (requested || f.LastReason != reason))
                {
                    f.LastReason = reason;
                    await SayAsync(f, $"{f.Callsign}, {f.Airfield.Facility} Tower, hold short runway {f.End.Name}, {reason}.", ct);
                }
                break;
            }
            case TowerPhase.ClearedForTakeoff:
                if (unit.InAir && unit.Agl > 30) Enter(f, TowerPhase.Departed);
                break;

            case TowerPhase.Departed:
                if (f.Runway.DistanceFrom(position) > HandoffDistance || unit.Agl > HandoffHeight)
                {
                    _flights.Remove(f.Key);
                    await SayAsync(f, Phraseology.ContactAfterDeparture(f.Callsign, f.Airfield.Layout.Labels?.Frequencies?.Approach), ct);
                }
                break;

            case TowerPhase.Inbound:
            {
                if (!unit.InAir && f.Runway.IsOnRunway(position))
                {
                    await LandedAsync(f, ct);
                    break;
                }
                double? toGo = DistanceOnFinal(f.Runway, f.End, unit);
                if (toGo is null || (toGo > LandingClearanceRange && !f.ReportedFinal)) break;
                string? reason = LandingBlocker(f, units);
                if (reason is null)
                {
                    var weather = await dcs.GetAirbaseWeatherAsync(f.Airfield.Airbase.Name, ct);
                    Enter(f, TowerPhase.ClearedToLand);
                    await SayAsync(f, Phraseology.ClearedToLand(f.Callsign, f.End, weather), ct);
                }
                else if (requested || f.LastReason != reason)
                {
                    f.LastReason = reason;
                    await SayAsync(f, $"{f.Callsign}, continue approach, {reason}.", ct);
                }
                break;
            }
            case TowerPhase.ClearedToLand:
            {
                if (!unit.InAir && f.Runway.IsOnRunway(position))
                {
                    await LandedAsync(f, ct);
                    break;
                }
                double? toGo = DistanceOnFinal(f.Runway, f.End, unit);
                if (toGo is not null && toGo < GoAroundRange && unit.Agl > 15 && RunwayOccupied(f, units) is { } occupied)
                {
                    Enter(f, TowerPhase.Inbound);
                    f.ReportedFinal = false;
                    await SayAsync(f, $"{f.Callsign}, go around, I say again, go around, {occupied}.", ct);
                }
                break;
            }
            case TowerPhase.Landed:
                if (!f.Runway.IsOnRunway(position, 15)) _flights.Remove(f.Key);
                break;
        }
    }

    private async Task LandedAsync(TowerFlight f, CancellationToken ct)
    {
        Enter(f, TowerPhase.Landed);
        string groundFreq = Phraseology.Frequency(f.Airfield.Layout.Labels?.Frequencies?.Ground) is { } g ? $" on {g}" : "";
        await SayAsync(f, $"{f.Callsign}, welcome to {f.Airfield.Facility}, vacate runway when able, contact Ground{groundFreq}.", ct);
    }

    private void Enter(TowerFlight f, TowerPhase phase)
    {
        f.Phase = phase;
        f.LastReason = null;
    }

    private async Task SayAsync(TowerFlight f, string text, CancellationToken ct)
    {
        f.LastText = text;
        await SayAsync(f.Party, text, ct);
        log?.Invoke($"{f.Callsign}: {text}");
    }

    private async Task SayAsync(Party party, string text, CancellationToken ct)
    {
        foreach (var g in party.Groups) await dcs.ShowTextAsync(g, text, 20, ct: ct);
    }

    /// <summary>
    /// Why a departure must hold short (null: it may take off), and whether the only obstacle is an
    /// earlier departure already on the runway (then it may line up and wait).
    /// </summary>
    private (string? Reason, bool DepartingAhead) DepartureBlocker(TowerFlight f, IReadOnlyDictionary<string, UnitState> units)
    {
        if (LandingTrafficWithin(f, units, DepartureBlockingFinal)) return ("traffic on final", false);
        if (_flights.Values.Any(o => o != f && o.Runway == f.Runway && o.Phase == TowerPhase.ClearedToLand)) return ("landing traffic", false);

        bool departingAhead = false;
        foreach (var other in units.Values)
        {
            if (f.Party.Units.Contains(other.Name) || other.InAir || !f.Runway.IsOnRunway(new Vec2(other.X, other.Z))) continue;
            var o = _flights.Values.FirstOrDefault(x => x.Party.Units.Contains(other.Name));
            if (o is { Phase: TowerPhase.ClearedForTakeoff or TowerPhase.LineUpAndWait }) departingAhead = true;
            else return ("runway occupied", false);
        }
        if (departingAhead) return ("traffic departing", true);

        // An earlier departure cleared for this runway but not yet on it goes first.
        if (_flights.Values.Any(o => o != f && o.Runway == f.Runway && o.Phase is TowerPhase.ClearedForTakeoff or TowerPhase.LineUpAndWait
                                     && units.TryGetValue(o.UnitName, out var u) && !u.InAir))
            return ("number two for departure", false);
        return (null, false);
    }

    /// <summary>Why a landing cannot be cleared yet (null: cleared to land).</summary>
    private string? LandingBlocker(TowerFlight f, IReadOnlyDictionary<string, UnitState> units)
    {
        if (RunwayOccupied(f, units) is { } occupied) return occupied;
        if (_flights.Values.Any(o => o != f && o.Runway == f.Runway && o.Phase is TowerPhase.LineUpAndWait or TowerPhase.ClearedForTakeoff
                                     && units.TryGetValue(o.UnitName, out var u) && !u.InAir))
            return "traffic departing";
        // Another aircraft cleared to land on this runway and still airborne goes first.
        double? mine = units.TryGetValue(f.UnitName, out var me) ? DistanceOnFinal(f.Runway, f.End, me) : null;
        foreach (var o in _flights.Values.Where(o => o != f && o.Runway == f.Runway && o.Phase == TowerPhase.ClearedToLand))
        {
            if (units.TryGetValue(o.UnitName, out var u) && u.InAir && (mine is null || DistanceOnFinal(f.Runway, o.End, u) is not double theirs || theirs < mine))
                return "number two, follow the traffic ahead";
        }
        return null;
    }

    /// <summary>Any other aircraft on the ground on the runway.</summary>
    private static string? RunwayOccupied(TowerFlight f, IReadOnlyDictionary<string, UnitState> units) =>
        units.Values.Any(u => !f.Party.Units.Contains(u.Name) && !u.InAir && f.Runway.IsOnRunway(new Vec2(u.X, u.Z))) ? "runway occupied" : null;

    /// <summary>Any other aircraft on final for either end of the runway, closer than <paramref name="range"/>.</summary>
    private static bool LandingTrafficWithin(TowerFlight f, IReadOnlyDictionary<string, UnitState> units, double range) =>
        units.Values.Any(u => !f.Party.Units.Contains(u.Name) && u.InAir && u.Agl < 1000 &&
                              new[] { f.Runway.EndA, f.Runway.EndB }.Any(e => DistanceOnFinal(f.Runway, e, u) is double d && d <= range));

    /// <summary>
    /// If the aircraft is on final for <paramref name="end"/> (airborne, within <see cref="FinalLength"/>, inside a
    /// 10° cone around the extended centerline and heading roughly down the runway): its distance to the threshold.
    /// </summary>
    public static double? DistanceOnFinal(Runway runway, RunwayEnd end, UnitState unit)
    {
        if (!unit.InAir) return null;
        var p = new Vec2(unit.X, unit.Z);
        double along = runway.AlongFrom(end, p);
        if (along > 300 || along < -FinalLength) return null;
        double distance = Math.Max(0, -along);
        if (runway.FromCenterline(p) > Math.Max(200, distance * Math.Tan(10 * Math.PI / 180))) return null;
        double headingOff = Math.Abs(((unit.Hdg - end.TrueHeading) % 360 + 540) % 360 - 180);
        return headingOff <= 35 ? distance : null;
    }
}
