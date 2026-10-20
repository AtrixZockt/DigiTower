using DcsAtc.Airfields;
using DcsAtc.Airfields.Taxi;
using DcsAtc.Atc;
using DcsAtc.Bridge;

namespace DcsAtc.Tests;

/// <summary>Ground traffic (M7) at Al Dhafra: giving way, holding and continuing, following.</summary>
public partial class TowerControlTests
{
    /// <summary>An AI Hornet (no player) on the ground.</summary>
    private static UnitState AiHornet(Vec2 p, double hdg, double speed) =>
        Aircraft("Enfield 2-1", 21, p, hdg: hdg, speed: speed) with { Type = "FA-18C_hornet" };

    /// <summary>The point <paramref name="along"/> metres along a line and the heading there.</summary>
    private static (Vec2 Point, double Heading) On(IReadOnlyList<Vec2> line, double along)
    {
        var p = Polyline.PointAt(line, along);
        return (p, (Polyline.PointAt(line, along + 5) - p).Heading);
    }

    /// <summary>
    /// A taxiway joining the route from the side, well along it: how far along the route it joins, and the last
    /// 200 m of the side taxiway leading to the route.
    /// </summary>
    private static (double Along, IReadOnlyList<Vec2> Approach) SideTaxiway(Airfield layout, IReadOnlyList<Vec2> route)
    {
        double length = Polyline.Length(route);
        var why = new List<string>();
        foreach (var link in layout.Graph.Links.Where(l => l.Kind == LinkKind.Taxiway && !l.IsConnector && l.Length > 20))
        {
            // Each end of a link touching the route, leaving it at a right angle: walk the taxiway away from the route.
            foreach (var points in new[] { link.Points, link.Points.Reverse().ToList() })
            {
                var (_, along, distance) = Polyline.Project(route, points[0]);
                if (distance > 20 || along < 250 || along > length - 250) continue;
                var away = (Polyline.PointAt(points, 60) - points[0]).Normalized;
                var tangent = (Polyline.PointAt(route, along + 10) - Polyline.PointAt(route, along - 10)).Normalized;
                if (Math.Abs(away.Dot(tangent)) > 0.5) { why.Add($"angle {link.Id} at {along:F0}"); continue; }
                var walk = GroundTraffic.NetworkAhead(layout.Graph, Polyline.PointAt(points, 15), away, 230);
                if (Polyline.Length(walk) < 220 || Polyline.Project(route, Polyline.PointAt(walk, 215)).Distance < 90) { why.Add($"walk {link.Id} at {along:F0}: {Polyline.Length(walk):F0} m, end {Polyline.Project(route, Polyline.PointAt(walk, 215)).Distance:F0} m off"); continue; }
                // The side taxiway must stay clear of the route on its way in.
                var approach = Polyline.Slice(walk, 0, 215).AsEnumerable().Reverse().ToList();
                approach.Add(points[0]);
                return (along, approach);
            }
        }
        throw new InvalidOperationException($"no side taxiway joins the route ({length:F0} m): {string.Join("; ", why)}");
    }

    /// <summary>Spot 207: the parking spot with the longest taxi route to 31R (2.4 km, plenty of taxiways joining it on the way).</summary>
    private static Vec2 FarSpot(World w) => w.Layout.Parking.Single(p => p.Index == 207).Position;

    [SkippableFact]
    public async Task A_taxiing_player_gives_way_to_ai_joining_ahead_and_continues_when_it_has_passed()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var spot = FarSpot(w!);
        await w.Ground.RequestTaxiAsync(w.Put(Viper(spot)), w.Units);
        var clearance = w.Ground.Clearances[1];
        var route = clearance.Route.Geometry;
        var (join, approach) = SideTaxiway(w.Layout, route);

        // Both 160 m from the junction at 8 m/s: they would meet there in about 20 seconds.
        var (viperAt, viperHdg) = On(route, join - 160);
        var (aiAt, aiHdg) = On(approach, 40);
        w.Put(Viper(viperAt, hdg: viperHdg, speed: 8));
        w.Put(AiHornet(aiAt, aiHdg, 8));
        await w.Ground.OnUnitsAsync(w.Units);
        Assert.Null(clearance.TrafficHold);   // confirmed on the next update only
        await w.Ground.OnUnitsAsync(w.Units);

        Assert.NotNull(clearance.TrafficHold);
        Assert.Matches(@"^Viper 1-1, Al Dhafra Ground, (hold short of \w+, )?give way to the Hornet from the (left|right)\.$", w.LastText);
        Assert.True(w.Dcs.Drawings.ContainsKey("hold-1"));
        var holdLine = w.Dcs.Drawings["hold-1"].Points;
        double holdAlong = Polyline.Project(route, new Vec2((holdLine[0].X + holdLine[1].X) / 2, (holdLine[0].Z + holdLine[1].Z) / 2)).Along;
        Assert.InRange(holdAlong, join - 100, join - 20);

        // Stopped at the hold; the Hornet turns onto the route ahead and taxis away from the junction.
        w.Put(Viper(Polyline.PointAt(route, holdAlong), hdg: viperHdg));
        var (aheadAt, aheadHdg) = On(route, join + 30);
        w.Put(AiHornet(aheadAt, aheadHdg, 8));
        await w.Ground.OnUnitsAsync(w.Units);
        Assert.NotNull(clearance.TrafficHold);   // at least 5 seconds
        w.Clock.Now = w.Clock.Now.AddSeconds(6);
        var (goneAt, goneHdg) = On(route, join + 150);
        w.Put(AiHornet(goneAt, goneHdg, 8));
        await w.Ground.OnUnitsAsync(w.Units);

        Assert.Null(clearance.TrafficHold);
        Assert.Equal("Viper 1-1, Al Dhafra Ground, continue taxi.", w.LastText);
        Assert.False(w.Dcs.Drawings.ContainsKey("hold-1"));
        Assert.True(w.Dcs.Drawings.ContainsKey("taxi-1"));
    }

    [SkippableFact]
    public async Task An_ai_wingman_in_the_players_own_group_is_no_traffic()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        await w!.Ground.RequestTaxiAsync(w.Put(Viper(FarSpot(w))), w.Units);
        var route = w.Ground.Clearances[1].Route.Geometry;
        var (join, approach) = SideTaxiway(w.Layout, route);

        var (viperAt, viperHdg) = On(route, join - 160);
        var (wingAt, wingHdg) = On(approach, 40);
        w.Put(Viper(viperAt, hdg: viperHdg, speed: 8));
        w.Put(Aircraft("Viper 1-2", 1, wingAt, hdg: wingHdg, speed: 8));   // same group, no player: Viper 1-1's AI wingman
        int before = w.Dcs.Texts.Count;
        for (int i = 0; i < 3; i++) await w.Ground.OnUnitsAsync(w.Units);
        Assert.Null(w.Ground.Clearances[1].TrafficHold);
        Assert.Equal(before, w.Dcs.Texts.Count);
    }

    [SkippableFact]
    public async Task Rolling_on_past_the_hold_gets_hold_position()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var spot = FarSpot(w!);
        await w.Ground.RequestTaxiAsync(w.Put(Viper(spot)), w.Units);
        var clearance = w.Ground.Clearances[1];
        var route = clearance.Route.Geometry;
        var (join, approach) = SideTaxiway(w.Layout, route);

        var (viperAt, viperHdg) = On(route, join - 200);
        w.Put(Viper(viperAt, hdg: viperHdg, speed: 8));
        var (aiAt, aiHdg) = On(approach, 0);
        w.Put(AiHornet(aiAt, aiHdg, 8));
        await w.Ground.OnUnitsAsync(w.Units);
        await w.Ground.OnUnitsAsync(w.Units);
        var hold = clearance.TrafficHold;
        Assert.NotNull(hold);

        // The Hornet is still coming; Viper rolls 40 m past the hold line.
        w.Put(Viper(Polyline.PointAt(route, hold.Along + 40), hdg: viperHdg, speed: 6));
        w.Put(AiHornet(On(approach, 60).Point, aiHdg, 8));
        await w.Ground.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, Al Dhafra Ground, hold position.", w.LastText);
    }

    [SkippableFact]
    public async Task Aircraft_parked_beside_the_route_and_a_flight_taxiing_in_trail_are_no_traffic()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var spot = w!.Layout.Parking.Single(p => p.Index == 106).Position;
        var one = w.Put(Hangman(1, spot, 11));
        w.Put(Hangman(2, spot + new Vec2(25, 0), 12));
        await w.Flights.DeclareAsync(one, 2, w.Units);
        await w.Ground.RequestTaxiAsync(one, w.Units);
        var route = w.Ground.Clearances[11].Route.Geometry;
        int before = w.Dcs.Texts.Count;

        // Lead 400 m along, the wingman 60 m behind it, both at 8 m/s; every parking spot taken by a parked AI.
        var (leadAt, hdg) = On(route, 400);
        var (wingAt, wingHdg) = On(route, 340);
        w.Put(Hangman(1, leadAt, 11, hdg: hdg, speed: 8));
        w.Put(Hangman(2, wingAt, 12, hdg: wingHdg, speed: 8));
        int n = 0;
        foreach (var p in w.Layout.Parking.Where(p => p.Index != 106).Take(80))
            w.Put(Aircraft($"Parked {n}", 100 + n++, p.Position, hdg: 0) with { Type = "FA-18C_hornet" });
        for (int i = 0; i < 3; i++) await w.Ground.OnUnitsAsync(w.Units);

        Assert.DoesNotContain(w.Dcs.Texts.Skip(before), t => t.Text.Contains("give way") || t.Text.Contains("follow") || t.Text.Contains("hold position"));
        Assert.Null(w.Ground.Clearances[11].TrafficHold);
    }

    [SkippableFact]
    public async Task Catching_up_with_traffic_ahead_on_the_route_gets_follow()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var spot = w!.Layout.Parking.Single(p => p.Index == 106).Position;
        await w.Ground.RequestTaxiAsync(w.Put(Viper(spot)), w.Units);
        var route = w.Ground.Clearances[1].Route.Geometry;

        var (viperAt, viperHdg) = On(route, 300);
        var (aiAt, aiHdg) = On(route, 420);
        w.Put(Viper(viperAt, hdg: viperHdg, speed: 10));
        w.Put(AiHornet(aiAt, aiHdg, 4));
        await w.Ground.OnUnitsAsync(w.Units);
        await w.Ground.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, Al Dhafra Ground, follow the Hornet ahead.", w.LastText);
        Assert.Null(w.Ground.Clearances[1].TrafficHold);

        // Said once, not on every update.
        int count = w.Dcs.Texts.Count;
        await w.Ground.OnUnitsAsync(w.Units);
        Assert.Equal(count, w.Dcs.Texts.Count);
    }
}
