namespace DcsAtc.Airfields.Taxi;

public sealed record RouteOptions
{
    /// <summary>Cost multiplier for rolling along a runway, so routes use taxiways when they can.</summary>
    public double RunwayCostFactor { get; init; } = 20;

    /// <summary>Extra cost (metres) per runway crossing, so routes avoid crossings when a short detour exists.</summary>
    public double RunwayCrossingPenalty { get; init; } = 500;
}

/// <summary>One link of a route, in travel direction.</summary>
public sealed record RouteStep(TaxiLink Link, int From, int To);

public sealed record TaxiRoute(IReadOnlyList<RouteStep> Steps, IReadOnlyList<Vec2> Geometry, double Length)
{
    /// <summary>Nodes where the route crosses a runway, in order.</summary>
    public IReadOnlyList<int> CrossingNodes { get; init; } = [];

    /// <summary>True if any part of the route rolls along a runway.</summary>
    public bool UsesRunway => Steps.Any(s => s.Link.Kind == LinkKind.Runway || s.Link.TouchesRunway);
}

/// <summary>
/// Shortest taxi routes (A*) on a <see cref="TaxiGraph"/>. The search state is a link plus its direction
/// of travel, so every transition can be checked against <see cref="TaxiGraph.MaxTurn"/>: routes never
/// turn around on the spot or take turns sharper than an aircraft can.
/// </summary>
public static class TaxiRouter
{
    /// <summary>
    /// Route from any point (e.g. a parking spot or an aircraft position) to a vertex, ending
    /// <paramref name="trimEnd"/> metres before that vertex (e.g. at a holding point).
    /// </summary>
    public static TaxiRoute? Route(TaxiGraph graph, Vec2 start, int target, RouteOptions? options = null, double trimEnd = 0)
    {
        options ??= new RouteOptions();
        var entry = graph.ProjectOntoTaxiway(start);
        var goal = graph.Vertices[target].Position;

        // State s = 2 × link + (0: travelled A→B, now at B | 1: travelled B→A, now at A).
        int n = graph.Links.Count * 2;
        var cost = Enumerable.Repeat(double.MaxValue, n).ToArray();
        var previous = Enumerable.Repeat(-1, n).ToArray();
        var queue = new PriorityQueue<int, double>();
        int At(int s) => s % 2 == 0 ? graph.Links[s / 2].B : graph.Links[s / 2].A;

        // Start on the closest taxiway piece, rolling either way.
        int e = entry.Link.Id;
        cost[2 * e] = entry.Link.Length - entry.Along;
        cost[2 * e + 1] = entry.Along;
        foreach (int s in new[] { 2 * e, 2 * e + 1 }) queue.Enqueue(s, cost[s] + graph.Vertices[At(s)].Position.DistanceTo(goal));

        var done = new bool[n];
        int reached = -1;
        while (queue.TryDequeue(out int s, out _))
        {
            if (done[s]) continue;
            done[s] = true;
            int v = At(s);
            if (v == target)
            {
                reached = s;
                break;
            }
            var arriving = graph.Links[s / 2].DirArriving(v);
            foreach (var link in graph.LinksOf(v))
            {
                if (link.Id == s / 2) continue;
                if (TaxiGraph.TurnAngle(arriving, link.DirLeaving(v)) > TaxiGraph.MaxTurn) continue;
                int t = 2 * link.Id + (v == link.A ? 0 : 1);
                if (done[t]) continue;
                double c = cost[s] + StepCost(link, options);
                if (c < cost[t])
                {
                    cost[t] = c;
                    previous[t] = s;
                    queue.Enqueue(t, c + graph.Vertices[At(t)].Position.DistanceTo(goal));
                }
            }
        }
        if (reached < 0) return null;

        var states = new List<int>();
        for (int s = reached; s >= 0; s = previous[s]) states.Add(s);
        states.Reverse();
        var steps = states.Select(s => s % 2 == 0
            ? new RouteStep(graph.Links[s / 2], graph.Links[s / 2].A, graph.Links[s / 2].B)
            : new RouteStep(graph.Links[s / 2], graph.Links[s / 2].B, graph.Links[s / 2].A)).ToList();

        // Geometry: from the start point along the entry piece, then the remaining steps.
        var first = steps[0];
        var geometry = first.To == entry.Link.B
            ? Polyline.Slice(entry.Link.Points, entry.Along, entry.Link.Length)
            : Polyline.Slice(entry.Link.Points, 0, entry.Along).AsEnumerable().Reverse().ToList();
        geometry.Insert(0, start);
        foreach (var step in steps.Skip(1)) geometry.AddRange(step.Link.PointsFrom(step.From).Skip(1));

        if (trimEnd > 0)
        {
            double total = Polyline.Length(geometry);
            geometry = Polyline.Slice(geometry, 0, Math.Max(0, total - trimEnd));
        }
        // Junction arms a metre apart can leave sub-metre back-and-forth wiggles; aircraft can't follow those.
        geometry = Polyline.RemoveSpikes(geometry);

        var crossings = steps.Where(s => s.Link.CrossesRunway).Select(s => graph.Vertices[s.To].Node).ToList();
        return new TaxiRoute(steps, geometry, Polyline.Length(geometry)) { CrossingNodes = crossings };
    }

    private static double StepCost(TaxiLink link, RouteOptions o) => link switch
    {
        { Kind: LinkKind.Runway } or { TouchesRunway: true } => link.Length * o.RunwayCostFactor,
        { CrossesRunway: true } => link.Length + o.RunwayCrossingPenalty,
        _ => link.Length,
    };
}
