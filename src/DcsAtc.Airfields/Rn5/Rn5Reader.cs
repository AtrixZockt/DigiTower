using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace DcsAtc.Airfields.Rn5;

/// <summary>
/// Reads DCS <c>.rn5</c> taxi network files. The format is undocumented; this is what we worked out:
/// <code>
/// 0x00  u32 version (2), u32 0x30, u32 file size, zero padding up to 0x20
/// 0x20  string "t5::rn5file", u32 0
///       u32 n, n × string   edge type names    (e.g. "taxiway_22m", "vpp_60")
///       u32 n, n × string   stand/marking type names
///       u32 edge count
///       per edge: u32 typeIndex, u32 nodeA, u32 portA, u32 nodeB, u32 portB, u32 0,
///                 u32 n, n × (f64 x, f64 0, f64 z),   then spline data (not needed, skipped)
///       node block: positions, "&lt;index&gt;#&lt;runway name&gt;" strings, distance tables (only names used)
/// strings are u32 length + bytes
/// </code>
/// The spline data between edges is not decoded; edges are found by scanning for the point list
/// that follows each header, and the count is checked against the declared edge count.
/// </summary>
public static partial class Rn5Reader
{
    public const string Magic = "t5::rn5file";

    public static Rn5Network Read(string path) => Parse(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));

    public static Rn5Network Parse(byte[] data, string name)
    {
        int offset = 0x20;
        string magic = ReadString(data, ref offset);
        if (magic != Magic) throw new InvalidDataException($"{name}: not an rn5 file (magic '{magic}')");
        offset += 4;
        var edgeTypes = ReadStringList(data, ref offset);
        var nodeTypes = ReadStringList(data, ref offset);
        int declaredEdges = (int)U32(data, offset);
        offset += 4;

        var edges = ScanEdges(data, offset, declaredEdges, edgeTypes, out int edgesEnd);
        if (edges.Count != declaredEdges)
            throw new InvalidDataException($"{name}: found {edges.Count} of {declaredEdges} edges");

        return new Rn5Network
        {
            Name = name,
            EdgeTypes = edgeTypes,
            NodeTypes = nodeTypes,
            Edges = edges,
            Nodes = BuildNodes(edges),
            RunwayNames = ReadRunwayNames(data, edgesEnd),
        };
    }

    private static List<Rn5Edge> ScanEdges(byte[] data, int offset, int count, IReadOnlyList<string> edgeTypes, out int end)
    {
        var edges = new List<Rn5Edge>(count);
        int p = Math.Max(offset, 24);
        while (edges.Count < count && p + 4 + 24 * 2 <= data.Length)
        {
            if (TryReadEdge(data, p, edgeTypes, edges.Count, out var edge, out int next))
            {
                edges.Add(edge);
                p = next;
            }
            else
            {
                p++;
            }
        }
        end = p;
        return edges;
    }

    private static bool TryReadEdge(byte[] data, int p, IReadOnlyList<string> edgeTypes, int index, out Rn5Edge edge, out int next)
    {
        edge = null!;
        next = p;
        uint n = U32(data, p);
        if (n < 2 || n > 100_000 || p + 4 + 24L * n > data.Length) return false;
        int first = p + 4;
        if (!IsPoint(data, first) || !IsPoint(data, first + 24 * ((int)n - 1))) return false;

        uint type = U32(data, p - 24), nodeA = U32(data, p - 20), portA = U32(data, p - 16), nodeB = U32(data, p - 12), portB = U32(data, p - 8), zero = U32(data, p - 4);
        if (type >= edgeTypes.Count || portA > 64 || portB > 64 || nodeA > 1_000_000 || nodeB > 1_000_000 || zero != 0) return false;

        var points = new Vec2[n];
        for (int i = 0; i < n; i++)
        {
            int o = first + 24 * i;
            if (!IsPoint(data, o)) return false;
            points[i] = new Vec2(F64(data, o), F64(data, o + 16));
            // Real polylines have distinct points a few metres apart; padding and other data do not.
            if (i > 0 && points[i].DistanceTo(points[i - 1]) is < 0.001 or > 5000) return false;
        }
        edge = new Rn5Edge(index, (int)nodeA, (int)portA, (int)nodeB, (int)portB, (int)type, edgeTypes[(int)type], points) { FileOffset = p };
        next = first + 24 * (int)n;
        return true;
    }

    /// <summary>
    /// Debug aid: every point list in the file (u32 n followed by n valid points) with the six header
    /// fields before it, whether or not it passes the edge checks.
    /// </summary>
    public static IEnumerable<(int Offset, int Count, uint[] Header)> FindPointLists(byte[] data)
    {
        for (int p = 24; p + 4 + 48 <= data.Length; p++)
        {
            uint n = U32(data, p);
            if (n < 2 || n > 100_000 || p + 4 + 24L * n > data.Length) continue;
            if (!IsPoint(data, p + 4) || !IsPoint(data, p + 4 + 24 * ((int)n - 1))) continue;
            var header = Enumerable.Range(0, 6).Select(i => U32(data, p - 24 + 4 * i)).ToArray();
            yield return (p, (int)n, header);
            p += 4 + 24 * (int)n - 1;
        }
    }

    internal static List<Rn5Node> BuildNodes(IReadOnlyList<Rn5Edge> edges)
    {
        if (edges.Count == 0) return [];
        int count = edges.Max(e => Math.Max(e.NodeA, e.NodeB)) + 1;
        var ports = new Dictionary<int, List<Vec2>>[count];
        var incident = new List<int>[count];
        for (int i = 0; i < count; i++)
        {
            ports[i] = [];
            incident[i] = [];
        }
        foreach (var e in edges)
        {
            Add(e.NodeA, e.PortA, e.Points[0]);
            Add(e.NodeB, e.PortB, e.Points[^1]);
            incident[e.NodeA].Add(e.Index);
            if (e.NodeB != e.NodeA) incident[e.NodeB].Add(e.Index);
        }
        return Enumerable.Range(0, count)
            .Select(i => new Rn5Node(i, ports[i].ToDictionary(kv => kv.Key, kv => Vec2.Average(kv.Value)), incident[i]))
            .ToList();

        void Add(int node, int port, Vec2 point)
        {
            if (!ports[node].TryGetValue(port, out var list)) ports[node][port] = list = [];
            list.Add(point);
        }
    }

    private static List<string> ReadRunwayNames(byte[] data, int from)
    {
        var names = new List<string>();
        for (int q = from; q + 8 <= data.Length; q++)
        {
            uint n = U32(data, q);
            if (n < 3 || n > 12 || q + 4 + n > data.Length) continue;
            string s = Encoding.Latin1.GetString(data, q + 4, (int)n);
            var m = RunwayNamePattern().Match(s);
            if (m.Success && !names.Contains(m.Groups[1].Value)) names.Add(m.Groups[1].Value);
        }
        return names;
    }

    [GeneratedRegex(@"^\d+#(\d{1,2}[LRC]?)$")]
    private static partial Regex RunwayNamePattern();

    private static bool IsPoint(byte[] data, int o)
    {
        if (o < 0 || o + 24 > data.Length) return false;
        double x = F64(data, o), y = F64(data, o + 8), z = F64(data, o + 16);
        // y is 0 in every file seen so far. Coordinates can be near 0 (Guam is at the Marianas map origin),
        // so zero-filled data is rejected by the distinct-points check in TryReadEdge instead.
        return y == 0 && double.IsFinite(x) && double.IsFinite(z) && Math.Abs(x) < 2e6 && Math.Abs(z) < 2e6;
    }

    private static List<string> ReadStringList(byte[] data, ref int offset)
    {
        int n = (int)U32(data, offset);
        offset += 4;
        if (n > 1000) throw new InvalidDataException($"implausible string list length {n}");
        var list = new List<string>(n);
        for (int i = 0; i < n; i++) list.Add(ReadString(data, ref offset));
        return list;
    }

    private static string ReadString(byte[] data, ref int offset)
    {
        int n = (int)U32(data, offset);
        if (n > 4096 || offset + 4 + n > data.Length) throw new InvalidDataException($"implausible string length {n} at 0x{offset:x}");
        string s = Encoding.Latin1.GetString(data, offset + 4, n);
        offset += 4 + n;
        return s;
    }

    private static uint U32(byte[] data, int o) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o, 4));
    private static double F64(byte[] data, int o) => BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(o, 8));
}
