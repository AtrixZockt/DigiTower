using DcsAtc.Airfields;
using DcsAtc.Bridge;

namespace DcsAtc.Atc;

/// <summary>An F10 menu entry: its submenu path under F10 → Other, its label, and the id the app dispatches on.</summary>
public sealed record MenuEntry(IReadOnlyList<string> Path, string Label, string Id);

/// <summary>
/// The ATC calls a pilot can make right now, as F10 menu entries (menus are per group in DCS, so every flight sees its
/// own), one submenu per airfield: on the ground the airfield you are at, in the air the five nearest within 100 NM
/// (plus any airfield working you). Under each, the calls in one flat list: Ground (taxi, cancel, say again), Tower
/// (ready for departure, request landing, initial, check in, report final, say again), Approach (request approach,
/// check in, field in sight, cancel, say again), and its frequencies. Then Flight (declare / leave) and Debug.
/// </summary>
public sealed class AtcMenu(AirfieldCatalog airfields, GroundControl ground, TowerControl tower, ApproachControl approach, FlightRegistry flights)
{
    private const double Nm = 1852;

    public const string Root = "ATC";
    public static readonly string[] Flight = [Root, "Flight"];
    public static readonly string[] Debug = [Root, "Debug"];

    /// <summary>In the air the menu lists this many airfields, within this distance (NM).</summary>
    public const int AirfieldCount = 5;
    public const double AirfieldRange = 100;

    /// <summary>Departures may check in with Approach this long after Tower's hand-off.</summary>
    public static readonly TimeSpan CheckInWindow = TimeSpan.FromMinutes(5);

    /// <summary>"Field in sight" is offered this close to the airfield (NM).</summary>
    public const double FieldInSightRange = 25;

    /// <summary>Closer than this to a parking spot counts as parked (metres).</summary>
    public const double ParkedDistance = 25;

    /// <summary>The "Ready for departure" choices: menu id suffix, label, intent.</summary>
    public static readonly IReadOnlyList<(string Id, string Label, DepartureIntent Intent)> Departures =
    [
        ("own", "Own navigation", DepartureIntent.OwnNavigation),
        ("north", "Departure north", DepartureIntent.North),
        ("east", "Departure east", DepartureIntent.East),
        ("south", "Departure south", DepartureIntent.South),
        ("west", "Departure west", DepartureIntent.West),
        ("closed", "Closed traffic", DepartureIntent.ClosedTraffic),
    ];

    /// <summary>The "Request approach" choices: menu id suffix, label, approach.</summary>
    public static readonly IReadOnlyList<(string Id, string Label, ApproachType Type)> Approaches =
    [
        ("ils", "ILS", ApproachType.Ils),
        ("tacan", "TACAN", ApproachType.Tacan),
        ("visual", "Visual", ApproachType.Visual),
        ("overhead", "Overhead", ApproachType.Overhead),
    ];

    public static DepartureIntent? IntentOf(string action) =>
        Departures.FirstOrDefault(d => action == $"tower.ready.{d.Id}") is { Label: not null } d ? d.Intent : null;

    public static ApproachType? ApproachOf(string action) =>
        Approaches.FirstOrDefault(a => action == $"approach.{a.Id}") is { Label: not null } a ? a.Type : null;

    /// <summary>The id of an airfield's call: "ab4:ground.taxi" (airbase 4).</summary>
    public static string IdFor(int airbaseId, string action) => $"ab{airbaseId}:{action}";

    /// <summary>"ab4:ground.taxi" → (4, "ground.taxi"); "flight.2" → (null, "flight.2").</summary>
    public static (int? Airbase, string Action) ParseId(string id)
    {
        int colon = id.IndexOf(':');
        return id.StartsWith("ab", StringComparison.Ordinal) && colon > 2 && int.TryParse(id.AsSpan(2, colon - 2), out int airbase)
            ? (airbase, id[(colon + 1)..])
            : (null, id);
    }

    // The airfield order last shown to each group: kept while the same airfields are listed, so the menu does not jump.
    private readonly Dictionary<int, List<int>> _order = [];

    /// <summary>The menu for the group of <paramref name="unit"/> (a player), from its flight's situation.</summary>
    public async Task<IReadOnlyList<MenuEntry>> ForAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        var entries = new List<MenuEntry>();
        var party = flights.PartyFor(unit, units);
        var lead = units.GetValueOrDefault(party.LeadUnit) ?? unit;
        var situation = new Situation(party, lead, ground.ClearanceFor(unit, units), tower.Flights.GetValueOrDefault(party.Key),
            approach.Flights.GetValueOrDefault(party.Key), tower.HandedOffRecently(party.Key, CheckInWindow) ? tower.Handoffs[party.Key].Airfield : null);

        foreach (var airfield in await AirfieldsAsync(unit.GroupId ?? party.Key, situation, ct))
        {
            string[] path = [Root, airfield.Facility];
            int id = airfield.Airbase.Id;
            var calls = Calls(airfield, situation).ToList();
            foreach (var (label, action, submenu) in calls)
                entries.Add(new(submenu is null ? path : [.. path, submenu], label, IdFor(id, action)));
        }

        if (flights.FlightOf(unit.Name) is not null)
        {
            entries.Add(new(Flight, "Single ship", "flight.single"));
        }
        else if (FlightRegistry.Split(Phraseology.Callsign(unit)) is not null)
        {
            for (int n = 2; n <= 4; n++) entries.Add(new(Flight, $"Flight of {n}", $"flight.{n}"));
        }
        entries.Add(new(Debug, "Where am I?", "debug.where"));
        return entries;
    }

    /// <summary>What ATC knows about the flight: its taxi clearance, Tower and Approach flights, a pending check-in.</summary>
    private sealed record Situation(Party Party, UnitState Lead, TaxiClearance? Taxi, TowerFlight? Tower, ApproachFlight? Approach, GroundAirfield? CheckInWith)
    {
        public Vec2 Position => new(Lead.X, Lead.Z);
    }

    /// <summary>
    /// On the ground: the airfield the flight is at. In the air: the nearest within range, at most five. Always: any
    /// airfield working the flight. Listed nearest first, in the same order as last time while the set is unchanged.
    /// </summary>
    private async Task<List<GroundAirfield>> AirfieldsAsync(int group, Situation s, CancellationToken ct)
    {
        var found = new List<GroundAirfield>();
        if (!s.Lead.InAir)
        {
            if (await airfields.NearestAsync(s.Position, ct: ct) is { } here) found.Add(here);
        }
        else
        {
            // Airfields whose layout is still loading are listed from the next update on, so the menu never waits for one.
            // A loading airfield takes its place in the five, so only the five nearest ever start loading.
            int places = 0;
            foreach (var airbase in airfields.Airbases.Where(a => a.Category == 0 && Distance(a, s.Position) <= AirfieldRange * Nm).OrderBy(a => Distance(a, s.Position)))
            {
                if (places >= AirfieldCount) break;
                var loading = airfields.GetAsync(airbase, ct);
                if (!loading.IsCompleted)
                {
                    places++;
                }
                else if (loading.IsCompletedSuccessfully && loading.Result is { } airfield)
                {
                    found.Add(airfield);
                    places++;
                }
            }
        }
        foreach (var working in new[] { s.Taxi?.Airfield, s.Tower?.Airfield, s.Approach?.Airfield, s.CheckInWith }.OfType<GroundAirfield>())
        {
            if (!found.Any(f => f.Airbase.Id == working.Airbase.Id)) found.Add(working);
        }

        var ids = found.Select(f => f.Airbase.Id).ToList();
        if (_order.TryGetValue(group, out var previous) && previous.Count == ids.Count && !previous.Except(ids).Any())
            return previous.Select(id => found.First(f => f.Airbase.Id == id)).ToList();
        var ordered = found.OrderBy(f => Distance(f.Airbase, s.Position)).ToList();
        _order[group] = ordered.Select(f => f.Airbase.Id).ToList();
        return ordered;
    }

    /// <summary>The calls the flight can make to this airfield now: (label, action, submenu or null).</summary>
    private IEnumerable<(string Label, string Action, string? Submenu)> Calls(GroundAirfield airfield, Situation s)
    {
        bool here(GroundAirfield? other) => other is not null && other.Airbase.Id == airfield.Airbase.Id;
        var position = s.Position;
        double miles = Distance(airfield.Airbase, position) / Nm;
        bool inAir = s.Lead.InAir;

        // Ground: at this airfield, on the ground.
        if (here(s.Taxi?.Airfield))
        {
            yield return ("Cancel taxi", "ground.cancel", null);
            yield return ("Say again (Ground)", "ground.repeat", null);
        }
        else if (!inAir && s.Taxi is null && (s.Tower is null || (here(s.Tower.Airfield) && s.Tower.Phase == TowerPhase.Landed)))
        {
            if (s.Tower is null) yield return ("Request taxi", "ground.taxi", null);
            if (!airfield.Layout.Parking.Any(p => p.Position.DistanceTo(position) < ParkedDistance))
                yield return ("Request taxi to parking", "ground.parking", null);
        }

        // Tower.
        if (here(s.Tower?.Airfield))
        {
            var t = s.Tower!;
            if (t.AwaitingCheckIn)
            {
                yield return t.Overhead ? ("Report initial", "tower.initial", null) : ("Check in (Tower)", "tower.landing", null);
            }
            else
            {
                if (t.Phase == TowerPhase.WaitingForDeparture)
                    foreach (var d in Departures) yield return (d.Label, $"tower.ready.{d.Id}", "Ready for departure");
                if (t.Phase == TowerPhase.Inbound && !t.ReportedFinal) yield return ("Report final", "tower.final", null);
                if (t.LastText != "") yield return ("Say again (Tower)", "tower.repeat", null);
            }
        }
        else if (!inAir)
        {
            bool holding = here(s.Taxi?.Airfield) ? s.Taxi!.Phase == TaxiPhase.HoldingShort
                : s.Taxi is null && airfield.Layout.Runways.Any(r => r.DistanceFrom(position) <= TowerControl.HoldingPointRange);
            if (holding && s.Tower is null)
                foreach (var d in Departures) yield return (d.Label, $"tower.ready.{d.Id}", "Ready for departure");
        }
        else if (s.Approach is not { IsArrival: true } && s.CheckInWith is null && Inbound(airfield, s.Lead))
        {
            // Arriving without Approach (or diverting here from another airfield); not while flying away from it.
            if (miles * Nm <= TowerControl.InboundRange) yield return ("Request landing", "tower.landing", null);
            if (miles * Nm <= TowerControl.InitialRange) yield return ("Report initial", "tower.initial", null);
        }

        // Approach: in the air.
        var client = here(s.Approach?.Airfield) ? s.Approach : null;
        if (inAir && client is { IsArrival: true })
        {
            if (client.Phase == ApproachPhase.Vectoring && client.Type != ApproachType.Overhead && !client.FieldInSight && miles <= FieldInSightRange)
                yield return ("Field in sight", "approach.field", null);
            yield return ("Cancel approach", "approach.cancel", null);
        }
        else if (inAir && !here(s.Tower?.Airfield))
        {
            if (here(s.CheckInWith) && s.Approach is null) yield return ("Check in (Approach)", "approach.checkin", null);
            if (miles <= ApproachControl.ProceduresOf(airfield).RequestRange)
            {
                foreach (var (id, label, type) in Approaches)
                {
                    if (type == ApproachType.Ils && !airfield.Radio.HasIls) continue;
                    if (type == ApproachType.Tacan && airfield.Radio.Tacan is null) continue;
                    yield return (label, $"approach.{id}", "Request approach");
                }
            }
        }
        if (client is not null && client.LastText != "") yield return ("Say again (Approach)", "approach.repeat", null);

        yield return ("ATIS", "atis", null);
        yield return ("Frequencies", "frequencies", null);
    }

    private static double Distance(AirbaseSummary a, Vec2 p) => new Vec2(a.X, a.Z).DistanceTo(p);

    /// <summary>Closing on the airfield (more than 30 knots towards it) or already over it (1.5 NM): what an arrival does, a departure does not.</summary>
    public static bool Inbound(GroundAirfield airfield, UnitState unit)
    {
        var toField = new Vec2(airfield.Airbase.X, airfield.Airbase.Z) - new Vec2(unit.X, unit.Z);
        double distance = toField.Length;
        if (distance < 1.5 * Nm) return true;
        return (toField.X * unit.Vx + toField.Z * unit.Vz) / distance > 15;
    }
}
