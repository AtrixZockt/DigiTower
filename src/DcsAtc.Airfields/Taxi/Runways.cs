using System.Text.RegularExpressions;
using DcsAtc.Airfields.Rn5;

namespace DcsAtc.Airfields.Taxi;

/// <summary>One end of a runway, as named by its painted designator (e.g. "13L" is where you line up to take off on heading ~130).</summary>
public sealed record RunwayEnd(string Name, Vec2 Threshold, double TrueHeading);

/// <summary>A taxiway entry to a runway and where an aircraft on it must hold short.</summary>
/// <param name="ArmVertex">The taxiway arm of the runway node this taxiway enters (route target).</param>
/// <param name="Position">The holding point, <see cref="RunwayFinder.DefaultHoldDistance"/> from the runway along the
/// entry's own edge. Routes recompute it along the actual route (see <see cref="Runway.HoldingPointOn"/>).</param>
/// <param name="DistanceFromArm">Distance along the entry from the arm to the holding point.</param>
/// <param name="AlongRunway">Where the entry joins the runway, along the axis from end A's threshold.</param>
public sealed record HoldPoint(int ArmVertex, Rn5Edge Edge, Vec2 Position, double DistanceFromArm, double AlongRunway);

public sealed class Runway
{
    public required int Index { get; init; }
    public required IReadOnlyList<int> Nodes { get; init; }
    /// <summary>End A: lining up here you take off towards end B.</summary>
    public required RunwayEnd EndA { get; init; }
    public required RunwayEnd EndB { get; init; }
    public required IReadOnlyList<HoldPoint> HoldPoints { get; init; }
    /// <summary>Width in metres (from the DCS runway type, e.g. "vpp_60"; 45 if unknown).</summary>
    public double Width { get; init; } = 45;

    public string Name => $"{EndA.Name}/{EndB.Name}";

    public RunwayEnd Opposite(RunwayEnd end) => end == EndA ? EndB : EndA;

    /// <summary>Distance along the runway from <paramref name="end"/>'s threshold (negative before it).</summary>
    public double AlongFrom(RunwayEnd end, Vec2 p) => (p - end.Threshold).Dot(Vec2.FromHeading(end.TrueHeading));

    /// <summary>Distance from the centerline (extended beyond the ends).</summary>
    public double FromCenterline(Vec2 p) => Math.Abs((p - EndA.Threshold).Dot(Axis.Left));

    /// <summary>Whether a point is on the runway surface, with <paramref name="margin"/> metres around it.</summary>
    public bool IsOnRunway(Vec2 p, double margin = 10)
    {
        double along = AlongFrom(EndA, p);
        return FromCenterline(p) <= Width / 2 + margin && along >= -margin && along <= Length + margin;
    }
    public Vec2 Axis => (EndB.Threshold - EndA.Threshold).Normalized;
    public double Length => EndA.Threshold.DistanceTo(EndB.Threshold);
    public Vec2 Center => (EndA.Threshold + EndB.Threshold) * 0.5;

    /// <summary>Distance from the runway: from its centerline between the thresholds, or behind an end from that end.</summary>
    public double DistanceFrom(Vec2 p) => Polyline.Project([EndA.Threshold, EndB.Threshold], p).Distance;

    /// <summary>
    /// The holding point on a route that ends on this runway: walking back from the runway, the first point
    /// <paramref name="holdDistance"/> metres from it. Returns the route geometry up to that point.
    /// </summary>
    public List<Vec2> HoldingPointOn(IReadOnlyList<Vec2> route, double holdDistance = RunwayFinder.DefaultHoldDistance)
    {
        int k = route.Count - 1;
        while (k >= 0 && DistanceFrom(route[k]) < holdDistance) k--;
        if (k < 0) return [route[0]];                    // the whole route is within the holding distance
        if (k == route.Count - 1) return route.ToList(); // the route never comes close
        // The holding point lies between route[k] (far enough) and route[k + 1] (too close).
        Vec2 a = route[k], b = route[k + 1];
        double lo = 0, hi = 1;
        for (int i = 0; i < 30; i++)
        {
            double mid = (lo + hi) / 2;
            if (DistanceFrom(a + (b - a) * mid) >= holdDistance) lo = mid; else hi = mid;
        }
        var result = route.Take(k + 1).ToList();
        result.Add(a + (b - a) * lo);
        return result;
    }

    public RunwayEnd? End(string name) =>
        string.Equals(EndA.Name, name, StringComparison.OrdinalIgnoreCase) ? EndA :
        string.Equals(EndB.Name, name, StringComparison.OrdinalIgnoreCase) ? EndB : null;

    /// <summary>Holding points for a departure from <paramref name="endName"/>, nearest to that threshold first.</summary>
    public IEnumerable<HoldPoint> DepartureHoldPoints(string endName)
    {
        bool fromA = End(endName) == EndA;
        return HoldPoints.OrderBy(h => fromA ? h.AlongRunway : Length - h.AlongRunway);
    }
}

public static partial class RunwayFinder
{
    /// <summary>ICAO Annex 14 holding position distance from the centerline for a code 4 instrument runway.</summary>
    public const double DefaultHoldDistance = 90;

    /// <summary>
    /// Groups the network's runway edges into runways, names both ends and finds the holding points.
    /// Names come from the designators stored in the .rn5 file (e.g. "13L"), then from
    /// <paramref name="extraNames"/> (e.g. DCS getRunways), else from the true heading.
    /// </summary>
    public static List<Runway> Find(Rn5Network net, TaxiGraph graph, IEnumerable<string>? extraNames = null, double holdDistance = DefaultHoldDistance)
    {
        var knownNumbers = net.RunwayNames.Concat(extraNames ?? [])
            .Select(n => NumberPattern().Match(n))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .SelectMany(n => new[] { n, n > 18 ? n - 18 : n + 18 })
            .Distinct()
            .ToList();

        var chains = RunwayChains(net);
        var raw = chains.Select(chain =>
        {
            var points = chain.SelectMany(e => e.Points).ToList();
            var (a, b) = FarthestPair(points);
            return (Edges: chain, A: a, B: b);
        }).ToList();

        // Ends get their number now and their L/C/R suffix once parallel runways are known.
        var runways = new List<Runway>();
        for (int i = 0; i < raw.Count; i++)
        {
            var (edges, a, b) = raw[i];
            double heading = (b - a).Heading;
            string suffixA = ParallelSuffix(raw, i, heading);
            string suffixB = ParallelSuffix(raw, i, (heading + 180) % 360);
            var nodes = edges.SelectMany(e => new[] { e.NodeA, e.NodeB }).Distinct().ToList();
            var axis = (b - a).Normalized;
            runways.Add(new Runway
            {
                Index = i,
                Nodes = nodes,
                EndA = new RunwayEnd(Number(heading, knownNumbers) + suffixA, a, heading),
                EndB = new RunwayEnd(Number((heading + 180) % 360, knownNumbers) + suffixB, b, (heading + 180) % 360),
                HoldPoints = HoldPoints(graph, nodes, a, b, axis, holdDistance),
                Width = WidthOf(edges),
            });
        }
        return runways;
    }

    private static List<HoldPoint> HoldPoints(TaxiGraph graph, List<int> nodes, Vec2 endA, Vec2 endB, Vec2 axis, double holdDistance)
    {
        var holds = new List<HoldPoint>();
        foreach (var node in nodes)
        {
            foreach (var arm in graph.Vertices.Where(v => v.Node == node && !v.IsRunwayArm))
            {
                var points = graph.PointsFromArm(arm.Id);
                double along = AlongUntilDistance(points, endA, endB, holdDistance);
                var position = Polyline.PointAt(points, along);
                holds.Add(new HoldPoint(arm.Id, graph.ArmEdge(arm.Id).Edge, position, along, (arm.Position - endA).Dot(axis)));
            }
        }
        return holds;
    }

    /// <summary>Distance along a polyline until it is <paramref name="distance"/> metres from the runway (or its end).</summary>
    private static double AlongUntilDistance(IReadOnlyList<Vec2> points, Vec2 endA, Vec2 endB, double distance)
    {
        double FromRunway(Vec2 p) => Polyline.Project([endA, endB], p).Distance;
        double walked = 0;
        for (int i = 1; i < points.Count; i++)
        {
            double segment = points[i - 1].DistanceTo(points[i]);
            double d0 = FromRunway(points[i - 1]), d1 = FromRunway(points[i]);
            if (d0 >= distance) return walked;
            if (d1 >= distance) return walked + segment * (distance - d0) / (d1 - d0);
            walked += segment;
        }
        return walked;
    }

    /// <summary>Width from the runway edge type name ("vpp_60", "runway_45m"), 45 m if it has none.</summary>
    private static double WidthOf(List<Rn5Edge> edges)
    {
        var m = WidthPattern().Match(edges[0].TypeName);
        return m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 45;
    }

    [GeneratedRegex(@"_(\d+)m?$")]
    private static partial Regex WidthPattern();

    /// <summary>Connected groups of runway edges (joined through shared nodes).</summary>
    private static List<List<Rn5Edge>> RunwayChains(Rn5Network net)
    {
        var edges = net.Edges.Where(e => e.IsRunway).ToList();
        var chains = new List<List<Rn5Edge>>();
        var used = new HashSet<int>();
        foreach (var start in edges)
        {
            if (!used.Add(start.Index)) continue;
            var chain = new List<Rn5Edge> { start };
            var nodes = new HashSet<int> { start.NodeA, start.NodeB };
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var e in edges.Where(e => !used.Contains(e.Index) && (nodes.Contains(e.NodeA) || nodes.Contains(e.NodeB))))
                {
                    used.Add(e.Index);
                    chain.Add(e);
                    nodes.Add(e.NodeA);
                    nodes.Add(e.NodeB);
                    grew = true;
                }
            }
            chains.Add(chain);
        }
        return chains;
    }

    private static (Vec2, Vec2) FarthestPair(List<Vec2> points)
    {
        var a = points.MaxBy(p => p.DistanceTo(points[0]));
        var b = points.MaxBy(p => p.DistanceTo(a));
        // Orient A→B so that end A gets the lower runway number (A heading 0–179°).
        return (b - a).Heading < 180 ? (a, b) : (b, a);
    }

    /// <summary>"L", "C" or "R" when other runways run parallel (within 10°), else "".</summary>
    private static string ParallelSuffix(List<(List<Rn5Edge> Edges, Vec2 A, Vec2 B)> runways, int index, double heading)
    {
        var dir = Vec2.FromHeading(heading);
        var parallel = runways
            .Select((r, i) => (i, r))
            .Where(x => Math.Abs(Math.Sin(((x.r.B - x.r.A).Heading - heading) * Math.PI / 180)) < Math.Sin(10 * Math.PI / 180))
            .OrderByDescending(x => (((x.r.A + x.r.B) * 0.5) - runways[index].A).Dot(dir.Left))
            .Select(x => x.i)
            .ToList();
        if (parallel.Count < 2) return "";
        int rank = parallel.IndexOf(index);
        return parallel.Count == 2 ? (rank == 0 ? "L" : "R") : rank == 0 ? "L" : rank == parallel.Count - 1 ? "R" : "C";
    }

    /// <summary>Runway number for a true heading: a known designator within 2 of the heading, else heading / 10.</summary>
    private static string Number(double trueHeading, List<int> known)
    {
        int n = (int)Math.Round(trueHeading / 10);
        if (n == 0) n = 36;
        var match = known.Where(k => CircularDistance(k, n) <= 2).OrderBy(k => CircularDistance(k, n)).Cast<int?>().FirstOrDefault();
        return (match ?? n).ToString("00");
    }

    private static int CircularDistance(int a, int b)
    {
        int d = Math.Abs(a - b) % 36;
        return Math.Min(d, 36 - d);
    }

    [GeneratedRegex(@"^(\d{1,2})[LRC]?$")]
    private static partial Regex NumberPattern();
}
