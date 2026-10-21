using DcsAtc.Airfields.Rn5;
using DcsAtc.Airfields.Taxi;

namespace DcsAtc.Airfields;

/// <summary>A DCS parking spot (from getParking): Term_Index, Term_Type and position.</summary>
public sealed record ParkingSpot(int Index, int TermType, Vec2 Position);

/// <summary>One element of a taxi instruction: a taxiway to follow, or a runway to cross.</summary>
public sealed record ViaItem(string Name, Runway? CrossedRunway = null)
{
    public bool IsRunwayCrossing => CrossedRunway is not null;
}

/// <summary>
/// A departure taxi route ending at a runway holding point. <paramref name="Backtrack"/>: the holding points near the
/// threshold can only be reached along the runway, so the route ends at the holding point before it, and the
/// aircraft backtracks on the runway to the threshold (Tower's clearance).
/// </summary>
public sealed record DepartureRoute(TaxiRoute Route, Runway Runway, RunwayEnd End, HoldPoint Hold, bool Backtrack = false);

/// <summary>A route on a runway between two taxiways: steps <c>First</c> to <c>Last</c>, the metres it rolls along it, and whether that is a crossing.</summary>
public sealed record RunwayStretch(int First, int Last, Runway Runway, double Rolled, bool IsCrossing);

/// <summary>Everything we know about one airfield's ground layout.</summary>
public sealed class Airfield
{
    public required string Name { get; init; }
    public required Rn5Network Network { get; init; }
    public required TaxiGraph Graph { get; init; }
    public required IReadOnlyList<Runway> Runways { get; init; }
    public required IReadOnlyList<ParkingSpot> Parking { get; init; }

    /// <summary>Taxiway name of each named DCS edge (by edge index), from <see cref="Labels"/>.</summary>
    public IReadOnlyDictionary<int, string> TaxiwayNames { get; init; } = new Dictionary<int, string>();

    /// <summary>Chart data (taxiway labels, areas, frequencies), if this airfield has been labelled.</summary>
    public AirfieldLabels? Labels { get; init; }

    /// <summary>Problems found while applying <see cref="Labels"/> (waypoints off the network, conflicts).</summary>
    public IReadOnlyList<string> LabelWarnings { get; init; } = [];

    /// <summary>A position this close to the airfield's taxiways, parking or runways counts as at the airfield (metres).</summary>
    public const double Margin = 500;

    /// <summary>The area the airfield covers: its taxi network, parking spots and runway thresholds (min and max corner).</summary>
    public (Vec2 Min, Vec2 Max) Bounds { get; init; }

    /// <summary>Whether a position is at the airfield: within <see cref="Margin"/> of the area it covers.</summary>
    public bool Covers(Vec2 p) =>
        p.X >= Bounds.Min.X - Margin && p.X <= Bounds.Max.X + Margin && p.Z >= Bounds.Min.Z - Margin && p.Z <= Bounds.Max.Z + Margin;

    /// <param name="dcsRunwayNames">Runway names reported by DCS getRunways(), used as hints for numbering.</param>
    public static Airfield Build(Rn5Network net, IEnumerable<string>? dcsRunwayNames = null, IEnumerable<ParkingSpot>? parking = null, AirfieldLabels? labels = null)
    {
        var graph = TaxiGraph.Build(net);
        var warnings = new List<string>();
        var runways = RunwayFinder.Find(net, graph, dcsRunwayNames);
        var spots = parking?.ToList() ?? [];
        var points = net.Edges.SelectMany(e => e.Points).Concat(spots.Select(s => s.Position))
            .Concat(runways.SelectMany(r => new[] { r.EndA.Threshold, r.EndB.Threshold })).ToList();
        return new Airfield
        {
            Name = net.Name,
            Network = net,
            Graph = graph,
            Runways = runways,
            Parking = spots,
            Bounds = points.Count == 0 ? default
                : (new Vec2(points.Min(p => p.X), points.Min(p => p.Z)), new Vec2(points.Max(p => p.X), points.Max(p => p.Z))),
            Labels = labels,
            TaxiwayNames = labels is null ? new Dictionary<int, string>() : TaxiwayNamer.Assign(net, graph, labels.Taxiways, warnings),
            LabelWarnings = warnings,
        };
    }

    /// <summary>
    /// The taxiways a route follows, in order, as a controller would say them: consecutive pieces of the
    /// same taxiway merged, names used for less than <paramref name="minLength"/> metres dropped (except
    /// the last), unnamed pieces skipped, runway crossings in between. Pieces of other taxiways shorter than
    /// <paramref name="maxSideStep"/> in all between two pieces of the same one are left out: an angled exit
    /// overlapping a parallel taxiway makes "A, A6, A, A3" otherwise, said "A, A3".
    /// </summary>
    public IReadOnlyList<ViaItem> Via(TaxiRoute route, double minLength = 40, double maxSideStep = 200)
    {
        var steps = route.Steps;
        var crossings = RunwayStretches(steps).Where(s => s.IsCrossing).ToDictionary(s => s.First);
        var runs = new List<(string? Name, double Length, Runway? Crossing)>();
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (crossings.TryGetValue(i, out var stretch))
            {
                runs.Add((stretch.Runway.Name, 0, stretch.Runway));
                i = stretch.Last;
                continue;
            }
            if (step.Link.CrossesRunway)
            {
                int node = Graph.Vertices[step.To].Node;
                var runway = Runways.FirstOrDefault(r => r.Nodes.Contains(node));
                if (runway is not null) runs.Add((runway.Name, 0, runway));
                continue;
            }
            if (step.Link.IsJunction) continue;
            string? name = NameOf(step.Link);
            if (runs.Count > 0 && runs[^1].Crossing is null && runs[^1].Name == name)
                runs[^1] = (name, runs[^1].Length + step.Link.Length, null);
            else
                runs.Add((name, step.Link.Length, null));
        }

        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Name is null || runs[i].Crossing is not null) continue;
            double between = 0;
            for (int j = i + 1; j < runs.Count && runs[j].Crossing is null && between < maxSideStep; j++)
            {
                if (runs[j].Name != runs[i].Name)
                {
                    between += runs[j].Length;
                    continue;
                }
                if (between < maxSideStep)
                {
                    runs[i] = (runs[i].Name, runs.Skip(i).Take(j - i + 1).Sum(r => r.Length), null);
                    runs.RemoveRange(i + 1, j - i);
                    i--;
                }
                break;
            }
        }

        int lastNamed = runs.FindLastIndex(r => r.Name is not null && r.Crossing is null);
        var via = new List<ViaItem>();
        for (int i = 0; i < runs.Count; i++)
        {
            var (name, length, crossing) = runs[i];
            if (name is null) continue;
            if (crossing is null && length < minLength && i != lastNamed) continue;
            var item = new ViaItem(name, crossing);
            if (via.Count == 0 || via[^1] != item) via.Add(item);
        }
        return via;
    }

    /// <summary>A runway is 45-60 m wide: crossing it at an angle rolls up to about this far along it (metres).</summary>
    public const double MaxCrossingRoll = 100;

    /// <summary>A crossing to the other side may roll this far along the runway when DCS offsets the way off it (metres).</summary>
    public const double MaxOffsetCrossingRoll = 200;

    /// <summary>A departure route that leaves its runway for at most this far to the holding point is a backtrack (metres).</summary>
    public const double MaxAfterBacktrack = 1500;

    /// <summary>
    /// Where a route is on a runway between two taxiways (steps <c>First</c> to <c>Last</c>), how far it rolls along it,
    /// and whether that is a crossing: up to <see cref="MaxCrossingRoll"/>, or <see cref="MaxOffsetCrossingRoll"/> to
    /// the other side (DCS draws some crossings as a way onto the runway and off it again, not as a crossing node).
    /// Anything longer taxis along the runway: a backtrack. <paramref name="toTheEnd"/>: a stretch the route ends with
    /// counts too (a route to a holding point that gets there from the runway).
    /// </summary>
    public IReadOnlyList<RunwayStretch> RunwayStretches(IReadOnlyList<RouteStep> steps, bool toTheEnd = false)
    {
        static bool OnRunway(TaxiLink l) => l.Kind == LinkKind.Runway || l.TouchesRunway;
        var stretches = new List<RunwayStretch>();
        for (int i = 0; i < steps.Count; i++)
        {
            if (!OnRunway(steps[i].Link)) continue;
            int last = i;
            while (last + 1 < steps.Count && OnRunway(steps[last + 1].Link)) last++;
            var on = steps.Skip(i).Take(last - i + 1).Where(s => s.Link.Kind == LinkKind.Runway).ToList();
            var runway = on.Select(s => s.Link.Rn5Edge).OfType<int>().Select(e => Network.Edges[e])
                .Select(e => Runways.FirstOrDefault(r => r.Nodes.Contains(e.NodeA) || r.Nodes.Contains(e.NodeB))).FirstOrDefault(r => r is not null);
            if (i > 0 && (last + 1 < steps.Count || toTheEnd) && runway is not null)
            {
                double rolled = on.Sum(s => s.Link.Length);
                bool across = last + 1 < steps.Count && Side(runway, steps, i - 1, -1) * Side(runway, steps, last + 1, +1) < 0;
                stretches.Add(new RunwayStretch(i, last, runway, rolled, rolled <= MaxCrossingRoll || (across && rolled <= MaxOffsetCrossingRoll)));
            }
            i = last;
        }
        return stretches;
    }

    /// <summary>The side of the runway (-1, 0, +1) of the route's first point more than 25 m off it, from step <paramref name="from"/> on in <paramref name="direction"/>.</summary>
    private int Side(Runway runway, IReadOnlyList<RouteStep> steps, int from, int direction)
    {
        for (int k = from; k >= 0 && k < steps.Count; k += direction)
        {
            double side = (Graph.Vertices[direction < 0 ? steps[k].From : steps[k].To].Position - runway.EndA.Threshold).Dot(runway.Axis.Left);
            if (Math.Abs(side) > 25) return Math.Sign(side);
        }
        return 0;
    }

    /// <summary>The taxiway name of a route step's link, if known.</summary>
    public string? NameOf(TaxiLink link) => link.Rn5Edge is int e && TaxiwayNames.TryGetValue(e, out var name) ? name : null;

    public (Runway Runway, RunwayEnd End)? FindRunwayEnd(string name)
    {
        foreach (var r in Runways)
            if (r.End(name) is { } end) return (r, end);
        return null;
    }

    /// <summary>
    /// The runway ends in use for a wind: those with the most headwind. Parallel runways tie, so all of
    /// them are returned (the caller picks, e.g. by taxi distance). In calm wind (below
    /// <paramref name="calm"/> m/s) the ends of every runway's lower-numbered direction are used.
    /// </summary>
    public IReadOnlyList<(Runway Runway, RunwayEnd End)> ActiveEnds(double windFrom, double windSpeed, double calm = 1.5)
    {
        var ends = Runways.SelectMany(r => new[] { (r, r.EndA), (r, r.EndB) }).ToList();
        if (ends.Count == 0) return [];
        if (windSpeed < calm) return Runways.Select(r => (r, r.EndA)).ToList();
        double Headwind((Runway, RunwayEnd End) e) => windSpeed * Math.Cos((windFrom - e.End.TrueHeading) * Math.PI / 180);
        double best = ends.Max(Headwind);
        return ends.Where(e => Headwind(e) >= best - 0.5).ToList();
    }

    /// <summary>
    /// Taxi route from <paramref name="from"/> to a holding point for a full-length departure from
    /// <paramref name="runwayEnd"/>: the shortest route to any holding point at most
    /// <paramref name="maxFromThreshold"/> metres further down the runway than the one nearest the threshold.
    /// (Intersection departures: pass a larger value.)
    /// </summary>
    public DepartureRoute? RouteToRunway(Vec2 from, string runwayEnd, RouteOptions? options = null, double maxFromThreshold = 250)
    {
        if (FindRunwayEnd(runwayEnd) is not { } found) return null;
        var (runway, end) = found;
        var holds = runway.DepartureHoldPoints(end.Name).ToList();
        if (holds.Count == 0) return null;
        double nearest = DistanceFromThreshold(runway, end, holds[0]);
        var candidates = holds.Where(h => DistanceFromThreshold(runway, end, h) <= nearest + maxFromThreshold).ToList();

        DepartureRoute? best = null;
        foreach (var hold in candidates)
        {
            // Route onto the runway, then cut the route at the holding point: walking back along it, the first
            // point 90 m from the runway. (An entry's own DCS edge can be a few metres long and lie along the
            // runway axis, e.g. when entering behind the threshold, so it may not contain the holding point.)
            var full = TaxiRouter.Route(Graph, from, hold.ArmVertex, options);
            if (full is null) continue;
            var steps = full.Steps;
            var before = full.Geometry;
            // Along this runway to get there (no taxiway to the threshold): stop at the holding point before the runway.
            // Not when the route goes a long way on taxiways after its last time on it: the runway was only a way
            // through (from a stand at its end).
            var along = RunwayStretches(steps, toTheEnd: true).Where(s => !s.IsCrossing && s.Runway == runway).ToList();
            bool backtrack = along.Count > 0 && steps.Skip(along[^1].Last + 1).Sum(x => x.Link.Length) <= MaxAfterBacktrack;
            if (backtrack)
            {
                steps = steps.Take(along[0].First).ToList();
                before = Polyline.Slice(full.Geometry, 0, Polyline.Project(full.Geometry, Graph.Vertices[full.Steps[along[0].First].From].Position).Along);
            }
            var geometry = runway.HoldingPointOn(before);
            var route = full with { Steps = steps, Geometry = geometry, Length = Polyline.Length(geometry) };
            // A route without a backtrack first, then the shortest.
            if (best is null || (best.Backtrack && !backtrack) || (best.Backtrack == backtrack && route.Length < best.Route.Length))
                best = new DepartureRoute(route, runway, end, hold with { Position = geometry[^1], DistanceFromArm = full.Length - route.Length }, backtrack);
        }
        return best;
    }

    /// <summary>
    /// Whether a DCS parking spot suits the aircraft. Terminal types (MOOSE AIRBASE.TerminalType): 16 runway,
    /// 40 helicopter only, 68 shelter, 72 open medium, 104 open big, 176 open medium or big, 216 helicopter
    /// usable, 244 fighter.
    /// </summary>
    public static bool SpotSuits(ParkingSpot spot, bool helicopter) => spot.TermType switch
    {
        16 => false,
        40 => helicopter,
        68 => !helicopter,
        _ => true,
    };

    /// <summary>
    /// Taxi route to the free parking spot (no aircraft within 25 m of it) that is quickest to reach:
    /// every suitable spot is ranked by its taxi cost (one search over the whole network), then the
    /// <paramref name="candidates"/> best are routed exactly.
    /// </summary>
    public (ParkingSpot Spot, TaxiRoute Route)? RouteToParking(Vec2 from, bool helicopter, IEnumerable<Vec2> otherAircraft, int candidates = 3)
    {
        var occupied = otherAircraft.ToList();
        var costs = TaxiRouter.CostsFrom(Graph, from);
        return Parking
            .Where(s => SpotSuits(s, helicopter) && !occupied.Any(a => a.DistanceTo(s.Position) < 25))
            .Select(s => (Spot: s, Cost: ApproximateCost(costs, s)))
            .Where(c => c.Cost < double.MaxValue)
            .OrderBy(c => c.Cost)
            .Take(candidates)
            .Select(c => (c.Spot, Route: TaxiRouter.RouteToPoint(Graph, from, c.Spot.Position)))
            .Where(c => c.Route is not null)
            .Select(c => (c.Spot, Route: c.Route!))
            .OrderBy(c => c.Route.Length)
            .Cast<(ParkingSpot, TaxiRoute)?>()
            .FirstOrDefault();
    }

    private readonly Dictionary<int, (TaxiLink Link, double Along, double Distance)> _spotEntries = [];

    /// <summary>Taxi cost to a spot: to either end of the taxiway piece nearest the spot, along it, then across to the spot.</summary>
    private double ApproximateCost(double[] costs, ParkingSpot spot)
    {
        if (!_spotEntries.TryGetValue(spot.Index, out var entry))
        {
            var p = Graph.ProjectOntoTaxiway(spot.Position);
            _spotEntries[spot.Index] = entry = (p.Link, p.Along, p.Distance);
        }
        double viaA = costs[entry.Link.A] == double.MaxValue ? double.MaxValue : costs[entry.Link.A] + entry.Along;
        double viaB = costs[entry.Link.B] == double.MaxValue ? double.MaxValue : costs[entry.Link.B] + entry.Link.Length - entry.Along;
        double best = Math.Min(viaA, viaB);
        return best == double.MaxValue ? best : best + entry.Distance;
    }

    private readonly Dictionary<(Runway, int), bool> _exitLeadsToParking = [];

    /// <summary>
    /// After landing on <paramref name="landing"/>, at <paramref name="position"/> on the runway: when no way off ahead
    /// leads to the parking without the runway again (a turn pad, a loop back onto it), the way off behind to backtrack
    /// to (its holding point, and its taxiway name if known). Null when one ahead does, or without parking spots to judge by.
    /// </summary>
    public (HoldPoint Exit, string? Name)? BacktrackToVacate(Runway runway, RunwayEnd landing, Vec2 position)
    {
        if (Parking.Count == 0) return null;
        double at = runway.AlongFrom(landing, position);
        double AlongOf(HoldPoint h) => landing == runway.EndA ? h.AlongRunway : runway.Length - h.AlongRunway;
        bool LeadsToParking(HoldPoint h)
        {
            if (!_exitLeadsToParking.TryGetValue((runway, h.ArmVertex), out bool leads))
                _exitLeadsToParking[(runway, h.ArmVertex)] = leads = RouteToParking(h.Position, helicopter: false, []) is { } p
                                                                     && RunwayStretches(p.Route.Steps).All(s => s.IsCrossing);
            return leads;
        }
        var exits = runway.HoldPoints.OrderBy(AlongOf).ToList();
        if (exits.Where(h => AlongOf(h) >= at - 30).Any(LeadsToParking)) return null;
        var behind = exits.Where(h => AlongOf(h) < at - 30).Reverse().FirstOrDefault(LeadsToParking);
        return behind is null ? null : (behind, TaxiwayNames.TryGetValue(behind.Edge.Index, out var name) ? name : null);
    }

    /// <summary>The name of the labelled area (apron, parking) nearest to a point, if one is within <paramref name="maxDistance"/>.</summary>
    public string? AreaNear(Vec2 p, double maxDistance = 500) =>
        Labels?.Areas.Where(a => a.Position.DistanceTo(p) <= maxDistance).MinBy(a => a.Position.DistanceTo(p))?.Name;

    private static double DistanceFromThreshold(Runway runway, RunwayEnd end, HoldPoint hold) =>
        end == runway.EndA ? hold.AlongRunway : runway.Length - hold.AlongRunway;
}
