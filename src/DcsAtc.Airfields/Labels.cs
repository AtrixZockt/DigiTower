using System.Text.Json;
using DcsAtc.Airfields.Rn5;
using DcsAtc.Airfields.Taxi;

namespace DcsAtc.Airfields;

/// <summary>
/// A taxiway name and waypoints on that taxiway's DCS line, in DCS coordinates ([x, z] pairs). One
/// waypoint names the edge under it; several name everything the network route between them uses.
/// </summary>
public sealed record TaxiwayPath(string Name, IReadOnlyList<double[]> Path)
{
    public IReadOnlyList<Vec2> Points => Path.Select(p => new Vec2(p[0], p[1])).ToList();
}

/// <summary>A named apron or parking area, in DCS coordinates.</summary>
public sealed record AreaLabel(string Name, double X, double Z)
{
    public Vec2 Position => new(X, Z);
}

public sealed record AirfieldFrequencies(double? Atis, double? Ground, double? Tower, double? Approach);

/// <summary>How Tower sends departures off. Generic military defaults; each airfield can tune them in its labels file.</summary>
public sealed record DepartureProcedures
{
    public static readonly DepartureProcedures Default = new();

    /// <summary>Behind departing traffic: "climb and maintain 3000 feet until advised" (feet MSL).</summary>
    public double InitialAltitude { get; init; } = 3000;
    /// <summary>Under pattern traffic: "maintain at or below 500 feet (AGL) until 3 miles (NM from the runway)".</summary>
    public double StayLowAltitude { get; init; } = 500;
    public double StayLowDistance { get; init; } = 3;
    /// <summary>Airborne traffic within this distance of the runway (NM), between floor and top (feet AGL), is pattern traffic.</summary>
    public double PatternRadius { get; init; } = 5;
    public double PatternFloor { get; init; } = 700;
    public double PatternTop { get; init; } = 3500;
    /// <summary>Departing traffic ahead closer than this (NM) restricts the next departure until it is this far away.</summary>
    public double Separation { get; init; } = 5;
    /// <summary>Closed traffic side per runway end ("left" or "right"); left where not given.</summary>
    public IReadOnlyDictionary<string, string> ClosedTraffic { get; init; } = new Dictionary<string, string>();
}

/// <summary>A point known both on the chart (pixels) and in DCS (e.g. a runway end).</summary>
public sealed record ControlPoint(double[] Chart, double X, double Z);

public sealed record Georeference(IReadOnlyList<ControlPoint> ControlPoints)
{
    public ChartAffine Affine() => ChartAffine.Fit(ControlPoints.Select(c => (c.Chart[0], c.Chart[1], new Vec2(c.X, c.Z))).ToList());
}

/// <summary>
/// Hand-curated data for one airfield, stored as <c>data/airfields/&lt;theatre&gt;/&lt;rn5 name&gt;.labels.json</c>:
/// taxiway names (as waypoint paths on the DCS network), named areas and frequencies, with the chart
/// they came from and how it was georeferenced.
/// </summary>
public sealed record AirfieldLabels
{
    public string Airfield { get; init; } = "";
    public string Rn5 { get; init; } = "";
    public string Source { get; init; } = "";
    public AirfieldFrequencies? Frequencies { get; init; }
    /// <summary>How the chart was fitted to DCS coordinates.</summary>
    public Georeference? Georeference { get; init; }
    public IReadOnlyList<TaxiwayPath> Taxiways { get; init; } = [];
    public IReadOnlyList<AreaLabel> Areas { get; init; } = [];
    public DepartureProcedures Departure { get; init; } = DepartureProcedures.Default;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static AirfieldLabels Load(string path) =>
        JsonSerializer.Deserialize<AirfieldLabels>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"empty labels file {path}");
}

/// <summary>
/// Names the taxiway edges of a DCS network from <see cref="TaxiwayPath"/>s: a single waypoint names
/// the taxiway edge under it; consecutive waypoints name every taxiway edge on the network route between
/// them. Names then extend along straight continuations for at most <c>extend</c> metres to close small
/// gaps (e.g. the last piece of a connector before the runway), never across a runway or into parking
/// lead-ins.
/// </summary>
public static class TaxiwayNamer
{
    /// <summary>A waypoint further than this from any taxiway is reported.</summary>
    public const double MaxWaypointDistance = 40;

    public static Dictionary<int, string> Assign(Rn5Network net, TaxiGraph graph, IEnumerable<TaxiwayPath> paths, List<string>? warnings = null,
        double extend = 300, double maxTurn = 25)
    {
        var deadEnds = net.Nodes.Where(n => n.Degree == 1).Select(n => n.Id).ToHashSet();
        var runwayNodes = net.Edges.Where(e => e.IsRunway).SelectMany(e => new[] { e.NodeA, e.NodeB }).ToHashSet();
        bool Nameable(Rn5Edge e) => !e.IsRunway && !deadEnds.Contains(e.NodeA) && !deadEnds.Contains(e.NodeB);
        var names = new Dictionary<int, string>();

        void Name(int edge, string name)
        {
            if (!Nameable(net.Edges[edge])) return;
            if (names.TryGetValue(edge, out var existing) && existing != name)
            {
                warnings?.Add($"edge {edge} named both {existing} and {name}; keeping {existing}");
                return;
            }
            names[edge] = name;
        }

        Rn5Edge? EdgeUnder(Vec2 p, string name)
        {
            var best = net.Edges.Where(Nameable).MinBy(e => Polyline.Project(e.Points, p).Distance);
            double d = best is null ? double.MaxValue : Polyline.Project(best.Points, p).Distance;
            if (d > MaxWaypointDistance)
            {
                warnings?.Add($"{name}: waypoint ({p.X:F0}, {p.Z:F0}) is {d:F0} m from the nearest taxiway");
                return null;
            }
            return best;
        }

        var options = new RouteOptions { RunwayCostFactor = 1000, RunwayCrossingPenalty = 5000 };
        foreach (var path in paths)
        {
            var points = path.Points;
            var edges = points.Select(p => EdgeUnder(p, path.Name)).ToList();
            foreach (var e in edges.OfType<Rn5Edge>()) Name(e.Index, path.Name);
            for (int i = 1; i < points.Count; i++)
            {
                if (edges[i] is not { } target) continue;
                // Route towards either end of the next waypoint's edge and keep the part up to where the
                // route passes that waypoint (edges overlap, so the route may reach it before the edge's end).
                var candidates = new[] { graph.VertexOfArm(target.NodeA, target.PortA), graph.VertexOfArm(target.NodeB, target.PortB) }
                    .Select(v => TaxiRouter.Route(graph, points[i - 1], v, options))
                    .OfType<TaxiRoute>()
                    .Select(r => UpToWaypoint(r, points[i]))
                    .OfType<(List<RouteStep> Steps, double Length)>()
                    .ToList();
                if (candidates.Count == 0)
                {
                    warnings?.Add($"{path.Name}: no route between waypoints {i} and {i + 1}");
                    continue;
                }
                var (steps, length) = candidates.MinBy(c => c.Length);
                double straight = points[i - 1].DistanceTo(points[i]);
                if (length > 1.6 * straight + 150)
                {
                    // A detour means the waypoints are not on one taxiway (or the network does not connect them directly).
                    warnings?.Add($"{path.Name}: route between waypoints {i} and {i + 1} is {length:F0} m for {straight:F0} m straight; not named");
                    continue;
                }
                foreach (var step in steps)
                    if (step.Link.Rn5Edge is int e && !step.Link.IsJunction) Name(e, path.Name);
            }
        }

        Extend(net, names, Nameable, runwayNodes, extend, maxTurn);
        return names;
    }

    /// <summary>The steps of a route up to the first one passing within 30 m of <paramref name="waypoint"/>, and their length.</summary>
    private static (List<RouteStep> Steps, double Length)? UpToWaypoint(TaxiRoute route, Vec2 waypoint)
    {
        var steps = new List<RouteStep>();
        double length = 0;
        foreach (var step in route.Steps)
        {
            steps.Add(step);
            var (_, along, distance) = Polyline.Project(step.Link.PointsFrom(step.From), waypoint);
            if (distance <= 30) return (steps, length + along);
            length += step.Link.Length;
        }
        return null;
    }

    /// <summary>Extends names along straight continuations into unnamed edges, up to <paramref name="limit"/> metres.</summary>
    private static void Extend(Rn5Network net, Dictionary<int, string> names, Func<Rn5Edge, bool> nameable, HashSet<int> runwayNodes, double limit, double maxTurn)
    {
        var armsAt = new Dictionary<int, List<(Rn5Edge Edge, bool AtStart)>>();
        foreach (var e in net.Edges)
        {
            foreach (var (node, atStart) in new[] { (e.NodeA, true), (e.NodeB, false) })
            {
                if (!armsAt.TryGetValue(node, out var list)) armsAt[node] = list = [];
                list.Add((e, atStart));
            }
        }

        var queue = new PriorityQueue<(int Edge, string Name), double>();
        foreach (var (edge, name) in names) queue.Enqueue((edge, name), 0);
        var reached = new Dictionary<int, double>(names.Keys.Select(k => KeyValuePair.Create(k, 0.0)));
        var assigned = new Dictionary<int, string>(names);

        while (queue.TryDequeue(out var item, out double cost))
        {
            var edge = net.Edges[item.Edge];
            foreach (bool atEnd in new[] { true, false })
            {
                int node = atEnd ? edge.NodeB : edge.NodeA;
                if (runwayNodes.Contains(node)) continue;
                Vec2 arrive = Direction(edge.Points, fromStart: !atEnd) * -1;
                foreach (var (next, nextAtStart) in armsAt[node])
                {
                    if (assigned.ContainsKey(next.Index) || !nameable(next)) continue;
                    if (Angle(arrive, Direction(next.Points, fromStart: nextAtStart)) > maxTurn) continue;
                    double c = cost + next.Length;
                    if (c > limit || (reached.TryGetValue(next.Index, out double r) && r <= c)) continue;
                    reached[next.Index] = c;
                    assigned[next.Index] = item.Name;
                    queue.Enqueue((next.Index, item.Name), c);
                }
            }
        }
        foreach (var (edge, name) in assigned) names[edge] = name;
    }

    /// <summary>Direction leaving one end of a polyline, measured over its first ~5 m.</summary>
    private static Vec2 Direction(IReadOnlyList<Vec2> points, bool fromStart)
    {
        var pts = fromStart ? points : points.Reverse().ToList();
        for (int k = 1; k < pts.Count; k++)
            if (pts[k].DistanceTo(pts[0]) > 5) return pts[k] - pts[0];
        return pts[^1] - pts[0];
    }

    private static double Angle(Vec2 a, Vec2 b)
    {
        if (a.Length == 0 || b.Length == 0) return 180;
        return Math.Acos(Math.Clamp(a.Normalized.Dot(b.Normalized), -1, 1)) * 180 / Math.PI;
    }
}

/// <summary>An affine map from chart pixels to DCS coordinates: x = A·px + B·py + C, z = D·px + E·py + F.</summary>
public sealed record ChartAffine(double A, double B, double C, double D, double E, double F)
{
    public Vec2 Apply(double px, double py) => new(A * px + B * py + C, D * px + E * py + F);

    /// <summary>Least-squares fit from at least three (chart pixel, DCS position) pairs.</summary>
    public static ChartAffine Fit(IReadOnlyList<(double Px, double Py, Vec2 Dcs)> points)
    {
        // Normal equations for [a b c] with rows [px py 1], once for x and once for z.
        var m = new double[3, 3];
        var bx = new double[3];
        var bz = new double[3];
        foreach (var (px, py, dcs) in points)
        {
            double[] row = [px, py, 1];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++) m[i, j] += row[i] * row[j];
                bx[i] += row[i] * dcs.X;
                bz[i] += row[i] * dcs.Z;
            }
        }
        var (a, b, c) = Solve3(m, bx);
        var (d, e, f) = Solve3(m, bz);
        return new ChartAffine(a, b, c, d, e, f);
    }

    private static (double, double, double) Solve3(double[,] m, double[] r)
    {
        double Det(double[,] x) =>
            x[0, 0] * (x[1, 1] * x[2, 2] - x[1, 2] * x[2, 1]) - x[0, 1] * (x[1, 0] * x[2, 2] - x[1, 2] * x[2, 0]) + x[0, 2] * (x[1, 0] * x[2, 1] - x[1, 1] * x[2, 0]);
        double det = Det(m);
        double Col(int c)
        {
            var t = (double[,])m.Clone();
            for (int i = 0; i < 3; i++) t[i, c] = r[i];
            return Det(t) / det;
        }
        return (Col(0), Col(1), Col(2));
    }
}
