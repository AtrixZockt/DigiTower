using System.Text.Json;
using DcsAtc.Airfields;
using DcsAtc.Airfields.Rn5;
using DcsAtc.Airfields.Taxi;
using DcsAtc.Bridge;

namespace DcsAtc.Tests;

public class TaxiRoutingTests
{
    /// <summary>
    /// An east–west runway (x = 0, z 0…2000) with a parallel taxiway at x = 200 built like Al Dhafra's:
    /// two edges that overlap by 200 m at junction 3 (T1 ends at z 1100, T2 starts at z 900), and
    /// connectors down to both runway ends.
    /// </summary>
    private static Airfield Synthetic()
    {
        string[] types = ["taxiway_22m", "vpp_60"];
        var edges = new List<Rn5Edge>
        {
            Edge(0, 1, nodeA: 0, portA: 0, nodeB: 1, portB: 0, (0, 0), (0, 1000)),          // runway west half
            Edge(1, 1, nodeA: 1, portA: 1, nodeB: 2, portB: 0, (0, 1000), (0, 2000)),       // runway east half
            Edge(2, 0, nodeA: 4, portA: 0, nodeB: 3, portB: 0, (200, 0), (200, 1100)),      // T1
            Edge(3, 0, nodeA: 3, portA: 1, nodeB: 5, portB: 0, (200, 900), (200, 2000)),    // T2, overlapping T1
            Edge(4, 0, nodeA: 4, portA: 1, nodeB: 0, portB: 1, (200, 0), (0, 0)),           // west connector
            Edge(5, 0, nodeA: 5, portA: 1, nodeB: 2, portB: 1, (200, 2000), (0, 2000)),     // east connector
        };
        return Airfield.Build(Rn5Network.Create("Synthetic", types, edges, ["09"]));

        Rn5Edge Edge(int index, int type, int nodeA, int portA, int nodeB, int portB, params (double X, double Z)[] pts) =>
            new(index, nodeA, portA, nodeB, portB, type, types[type], Densify(pts.Select(p => new Vec2(p.X, p.Z)).ToList()));
    }

    /// <summary>Points every 50 m, like real DCS polylines.</summary>
    private static List<Vec2> Densify(List<Vec2> pts)
    {
        var result = new List<Vec2> { pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            int n = Math.Max(1, (int)(pts[i - 1].DistanceTo(pts[i]) / 50));
            for (int k = 1; k <= n; k++) result.Add(pts[i - 1] + (pts[i] - pts[i - 1]) * (k / (double)n));
        }
        return result;
    }

    private static int Reversals(IReadOnlyList<Vec2> route) => ReversalPoints(route).Count;

    /// <summary>Points where the route turns back by more than ~135° (segments over 0.5 m).</summary>
    private static List<string> ReversalPoints(IReadOnlyList<Vec2> route)
    {
        var found = new List<string>();
        for (int i = 1; i < route.Count - 1; i++)
        {
            Vec2 a = route[i] - route[i - 1], b = route[i + 1] - route[i];
            if (a.Length > 0.5 && b.Length > 0.5 && a.Normalized.Dot(b.Normalized) < -0.7)
                found.Add($"#{i}/{route.Count} at ({route[i].X:F1},{route[i].Z:F1}) after {a.Length:F1} m, back {b.Length:F1} m");
        }
        return found;
    }

    [Fact]
    public void Finds_and_names_the_runway_with_holding_points()
    {
        var airfield = Synthetic();
        var runway = Assert.Single(airfield.Runways);
        Assert.Equal("09/27", runway.Name);
        Assert.Equal(2000, runway.Length, 3);
        Assert.Equal(90, runway.EndA.TrueHeading, 3);
        Assert.Equal(2, runway.HoldPoints.Count);
        Assert.All(runway.HoldPoints, h => Assert.Equal(RunwayFinder.DefaultHoldDistance, h.Position.X, 3));
    }

    [Fact]
    public void Merges_across_overlapping_edges_without_turning_back()
    {
        var airfield = Synthetic();

        // From 10 m beside the parallel taxiway at z 1500, west to runway 09: 10 m onto it, 600 m on T2,
        // onto T1 at z 900 (not back to T1's end at z 1100), 900 m to the corner, 110 m to the holding point.
        var west = airfield.RouteToRunway(new Vec2(210, 1500), "09")!;
        Assert.Equal(1620, west.Route.Length, 0);
        Assert.Equal(0, Reversals(west.Route.Geometry));
        Assert.Equal(90, west.Route.Geometry[^1].X, 3);
        Assert.Equal(0, west.Route.Geometry[^1].Z, 3);
        Assert.False(west.Route.UsesRunway);

        var east = airfield.RouteToRunway(new Vec2(210, 1500), "27")!;
        Assert.Equal(620, east.Route.Length, 0);
        Assert.Equal(0, Reversals(east.Route.Geometry));
    }

    [Fact]
    public void Junction_links_never_allow_u_turns()
    {
        var graph = Synthetic().Graph;
        foreach (var link in graph.Links.Where(l => l.IsJunction))
            Assert.True(TaxiGraph.TurnAngle(link.StartDir, link.EndDir) <= TaxiGraph.MaxTurn);
    }

    [SkippableTheory]
    [InlineData("13L", 4000)]
    [InlineData("31R", 1100)]
    [InlineData("13R", 4800)]
    [InlineData("31L", 3800)]
    public void Routes_at_al_dhafra_from_spot_106(string runwayEnd, double maxLength)
    {
        Skip.If(TestPaths.Terrains is null, "DCS install not found (set DIGITOWER_DCS_DIR)");
        string rn5 = Path.Combine(TestPaths.Terrains!, "PersianGulf", "AirfieldsTaxiways", "Dhafra_AFB.rn5");
        string dump = Path.Combine(TestPaths.RepoRoot, "data", "dcs", "Al Dhafra AFB.json");
        Skip.IfNot(File.Exists(rn5) && File.Exists(dump), "Al Dhafra data not available");

        var info = JsonSerializer.Deserialize<AirbaseInfo>(File.ReadAllText(dump), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var airfield = Airfield.Build(Rn5Reader.Read(rn5), info.Runways.Select(r => r.Name!),
            info.Parking.Select(p => new ParkingSpot(p.Index, p.TermType, new Vec2(p.X, p.Z))));

        Assert.Equal(["13L/31R", "13R/31L"], airfield.Runways.Select(r => r.Name).Order());
        var spot = airfield.Parking.Single(p => p.Index == 106);
        var departure = airfield.RouteToRunway(spot.Position, runwayEnd)!;
        Assert.Empty(ReversalPoints(departure.Route.Geometry));
        Assert.InRange(departure.Route.Length, 500, maxLength);
        // The route ends at the holding point, 90 m from the runway (not on the runway at the entry).
        Assert.Equal(RunwayFinder.DefaultHoldDistance, departure.Runway.DistanceFrom(departure.Route.Geometry[^1]), 1);
        Assert.False(departure.Route.UsesRunway);
    }

    [Fact]
    public void Removes_sub_metre_spikes_but_keeps_real_turns()
    {
        // The 0.9 m step back after (0, 50) is a spike; its tip is dropped.
        List<Vec2> line = [new(0, 0), new(0, 50), new(0, 49.1), new(0, 100), new(50, 100)];
        Assert.Equal([new(0, 0), new(0, 49.1), new(0, 100), new(50, 100)], Polyline.RemoveSpikes(line));
        List<Vec2> corner = [new(0, 0), new(0, 50), new(40, 50)];
        Assert.Equal(corner, Polyline.RemoveSpikes(corner));
    }
}
