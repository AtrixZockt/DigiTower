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

        return new TaxiRoute(steps, geometry, Polyline.Length(geometry));
    }

    /// <summary>
    /// Route from any point to any point (e.g. from a runway exit to a parking spot): the route turns onto the
    /// taxiway piece closest to <paramref name="target"/>, follows it to the point nearest the target, and
    /// ends at the target.
    /// </summary>
    public static TaxiRoute? RouteToPoint(TaxiGraph graph, Vec2 start, Vec2 target, RouteOptions? options = null)
    {
        options ??= new RouteOptions();
        var entry = graph.ProjectOntoTaxiway(start);
        var goal = graph.ProjectOntoTaxiway(target);

        // Start and target on the same piece: roll along it.
        if (entry.Link.Id == goal.Link.Id)
        {
            var direct = entry.Along <= goal.Along
                ? Polyline.Slice(entry.Link.Points, entry.Along, goal.Along)
                : Polyline.Slice(entry.Link.Points, goal.Along, entry.Along).AsEnumerable().Reverse().ToList();
            List<Vec2> line = [start, .. direct, target];
            return new TaxiRoute([new RouteStep(entry.Link, entry.Link.A, entry.Link.B)], line, Polyline.Length(line));
        }

        int n = graph.Links.Count * 2;
        var cost = Enumerable.Repeat(double.MaxValue, n).ToArray();
        var previous = Enumerable.Repeat(-1, n).ToArray();
        var queue = new PriorityQueue<int, double>();
        int At(int s) => s % 2 == 0 ? graph.Links[s / 2].B : graph.Links[s / 2].A;

        int e = entry.Link.Id;
        cost[2 * e] = entry.Link.Length - entry.Along;
        cost[2 * e + 1] = entry.Along;
        foreach (int s in new[] { 2 * e, 2 * e + 1 }) queue.Enqueue(s, cost[s] + graph.Vertices[At(s)].Position.DistanceTo(goal.Point));

        // Finishing: arrive at one end of the goal piece, then roll along it to the goal point.
        double bestCost = double.MaxValue;
        int bestState = -1;
        bool bestFromA = false;
        var done = new bool[n];
        while (queue.TryDequeue(out int s, out double priority))
        {
            if (priority >= bestCost) break;
            if (done[s]) continue;
            done[s] = true;
            int v = At(s);
            var arriving = graph.Links[s / 2].DirArriving(v);
            if ((v == goal.Link.A || v == goal.Link.B) && s / 2 != goal.Link.Id &&
                TaxiGraph.TurnAngle(arriving, goal.Link.DirLeaving(v)) <= TaxiGraph.MaxTurn)
            {
                double finish = cost[s] + (v == goal.Link.A ? goal.Along : goal.Link.Length - goal.Along);
                if (finish < bestCost) (bestCost, bestState, bestFromA) = (finish, s, v == goal.Link.A);
            }
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
                    queue.Enqueue(t, c + graph.Vertices[At(t)].Position.DistanceTo(goal.Point));
                }
            }
        }
        if (bestState < 0) return null;

        var states = new List<int>();
        for (int s = bestState; s >= 0; s = previous[s]) states.Add(s);
        states.Reverse();
        var steps = states.Select(s => s % 2 == 0
            ? new RouteStep(graph.Links[s / 2], graph.Links[s / 2].A, graph.Links[s / 2].B)
            : new RouteStep(graph.Links[s / 2], graph.Links[s / 2].B, graph.Links[s / 2].A)).ToList();

        var geometry = steps[0].To == entry.Link.B
            ? Polyline.Slice(entry.Link.Points, entry.Along, entry.Link.Length)
            : Polyline.Slice(entry.Link.Points, 0, entry.Along).AsEnumerable().Reverse().ToList();
        geometry.Insert(0, start);
        foreach (var step in steps.Skip(1)) geometry.AddRange(step.Link.PointsFrom(step.From).Skip(1));
        var tail = bestFromA
            ? Polyline.Slice(goal.Link.Points, 0, goal.Along)
            : Polyline.Slice(goal.Link.Points, goal.Along, goal.Link.Length).AsEnumerable().Reverse().ToList();
        geometry.AddRange(tail.Skip(1));
        geometry.Add(target);
        steps.Add(bestFromA
            ? new RouteStep(goal.Link, goal.Link.A, goal.Link.B)
            : new RouteStep(goal.Link, goal.Link.B, goal.Link.A));
        geometry = Polyline.RemoveSpikes(geometry);

        return new TaxiRoute(steps, geometry, Polyline.Length(geometry));
    }

    /// <summary>
    /// The cheapest taxi cost (same rules as <see cref="Route"/>) from a start point to every vertex, for
    /// ranking many possible destinations with one search. Unreachable vertices are <see cref="double.MaxValue"/>.
    /// </summary>
    public static double[] CostsFrom(TaxiGraph graph, Vec2 start, RouteOptions? options = null)
    {
        options ??= new RouteOptions();
        var entry = graph.ProjectOntoTaxiway(start);
        int n = graph.Links.Count * 2;
        var cost = Enumerable.Repeat(double.MaxValue, n).ToArray();
        var vertexCost = Enumerable.Repeat(double.MaxValue, graph.Vertices.Count).ToArray();
        var queue = new PriorityQueue<int, double>();
        int At(int s) => s % 2 == 0 ? graph.Links[s / 2].B : graph.Links[s / 2].A;

        int e = entry.Link.Id;
        cost[2 * e] = entry.Link.Length - entry.Along;
        cost[2 * e + 1] = entry.Along;
        queue.Enqueue(2 * e, cost[2 * e]);
        queue.Enqueue(2 * e + 1, cost[2 * e + 1]);
        var done = new bool[n];
        while (queue.TryDequeue(out int s, out _))
        {
            if (done[s]) continue;
            done[s] = true;
            int v = At(s);
            vertexCost[v] = Math.Min(vertexCost[v], cost[s]);
            var arriving = graph.Links[s / 2].DirArriving(v);
            foreach (var link in graph.LinksOf(v))
            {
                if (link.Id == s / 2) continue;
                if (TaxiGraph.TurnAngle(arriving, link.DirLeaving(v)) > TaxiGraph.MaxTurn) continue;
                int t = 2 * link.Id + (v == link.A ? 0 : 1);
                double c = cost[s] + StepCost(link, options);
                if (!done[t] && c < cost[t])
                {
                    cost[t] = c;
                    queue.Enqueue(t, c);
                }
            }
        }
        return vertexCost;
    }

    private static double StepCost(TaxiLink link, RouteOptions o) => link switch
    {
        // Every way onto or off a runway costs half a crossing: on and off again close by is a crossing.
        { Kind: LinkKind.Runway } or { TouchesRunway: true } => link.Length * o.RunwayCostFactor + (link.TouchesRunway ? o.RunwayCrossingPenalty / 2 : 0),
        { CrossesRunway: true } => link.Length + o.RunwayCrossingPenalty,
        _ => link.Length,
    };
}
