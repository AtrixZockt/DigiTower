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
    /// <summary>Arriving for the overhead break: cleared to land on the downwind.</summary>
    public bool Overhead { get; set; }
    /// <summary>Handed over by Approach and not yet checked in: Tower's first call names itself.</summary>
    public bool AwaitingCheckIn { get; set; }
    /// <summary>A helicopter: hovering below 100 ft is not departing.</summary>
    public bool Helicopter { get; init; }
    /// <summary>A helicopter departing from where it stands; <see cref="Runway"/> is the nearest runway in use.</summary>
    public bool FromPresentPosition { get; init; }
    /// <summary>Which way a departure from present position goes (degrees true).</summary>
    public double Bearing { get; init; }
    /// <summary>A helicopter landing at this parking spot, not on the runway.</summary>
    public ParkingSpot? Spot { get; init; }
    /// <summary>Takes off from or lands on <see cref="Runway"/> (helicopters at a spot or leaving from where they stand do not).</summary>
    public bool UsesRunway => Spot is null && !FromPresentPosition;
    public string SpotDrawingKey => $"heli-{Key}";
    /// <summary>The members that take off together (on the ground at the runway when cleared): departed once all are airborne.</summary>
    internal IReadOnlyCollection<string> Departing { get; set; } = [];
    internal DateTime ClearedAt { get; set; }
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

    /// <summary>Closed traffic further than this from the runway and flying away has left the pattern (metres).</summary>
    public const double PatternLimit = 5 * NauticalMile;

    /// <summary>"Initial" is taken within this distance of the airfield (metres).</summary>
    public const double InitialRange = 15 * NauticalMile;

    /// <summary>A departure is handed to Approach once this far from the runway or this high (metres).</summary>
    public const double HandoffDistance = 4000, HandoffHeight = 900;

    /// <summary>A departure held "until advised" is released at the latest this far out (metres).</summary>
    public const double ForcedRelease = 10 * NauticalMile;

    /// <summary>Tolerances before "check altitude" / "check heading" (feet, degrees), and how often.</summary>
    public const double AltitudeTolerance = 200, HeadingTolerance = 25;
    public static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(20);

    private const double Feet = 0.3048;
    private static readonly double[] SpotColor = [0, 0.9, 0.2, 1];

    private readonly Dictionary<int, TowerFlight> _flights = [];
    private readonly SpotMarks _marks = new(dcs, clock);

    /// <summary>The drawing keys of the wingmen's landing spots marked now.</summary>
    public IReadOnlyCollection<string> SpotMarkKeys => _marks.Keys;
    private readonly Dictionary<int, (DateTime At, GroundAirfield Airfield)> _handoffs = [];
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);

    /// <summary>Departures handed to Approach (party key → when, from where), for their check-in.</summary>
    public IReadOnlyDictionary<int, (DateTime At, GroundAirfield Airfield)> Handoffs => _handoffs;

    /// <summary>Whether the party was handed to Approach within <paramref name="within"/>.</summary>
    public bool HandedOffRecently(int key, TimeSpan within) => _handoffs.TryGetValue(key, out var h) && _now() - h.At <= within;

    /// <summary>The departure has checked in with Approach.</summary>
    public void HandoffTaken(int key) => _handoffs.Remove(key);

    /// <summary>
    /// Approach hands an arrival over ("contact Tower"): Tower expects it for this airfield and runway, takes its check-in
    /// (or, for the overhead, its "initial"), and clears it to land on final, calling it first if it has not checked in.
    /// </summary>
    public void ExpectArrival(Party party, string unitType, GroundAirfield airfield, Runway runway, RunwayEnd end, bool overhead)
    {
        _flights[party.Key] = new TowerFlight
        {
            Party = party,
            UnitType = unitType,
            Airfield = airfield,
            Runway = runway,
            End = end,
            Phase = TowerPhase.Inbound,
            Since = _now(),
            Overhead = overhead,
            ReportedFinal = !overhead,
            AwaitingCheckIn = true,
        };
    }

    /// <summary>Tower clients by party key (the lead's group id).</summary>
    public IReadOnlyDictionary<int, TowerFlight> Flights => _flights;

    /// <summary>"Ready for departure" at a holding point, with where the pilot wants to go after takeoff.</summary>
    public async Task ReadyForDepartureAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, DepartureIntent intent = DepartureIntent.OwnNavigation,
        GroundAirfield? at = null, CancellationToken ct = default)
    {
        if (unit.GroupId is null) return;
        var party = flights.PartyFor(unit, units);
        string callsign = party.Callsign;
        var lead = units.GetValueOrDefault(party.LeadUnit) ?? unit;
        if (!Helicopters.OnField(lead))
        {
            await SayAsync(party, null, Phraseology.AlreadyAirborne(callsign), ct);
            return;
        }

        var position = new Vec2(lead.X, lead.Z);
        GroundAirfield? airfield;
        Runway? runway;
        RunwayEnd? end;
        if (ground.ClearanceFor(unit, units) is { Departure: { } departure } taxi && (at is null || taxi.Airfield.Airbase.Id == at.Airbase.Id))
        {
            (airfield, runway, end) = (taxi.Airfield, departure.Runway, departure.End);
        }
        else
        {
            airfield = at ?? await airfields.AtAsync(position, ct);
            if (airfield is null)
            {
                await SayAsync(party, null, Phraseology.NoTower(callsign), ct);
                return;
            }
            // A helicopter departs from where it stands, unless it asked Ground for the runway (31 of Al Dhafra's spots are
            // within 400 m of a runway, so nearness says nothing).
            if (lead.IsHelicopter)
            {
                await DepartFromPresentPositionAsync(party, lead, airfield, intent, units, ct);
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
        var flight = Track(party, lead, airfield, runway, end, TowerPhase.WaitingForDeparture, intent, lead.IsHelicopter);
        await EvaluateAsync(flight, units, ct, requested: true);
    }

    /// <summary>
    /// A helicopter departing from where it stands (FAA JO 7110.65 §3-11-2): cleared at once, with a turn for its
    /// direction, unless its way out crosses a runway in use or another helicopter is cleared to take off or land within
    /// 200 ft; then it holds position until it may go.
    /// </summary>
    private async Task DepartFromPresentPositionAsync(Party party, UnitState lead, GroundAirfield airfield, DepartureIntent intent,
        IReadOnlyDictionary<string, UnitState> units, CancellationToken ct)
    {
        var position = new Vec2(lead.X, lead.Z);
        var weather = await dcs.GetAirbaseWeatherAsync(airfield.Airbase.Name, ct);
        var (runway, end) = airfield.Layout.ActiveEnds(weather?.WindFrom ?? 0, weather?.WindSpeed ?? 0).MinBy(e => e.Runway.DistanceFrom(position));
        if (runway is null) return;
        await ground.ReleaseAsync(party.Key, ct);
        var direction = intent == DepartureIntent.ClosedTraffic ? DepartureIntent.OwnNavigation : intent;
        var flight = new TowerFlight
        {
            Party = party,
            UnitType = lead.Type,
            Airfield = airfield,
            Runway = runway,
            End = end,
            Phase = TowerPhase.WaitingForDeparture,
            Since = _now(),
            Intent = direction,
            Helicopter = true,
            FromPresentPosition = true,
            Bearing = Helicopters.Bearing(direction) ?? lead.Hdg,
        };
        _flights[party.Key] = flight;
        await EvaluateAsync(flight, units, ct, requested: true);
    }

    /// <summary>
    /// A helicopter arriving: a free spot for it (helicopter-only first, then open parking; nearest first), marked on the
    /// F10 map, and cleared straight in (FAA JO 7110.65 §3-11-6). False when no spot is free: it lands on the runway.
    /// </summary>
    private async Task<bool> LandAtSpotAsync(Party party, UnitState lead, GroundAirfield airfield, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct)
    {
        var position = new Vec2(lead.X, lead.Z);
        var standing = units.Values.Where(u => !party.Units.Contains(u.Name) && Helicopters.OnField(u)).Select(u => new Vec2(u.X, u.Z)).ToList();
        var assigned = _flights.Values.Where(o => o.Spot is not null && o.Key != party.Key).Select(o => o.Spot!.Position).Concat(_marks.TakenExcept($"heli-{party.Key}-")).ToList();
        var spot = airfield.Layout.Parking
            .Where(p => Helicopters.SpotPreference(p) is not null
                        && !standing.Any(q => q.DistanceTo(p.Position) < AtcMenu.ParkedDistance)
                        && !assigned.Any(q => q.DistanceTo(p.Position) < Helicopters.Separation))
            .OrderBy(p => Helicopters.SpotPreference(p)).ThenBy(p => p.Position.DistanceTo(position))
            .FirstOrDefault();
        if (spot is null) return false;
        var weather = await dcs.GetAirbaseWeatherAsync(airfield.Airbase.Name, ct);
        var (runway, end) = airfield.Layout.ActiveEnds(weather?.WindFrom ?? 0, weather?.WindSpeed ?? 0).MinBy(e => e.Runway.DistanceFrom(spot.Position));
        if (runway is null) return false;
        // Hovering over the field with an air-taxi clearance: Tower takes over.
        await ground.ReleaseAsync(party.Key, ct);
        var flight = new TowerFlight
        {
            Party = party,
            UnitType = lead.Type,
            Airfield = airfield,
            Runway = runway,
            End = end,
            Phase = TowerPhase.ClearedToLand,
            Since = _now(),
            Helicopter = true,
            Spot = spot,
        };
        _flights[party.Key] = flight;
        // A square around the spot, with its name (and in a flight the lead's number).
        var c = spot.Position;
        Vec2[] square = [c + new Vec2(-15, -15), c + new Vec2(-15, 15), c + new Vec2(15, 15), c + new Vec2(15, -15), c + new Vec2(-15, -15)];
        string leadLabel = party.IsFlight ? $"LANDING SPOT {SpotMarks.Label(Phraseology.Callsign(lead))}" : "LANDING SPOT";
        await dcs.DrawRouteAsync(flight.SpotDrawingKey, lead.Coal, square.Select(p => (p.X, p.Z)), SpotColor, leadLabel, ct);
        // The wingmen: the free helicopter spots nearest the lead's, each marked with its number until it is down on it.
        if (party.IsFlight)
        {
            await _marks.RemoveAsync($"heli-{party.Key}-", ct);
            var taken = new List<Vec2>(assigned) { spot.Position };
            foreach (var member in SpotMarks.Members(party, units).Where(m => m.Name != lead.Name && m.InAir))
            {
                var own = airfield.Layout.Parking
                    .Where(p => Helicopters.SpotPreference(p) is not null && p.Position.DistanceTo(spot.Position) <= MemberSpotRange
                                && !standing.Any(q => q.DistanceTo(p.Position) < AtcMenu.ParkedDistance) && !taken.Any(q => q.DistanceTo(p.Position) < SpotMarks.Reached))
                    .OrderBy(p => Helicopters.SpotPreference(p)).ThenBy(p => p.Position.DistanceTo(spot.Position))
                    .FirstOrDefault();
                if (own is null) continue;
                taken.Add(own.Position);
                string label = SpotMarks.Label(Phraseology.Callsign(member));
                await _marks.AddAsync($"heli-{party.Key}-{label}", member.Name, lead.Coal, own.Position, 15, SpotColor, $"LANDING SPOT {label}", ct);
            }
        }
        await SayAsync(flight, Phraseology.LandAtSpot(party.Callsign, $"{airfield.Facility} Tower", airfield.Layout.AreaNear(spot.Position), weather), ct);
        return true;
    }

    /// <summary>"Request landing": airborne within <see cref="InboundRange"/> of an airfield.</summary>
    public async Task RequestLandingAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, GroundAirfield? at = null, CancellationToken ct = default)
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
        if (_flights.TryGetValue(party.Key, out var expected) && expected.Phase is TowerPhase.Inbound or TowerPhase.ClearedToLand
            && (at is null || expected.Airfield.Airbase.Id == at.Airbase.Id))
        {
            // Handed over by Approach (or asking again): this airfield and runway, whatever else is nearer.
            await CheckInAsync(expected, lead, units, ct);
            return;
        }
        var airfield = at is null ? await ArrivalAirfieldAsync(lead, InboundRange, ct) : Within(at, position, InboundRange);
        if (airfield is null)
        {
            await SayAsync(party, null, Phraseology.NoTowerWithin(callsign, InboundRange / NauticalMile), ct);
            return;
        }
        // A helicopter lands at a spot of its own, if one is free.
        if (lead.IsHelicopter && await LandAtSpotAsync(party, lead, airfield, units, ct)) return;

        // The landing runway: of the ends in use, the one whose extended centerline the aircraft is closest to.
        var weather = await dcs.GetAirbaseWeatherAsync(airfield.Airbase.Name, ct);
        var (runway, end) = airfield.Layout.ActiveEnds(weather?.WindFrom ?? 0, weather?.WindSpeed ?? 0)
            .MinBy(e => e.Runway.FromCenterline(position));
        if (runway is null) return;

        var flight = Track(party, lead, airfield, runway, end, TowerPhase.Inbound, helicopter: lead.IsHelicopter);
        if (DistanceOnFinal(runway, end, lead) is not null)
        {
            // Already on final (e.g. from Approach): continue; the landing clearance follows as soon as the runway allows.
            flight.ReportedFinal = true;
            await SayAsync(flight, Phraseology.ContinueOnFinal(callsign, airfield.Facility, end, weather), ct);
            await EvaluateAsync(flight, units, ct, requested: false);
            return;
        }
        await SayAsync(flight, Phraseology.ReportFinal(callsign, airfield.Facility, end, weather, lead.Type), ct);
    }

    /// <summary>"Initial" for the overhead: break approved; cleared to land on the downwind (or on final) as soon as the runway allows.</summary>
    public async Task ReportInitialAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, GroundAirfield? at = null, CancellationToken ct = default)
    {
        if (unit.GroupId is null) return;
        var party = flights.PartyFor(unit, units);
        var lead = units.GetValueOrDefault(party.LeadUnit) ?? unit;
        if (!lead.InAir)
        {
            await SayAsync(party, null, Phraseology.OnTheGround(party.Callsign), ct);
            return;
        }
        var position = new Vec2(lead.X, lead.Z);
        if (_flights.TryGetValue(party.Key, out var expected) && expected.Phase == TowerPhase.Inbound && (at is null || expected.Airfield.Airbase.Id == at.Airbase.Id))
        {
            // Sent to the initial point by Approach: that airfield and runway.
            expected.Overhead = true;
            expected.ReportedFinal = false;
            expected.AwaitingCheckIn = false;
            var w = await dcs.GetAirbaseWeatherAsync(expected.Airfield.Airbase.Name, ct);
            await SayAsync(expected, Phraseology.BreakApproved(party.Callsign, expected.Airfield.Facility, expected.End, w), ct);
            return;
        }
        var airfield = at is null ? await ArrivalAirfieldAsync(lead, InitialRange, ct) : Within(at, position, InitialRange);
        if (airfield is null)
        {
            await SayAsync(party, null, Phraseology.NoTowerWithin(party.Callsign, InitialRange / NauticalMile), ct);
            return;
        }
        var weather = await dcs.GetAirbaseWeatherAsync(airfield.Airbase.Name, ct);
        var (runway, end) = airfield.Layout.ActiveEnds(weather?.WindFrom ?? 0, weather?.WindSpeed ?? 0)
            .MinBy(e => e.Runway.FromCenterline(position));
        if (runway is null) return;
        var flight = Track(party, lead, airfield, runway, end, TowerPhase.Inbound);
        flight.Overhead = true;
        await SayAsync(flight, Phraseology.BreakApproved(party.Callsign, airfield.Facility, end, weather), ct);
    }

    /// <summary>
    /// An arrival handed over by Approach checks in: one answer, the landing clearance if the runway allows it now,
    /// otherwise "continue approach" (the clearance follows); a repeat if it is already cleared.
    /// </summary>
    private async Task CheckInAsync(TowerFlight f, UnitState lead, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct)
    {
        if (f.Phase == TowerPhase.ClearedToLand)
        {
            f.AwaitingCheckIn = false;
            await SayAsync(f, f.LastText, ct);
            return;
        }
        var weather = await dcs.GetAirbaseWeatherAsync(f.Airfield.Airbase.Name, ct);
        f.ReportedFinal = true;
        f.AwaitingCheckIn = false;
        string? reason = LandingBlocker(f, units);
        if (reason is null && DistanceOnFinal(f.Runway, f.End, lead) is not null)
        {
            Enter(f, TowerPhase.ClearedToLand);
            await SayAsync(f, Phraseology.ClearedToLandFirstCall(f.Callsign, f.Airfield.Facility, f.End, weather), ct);
            return;
        }
        // The reason is not repeated on the next update; the clearance follows when the runway allows.
        f.LastReason = reason;
        await SayAsync(f, Phraseology.ContinueOnFinal(f.Callsign, f.Airfield.Facility, f.End, weather), ct);
    }

    private static GroundAirfield? Within(GroundAirfield airfield, Vec2 position, double range) =>
        new Vec2(airfield.Airbase.X, airfield.Airbase.Z).DistanceTo(position) <= range ? airfield : null;

    /// <summary>
    /// The airfield an arrival means: the one it is lined up on final for, else the nearest with our chart data, else
    /// the nearest, within <paramref name="range"/> metres.
    /// </summary>
    private async Task<GroundAirfield?> ArrivalAirfieldAsync(UnitState lead, double range, CancellationToken ct)
    {
        var position = new Vec2(lead.X, lead.Z);
        var candidates = new List<GroundAirfield>();
        foreach (var airbase in airfields.Airbases.Where(a => a.Category == 0 && new Vec2(a.X, a.Z).DistanceTo(position) <= range)
                     .OrderBy(a => new Vec2(a.X, a.Z).DistanceTo(position)))
        {
            if (await airfields.GetAsync(airbase, ct) is { } airfield) candidates.Add(airfield);
        }
        return candidates.FirstOrDefault(a => a.Layout.Runways.Any(r => DistanceOnFinal(r, r.EndA, lead) is not null || DistanceOnFinal(r, r.EndB, lead) is not null))
               ?? candidates.FirstOrDefault(a => a.Layout.Labels is not null)
               ?? candidates.FirstOrDefault();
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
        await _marks.OnUnitsAsync(units, ct);
        // Departures first in the order they became ready, so the earliest is cleared first.
        foreach (var flight in _flights.Values.OrderBy(f => f.Since).ToList())
            await EvaluateAsync(flight, units, ct, requested: false);
    }

    public async Task OnEventAsync(DcsEventMessage e, CancellationToken ct = default)
    {
        if (e.Event is "crash" or "dead" or "unit_lost" or "player_leave_unit" or "pilot_dead" or "ejection")
        {
            var flight = _flights.Values.FirstOrDefault(f => f.UnitName == e.Unit);
            if (flight is not null) await RemoveAsync(flight, ct);
        }
    }

    /// <summary>Tower is done with a flight (and its landing spot's drawing).</summary>
    private async Task RemoveAsync(TowerFlight f, CancellationToken ct)
    {
        _flights.Remove(f.Key);
        if (f.Spot is not null) await dcs.ClearDrawingAsync(f.SpotDrawingKey, ct);
    }

    public void Reset()
    {
        _flights.Clear();
        _handoffs.Clear();
        _marks.Reset();
    }

    private TowerFlight Track(Party party, UnitState lead, GroundAirfield airfield, Runway runway, RunwayEnd end, TowerPhase phase,
        DepartureIntent intent = DepartureIntent.OwnNavigation, bool helicopter = false)
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
            Helicopter = helicopter,
        };
        _flights[party.Key] = flight;
        return flight;
    }

    /// <summary>A helicopter wingman's landing spot is at most this far from the lead's (metres): it flies there, nearest first.</summary>
    public const double MemberSpotRange = 3000;

    /// <summary>A flight's members that take off with the clearance: on the ground at the runway (from present position: near the lead).</summary>
    public const double DepartingRange = 600;

    /// <summary>Waiting for the last member to get airborne at most this long after the clearance.</summary>
    public static readonly TimeSpan DepartingTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Notes who takes off with the clearance just given: the flight is departed once all of them are airborne.</summary>
    private void TakingOff(TowerFlight f, UnitState lead, IReadOnlyDictionary<string, UnitState> units)
    {
        var at = new Vec2(lead.X, lead.Z);
        f.ClearedAt = _now();
        f.Departing = f.Party.Units.Where(n => units.TryGetValue(n, out var u) && (f.Helicopter ? Helicopters.OnField(u) : !u.InAir)
                && (f.FromPresentPosition ? new Vec2(u.X, u.Z).DistanceTo(at) < DepartingRange / 2 : f.Runway.DistanceFrom(new Vec2(u.X, u.Z)) < DepartingRange))
            .ToList();
    }

    /// <summary>Every member that took off with the flight is airborne (or gone); after <see cref="DepartingTimeout"/> the lead alone counts.</summary>
    private bool AllAirborne(TowerFlight f, IReadOnlyDictionary<string, UnitState> units) =>
        _now() - f.ClearedAt > DepartingTimeout
        || f.Departing.All(n => !units.TryGetValue(n, out var u) || (u.InAir && u.Agl > 30));

    /// <summary>Off the ground for a departure: a helicopter hovering below 100 ft is not.</summary>
    private static bool Airborne(TowerFlight f, UnitState unit) => f.Helicopter ? unit.InAir && unit.Agl > Helicopters.AirTaxiHeight : unit.InAir;

    private async Task EvaluateAsync(TowerFlight f, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct, bool requested)
    {
        if (!units.TryGetValue(f.UnitName, out var unit))
        {
            await RemoveAsync(f, ct);
            return;
        }
        var position = new Vec2(unit.X, unit.Z);

        switch (f.Phase)
        {
            case TowerPhase.WaitingForDeparture or TowerPhase.LineUpAndWait:
            {
                if (Airborne(f, unit))
                {
                    Enter(f, TowerPhase.Departed);
                    break;
                }
                if (f.FromPresentPosition)
                {
                    await HelicopterDepartureAsync(f, unit, units, ct, requested);
                    break;
                }
                var (reason, departingAhead) = DepartureBlocker(f, units);
                if (reason is null)
                {
                    var weather = await dcs.GetAirbaseWeatherAsync(f.Airfield.Airbase.Name, ct);
                    f.Departure = PlanDeparture(f, units);
                    f.UntilAdvised = f.Departure.UntilAdvised;
                    Enter(f, TowerPhase.ClearedForTakeoff);
                    TakingOff(f, unit, units);
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
                if (unit.InAir && unit.Agl > 30 && AllAirborne(f, units))
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
                if (f.InPattern && LeavingPattern(f.Runway, unit))
                {
                    // Pattern work over: on to Approach like a departure.
                    _flights.Remove(f.Key);
                    _handoffs[f.Key] = (_now(), f.Airfield);
                    await SayAsync(f, Phraseology.LeavingPattern(f.Callsign, f.Airfield.Frequencies.ContactFor("Approach")), ct);
                    break;
                }
                if (!unit.InAir && f.Runway.IsOnRunway(position))
                {
                    await LandedAsync(f, ct);
                    break;
                }
                double? toGo = DistanceOnFinal(f.Runway, f.End, unit);
                // From the break or closed traffic the landing clearance comes on the downwind.
                bool onDownwind = (f.Overhead || f.InPattern) && OnDownwind(f.Runway, f.End, unit);
                if (!onDownwind && (toGo is null || (toGo > LandingClearanceRange && !f.ReportedFinal))) break;
                string? reason = LandingBlocker(f, units);
                if (reason is null)
                {
                    var weather = await dcs.GetAirbaseWeatherAsync(f.Airfield.Airbase.Name, ct);
                    Enter(f, TowerPhase.ClearedToLand);
                    // An arrival from Approach that has not called yet hears who is calling.
                    await SayAsync(f, f.AwaitingCheckIn
                        ? Phraseology.ClearedToLandFirstCall(f.Callsign, f.Airfield.Facility, f.End, weather)
                        : Phraseology.ClearedToLand(f.Callsign, f.End, weather), ct);
                    f.AwaitingCheckIn = false;
                }
                else if (requested || f.LastReason != reason)
                {
                    f.LastReason = reason;
                    await SayAsync(f, Phraseology.ContinueApproach(f.Callsign, reason), ct);
                }
                break;
            }
            case TowerPhase.ClearedToLand when f.Spot is not null:
                // Down (on its spot or where it chose), or gone: done.
                if (!unit.InAir || new Vec2(f.Airfield.Airbase.X, f.Airfield.Airbase.Z).DistanceTo(position) > InboundRange) await RemoveAsync(f, ct);
                break;

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

    /// <summary>A helicopter waiting to depart from where it stands: cleared as soon as nothing is in its way, held otherwise.</summary>
    private async Task HelicopterDepartureAsync(TowerFlight f, UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct, bool requested)
    {
        string? reason = HelicopterDepartureBlocker(f, unit, units);
        if (reason is null)
        {
            var weather = await dcs.GetAirbaseWeatherAsync(f.Airfield.Airbase.Name, ct);
            double? bearing = Helicopters.Bearing(f.Intent);
            string? direction = bearing is null ? null : f.Intent.ToString().ToLowerInvariant();
            string? turn = bearing is double b ? Phraseology.TurnTo(unit.Hdg, b) : null;
            Enter(f, TowerPhase.ClearedForTakeoff);
            TakingOff(f, unit, units);
            await SayAsync(f, Phraseology.HelicopterDeparture(f.Callsign, $"{f.Airfield.Facility} Tower", turn, direction, weather), ct);
        }
        else if (requested || f.LastReason != reason)
        {
            f.LastReason = reason;
            await SayAsync(f, Phraseology.HoldPosition(f.Callsign, $"{f.Airfield.Facility} Tower", reason), ct);
        }
    }

    /// <summary>
    /// Why a helicopter may not depart from where it stands yet (null: it may): another helicopter cleared to take off or
    /// land within 200 ft, or a runway in use that its way out crosses (within <see cref="Helicopters.DeparturePath"/>).
    /// </summary>
    private string? HelicopterDepartureBlocker(TowerFlight f, UnitState unit, IReadOnlyDictionary<string, UnitState> units)
    {
        var position = new Vec2(unit.X, unit.Z);
        foreach (var o in _flights.Values.Where(o => o != f && o.Airfield.Airbase.Id == f.Airfield.Airbase.Id))
        {
            Vec2? point = o.Phase == TowerPhase.ClearedToLand ? o.Spot?.Position
                : o.Phase == TowerPhase.ClearedForTakeoff && o.FromPresentPosition && units.TryGetValue(o.UnitName, out var u) && !Airborne(o, u) ? new Vec2(u.X, u.Z)
                : null;
            if (point is Vec2 p && p.DistanceTo(position) < Helicopters.Separation) return Phraseology.HelicopterTraffic;
        }
        var to = position + Vec2.FromHeading(f.Bearing) * Helicopters.DeparturePath;
        foreach (var runway in f.Airfield.Layout.Runways.Where(r => Crosses(r, position, to)))
        {
            if (RunwayInUse(runway, f, units) is { } reason) return reason;
        }
        return null;
    }

    /// <summary>Why a runway may not be crossed now (null: free): traffic on final for it, a landing or takeoff cleared on it, an aircraft on it.</summary>
    private string? RunwayInUse(Runway runway, TowerFlight f, IReadOnlyDictionary<string, UnitState> units)
    {
        bool others(UnitState u) => !f.Party.Units.Contains(u.Name);
        if (units.Values.Any(u => others(u) && u.InAir && u.Agl < 1000 &&
                                  new[] { runway.EndA, runway.EndB }.Any(e => DistanceOnFinal(runway, e, u) is double d && d <= DepartureBlockingFinal)))
            return Phraseology.TrafficOnFinal;
        if (_flights.Values.Any(o => o != f && o.UsesRunway && o.Runway == runway && o.Phase == TowerPhase.ClearedToLand)) return Phraseology.LandingTraffic;
        if (_flights.Values.Any(o => o != f && o.UsesRunway && o.Runway == runway && o.Phase is TowerPhase.ClearedForTakeoff or TowerPhase.LineUpAndWait))
            return Phraseology.TrafficDeparting;
        if (units.Values.Any(u => others(u) && !u.InAir && runway.IsOnRunway(new Vec2(u.X, u.Z)))) return Phraseology.RunwayOccupied;
        return null;
    }

    /// <summary>Whether the way from <paramref name="a"/> to <paramref name="b"/> crosses the runway, or its first mile of approach at either end.</summary>
    private static bool Crosses(Runway runway, Vec2 a, Vec2 b)
    {
        var left = runway.Axis.Left;
        double sa = (a - runway.EndA.Threshold).Dot(left), sb = (b - runway.EndA.Threshold).Dot(left);
        if (Math.Abs(sa) < 50) return InSpan(runway, a);
        if (Math.Sign(sa) == Math.Sign(sb)) return false;
        return InSpan(runway, a + (b - a) * (sa / (sa - sb)));
    }

    private static bool InSpan(Runway runway, Vec2 p)
    {
        double along = (p - runway.EndA.Threshold).Dot(runway.Axis);
        return along > -NauticalMile && along < runway.Length + NauticalMile;
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
            _handoffs[f.Key] = (_now(), f.Airfield);
            await SayAsync(f, Phraseology.ContactAfterDeparture(f.Callsign, f.Airfield.Frequencies.ContactFor("Approach"), resumeOwnNavigation: d?.Heading is not null), ct);
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

    /// <summary>Further than <see cref="PatternLimit"/> from the runway and flying away from it.</summary>
    private static bool LeavingPattern(Runway runway, UnitState unit)
    {
        var p = new Vec2(unit.X, unit.Z);
        if (runway.DistanceFrom(p) <= PatternLimit) return false;
        var toRunway = runway.Center - p;
        return toRunway.X * unit.Vx + toRunway.Z * unit.Vz < 0;
    }

    /// <summary>On the downwind for <paramref name="end"/>: low, beside the runway (0.3–3 NM), flying the opposite way, abeam it.</summary>
    public static bool OnDownwind(Runway runway, RunwayEnd end, UnitState unit)
    {
        var p = new Vec2(unit.X, unit.Z);
        double beside = runway.FromCenterline(p), along = runway.AlongFrom(end, p);
        return unit.InAir && unit.Agl < 1000 && beside > 0.3 * NauticalMile && beside < 3 * NauticalMile
               && along > -1.5 * NauticalMile && along < runway.Length + NauticalMile && HeadingOff(unit.Hdg, end.TrueHeading + 180) < 45;
    }

    private async Task LandedAsync(TowerFlight f, CancellationToken ct)
    {
        Enter(f, TowerPhase.Landed);
        // Closed traffic may touch and go: vacate only once it has slowed down.
        if (!f.InPattern) await SayVacateAsync(f, ct);
    }

    private Task SayVacateAsync(TowerFlight f, CancellationToken ct) =>
        SayAsync(f, Phraseology.VacateContactGround(f.Callsign, f.Airfield.Facility, f.Airfield.Frequencies.Own("Ground")), ct);

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
        if (radio is not null && airfield?.Call("Tower", coalition, text) is { } call) radio.Transmit(call);
    }

    /// <summary>
    /// Why a departure must hold short (null: it may take off), and whether the only obstacle is an
    /// earlier departure already on the runway (then it may line up and wait).
    /// </summary>
    private (string? Reason, bool DepartingAhead) DepartureBlocker(TowerFlight f, IReadOnlyDictionary<string, UnitState> units)
    {
        if (LandingTrafficWithin(f, units, DepartureBlockingFinal)) return (Phraseology.TrafficOnFinal, false);
        if (_flights.Values.Any(o => o != f && o.UsesRunway && o.Runway == f.Runway && o.Phase == TowerPhase.ClearedToLand)) return (Phraseology.LandingTraffic, false);

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
        if (_flights.Values.Any(o => o != f && o.UsesRunway && o.Runway == f.Runway && o.Phase is TowerPhase.ClearedForTakeoff or TowerPhase.LineUpAndWait
                                     && units.TryGetValue(o.UnitName, out var u) && !u.InAir))
            return (Phraseology.NumberTwoForDeparture, false);
        return (null, false);
    }

    /// <summary>Why a landing cannot be cleared yet (null: cleared to land).</summary>
    private string? LandingBlocker(TowerFlight f, IReadOnlyDictionary<string, UnitState> units)
    {
        if (RunwayOccupied(f, units) is { } occupied) return occupied;
        if (_flights.Values.Any(o => o != f && o.UsesRunway && o.Runway == f.Runway && o.Phase is TowerPhase.LineUpAndWait or TowerPhase.ClearedForTakeoff
                                     && units.TryGetValue(o.UnitName, out var u) && !u.InAir))
            return Phraseology.TrafficDeparting;
        // Another aircraft cleared to land on this runway and still airborne goes first.
        double? mine = units.TryGetValue(f.UnitName, out var me) ? DistanceOnFinal(f.Runway, f.End, me) : null;
        foreach (var o in _flights.Values.Where(o => o != f && o.UsesRunway && o.Runway == f.Runway && o.Phase == TowerPhase.ClearedToLand))
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
