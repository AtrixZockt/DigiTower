using DcsAtc.Airfields.Rn5;

namespace DcsAtc.Airfields.Taxi;

public enum LinkKind
{
    /// <summary>A piece of a taxiway edge of the DCS network.</summary>
    Taxiway,
    /// <summary>A piece of a runway edge, or passing along the runway through a junction.</summary>
    Runway,
    /// <summary>Turning inside a junction from one arm to another.</summary>
    Junction,
}

/// <summary>
/// A graph vertex: one arm (port) of a DCS network node, or a split point in the middle of an edge
/// (<see cref="Node"/> = -1) where another arm of the same junction touches it.
/// </summary>
public sealed record TaxiVertex(int Id, int Node, int Port, Vec2 Position, bool IsRunwayArm)
{
    public bool IsSplit => Node < 0;
}

/// <summary>
/// An undirected link between two vertices. <see cref="StartDir"/> and <see cref="EndDir"/> are the
/// directions of travel when leaving A and arriving at B (travelling A→B); the router uses them to
/// forbid sharp turns and U-turns.
/// </summary>
public sealed record TaxiLink(int Id, int A, int B, LinkKind Kind, IReadOnlyList<Vec2> Points, string TypeName, int? Rn5Edge)
{
    public double Length { get; } = Polyline.Length(Points);

    public Vec2 StartDir { get; init; }
    public Vec2 EndDir { get; init; }

    /// <summary>A junction link between two taxiway arms of a node that also has runway arms: crossing the runway.</summary>
    public bool CrossesRunway { get; init; }

    /// <summary>A link between a taxiway and a runway: entering or leaving the runway.</summary>
    public bool TouchesRunway { get; init; }

    /// <summary>A link inside a junction (from one arm to another).</summary>
    public bool IsJunction { get; init; }

    /// <summary>A short hop from an arm onto a split point of another edge of the same junction.</summary>
    public bool IsConnector { get; init; }

    /// <summary>Added to the length when routing (metres): a way the router takes only when it saves at least that much.</summary>
    public double ExtraCost { get; init; }

    public int Other(int vertex) => vertex == A ? B : A;

    /// <summary>The link's points in travel direction when leaving <paramref name="vertex"/>.</summary>
    public IReadOnlyList<Vec2> PointsFrom(int vertex) => vertex == A ? Points : Points.Reverse().ToList();

    public Vec2 DirLeaving(int vertex) => vertex == A ? StartDir : EndDir * -1;
    public Vec2 DirArriving(int vertex) => vertex == B ? EndDir : StartDir * -1;
}

/// <summary>
/// The taxi network as a routable graph.
/// </summary>
/// <remarks>
/// DCS junctions are not points. At Al Dhafra the parallel taxiway is a chain of overlapping edges: each
/// edge ends up to ~250 m past the point where the next one starts. So, besides straight links between
/// the arms of a junction (only where that is a smooth turn), every edge is split where another arm of
/// the same junction touches it (within <see cref="MergeDistance"/>), with a short connector to that arm.
/// Connectors carry the arm's direction, so the router's turn check lets aircraft merge and diverge but
/// never turn around.
/// </remarks>
public sealed class TaxiGraph
{
    /// <summary>Sharpest turn allowed between two links, in degrees.</summary>
    public const double MaxTurn = 120;

    /// <summary>How close an arm must be to another edge of its junction to connect to it, in metres.</summary>
    public const double MergeDistance = 15;

    /// <summary>How close an arm not joined to another edge of its node in any other way must be to it, in metres.</summary>
    public const double LongNodeMergeDistance = 40;

    /// <summary>Two edges running on through a junction cross (rather than run side by side) at this angle or more, in degrees.</summary>
    public const double MinCrossingAngle = 30;

    /// <summary>Extra routing cost of a turn between two such edges (metres).</summary>
    public const double CrossingTurnCost = 50;

    private readonly List<int>[] _adjacency;
    private readonly Dictionary<int, (Rn5Edge Edge, bool AtStart)> _armEdge;
    private readonly Dictionary<(int, int), int> _byPort;

    private TaxiGraph(List<TaxiVertex> vertices, List<TaxiLink> links, HashSet<int> runwayNodes, Dictionary<int, (Rn5Edge, bool)> armEdge, Dictionary<(int, int), int> byPort)
    {
        Vertices = vertices;
        Links = links;
        RunwayNodes = runwayNodes;
        _armEdge = armEdge;
        _byPort = byPort;
        _adjacency = vertices.Select(_ => new List<int>()).ToArray();
        foreach (var l in links)
        {
            _adjacency[l.A].Add(l.Id);
            _adjacency[l.B].Add(l.Id);
        }
    }

    public IReadOnlyList<TaxiVertex> Vertices { get; }
    public IReadOnlyList<TaxiLink> Links { get; }

    /// <summary>Nodes that have at least one runway arm.</summary>
    public IReadOnlySet<int> RunwayNodes { get; }

    public IEnumerable<TaxiLink> LinksOf(int vertex) => _adjacency[vertex].Select(i => Links[i]);

    /// <summary>The vertex of a DCS node's arm (port).</summary>
    public int VertexOfArm(int node, int port) => _byPort[(node, port)];

    /// <summary>The DCS edge that starts at an arm vertex, and whether the arm is that edge's start.</summary>
    public (Rn5Edge Edge, bool AtStart) ArmEdge(int armVertex) => _armEdge[armVertex];

    /// <summary>The DCS edge's points leading away from an arm vertex.</summary>
    public IReadOnlyList<Vec2> PointsFromArm(int armVertex)
    {
        var (edge, atStart) = _armEdge[armVertex];
        return atStart ? edge.Points : edge.Points.Reverse().ToList();
    }

    public static TaxiGraph Build(Rn5Network net)
    {
        var runwayNodes = net.Edges.Where(e => e.IsRunway).SelectMany(e => new[] { e.NodeA, e.NodeB }).ToHashSet();

        // Arm vertices, one per (node, port).
        var vertices = new List<TaxiVertex>();
        var armEdge = new Dictionary<int, (Rn5Edge, bool)>();
        var byPort = new Dictionary<(int, int), int>();
        foreach (var e in net.Edges)
        {
            foreach (var (node, port, atStart) in new[] { (e.NodeA, e.PortA, true), (e.NodeB, e.PortB, false) })
            {
                var v = new TaxiVertex(vertices.Count, node, port, atStart ? e.Points[0] : e.Points[^1], e.IsRunway);
                vertices.Add(v);
                byPort[(node, port)] = v.Id;
                armEdge[v.Id] = (e, atStart);
            }
        }
        Vec2 Outward(int arm) => Direction(armEdge[arm].Item2 ? armEdge[arm].Item1.Points : armEdge[arm].Item1.Points.Reverse().ToList());

        var links = new List<TaxiLink>();
        var splits = net.Edges.ToDictionary(e => e.Index, _ => new List<(double Along, int Arm)>());

        foreach (var node in net.Nodes)
        {
            var arms = node.Ports.Keys.Select(p => byPort[(node.Id, p)]).ToList();
            // Where a taxiway crosses a runway, the runway's edges may run on through the node; a taxiway arm joined to
            // the middle of one would be a way across that is not a crossing (no "cross runway", no crossing penalty).
            bool crossing = runwayNodes.Contains(node.Id) && arms.Count(x => !vertices[x].IsRunwayArm) >= 2;
            var joined = new HashSet<(int Arm, int Edge)>();
            foreach (int a in arms)
            {
                foreach (int b in arms)
                {
                    if (a == b) continue;
                    var (va, vb) = (vertices[a], vertices[b]);
                    Vec2 arrive = Outward(a) * -1, depart = Outward(b);
                    if (a < b && IsSmoothTurn(va.Position, arrive, vb.Position, depart))
                    {
                        joined.Add((a, armEdge[b].Item1.Index));
                        joined.Add((b, armEdge[a].Item1.Index));
                        links.Add(new TaxiLink(links.Count, a, b, va.IsRunwayArm && vb.IsRunwayArm ? LinkKind.Runway : LinkKind.Junction,
                            [va.Position, vb.Position], "junction", null)
                        {
                            StartDir = arrive,
                            EndDir = depart,
                            IsJunction = true,
                            CrossesRunway = !va.IsRunwayArm && !vb.IsRunwayArm && runwayNodes.Contains(node.Id),
                            TouchesRunway = va.IsRunwayArm != vb.IsRunwayArm,
                        });
                    }
                    // Does arm a touch the middle of arm b's edge?
                    var other = armEdge[b].Item1;
                    if (crossing && other.IsRunway && !va.IsRunwayArm) continue;
                    var (_, along, distance) = Polyline.Project(other.Points, va.Position);
                    if (distance <= MergeDistance && along > 2 && along < other.Length - 2)
                    {
                        splits[other.Index].Add((along, a));
                        joined.Add((a, other.Index));
                        joined.Add((b, armEdge[a].Item1.Index));
                    }
                }
            }
            // A "long node" (older DCS networks, Kutaisi's north ramp): its ports lie 20-45 m apart, and a stand's lead-in
            // ends on the far side of the taxiway it joins, further than MergeDistance from it. An arm joined to another
            // edge of its node in no other way touches that edge where it passes closest, up to LongNodeMergeDistance.
            foreach (int a in arms)
            {
                var va = vertices[a];
                if (va.IsRunwayArm) continue;
                foreach (int b in arms)
                {
                    var other = armEdge[b].Item1;
                    if (a == b || other.IsRunway || other.Index == armEdge[a].Item1.Index || joined.Contains((a, other.Index))) continue;
                    var (_, along, distance) = Polyline.Project(other.Points, va.Position);
                    if (distance > MergeDistance && distance <= LongNodeMergeDistance && along > 2 && along < other.Length - 2)
                        splits[other.Index].Add((along, a));
                }
            }
        }

        // Split points per arm, for the turns between two edges that both run on through the arm's junction.
        var touches = new List<(int Arm, int Split, Rn5Edge Edge, double Along)>();

        // Edge pieces between the edge's ends and its split points, and connectors to the touching arms.
        foreach (var e in net.Edges)
        {
            var stations = new List<(double Along, int Vertex)> { (0, byPort[(e.NodeA, e.PortA)]) };
            var connectors = new List<(int Split, int Arm)>();
            foreach (var group in splits[e.Index].OrderBy(s => s.Along).GroupBy(s => Math.Round(s.Along)))
            {
                double along = group.First().Along;
                if (along - stations[^1].Along < 1)
                {
                    connectors.AddRange(group.Select(s => (stations[^1].Vertex, s.Arm)));
                    continue;
                }
                var split = new TaxiVertex(vertices.Count, -1, -1, Polyline.PointAt(e.Points, along), e.IsRunway);
                vertices.Add(split);
                stations.Add((along, split.Id));
                connectors.AddRange(group.Select(s => (split.Id, s.Arm)));
                if (!e.IsRunway) touches.AddRange(group.Select(s => (s.Arm, split.Id, e, along)));
            }
            stations.Add((e.Length, byPort[(e.NodeB, e.PortB)]));

            for (int i = 1; i < stations.Count; i++)
            {
                var piece = Polyline.Slice(e.Points, stations[i - 1].Along, stations[i].Along);
                links.Add(new TaxiLink(links.Count, stations[i - 1].Vertex, stations[i].Vertex, e.IsRunway ? LinkKind.Runway : LinkKind.Taxiway, piece, e.TypeName, e.Index)
                {
                    StartDir = Direction(piece),
                    EndDir = Direction(piece.AsEnumerable().Reverse().ToList()) * -1,
                });
            }

            foreach (var (split, arm) in connectors)
            {
                // Travelling arm → split continues the arm's direction of arrival; split → arm continues into the arm's edge.
                Vec2 dir = Outward(arm) * -1;
                links.Add(new TaxiLink(links.Count, arm, split, e.IsRunway ? LinkKind.Runway : LinkKind.Taxiway,
                    [vertices[arm].Position, vertices[split].Position], "connector", e.Index)
                {
                    StartDir = dir,
                    EndDir = dir,
                    IsConnector = true,
                    TouchesRunway = e.IsRunway != vertices[arm].IsRunwayArm,
                });
            }
        }

        // Two taxiway edges that cross each other, both running on through a junction (Kutaisi's N and D, Anapa's W
        // and W2), touch it only through a short arm between them, and a connector keeps that arm's direction: turning
        // from one onto the other was only possible from the arm's side. Link the two split points directly, for each
        // turn of at most MaxTurn (StartDir: along the first edge, EndDir: along the second, either way). Overlapping
        // edges that run side by side (Al Dhafra's parallel) are left as they are, and such a turn is taken only where
        // it saves a detour (CrossingTurnCost), not where a route of the same length goes round through the arm.
        foreach (var group in touches.GroupBy(t => t.Arm))
        {
            var at = group.ToList();
            for (int i = 0; i < at.Count; i++)
                for (int j = i + 1; j < at.Count; j++)
                {
                    var (p, q) = (at[i], at[j]);
                    if (p.Edge.Index == q.Edge.Index || p.Split == q.Split) continue;
                    Vec2 alongP = Tangent(p.Edge.Points, p.Along), alongQ = Tangent(q.Edge.Points, q.Along);
                    double crossing = TurnAngle(alongP, alongQ);
                    if (crossing < MinCrossingAngle || crossing > 180 - MinCrossingAngle) continue;
                    foreach (var depart in new[] { alongQ, alongQ * -1 })
                    {
                        if (!IsSmoothTurn(vertices[p.Split].Position, alongP, vertices[q.Split].Position, depart)) continue;
                        links.Add(new TaxiLink(links.Count, p.Split, q.Split, LinkKind.Junction,
                            [vertices[p.Split].Position, vertices[q.Split].Position], "junction", null)
                        {
                            StartDir = alongP,
                            EndDir = depart,
                            IsJunction = true,
                            ExtraCost = CrossingTurnCost,
                        });
                    }
                }
        }
        return new TaxiGraph(vertices, links, runwayNodes, armEdge, byPort);
    }

    /// <summary>The closest point on a taxiway piece (not runway, junction or connector links).</summary>
    public (TaxiLink Link, Vec2 Point, double Along, double Distance) ProjectOntoTaxiway(Vec2 p)
    {
        (TaxiLink Link, Vec2 Point, double Along, double Distance) best = default;
        best.Distance = double.MaxValue;
        foreach (var link in Links)
        {
            if (link.Kind != LinkKind.Taxiway || link.IsConnector) continue;
            var (point, along, distance) = Polyline.Project(link.Points, p);
            if (distance < best.Distance) best = (link, point, along, distance);
        }
        return best;
    }

    public static double TurnAngle(Vec2 from, Vec2 to)
    {
        if (from.Length == 0 || to.Length == 0) return 0;
        double cos = Math.Clamp(from.Normalized.Dot(to.Normalized), -1, 1);
        return Math.Acos(cos) * 180 / Math.PI;
    }

    /// <summary>A straight hop from arm a to arm b is allowed if it neither reverses nor turns too sharply.</summary>
    private static bool IsSmoothTurn(Vec2 a, Vec2 arrive, Vec2 b, Vec2 depart)
    {
        if (TurnAngle(arrive, depart) > MaxTurn) return false;
        var hop = b - a;
        if (hop.Length < 5) return true;
        return hop.Dot(arrive) >= -5 && hop.Dot(depart) >= -5;
    }

    /// <summary>Direction of a polyline at <paramref name="along"/> metres, towards its end.</summary>
    private static Vec2 Tangent(IReadOnlyList<Vec2> pts, double along)
    {
        double length = Polyline.Length(pts);
        return Direction(Polyline.Slice(pts, Math.Max(0, Math.Min(along, length - 5)), Math.Min(length, along + 5)));
    }

    /// <summary>Direction of a polyline at its start (first point more than 0.5 m away).</summary>
    private static Vec2 Direction(IReadOnlyList<Vec2> pts)
    {
        for (int k = 1; k < pts.Count; k++)
            if (pts[k].DistanceTo(pts[0]) > 0.5) return (pts[k] - pts[0]).Normalized;
        return pts.Count > 1 ? (pts[^1] - pts[0]).Normalized : default;
    }
}
