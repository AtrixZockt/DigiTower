namespace DcsAtc.Airfields.Rn5;

/// <summary>
/// The taxi network of one airfield, read from DCS's <c>Mods\terrains\&lt;map&gt;\AirfieldsTaxiways\&lt;airfield&gt;.rn5</c>.
/// This is the network the DCS AI taxis on. It has no taxiway names; those are added by our own labeling.
/// </summary>
public sealed class Rn5Network
{
    public required string Name { get; init; }
    /// <summary>Edge type names, indexed by <see cref="Rn5Edge.TypeIndex"/> (e.g. "taxiway_22m", "vpp_60").</summary>
    public required IReadOnlyList<string> EdgeTypes { get; init; }
    /// <summary>Second type list of the file (stand and marking kinds). Not yet linked to nodes.</summary>
    public required IReadOnlyList<string> NodeTypes { get; init; }
    public required IReadOnlyList<Rn5Edge> Edges { get; init; }
    public required IReadOnlyList<Rn5Node> Nodes { get; init; }
    /// <summary>Runway names found in the node block, e.g. "13L" (stored as "1#13L").</summary>
    public required IReadOnlyList<string> RunwayNames { get; init; }

    /// <summary>A network built from edges directly (tests, hand-made airfields).</summary>
    public static Rn5Network Create(string name, IReadOnlyList<string> edgeTypes, IReadOnlyList<Rn5Edge> edges, IReadOnlyList<string>? runwayNames = null) => new()
    {
        Name = name,
        EdgeTypes = edgeTypes,
        NodeTypes = [],
        Edges = edges,
        Nodes = Rn5Reader.BuildNodes(edges),
        RunwayNames = runwayNames ?? [],
    };
}

/// <summary>A taxi network edge: a polyline between a port of node A and a port of node B.</summary>
public sealed record Rn5Edge(int Index, int NodeA, int PortA, int NodeB, int PortB, int TypeIndex, string TypeName, IReadOnlyList<Vec2> Points)
{
    /// <summary>File offset of the point count (debugging aid).</summary>
    public int FileOffset { get; init; }

    /// <summary>Runway edge types are "vpp…" (Russian ВПП) on older maps and "runway…" on newer ones.</summary>
    public bool IsRunway => TypeName.StartsWith("vpp", StringComparison.Ordinal) || TypeName.StartsWith("runway", StringComparison.Ordinal);

    public double Length
    {
        get
        {
            double length = 0;
            for (int i = 1; i < Points.Count; i++) length += Points[i - 1].DistanceTo(Points[i]);
            return length;
        }
    }
}

/// <summary>
/// A node of the network: a junction (several ports where edges meet), a dead end (usually a stand),
/// or a long runway node with ports along it. Port positions are where the edges end.
/// </summary>
public sealed record Rn5Node(int Id, IReadOnlyDictionary<int, Vec2> Ports, IReadOnlyList<int> EdgeIndices)
{
    public int Degree => EdgeIndices.Count;
    public Vec2 Center => Vec2.Average(Ports.Values.ToList());

    /// <summary>Largest distance between two ports. Junctions are a few tens of metres; runway nodes are hundreds.</summary>
    public double Spread
    {
        get
        {
            var ports = Ports.Values.ToList();
            double max = 0;
            for (int i = 0; i < ports.Count; i++)
                for (int j = i + 1; j < ports.Count; j++)
                    max = Math.Max(max, ports[i].DistanceTo(ports[j]));
            return max;
        }
    }
}
