using DcsAtc.Airfields;
using DcsAtc.Airfields.Taxi;
using DcsAtc.Atc;
using DcsAtc.Bridge;

namespace DcsAtc.Tests;

public class GroundTrafficTests
{
    /// <summary>Al Dhafra with its taxiway names, or null without a DCS install.</summary>
    private static Airfield? Dhafra()
    {
        if (TestPaths.Terrains is null) return null;
        string rn5 = Path.Combine(TestPaths.Terrains, "PersianGulf", "AirfieldsTaxiways", "Dhafra_AFB.rn5");
        if (!File.Exists(rn5)) return null;
        var labels = AirfieldLabels.Load(Path.Combine(TestPaths.RepoRoot, "data", "airfields", "PersianGulf", "Dhafra_AFB.labels.json"));
        return Airfield.Build(DcsAtc.Airfields.Rn5.Rn5Reader.Read(rn5), labels: labels);
    }

    /// <summary>A layout far from these tests' straight lines (no taxiway names there); only needed for clearances.</summary>
    private static readonly Lazy<Airfield?> Layout = new(Dhafra);

    private static TaxiClearance Clearance(int key, IReadOnlyList<Vec2> route, bool departure)
    {
        var airbase = new AirbaseSummary { Name = "Test AFB", Id = 1, Category = 0 };
        var taxiRoute = new TaxiRoute([], route, Polyline.Length(route));
        return new TaxiClearance
        {
            Party = new Party($"Viper {key}-1", key, $"unit {key}", [key], new HashSet<string> { $"unit {key}" }),
            Coalition = 2,
            Airfield = new GroundAirfield(airbase, Layout.Value!),
            Route = taxiRoute,
            Text = "",
            Departure = departure ? new DepartureRoute(taxiRoute, null!, null!, null!) : null,
        };
    }

    /// <summary>A mover at <paramref name="start"/> going towards <paramref name="towards"/> along a straight line.</summary>
    private static GroundMover Mover(string name, Vec2 start, Vec2 towards, double speed, TaxiClearance? clearance = null)
    {
        var dir = (towards - start).Normalized;
        return new GroundMover(name, "F-16", start, dir, speed, [start, start + dir * 2000], clearance);
    }

    private static GroundMover Standing(string name, Vec2 at, Vec2 facing) => new(name, "F-16", at, facing.Normalized, 0, [at], null);

    [SkippableFact]
    public void Crossing_paths_meet_and_the_one_with_a_clearance_gives_way_to_traffic_without_one()
    {
        var ai = Mover("ai", new Vec2(0, -150), new Vec2(0, 0), 8);
        var player = Mover("player", new Vec2(-150, 0), new Vec2(0, 0), 8, PlayerClearance(1, departure: true));

        var conflict = GroundTraffic.FirstConflict(player, ai);
        Assert.NotNull(conflict);
        Assert.Equal(TrafficKind.Crossing, conflict.Kind);
        Assert.InRange(conflict.Time, 12, 19);
        Assert.Equal("player", GroundTraffic.WhoGivesWay(conflict)!.Value.Yields.UnitName);
    }

    [SkippableFact]
    public void Between_two_cleared_flights_a_departure_goes_first()
    {
        var outbound = Mover("out", new Vec2(0, -150), new Vec2(0, 0), 8, PlayerClearance(1, departure: true));
        var inbound = Mover("in", new Vec2(-150, 0), new Vec2(0, 0), 8, PlayerClearance(2, departure: false));
        // Inbound comes from the south, outbound from the west: outbound has inbound on its right, but departures go first.
        Assert.Equal("in", GroundTraffic.WhoGivesWay(GroundTraffic.FirstConflict(outbound, inbound)!)!.Value.Yields.UnitName);
    }

    [SkippableFact]
    public void Between_two_of_the_same_kind_the_one_with_the_other_on_its_right_gives_way()
    {
        // X is north, Z east. "west" heads east (from z -150), "south" heads north (from x -150).
        var west = Mover("west", new Vec2(0, -150), new Vec2(0, 0), 8, PlayerClearance(1, departure: true));
        var south = Mover("south", new Vec2(-150, 0), new Vec2(0, 0), 8, PlayerClearance(2, departure: true));
        // Heading east, the aircraft coming up from the south is on the right: "west" gives way.
        Assert.Equal("west", GroundTraffic.WhoGivesWay(GroundTraffic.FirstConflict(west, south)!)!.Value.Yields.UnitName);
        Assert.Equal("west", GroundTraffic.WhoGivesWay(GroundTraffic.FirstConflict(south, west)!)!.Value.Yields.UnitName);
    }

    [SkippableFact]
    public void Head_on_on_one_line_is_found_and_the_inbound_flight_gives_way()
    {
        var outbound = Mover("out", new Vec2(0, 0), new Vec2(0, 1000), 8, PlayerClearance(1, departure: true));
        var inbound = Mover("in", new Vec2(0, 500), new Vec2(0, 0), 8, PlayerClearance(2, departure: false));
        var conflict = GroundTraffic.FirstConflict(outbound, inbound);
        Assert.NotNull(conflict);
        Assert.Equal(TrafficKind.HeadOn, conflict.Kind);
        Assert.InRange(conflict.Time, 26, 30);
        Assert.Equal("in", GroundTraffic.WhoGivesWay(conflict)!.Value.Yields.UnitName);
    }

    [SkippableFact]
    public void Head_on_the_one_that_can_still_stop_clear_gives_way_even_a_departure()
    {
        // The inbound is already on the outbound's taxiway and turns off at z = 200; the outbound can wait short of that.
        var outbound = Mover("out", new Vec2(0, 0), new Vec2(0, 1000), 8, PlayerClearance(1, departure: true));
        var inboundPath = new List<Vec2> { new(0, 400), new(0, 200), new(500, 200) };
        var inbound = new GroundMover("in", "F-16", inboundPath[0], new Vec2(0, -1), 8, inboundPath, PlayerClearance(2, departure: false));

        var conflict = GroundTraffic.FirstConflict(outbound, inbound)!;
        Assert.Equal(TrafficKind.HeadOn, conflict.Kind);
        var (yields, other) = GroundTraffic.WhoGivesWay(conflict)!.Value;
        Assert.Equal("out", yields.UnitName);
        var plan = GroundTraffic.Plan(conflict, yields, other, Layout.Value!);
        Assert.False(plan.HoldNow);
        Assert.InRange(plan.HoldPoint.Z, 120, 170);
    }

    [SkippableFact]
    public void Catching_up_from_behind_the_one_behind_follows_even_a_departure()
    {
        var fast = Mover("fast", new Vec2(0, 0), new Vec2(0, 1000), 12, PlayerClearance(1, departure: true));
        var slow = Mover("slow", new Vec2(0, 150), new Vec2(0, 1000), 4, PlayerClearance(2, departure: false));
        var conflict = GroundTraffic.FirstConflict(slow, fast)!;
        Assert.Equal(TrafficKind.Behind, conflict.Kind);
        Assert.Equal("fast", GroundTraffic.WhoGivesWay(conflict)!.Value.Yields.UnitName);

        // Traffic without a clearance catching up with a cleared flight: nobody to tell.
        var ai = Mover("ai", new Vec2(0, 0), new Vec2(0, 1000), 12);
        Assert.Null(GroundTraffic.WhoGivesWay(GroundTraffic.FirstConflict(ai, slow)!));
    }

    [SkippableFact]
    public void A_standing_aircraft_counts_only_when_it_is_on_the_path()
    {
        var player = Mover("player", new Vec2(0, 0), new Vec2(0, 1000), 8, PlayerClearance(1, departure: true));
        var onPath = Standing("parked on the taxiway", new Vec2(5, 200), new Vec2(0, 1));
        var beside = Standing("parked beside it", new Vec2(30, 200), new Vec2(1, 0));
        var conflict = GroundTraffic.FirstConflict(player, onPath);
        Assert.NotNull(conflict);
        Assert.Equal(TrafficKind.Behind, conflict.Kind);
        Assert.Null(GroundTraffic.FirstConflict(player, beside));
    }

    [SkippableFact]
    public void Aircraft_that_are_close_but_moving_apart_are_no_conflict()
    {
        var a = Mover("a", new Vec2(0, 20), new Vec2(0, 1000), 8, PlayerClearance(1, departure: true));
        var b = Mover("b", new Vec2(0, -20), new Vec2(0, -1000), 8);
        Assert.Null(GroundTraffic.FirstConflict(a, b));
    }

    [SkippableFact]
    public void The_one_giving_way_holds_short_of_where_the_paths_come_within_reach()
    {
        var ai = Mover("ai", new Vec2(0, -250), new Vec2(0, 0), 8);
        var player = Mover("player", new Vec2(-200, 0), new Vec2(0, 0), 8, PlayerClearance(1, departure: true));
        var conflict = GroundTraffic.FirstConflict(player, ai)!;
        var plan = GroundTraffic.Plan(conflict, player, ai, Layout.Value!);
        Assert.False(plan.HoldNow);
        // Within reach: 0.7 × 45 m from the other's line, so the hold is about 46 m short of the crossing.
        Assert.InRange(plan.HoldPoint.X, -60, -35);
        Assert.Null(plan.HoldShortOf);
    }

    [SkippableFact]
    public void Traffic_without_a_clearance_is_expected_straight_on_along_its_taxiway()
    {
        var layout = Layout.Value;
        Skip.If(layout is null, "DCS install not available");
        // The longest straight taxiway piece: taxiing along it, the prediction stays on it.
        var line = layout!.Graph.Links.Where(l => l.Kind == LinkKind.Taxiway && !l.IsConnector && l.Length > 400 && Straight(l.Points, 0, l.Length - 10))
            .MaxBy(l => l.Length)!.Points;
        var p = Polyline.PointAt(line, 50);
        var dir = (Polyline.PointAt(line, 60) - p).Normalized;

        var ahead = GroundTraffic.NetworkAhead(layout.Graph, p, dir, 200);
        Assert.InRange(Polyline.Length(ahead), 190, 201);
        for (double d = 0; d <= 200; d += 20)
            Assert.True(Polyline.Project(line, Polyline.PointAt(ahead, d)).Distance < 15, $"{d} m ahead is off the taxiway");
        // The other way along it as well.
        var back = GroundTraffic.NetworkAhead(layout.Graph, Polyline.PointAt(line, 300), dir * -1, 200);
        Assert.True(Polyline.Project(line, Polyline.PointAt(back, 200)).Distance < 15);
        Assert.True(Polyline.PointAt(back, 200).DistanceTo(Polyline.PointAt(line, 100)) < 15);
    }

    private static bool Straight(IReadOnlyList<Vec2> line, double from, double length)
    {
        var d0 = (Polyline.PointAt(line, from + 5) - Polyline.PointAt(line, from)).Normalized;
        for (double s = from; s <= from + length; s += 10)
            if ((Polyline.PointAt(line, s + 5) - Polyline.PointAt(line, s)).Normalized.Dot(d0) < 0.98) return false;
        return true;
    }

    private static TaxiClearance PlayerClearance(int key, bool departure)
    {
        Skip.If(Layout.Value is null, "DCS install not available");
        return Clearance(key, [new Vec2(0, 0), new Vec2(1, 1)], departure);
    }
}
