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

/// <summary>A departure taxi route ending at a runway holding point.</summary>
public sealed record DepartureRoute(TaxiRoute Route, Runway Runway, RunwayEnd End, HoldPoint Hold);

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

    /// <param name="dcsRunwayNames">Runway names reported by DCS getRunways(), used as hints for numbering.</param>
    public static Airfield Build(Rn5Network net, IEnumerable<string>? dcsRunwayNames = null, IEnumerable<ParkingSpot>? parking = null, AirfieldLabels? labels = null)
    {
        var graph = TaxiGraph.Build(net);
        var warnings = new List<string>();
        return new Airfield
        {
            Name = net.Name,
            Network = net,
            Graph = graph,
            Runways = RunwayFinder.Find(net, graph, dcsRunwayNames),
            Parking = parking?.ToList() ?? [],
            Labels = labels,
            TaxiwayNames = labels is null ? new Dictionary<int, string>() : TaxiwayNamer.Assign(net, graph, labels.Taxiways, warnings),
            LabelWarnings = warnings,
        };
    }

    /// <summary>
    /// The taxiways a route follows, in order, as a controller would say them: consecutive pieces of the
    /// same taxiway merged, names used for less than <paramref name="minLength"/> metres dropped (except
    /// the last), unnamed pieces skipped, runway crossings in between.
    /// </summary>
    public IReadOnlyList<ViaItem> Via(TaxiRoute route, double minLength = 40)
    {
        var runs = new List<(string? Name, double Length, Runway? Crossing)>();
        foreach (var step in route.Steps)
        {
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
            var geometry = runway.HoldingPointOn(full.Geometry);
            var route = full with { Geometry = geometry, Length = Polyline.Length(geometry) };
            if (best is null || route.Length < best.Route.Length)
                best = new DepartureRoute(route, runway, end, hold with { Position = geometry[^1], DistanceFromArm = full.Length - route.Length });
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

    /// <summary>The name of the labelled area (apron, parking) nearest to a point, if one is within <paramref name="maxDistance"/>.</summary>
    public string? AreaNear(Vec2 p, double maxDistance = 500) =>
        Labels?.Areas.Where(a => a.Position.DistanceTo(p) <= maxDistance).MinBy(a => a.Position.DistanceTo(p))?.Name;

    private static double DistanceFromThreshold(Runway runway, RunwayEnd end, HoldPoint hold) =>
        end == runway.EndA ? hold.AlongRunway : runway.Length - hold.AlongRunway;
}
