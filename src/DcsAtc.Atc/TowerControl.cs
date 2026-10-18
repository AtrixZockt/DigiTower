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
    /// <summary>Where the pilot wants to go after takeoff.</summary>
    public DepartureIntent Intent { get; init; }
    /// <summary>The climb-out given with the takeoff clearance.</summary>
    public DepartureInstruction? Departure { get; set; }
    /// <summary>Still held at the initial altitude behind departing traffic ("until advised").</summary>
    public bool UntilAdvised { get; set; }
    /// <summary>Flying closed traffic: stays with Tower, may touch and go.</summary>
    public bool InPattern { get; set; }
    /// <summary>The last "hold" / "continue" reason given, so it is not repeated every second.</summary>
    internal string? LastReason { get; set; }
    internal DateTime LastWarning { get; set; } = DateTime.MinValue;

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
/// Everything said to a pilot at an airfield is also transmitted on its Tower frequency when a radio is given.
/// </summary>
public sealed class TowerControl(IDcsCommands dcs, AirfieldCatalog airfields, GroundControl ground, FlightRegistry flights, Action<string>? log = null, Func<DateTime>? clock = null, IAtcRadio? radio = null)
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

    /// <summary>"Ready for departure" is taken this close to a runway (metres).</summary>
    public const double HoldingPointRange = 400;

    /// <summary>A departure is handed to Approach once this far from the runway or this high (metres).</summary>
    public const double HandoffDistance = 4000, HandoffHeight = 900;

    /// <summary>A departure held "until advised" is released at the latest this far out (metres).</summary>
    public const double ForcedRelease = 10 * NauticalMile;

    /// <summary>Tolerances before "check altitude" / "check heading" (feet, degrees), and how often.</summary>
    public const double AltitudeTolerance = 200, HeadingTolerance = 25;
    public static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(20);

    private const double Feet = 0.3048;

    private readonly Dictionary<int, TowerFlight> _flights = [];
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);

    /// <summary>Tower clients by party key (the lead's group id).</summary>
    public IReadOnlyDictionary<int, TowerFlight> Flights => _flights;

    /// <summary>"Ready for departure" at a holding point, with where the pilot wants to go after takeoff.</summary>
    public async Task ReadyForDepartureAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, DepartureIntent intent = DepartureIntent.OwnNavigation, CancellationToken ct = default)
    {
        if (unit.GroupId is null) return;
        var party = flights.PartyFor(unit, units);
        string callsign = party.Callsign;
        var lead = units.GetValueOrDefault(party.LeadUnit) ?? unit;
        if (lead.InAir)
        {
            await SayAsync(party, null, Phraseology.AlreadyAirborne(callsign), ct);
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
                await SayAsync(party, null, Phraseology.NoTower(callsign), ct);
                return;
            }
            // Not cleared by Ground: the nearest runway, in the direction of the wind.
            runway = airfield.Layout.Runways.MinBy(r => r.DistanceFrom(position));
            var weather = await dcs.GetAirbaseWeatherAsync(airfield.Airbase.Name, ct);
            var active = airfield.Layout.ActiveEnds(weather?.WindFrom ?? 0, weather?.WindSpeed ?? 0);
            end = active.FirstOrDefault(a => a.Runway == runway).End ?? runway?.EndA;
        }
        if (runway is null || end is null) return;

        if (runway.DistanceFrom(position) > HoldingPointRange)
        {
            await SayAsync(party, airfield, Phraseology.NotAtHoldingPoint(callsign, airfield.Facility, end), ct);
            return;
        }

        await ground.ReleaseAsync(party.Key, ct);
        var flight = Track(party, lead, airfield, runway, end, TowerPhase.WaitingForDeparture, intent);
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
            await SayAsync(party, null, Phraseology.OnTheGround(callsign), ct);
            return;
        }
        var position = new Vec2(lead.X, lead.Z);
        var airfield = await airfields.NearestAsync(position, InboundRange, ct);
        if (airfield is null)
        {
            await SayAsync(party, null, Phraseology.NoTowerWithin(callsign, InboundRange / NauticalMile), ct);
            return;
        }

        // The landing runway: of the ends in use, the one whose extended centerline the aircraft is closest to.
        var weather = await dcs.GetAirbaseWeatherAsync(airfield.Airbase.Name, ct);
        var (runway, end) = airfield.Layout.ActiveEnds(weather?.WindFrom ?? 0, weather?.WindSpeed ?? 0)
            .MinBy(e => e.Runway.FromCenterline(position));
        if (runway is null) return;

        var flight = Track(party, lead, airfield, runway, end, TowerPhase.Inbound);
        await SayAsync(flight, Phraseology.ReportFinal(callsign, airfield.Facility, end, weather, lead.Type), ct);
    }

    /// <summary>"Final": the pilot reports being on final; landing clearance follows as soon as the runway allows.</summary>
    public async Task ReportFinalAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (unit.GroupId is not int groupId) return;
        var party = flights.PartyFor(unit, units);
        if (!_flights.TryGetValue(party.Key, out var flight) || flight.Phase != TowerPhase.Inbound)
        {
            await dcs.ShowTextAsync(groupId, Phraseology.RequestLandingFirst(party.Callsign), 10, ct: ct);
            return;
        }
        flight.ReportedFinal = true;
        flight.LastReason = null;
        await EvaluateAsync(flight, units, ct, requested: true);
    }

    public async Task SayAgainAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (unit.GroupId is int g && _flights.TryGetValue(flights.PartyFor(unit, units).Key, out var flight) && flight.LastText != "")
        {
            await dcs.ShowTextAsync(g, flight.LastText, 20, ct: ct);
            Transmit(flight.Airfield, flight.Party.Coalition, flight.LastText);
        }
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

    private TowerFlight Track(Party party, UnitState lead, GroundAirfield airfield, Runway runway, RunwayEnd end, TowerPhase phase, DepartureIntent intent = DepartureIntent.OwnNavigation)
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
            Intent = intent,
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
                    f.Departure = PlanDeparture(f, units);
                    f.UntilAdvised = f.Departure.UntilAdvised;
                    Enter(f, TowerPhase.ClearedForTakeoff);
                    await SayAsync(f, Phraseology.ClearedForTakeoff(f.Callsign, f.End, weather, f.Departure), ct);
                }
                else if (f.Phase == TowerPhase.WaitingForDeparture && departingAhead && !LandingTrafficWithin(f, units, LandingClearanceRange))
                {
                    Enter(f, TowerPhase.LineUpAndWait);
                    await SayAsync(f, Phraseology.LineUpAndWait(f.Callsign, f.End), ct);
                }
                else if (f.Phase == TowerPhase.WaitingForDeparture && (requested || f.LastReason != reason))
                {
                    f.LastReason = reason;
                    await SayAsync(f, Phraseology.HoldShort(f.Callsign, f.Airfield.Facility, f.End, reason), ct);
                }
                break;
            }
            case TowerPhase.ClearedForTakeoff:
                if (unit.InAir && unit.Agl > 30)
                {
                    if (f.Departure?.Intent == DepartureIntent.ClosedTraffic)
                    {
                        // In the pattern: back to this runway, landing clearance on final.
                        f.InPattern = true;
                        f.ReportedFinal = false;
                        Enter(f, TowerPhase.Inbound);
                    }
                    else
                    {
                        Enter(f, TowerPhase.Departed);
                    }
                }
                break;

            case TowerPhase.Departed:
                await ClimbOutAsync(f, unit, units, ct);
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
                    await SayAsync(f, Phraseology.ContinueApproach(f.Callsign, reason), ct);
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
                    await SayAsync(f, Phraseology.GoAround(f.Callsign, occupied), ct);
                }
                break;
            }
            case TowerPhase.Landed:
                if (f.InPattern)
                {
                    // Closed traffic: airborne again is a touch and go; slow on the runway is a full stop.
                    if (unit.InAir)
                    {
                        f.ReportedFinal = false;
                        Enter(f, TowerPhase.Inbound);
                    }
                    else if (unit.GroundSpeed < 15)
                    {
                        f.InPattern = false;
                        await SayVacateAsync(f, ct);
                    }
                    break;
                }
                if (!f.Runway.IsOnRunway(position, 15)) _flights.Remove(f.Key);
                break;
        }
    }

    /// <summary>
    /// After takeoff: holds the departure to its restrictions ("check altitude", "check heading"), releases it once the
    /// departing traffic ahead is far enough away, and hands it to Approach when clear of the field and restrictions.
    /// </summary>
    private async Task ClimbOutAsync(TowerFlight f, UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct)
    {
        var d = f.Departure;
        var p = d?.Procedures ?? DepartureProcedures.Default;
        var position = new Vec2(unit.X, unit.Z);
        double fromRunway = f.Runway.DistanceFrom(position);
        bool stayingLow = d is { StayLow: true } && fromRunway < p.StayLowDistance * NauticalMile;

        if (d is not null && f.UntilAdvised)
        {
            bool clear = d.Following is not { } ahead || !units.TryGetValue(ahead, out var traffic) || !traffic.InAir
                         || new Vec2(traffic.X, traffic.Z).DistanceTo(position) >= p.Separation * NauticalMile;
            if ((clear && !stayingLow) || fromRunway > ForcedRelease)
            {
                f.UntilAdvised = false;
                f.LastWarning = DateTime.MinValue;
                await SayAsync(f, Phraseology.ClimbUnrestricted(f.Callsign, d), ct);
                return;
            }
        }

        var now = _now();
        if (d is not null && now - f.LastWarning >= WarningInterval)
        {
            string? warning = null;
            if (stayingLow && unit.Agl / Feet > p.StayLowAltitude + AltitudeTolerance)
                warning = Phraseology.CheckAltitudeStayLow(f.Callsign, p);
            else if (f.UntilAdvised && unit.Y / Feet > p.InitialAltitude + AltitudeTolerance)
                warning = Phraseology.CheckAltitudeMaintain(f.Callsign, p);
            else if ((stayingLow || f.UntilAdvised) && fromRunway > NauticalMile && HeadingOff(unit.Hdg, f.End.TrueHeading) > HeadingTolerance)
                warning = Phraseology.CheckHeading(f.Callsign);
            if (warning is not null)
            {
                f.LastWarning = now;
                await SayAsync(f, warning, ct);
                return;
            }
        }

        if (!f.UntilAdvised && !stayingLow && (fromRunway > HandoffDistance || unit.Agl > HandoffHeight))
        {
            _flights.Remove(f.Key);
            await SayAsync(f, Phraseology.ContactAfterDeparture(f.Callsign, f.Airfield.Layout.Labels?.Frequencies?.Approach, resumeOwnNavigation: d?.Heading is not null), ct);
        }
    }

    /// <summary>
    /// The climb-out for a departure about to be cleared: its intent, plus "fly runway heading, stay low" under pattern
    /// traffic and "maintain the initial altitude until advised" behind departing traffic.
    /// </summary>
    private DepartureInstruction PlanDeparture(TowerFlight f, IReadOnlyDictionary<string, UnitState> units)
    {
        var p = f.Airfield.Layout.Labels?.Departure ?? DepartureProcedures.Default;
        if (f.Intent == DepartureIntent.ClosedTraffic) return DepartureInstruction.Create(f.Intent, f.End, p);

        var airborne = units.Values.Where(u => !f.Party.Units.Contains(u.Name) && u.InAir).ToList();
        var far = f.Runway.Opposite(f.End).Threshold;
        // Departing traffic ahead: past the runway start, near the extended centerline, outbound and climbing (unlike an
        // overhead initial, which flies the same way level), not yet far away.
        var ahead = airborne
            .Where(u =>
            {
                var q = new Vec2(u.X, u.Z);
                return f.Runway.AlongFrom(f.End, q) > 0 && f.Runway.FromCenterline(q) < 2 * NauticalMile && HeadingOff(u.Hdg, f.End.TrueHeading) < 60
                       && u.Vy > 1.5 && u.Agl < 10000 * Feet && q.DistanceTo(far) < p.Separation * NauticalMile;
            })
            .MinBy(u => new Vec2(u.X, u.Z).DistanceTo(far));
        // Pattern traffic: around the field at pattern heights, not established on final (that already holds departures).
        bool pattern = airborne.Any(u => u != ahead
            && f.Runway.DistanceFrom(new Vec2(u.X, u.Z)) < p.PatternRadius * NauticalMile
            && u.Agl / Feet >= p.PatternFloor && u.Agl / Feet <= p.PatternTop
            && DistanceOnFinal(f.Runway, f.Runway.EndA, u) is null && DistanceOnFinal(f.Runway, f.Runway.EndB, u) is null);
        return DepartureInstruction.Create(f.Intent, f.End, p, stayLow: pattern, following: ahead?.Name);
    }

    private static double HeadingOff(double heading, double reference) => Math.Abs(((heading - reference) % 360 + 540) % 360 - 180);

    private async Task LandedAsync(TowerFlight f, CancellationToken ct)
    {
        Enter(f, TowerPhase.Landed);
        // Closed traffic may touch and go: vacate only once it has slowed down.
        if (!f.InPattern) await SayVacateAsync(f, ct);
    }

    private Task SayVacateAsync(TowerFlight f, CancellationToken ct) =>
        SayAsync(f, Phraseology.VacateContactGround(f.Callsign, f.Airfield.Facility, f.Airfield.Layout.Labels?.Frequencies?.Ground), ct);

    private void Enter(TowerFlight f, TowerPhase phase)
    {
        f.Phase = phase;
        f.LastReason = null;
    }

    private async Task SayAsync(TowerFlight f, string text, CancellationToken ct)
    {
        f.LastText = text;
        await SayAsync(f.Party, f.Airfield, text, ct);
        log?.Invoke($"{f.Callsign}: {text}");
    }

    /// <summary>Text to every member; on the radio too when it comes from an airfield's Tower.</summary>
    private async Task SayAsync(Party party, GroundAirfield? airfield, string text, CancellationToken ct)
    {
        foreach (var g in party.Groups) await dcs.ShowTextAsync(g, text, 20, ct: ct);
        Transmit(airfield, party.Coalition, text);
    }

    private void Transmit(GroundAirfield? airfield, int coalition, string text)
    {
        if (radio is not null && airfield?.Layout.Labels?.Frequencies?.Tower is double mhz)
            radio.Transmit(new RadioCall($"{airfield.Facility} Tower", mhz, coalition, text));
    }

    /// <summary>
    /// Why a departure must hold short (null: it may take off), and whether the only obstacle is an
    /// earlier departure already on the runway (then it may line up and wait).
    /// </summary>
    private (string? Reason, bool DepartingAhead) DepartureBlocker(TowerFlight f, IReadOnlyDictionary<string, UnitState> units)
    {
        if (LandingTrafficWithin(f, units, DepartureBlockingFinal)) return (Phraseology.TrafficOnFinal, false);
        if (_flights.Values.Any(o => o != f && o.Runway == f.Runway && o.Phase == TowerPhase.ClearedToLand)) return (Phraseology.LandingTraffic, false);

        bool departingAhead = false;
        foreach (var other in units.Values)
        {
            if (f.Party.Units.Contains(other.Name) || other.InAir || !f.Runway.IsOnRunway(new Vec2(other.X, other.Z))) continue;
            var o = _flights.Values.FirstOrDefault(x => x.Party.Units.Contains(other.Name));
            if (o is { Phase: TowerPhase.ClearedForTakeoff or TowerPhase.LineUpAndWait }) departingAhead = true;
            else return (Phraseology.RunwayOccupied, false);
        }
        if (departingAhead) return (Phraseology.TrafficDeparting, true);

        // An earlier departure cleared for this runway but not yet on it goes first.
        if (_flights.Values.Any(o => o != f && o.Runway == f.Runway && o.Phase is TowerPhase.ClearedForTakeoff or TowerPhase.LineUpAndWait
                                     && units.TryGetValue(o.UnitName, out var u) && !u.InAir))
            return (Phraseology.NumberTwoForDeparture, false);
        return (null, false);
    }

    /// <summary>Why a landing cannot be cleared yet (null: cleared to land).</summary>
    private string? LandingBlocker(TowerFlight f, IReadOnlyDictionary<string, UnitState> units)
    {
        if (RunwayOccupied(f, units) is { } occupied) return occupied;
        if (_flights.Values.Any(o => o != f && o.Runway == f.Runway && o.Phase is TowerPhase.LineUpAndWait or TowerPhase.ClearedForTakeoff
                                     && units.TryGetValue(o.UnitName, out var u) && !u.InAir))
            return Phraseology.TrafficDeparting;
        // Another aircraft cleared to land on this runway and still airborne goes first.
        double? mine = units.TryGetValue(f.UnitName, out var me) ? DistanceOnFinal(f.Runway, f.End, me) : null;
        foreach (var o in _flights.Values.Where(o => o != f && o.Runway == f.Runway && o.Phase == TowerPhase.ClearedToLand))
        {
            if (units.TryGetValue(o.UnitName, out var u) && u.InAir && (mine is null || DistanceOnFinal(f.Runway, o.End, u) is not double theirs || theirs < mine))
                return Phraseology.NumberTwoFollowTraffic;
        }
        return null;
    }

    /// <summary>Any other aircraft on the ground on the runway.</summary>
    private static string? RunwayOccupied(TowerFlight f, IReadOnlyDictionary<string, UnitState> units) =>
        units.Values.Any(u => !f.Party.Units.Contains(u.Name) && !u.InAir && f.Runway.IsOnRunway(new Vec2(u.X, u.Z))) ? Phraseology.RunwayOccupied : null;

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
