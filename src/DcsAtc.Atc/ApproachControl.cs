using DcsAtc.Airfields;
using DcsAtc.Airfields.Taxi;
using DcsAtc.Bridge;

namespace DcsAtc.Atc;

public enum ApproachPhase
{
    /// <summary>An arrival being vectored to final.</summary>
    Vectoring,
    /// <summary>An arrival waiting over the TACAN because too many are being vectored.</summary>
    Holding,
    /// <summary>An overhead arrival flying to the initial point itself.</summary>
    Overhead,
    /// <summary>A departure with radar service until it leaves the area.</summary>
    RadarService,
}

/// <summary>The legs of the vectors to final: to the downwind, downwind, base, and the intercept onto final.</summary>
public enum VectorLeg
{
    Join,
    Downwind,
    Base,
    Final,
}

/// <summary>An aircraft or declared flight Approach is working.</summary>
public sealed class ApproachFlight
{
    public required Party Party { get; init; }
    public required string UnitType { get; init; }
    public required GroundAirfield Airfield { get; init; }
    public required Runway Runway { get; init; }
    public required RunwayEnd End { get; init; }
    /// <summary>The approach flown; null for a departure.</summary>
    public ApproachType? Type { get; init; }
    public ApproachPhase Phase { get; set; }
    public VectorLeg Leg { get; set; }
    /// <summary>The side of final the vectors use: +1 right, -1 left (looking in the landing direction).</summary>
    public int Side { get; set; }
    public Vec2 JoinPoint { get; set; }
    /// <summary>The heading and altitude (feet) last assigned.</summary>
    public int? Heading { get; set; }
    public double? Altitude { get; set; }
    /// <summary>The approach clearance has been given.</summary>
    public bool Cleared { get; set; }
    public bool FieldInSight { get; set; }
    /// <summary>"Extend downwind" has been said.</summary>
    public bool Extended { get; set; }
    public double HoldAltitude { get; set; }
    /// <summary>A departure held below arrival traffic: "maintain 6000 feet until advised".</summary>
    public double? Restriction { get; set; }
    public DateTime Since { get; set; }
    public string LastText { get; set; } = "";

    internal DateTime LastVector { get; set; } = DateTime.MinValue;
    internal DateTime LastWarning { get; set; } = DateTime.MinValue;
    internal DateTime? OffHeadingSince { get; set; }
    internal bool AltitudeReached { get; set; }
    internal Dictionary<string, DateTime> Advised { get; } = [];

    public int Key => Party.Key;
    public string Callsign => Party.Callsign;
    public string UnitName => Party.LeadUnit;
    public bool IsArrival => Type is not null;
}

/// <summary>
/// Approach (and departure) radar control: vectors arrivals to an ILS, TACAN or visual final (join, downwind, base, 30°
/// intercept), spaced on final by extending the downwind, with a hold over the TACAN when too many arrive at once;
/// sends overhead arrivals to the initial point; gives departures radar service (held below descending arrivals) up to
/// the edge of its area; and calls traffic to everyone it works. Hands arrivals to Tower on final.
/// </summary>
public sealed class ApproachControl(IDcsCommands dcs, AirfieldCatalog airfields, TowerControl tower, FlightRegistry flights,
    Action<string>? log = null, Func<DateTime>? clock = null, IAtcRadio? radio = null, ApproachProcedures? defaults = null, HoldingStacks? stacks = null)
{
    private const double Nm = 1852, Ft = 0.3048;
    private static readonly TimeSpan VectorInterval = TimeSpan.FromSeconds(30), WarningInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TrafficInterval = TimeSpan.FromMinutes(2), TurnGrace = TimeSpan.FromSeconds(20);

    private readonly Dictionary<int, ApproachFlight> _flights = [];
    private readonly Dictionary<int, (DateTime At, bool Overhead)> _handedToTower = [];
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);

    public IReadOnlyDictionary<int, ApproachFlight> Flights => _flights;

    /// <summary>The holding levels, shared with the AI arrivals.</summary>
    public HoldingStacks Stacks { get; } = stacks ?? new HoldingStacks();

    private static string Occupant(int key) => $"player:{key}";

    // Levels of players no longer holding (landed, gone, released) are free again.
    private void SyncStacks()
    {
        foreach (var o in Stacks.Occupants.Where(o => o.StartsWith("player:", StringComparison.Ordinal)).ToList())
        {
            if (!(int.TryParse(o["player:".Length..], out int key) && _flights.TryGetValue(key, out var f) && f.Phase == ApproachPhase.Holding))
                Stacks.Leave(o);
        }
    }

    /// <summary>Arrivals sent to Tower (party key → when, and whether for the overhead).</summary>
    public IReadOnlyDictionary<int, (DateTime At, bool Overhead)> HandedToTower => _handedToTower;

    /// <summary>
    /// The procedures where an airfield's labels file has none. Its request range and service radius (the settings'
    /// Approach distances) apply at every airfield.
    /// </summary>
    public ApproachProcedures Defaults { get; } = defaults ?? ApproachProcedures.Default;

    private readonly Dictionary<GroundAirfield, ApproachProcedures> _procedures = [];

    /// <summary>An airfield's procedures: its labels file's, else <see cref="Defaults"/>; the distances from <see cref="Defaults"/>.</summary>
    public ApproachProcedures ProceduresFor(GroundAirfield airfield)
    {
        if (_procedures.TryGetValue(airfield, out var p)) return p;
        p = airfield.Layout.Labels?.Approach is { } own
            ? own with { RequestRange = Defaults.RequestRange, ServiceRadius = Defaults.ServiceRadius }
            : Defaults;
        return _procedures[airfield] = p;
    }

    /// <summary>Whether Approach works this airfield (it has frequencies).</summary>
    public static bool Serves(AirfieldStations stations) => stations.Serves("Approach");

    /// <summary>The nearest airfield with Approach within its request range of <paramref name="position"/>.</summary>
    public Task<GroundAirfield?> NearestAsync(Vec2 position, double rangeNm, CancellationToken ct = default) =>
        airfields.NearestAsync(position, rangeNm * Nm, a => Serves(airfields.StationsFor(a)), ct);

    /// <summary>
    /// "Request approach": radar contact, the first descent and the approach to expect; then vectors (or the hold, or the
    /// overhead). A departure (handed off or in radar service) may ask too, to come back.
    /// </summary>
    public async Task RequestApproachAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, ApproachType type, GroundAirfield? at = null, CancellationToken ct = default)
    {
        if (unit.GroupId is null) return;
        var party = flights.PartyFor(unit, units);
        var lead = units.GetValueOrDefault(party.LeadUnit) ?? unit;
        bool identified = false;
        if (_flights.TryGetValue(party.Key, out var existing))
        {
            bool same = at is null || existing.Airfield.Airbase.Id == at.Airbase.Id;
            if (existing.IsArrival && same)
            {
                await SayAsync(existing, existing.LastText, ct);
                return;
            }
            // A departure in radar service turns into an arrival (already identified by this Approach); an arrival diverts.
            _flights.Remove(party.Key);
            identified = !existing.IsArrival && same;
        }
        tower.HandoffTaken(party.Key);
        if (!lead.InAir)
        {
            await TextAsync(party, Phraseology.OnTheGround(party.Callsign), ct);
            return;
        }
        var position = new Vec2(lead.X, lead.Z);
        var airfield = at is null ? await NearestAsync(position, Defaults.RequestRange, ct)
            : DistanceToField(at, position) <= ProceduresFor(at).RequestRange ? at : null;
        if (airfield is null)
        {
            await TextAsync(party, Phraseology.NoApproach(party.Callsign, Defaults.RequestRange), ct);
            return;
        }
        var p = ProceduresFor(airfield);
        var weather = await dcs.GetAirbaseWeatherAsync(airfield.Airbase.Name, ct);
        var (runway, end) = airfield.Layout.ActiveEnds(weather?.WindFrom ?? 0, weather?.WindSpeed ?? 0).MinBy(e => e.Runway.FromCenterline(position));
        if (runway is null) return;

        // Guidance: an ILS needs a localizer for that runway, a TACAN approach a TACAN; else the next best.
        if (type == ApproachType.Ils && airfield.Radio.LocalizerFor(runway, end) is null) type = airfield.Radio.Tacan is not null ? ApproachType.Tacan : ApproachType.Visual;
        if (type == ApproachType.Tacan && airfield.Radio.Tacan is null) type = ApproachType.Visual;
        var guidance = type == ApproachType.Ils ? airfield.Radio.LocalizerFor(runway, end) : type == ApproachType.Tacan ? airfield.Radio.Tacan : null;

        var f = new ApproachFlight
        {
            Party = party, UnitType = lead.Type, Airfield = airfield, Runway = runway, End = end, Type = type, Since = _now(),
            Side = SideOf(airfield, end, position),
        };
        double feet = lead.Y / Ft;
        string? where = identified ? null : PositionOf(airfield, position);
        string expect = Phraseology.ExpectApproach(type, end, guidance);
        string text;
        if (type == ApproachType.Overhead)
        {
            f.Phase = ApproachPhase.Overhead;
            f.Altitude = DistanceToField(airfield, position) > 20 ? FirstAltitude(p, feet) : p.InitialAltitude;
            text = Phraseology.ApproachContact(party.Callsign, airfield.Facility, where, Phraseology.AltitudeChange(feet, f.Altitude.Value), weather, lead.Type, expect, null);
        }
        else if (_flights.Values.Count(o => o.Airfield == airfield && o.Phase == ApproachPhase.Vectoring) >= p.MaxVectored)
        {
            f.Phase = ApproachPhase.Holding;
            SyncStacks();
            int holding = Stacks.At(airfield.Airbase.Id).Count;
            f.HoldAltitude = Stacks.Take(airfield.Airbase.Id, Occupant(party.Key), p.HoldingAltitude, p.HoldingStep);
            f.Altitude = f.HoldAltitude;
            int minutes = 5 * (holding + 1);
            text = Phraseology.Hold(party.Callsign, airfield.Facility, where, airfield.Radio.Tacan is not null, f.HoldAltitude, weather, lead.Type, minutes);
        }
        else
        {
            f.Phase = ApproachPhase.Vectoring;
            Plan(f, lead, units, feet);
            text = Phraseology.ApproachContact(party.Callsign, airfield.Facility, where, Phraseology.AltitudeChange(feet, f.Altitude!.Value), weather, lead.Type, expect,
                Phraseology.Vector(lead.Hdg, f.Heading!.Value));
        }
        _flights[party.Key] = f;
        f.LastVector = _now();
        await SayAsync(f, text, ct);
    }

    /// <summary>A departure checks in after Tower's hand-off: radar contact (held below arrivals if need be).</summary>
    public async Task CheckInAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, GroundAirfield? at = null, CancellationToken ct = default)
    {
        if (unit.GroupId is null) return;
        var party = flights.PartyFor(unit, units);
        var lead = units.GetValueOrDefault(party.LeadUnit) ?? unit;
        if (_flights.TryGetValue(party.Key, out var existing))
        {
            await SayAsync(existing, existing.LastText, ct);
            return;
        }
        var position = new Vec2(lead.X, lead.Z);
        var airfield = at ?? (tower.Handoffs.TryGetValue(party.Key, out var handoff) ? handoff.Airfield : null)
                       ?? await NearestAsync(position, Defaults.ServiceRadius, ct);
        tower.HandoffTaken(party.Key);
        if (airfield is null || !lead.InAir)
        {
            await TextAsync(party, Phraseology.NoApproach(party.Callsign, Defaults.ServiceRadius), ct);
            return;
        }
        var runway = airfield.Layout.Runways.MinBy(r => r.DistanceFrom(position))!;
        var f = new ApproachFlight
        {
            Party = party, UnitType = lead.Type, Airfield = airfield, Runway = runway, End = runway.EndA, Type = null,
            Phase = ApproachPhase.RadarService, Since = _now(),
        };
        f.Restriction = ConflictAltitude(f, lead, units);
        _flights[party.Key] = f;
        await SayAsync(f, Phraseology.DepartureContact(party.Callsign, airfield.Facility, PositionOf(airfield, position), f.Restriction), ct);
    }

    /// <summary>The pilot sees the field: cleared for the visual approach and sent to Tower.</summary>
    public async Task FieldInSightAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (unit.GroupId is null || !_flights.TryGetValue(flights.PartyFor(unit, units).Key, out var f) || !f.IsArrival) return;
        f.FieldInSight = true;
        await HandToTowerAsync(f, Phraseology.ClearedVisualApproach(f.Callsign, f.End, f.Airfield.Frequencies.Own("Tower")), overhead: false, ct);
    }

    public async Task CancelAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (unit.GroupId is null || !_flights.Remove(flights.PartyFor(unit, units).Key, out var f)) return;
        await SayAsync(f, Phraseology.ApproachCancelled(f.Callsign), ct);
    }

    public async Task SayAgainAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (unit.GroupId is int g && _flights.TryGetValue(flights.PartyFor(unit, units).Key, out var f) && f.LastText != "")
        {
            await dcs.ShowTextAsync(g, f.LastText, 20, ct: ct);
            Transmit(f, f.LastText);
        }
    }

    public async Task OnUnitsAsync(IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        SyncStacks();
        foreach (var f in _flights.Values.OrderBy(f => f.Since).ToList())
        {
            if (!units.TryGetValue(f.UnitName, out var unit) || !unit.InAir)
            {
                _flights.Remove(f.Key);
                continue;
            }
            bool spoke = f.Phase switch
            {
                ApproachPhase.Vectoring => await VectorAsync(f, unit, units, ct),
                ApproachPhase.Overhead => await OverheadAsync(f, unit, ct),
                ApproachPhase.RadarService => await RadarServiceAsync(f, unit, units, ct),
                _ => false,
            };
            if (!spoke && _flights.ContainsKey(f.Key)) await TrafficAsync(f, unit, units, ct);
        }
        await ReleaseHoldersAsync(units, ct);
    }

    public Task OnEventAsync(DcsEventMessage e, CancellationToken ct = default)
    {
        if (e.Event is "crash" or "dead" or "unit_lost" or "player_leave_unit" or "pilot_dead" or "ejection" or "land")
        {
            var f = _flights.Values.FirstOrDefault(x => x.UnitName == e.Unit);
            if (f is not null) _flights.Remove(f.Key);
        }
        return Task.CompletedTask;
    }

    public void Reset()
    {
        _flights.Clear();
        _handedToTower.Clear();
        SyncStacks();
    }

    // Vectors.

    /// <summary>
    /// Plans the vectors from where the aircraft is: straight onto final from in front of it (if that keeps the spacing),
    /// otherwise to the downwind on its side. Sets the leg, heading and altitude.
    /// </summary>
    private void Plan(ApproachFlight f, UnitState unit, IReadOnlyDictionary<string, UnitState> units, double feet)
    {
        var p = ProceduresFor(f.Airfield);
        var position = new Vec2(unit.X, unit.Z);
        double a = Along(f.End, position), s = Lateral(f.End, position);
        double beyondIntercept = -a - p.FinalDistance * Nm;
        bool inFront = beyondIntercept > 2 * Nm && Math.Abs(s) < beyondIntercept * Math.Tan(40 * Math.PI / 180) + Nm;
        f.Altitude = FirstAltitude(p, feet);
        f.AltitudeReached = false;
        if (inFront && SpacingOk(f, units, Math.Sqrt(a * a + s * s) / Nm))
        {
            f.Leg = VectorLeg.Final;
            f.Heading = InterceptHeading(f, position);
            return;
        }
        f.Side = s >= 0 ? 1 : -1;
        double baseTurn = (p.FinalDistance + p.BaseExtension) * Nm;
        double joinAlong = Math.Clamp(a, -(baseTurn - 4 * Nm), 2 * Nm);
        f.JoinPoint = f.End.Threshold + Vec2.FromHeading(f.End.TrueHeading) * joinAlong + Vec2.FromHeading(f.End.TrueHeading + 90) * (f.Side * p.DownwindOffset * Nm);
        if (position.DistanceTo(f.JoinPoint) < 2 * Nm)
        {
            f.Leg = VectorLeg.Downwind;
            f.Heading = Phraseology.Heading(f.End.TrueHeading + 180);
            return;
        }
        f.Leg = VectorLeg.Join;
        f.Heading = Phraseology.Heading((f.JoinPoint - position).Heading);
    }

    /// <summary>One update of an arrival on vectors: the next leg when its point is reached, re-vectors, compliance. True if it spoke.</summary>
    private async Task<bool> VectorAsync(ApproachFlight f, UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct)
    {
        var p = ProceduresFor(f.Airfield);
        var position = new Vec2(unit.X, unit.Z);
        double a = Along(f.End, position), s = Lateral(f.End, position);
        double feet = unit.Y / Ft;
        var now = _now();
        if (f.Altitude is double assigned && Math.Abs(feet - assigned) < 200) f.AltitudeReached = true;

        // Established on final by itself: on with the clearance.
        bool established = Math.Abs(s) < 0.5 * Nm && a < -2 * Nm && a > -(p.FinalDistance + 8) * Nm && HeadingOff(unit.Hdg, f.End.TrueHeading) < 20;
        if (established && f.Leg != VectorLeg.Final) f.Leg = VectorLeg.Final;

        switch (f.Leg)
        {
            case VectorLeg.Join:
                if (position.DistanceTo(f.JoinPoint) < 2 * Nm || (Math.Abs(s - f.Side * p.DownwindOffset * Nm) < Nm && a < 2 * Nm))
                {
                    f.Leg = VectorLeg.Downwind;
                    return await VectorCallAsync(f, unit, Phraseology.Heading(f.End.TrueHeading + 180), Math.Min(f.Altitude ?? double.MaxValue, p.InterceptAltitude + 1000), ct);
                }
                int toJoin = Phraseology.Heading((f.JoinPoint - position).Heading);
                if (now - f.LastVector >= VectorInterval && HeadingOff(toJoin, f.Heading ?? toJoin) >= 20)
                    return await VectorCallAsync(f, unit, toJoin, null, ct);
                break;

            case VectorLeg.Downwind:
            {
                double baseTurn = (p.FinalDistance + p.BaseExtension) * Nm;
                if (a > -baseTurn) break;
                // Track miles after turning base: base, the 30° intercept (cuts the corner by about 1.5 NM), final.
                double toGo = (Math.Abs(a) + Math.Abs(s)) / Nm - 1.5;
                if (SpacingOk(f, units, toGo) || a < -30 * Nm)
                {
                    f.Leg = VectorLeg.Base;
                    return await VectorCallAsync(f, unit, Phraseology.Heading(f.End.TrueHeading - f.Side * 90), p.InterceptAltitude, ct);
                }
                if (!f.Extended)
                {
                    f.Extended = true;
                    await SayAsync(f, Phraseology.ExtendDownwind(f.Callsign, 1 + Leaders(f, units).Count(d => d < toGo)), ct);
                    return true;
                }
                break;
            }

            case VectorLeg.Base:
                if (Math.Abs(s) <= 2 * Nm || Math.Sign(s) != f.Side) f.Leg = VectorLeg.Final;
                break;
        }

        if (f.Leg == VectorLeg.Final)
        {
            // A straight-in from far out gets its clearance within 15 NM; down to 1000 feet above the intercept altitude before that.
            if (!f.Cleared && !established && -a > (p.FinalDistance + 5) * Nm)
            {
                if (f.Altitude > p.InterceptAltitude + 1000 && -a <= (p.FinalDistance + 15) * Nm)
                    return await VectorCallAsync(f, unit, f.Heading ?? InterceptHeading(f, position), p.InterceptAltitude + 1000, ct);
                return await ComplianceAsync(f, unit, feet, now, ct);
            }
            if (!f.Cleared)
            {
                f.Cleared = true;
                f.Heading = InterceptHeading(f, position);
                f.Altitude = p.InterceptAltitude;
                f.AltitudeReached = false;
                f.LastVector = now;
                string vector = Phraseology.Vector(unit.Hdg, f.Heading.Value);
                if (f.Type == ApproachType.Visual && f.FieldInSight)
                {
                    await HandToTowerAsync(f, Phraseology.ClearedVisualApproach(f.Callsign, f.End, f.Airfield.Frequencies.Own("Tower")), overhead: false, ct);
                    return true;
                }
                await SayAsync(f, Phraseology.ApproachClearance(f.Callsign, Math.Sqrt(a * a + s * s) / Nm, vector, p.InterceptAltitude, f.Type!.Value, f.End), ct);
                return true;
            }
            if ((-a <= p.HandoffDistance * Nm && Math.Abs(s) < Nm) || -a <= 4 * Nm)
            {
                await HandToTowerAsync(f, Phraseology.ApproachToTower(f.Callsign, f.Airfield.Frequencies.Own("Tower"), overhead: false), overhead: false, ct);
                return true;
            }
            if (established) return false;
        }

        return await ComplianceAsync(f, unit, feet, now, ct);
    }

    /// <summary>"Check altitude" below the assigned altitude, "check heading" off the assigned heading (after time to turn).</summary>
    private async Task<bool> ComplianceAsync(ApproachFlight f, UnitState unit, double feet, DateTime now, CancellationToken ct)
    {
        if (now - f.LastWarning < WarningInterval) return false;
        if (f.AltitudeReached && f.Altitude is double alt && feet < alt - 300)
        {
            f.LastWarning = now;
            await SayAsync(f, Phraseology.CheckAltitude(f.Callsign, alt), ct);
            return true;
        }
        if (f.Heading is int heading && now - f.LastVector >= TurnGrace && HeadingOff(unit.Hdg, heading) > 30)
        {
            f.OffHeadingSince ??= now;
            if (now - f.OffHeadingSince >= TimeSpan.FromSeconds(15))
            {
                f.LastWarning = now;
                f.OffHeadingSince = null;
                await SayAsync(f, Phraseology.CheckHeading(f.Callsign, heading), ct);
                return true;
            }
        }
        else
        {
            f.OffHeadingSince = null;
        }
        return false;
    }

    private async Task<bool> VectorCallAsync(ApproachFlight f, UnitState unit, int heading, double? altitude, CancellationToken ct)
    {
        string? altitudeText = null;
        if (altitude is double alt && alt != f.Altitude)
        {
            f.Altitude = alt;
            f.AltitudeReached = false;
            altitudeText = Phraseology.AltitudeChange(unit.Y / Ft, alt);
        }
        f.Heading = heading;
        f.LastVector = _now();
        f.OffHeadingSince = null;
        await SayAsync(f, Phraseology.VectorCall(f.Callsign, Phraseology.Vector(unit.Hdg, heading), altitudeText), ct);
        return true;
    }

    /// <summary>
    /// Onto final from the side the aircraft is on, aimed to join it at least 2 NM before the intercept point (20° to 45°;
    /// from the base leg that is about 30°), or straight down final when already on it.
    /// </summary>
    private int InterceptHeading(ApproachFlight f, Vec2 position)
    {
        var p = ProceduresFor(f.Airfield);
        double a = Along(f.End, position), s = Lateral(f.End, position);
        if (Math.Abs(s) < 0.3 * Nm) return Phraseology.Heading(f.End.TrueHeading);
        double room = Math.Max(Nm, -a - (p.FinalDistance - 2) * Nm);
        double angle = Math.Clamp(Math.Atan2(Math.Abs(s), room) * 180 / Math.PI, 20, 45);
        return Phraseology.Heading(f.End.TrueHeading - Math.Sign(s) * angle);
    }

    /// <summary>
    /// Whether an arrival <paramref name="toGo"/> NM from touchdown may turn onto final now: everyone already committed
    /// to this runway (on base or final, with Tower, or AI on final) that is not well behind must be at least the spacing
    /// ahead, plus a margin for the compression as the leader slows down on final.
    /// </summary>
    private bool SpacingOk(ApproachFlight f, IReadOnlyDictionary<string, UnitState> units, double toGo)
    {
        double spacing = ProceduresFor(f.Airfield).Spacing + CompressionMargin;
        return Leaders(f, units).All(d => d <= toGo - spacing || d >= toGo + spacing);
    }

    /// <summary>Extra spacing at the base turn (NM): leaders slow down on final, followers are still faster on base.</summary>
    private const double CompressionMargin = 1.5;

    /// <summary>The distances to touchdown (NM) of the aircraft committed to this runway end ahead of or beside <paramref name="f"/>.</summary>
    private IEnumerable<double> Leaders(ApproachFlight f, IReadOnlyDictionary<string, UnitState> units)
    {
        var counted = new HashSet<string>(f.Party.Units);
        foreach (var o in _flights.Values.Where(o => o != f && o.End == f.End && o.Phase == ApproachPhase.Vectoring && o.Leg is VectorLeg.Base or VectorLeg.Final))
        {
            if (!units.TryGetValue(o.UnitName, out var u)) continue;
            counted.UnionWith(o.Party.Units);
            var q = new Vec2(u.X, u.Z);
            double a = Along(o.End, q), s = Lateral(o.End, q);
            yield return o.Leg == VectorLeg.Base ? (Math.Abs(a) + Math.Abs(s)) / Nm : Math.Sqrt(a * a + s * s) / Nm;
        }
        // Everyone else on final for this runway: with Tower, or AI.
        foreach (var u in units.Values)
        {
            if (counted.Contains(u.Name) || !u.InAir || u.Agl > 5000 * Ft) continue;
            if (TowerControl.DistanceOnFinal(f.Runway, f.End, u) is double d) yield return d / Nm;
        }
    }

    private async Task ReleaseHoldersAsync(IReadOnlyDictionary<string, UnitState> units, CancellationToken ct)
    {
        foreach (var group in _flights.Values.Where(f => f.Phase == ApproachPhase.Holding).GroupBy(f => f.Airfield).ToList())
        {
            var p = ProceduresFor(group.Key);
            if (_flights.Values.Count(o => o.Airfield == group.Key && o.Phase == ApproachPhase.Vectoring) >= p.MaxVectored) continue;
            var next = group.OrderBy(f => f.HoldAltitude).First();
            if (!units.TryGetValue(next.UnitName, out var unit)) continue;
            next.Phase = ApproachPhase.Vectoring;
            Stacks.Leave(Occupant(next.Key));
            Plan(next, unit, units, unit.Y / Ft);
            next.LastVector = _now();
            var guidance = next.Type == ApproachType.Ils ? next.Airfield.Radio.LocalizerFor(next.Runway, next.End) : next.Type == ApproachType.Tacan ? next.Airfield.Radio.Tacan : null;
            await SayAsync(next, Phraseology.LeaveHold(next.Callsign, Phraseology.Vector(unit.Hdg, next.Heading!.Value),
                Phraseology.AltitudeChange(unit.Y / Ft, next.Altitude!.Value), Phraseology.ExpectApproach(next.Type!.Value, next.End, guidance)), ct);
        }
    }

    // Overhead.

    private async Task<bool> OverheadAsync(ApproachFlight f, UnitState unit, CancellationToken ct)
    {
        var p = ProceduresFor(f.Airfield);
        var position = new Vec2(unit.X, unit.Z);
        double toField = DistanceToField(f.Airfield, position);
        var initial = f.End.Threshold - Vec2.FromHeading(f.End.TrueHeading) * (p.InitialDistance * Nm);
        if (toField <= 12 || position.DistanceTo(initial) <= 8 * Nm)
        {
            await HandToTowerAsync(f, Phraseology.ApproachToTower(f.Callsign, f.Airfield.Frequencies.Own("Tower"), overhead: true), overhead: true, ct);
            return true;
        }
        if (toField <= 20 && f.Altitude > p.InitialAltitude)
        {
            f.Altitude = p.InitialAltitude;
            await SayAsync(f, Phraseology.ProceedToInitial(f.Callsign, Phraseology.AltitudeChange(unit.Y / Ft, p.InitialAltitude), f.End), ct);
            return true;
        }
        return false;
    }

    // Departures.

    private async Task<bool> RadarServiceAsync(ApproachFlight f, UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct)
    {
        var p = ProceduresFor(f.Airfield);
        var position = new Vec2(unit.X, unit.Z);
        if (DistanceToField(f.Airfield, position) > p.ServiceRadius)
        {
            _flights.Remove(f.Key);
            await SayAsync(f, Phraseology.RadarServiceTerminated(f.Callsign), ct);
            return true;
        }
        double feet = unit.Y / Ft;
        var conflict = ConflictAltitude(f, unit, units);
        if (f.Restriction is null && conflict is double c)
        {
            f.Restriction = c;
            await SayAsync(f, Phraseology.MaintainUntilAdvised(f.Callsign, c), ct);
            return true;
        }
        if (f.Restriction is not null && !ArrivalInTheWay(f, unit, units))
        {
            f.Restriction = null;
            await SayAsync(f, Phraseology.ApproachClimbUnrestricted(f.Callsign), ct);
            return true;
        }
        var now = _now();
        if (f.Restriction is double r && feet > r + 300 && now - f.LastWarning >= WarningInterval)
        {
            f.LastWarning = now;
            await SayAsync(f, Phraseology.CheckAltitude(f.Callsign, r), ct);
            return true;
        }
        return false;
    }

    /// <summary>
    /// The altitude a departure must stay at or below for arrivals descending above it nearby (1000 feet below the lowest,
    /// in whole thousands), or null if none is in the way (or one is already too close below: traffic advisories handle that).
    /// </summary>
    private double? ConflictAltitude(ApproachFlight f, UnitState unit, IReadOnlyDictionary<string, UnitState> units)
    {
        var position = new Vec2(unit.X, unit.Z);
        double feet = unit.Y / Ft;
        double? limit = null;
        foreach (var o in _flights.Values.Where(o => o.IsArrival && o.Airfield == f.Airfield))
        {
            if (!units.TryGetValue(o.UnitName, out var u)) continue;
            double above = u.Y / Ft - feet;
            if (above < 1000 || above > 8000 || new Vec2(u.X, u.Z).DistanceTo(position) > 10 * Nm) continue;
            double altitude = Math.Floor((u.Y / Ft - 1000) / 1000) * 1000;
            if (altitude < Math.Max(2000, feet - 200)) continue;
            limit = Math.Min(limit ?? double.MaxValue, altitude);
        }
        return limit;
    }

    /// <summary>An arrival near the departure (10 NM) that is above it or less than 1000 feet below: a restriction stays until none is.</summary>
    private bool ArrivalInTheWay(ApproachFlight f, UnitState unit, IReadOnlyDictionary<string, UnitState> units)
    {
        var position = new Vec2(unit.X, unit.Z);
        return _flights.Values.Any(o => o.IsArrival && o.Airfield == f.Airfield && units.TryGetValue(o.UnitName, out var u)
            && (u.Y - unit.Y) / Ft > -1000 && (u.Y - unit.Y) / Ft < 8000 && new Vec2(u.X, u.Z).DistanceTo(position) <= 10 * Nm);
    }

    // Traffic.

    /// <summary>"Traffic, 2 o'clock, 5 miles, opposite direction, 2000 feet above": once per pair every two minutes, when closing.</summary>
    private async Task TrafficAsync(ApproachFlight f, UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct)
    {
        var p = ProceduresFor(f.Airfield);
        var now = _now();
        var position = new Vec2(unit.X, unit.Z);
        bool onFinal = OnAnyFinal(f.Airfield, unit);
        foreach (var u in units.Values.Where(u => u.InAir && !f.Party.Units.Contains(u.Name)).OrderBy(u => new Vec2(u.X, u.Z).DistanceTo(position)))
        {
            // Aircraft on the parallel finals are known to each other.
            if (onFinal && OnAnyFinal(f.Airfield, u) && HeadingOff(u.Hdg, unit.Hdg) < 30) continue;
            var other = new Vec2(u.X, u.Z);
            var offset = other - position;
            double miles = offset.Length / Nm, above = (u.Y - unit.Y) / Ft;
            if (miles > p.TrafficRange) break;
            if (Math.Abs(above) > p.TrafficVertical) continue;
            bool closing = offset.X * (u.Vx - unit.Vx) + offset.Z * (u.Vz - unit.Vz) < 0;
            if (!closing && miles > 2) continue;
            if (f.Advised.TryGetValue(u.Name, out var last) && now - last < TrafficInterval) continue;
            f.Advised[u.Name] = now;
            double relative = ((offset.Heading - unit.Hdg) % 360 + 360) % 360;
            int clockPosition = (int)Math.Round(relative / 30) % 12;
            double headingDiff = HeadingOff(u.Hdg, unit.Hdg);
            string direction = headingDiff < 45 ? "same direction" : headingDiff > 135 ? "opposite direction" : $"{Phraseology.CompassPoint(u.Hdg)}bound";
            await SayAsync(f, Phraseology.Traffic(f.Callsign, clockPosition == 0 ? 12 : clockPosition, miles, direction, above), ct);
            return;
        }
    }

    // Helpers.

    private static bool OnAnyFinal(GroundAirfield airfield, UnitState unit) =>
        airfield.Layout.Runways.Any(r => TowerControl.DistanceOnFinal(r, r.EndA, unit) is not null || TowerControl.DistanceOnFinal(r, r.EndB, unit) is not null);

    private async Task HandToTowerAsync(ApproachFlight f, string text, bool overhead, CancellationToken ct)
    {
        _flights.Remove(f.Key);
        _handedToTower[f.Key] = (_now(), overhead);
        // Tower now expects this flight for this airfield and runway, even where another airfield is nearer.
        tower.ExpectArrival(f.Party, f.UnitType, f.Airfield, f.Runway, f.End, overhead);
        await SayAsync(f, text, ct);
    }

    /// <summary>The first altitude for an arrival: the arrival altitude, or where it is (whole thousands) if lower, not below the intercept altitude.</summary>
    private static double FirstAltitude(ApproachProcedures p, double feet) =>
        feet > p.ArrivalAltitude + 300 ? p.ArrivalAltitude : Math.Max(p.InterceptAltitude, Math.Floor(feet / 1000) * 1000);

    /// <summary>The side of final the aircraft is on; near the centerline, the airfield's pattern side.</summary>
    private static int SideOf(GroundAirfield airfield, RunwayEnd end, Vec2 position)
    {
        double s = Lateral(end, position);
        if (Math.Abs(s) >= Nm) return Math.Sign(s);
        return (airfield.Layout.Labels?.Departure ?? DepartureProcedures.Default).ClosedTraffic.GetValueOrDefault(end.Name, "left") == "right" ? 1 : -1;
    }

    internal static string PositionOf(GroundAirfield airfield, Vec2 position)
    {
        var offset = position - new Vec2(airfield.Airbase.X, airfield.Airbase.Z);
        return Phraseology.Position(offset.Length / Nm, offset.Heading, airfield.Facility);
    }

    private static double DistanceToField(GroundAirfield airfield, Vec2 position) => position.DistanceTo(new Vec2(airfield.Airbase.X, airfield.Airbase.Z)) / Nm;

    /// <summary>Distance along the landing direction from the threshold (negative before it, on the approach side).</summary>
    private static double Along(RunwayEnd end, Vec2 p) => (p - end.Threshold).Dot(Vec2.FromHeading(end.TrueHeading));

    /// <summary>Distance to the right of final (looking in the landing direction).</summary>
    private static double Lateral(RunwayEnd end, Vec2 p) => (p - end.Threshold).Dot(Vec2.FromHeading(end.TrueHeading + 90));

    private static double HeadingOff(double heading, double reference) => Math.Abs(((heading - reference) % 360 + 540) % 360 - 180);

    private async Task SayAsync(ApproachFlight f, string text, CancellationToken ct)
    {
        f.LastText = text;
        foreach (var g in f.Party.Groups) await dcs.ShowTextAsync(g, text, 20, ct: ct);
        Transmit(f, text);
        log?.Invoke($"{f.Callsign}: {text}");
    }

    private void Transmit(ApproachFlight f, string text)
    {
        if (radio is not null && f.Airfield.Call("Approach", f.Party.Coalition, text) is { } call) radio.Transmit(call);
    }

    private async Task TextAsync(Party party, string text, CancellationToken ct)
    {
        foreach (var g in party.Groups) await dcs.ShowTextAsync(g, text, 10, ct: ct);
    }
}
