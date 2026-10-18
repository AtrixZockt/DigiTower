using System.Text;
using DcsAtc.Airfields.Rn5;

namespace DcsAtc.Tests;

public class Rn5ReaderTests
{
    [Fact]
    public void Parses_a_synthetic_file()
    {
        var net = Rn5Reader.Parse(BuildFile(), "Synthetic");

        Assert.Equal(["taxiway_22m", "vpp_60"], net.EdgeTypes);
        Assert.Equal(["stand_end"], net.NodeTypes);
        Assert.Equal(2, net.Edges.Count);
        Assert.Equal(["13L"], net.RunwayNames);

        var taxi = net.Edges[0];
        Assert.Equal((0, 0, 1, 0), (taxi.NodeA, taxi.PortA, taxi.NodeB, taxi.PortB));
        Assert.False(taxi.IsRunway);
        Assert.Equal(200, taxi.Length, 6);

        var runway = net.Edges[1];
        Assert.True(runway.IsRunway);
        Assert.Equal("vpp_60", runway.TypeName);

        Assert.Equal(3, net.Nodes.Count);
        var junction = net.Nodes[1];
        Assert.Equal(2, junction.Degree);
        Assert.Equal(new Vec2(-1000, -2000), junction.Ports[0]);
        Assert.Equal(new Vec2(-1000, -1990), junction.Ports[1]);
        Assert.Equal(10, junction.Spread, 6);
    }

    [Fact]
    public void Rejects_other_files()
    {
        var data = BuildFile();
        data[0x24] = (byte)'x';
        Assert.Throws<InvalidDataException>(() => Rn5Reader.Parse(data, "Broken"));
    }

    [SkippableTheory]
    [InlineData(@"Caucasus\AirfieldsTaxiways\Batumi.rn5", 33, 32, new[] { "31" })]
    [InlineData(@"PersianGulf\AirfieldsTaxiways\Dhafra_AFB.rn5", 2126, 1531, new[] { "13L", "13R" })]
    [InlineData(@"MarianaIslands\AirfieldsTaxiways\Antonio_B._Won_Pat_Int_Airport.rn5", 268, 201, new[] { "06L", "06R" })]
    public void Parses_real_dcs_files(string relativePath, int edges, int nodes, string[] runways)
    {
        Skip.If(TestPaths.Terrains is null, "DCS install not found (set DCSATC_DCS_DIR)");
        string path = Path.Combine(TestPaths.Terrains!, relativePath);
        Skip.IfNot(File.Exists(path), $"{relativePath} not installed");

        var net = Rn5Reader.Read(path);

        Assert.Equal(edges, net.Edges.Count);
        Assert.Equal(nodes, net.Nodes.Count);
        Assert.Equal(runways, net.RunwayNames);
        Assert.Contains(net.Edges, e => e.IsRunway);
        // Every junction arm (node, port) carries exactly one edge end.
        var ends = net.Edges.SelectMany(e => new[] { (e.NodeA, e.PortA), (e.NodeB, e.PortB) }).ToList();
        Assert.Equal(ends.Count, ends.Distinct().Count());
    }

    /// <summary>A minimal file: a 200 m taxiway from node 0 to node 1, a runway edge from node 1 to node 2.</summary>
    private static byte[] BuildFile()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.Latin1);
        w.Write(2u); w.Write(0x30u); w.Write(0u); w.Write(new byte[0x20 - 12]);
        WriteString(w, Rn5Reader.Magic);
        w.Write(0u);
        WriteList(w, "taxiway_22m", "vpp_60");
        WriteList(w, "stand_end");
        w.Write(2u);

        WriteEdge(w, type: 0, nodeA: 0, portA: 0, nodeB: 1, portB: 0, (-1000, -2200), (-1000, -2100), (-1000, -2000));
        w.Write(1u); w.Write(new byte[32]);   // spline data stand-in
        WriteEdge(w, type: 1, nodeA: 1, portA: 1, nodeB: 2, portB: 0, (-1000, -1990), (-1000, -1500));
        w.Write(1u); w.Write(new byte[32]);
        WriteString(w, "1#13L");
        w.Flush();
        return ms.ToArray();

        static void WriteEdge(BinaryWriter w, uint type, uint nodeA, uint portA, uint nodeB, uint portB, params (double X, double Z)[] points)
        {
            w.Write(type); w.Write(nodeA); w.Write(portA); w.Write(nodeB); w.Write(portB); w.Write(0u);
            w.Write((uint)points.Length);
            foreach (var (x, z) in points)
            {
                w.Write(x); w.Write(0.0); w.Write(z);
            }
        }

        static void WriteList(BinaryWriter w, params string[] items)
        {
            w.Write((uint)items.Length);
            foreach (var s in items) WriteString(w, s);
        }

        static void WriteString(BinaryWriter w, string s)
        {
            w.Write((uint)s.Length);
            w.Write(Encoding.Latin1.GetBytes(s));
        }
    }
}
