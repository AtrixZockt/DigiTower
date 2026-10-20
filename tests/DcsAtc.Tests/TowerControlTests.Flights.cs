using DcsAtc.Airfields;
using DcsAtc.Atc;
using DcsAtc.Bridge;

namespace DcsAtc.Tests;

/// <summary>Declared flights (M8): departing together, a parking spot, landing spot or pad for every member.</summary>
public partial class TowerControlTests
{
    private static UnitState HangmanHeli(int number, Vec2 p, int group, bool inAir = false, double agl = 0, double hdg = 0, double speed = 0) =>
        Hangman(number, p, group, inAir, agl, hdg, speed) with { Cat = "heli", Type = "UH-1H" };

    /// <summary>The label of a drawing on the F10 map.</summary>
    private static string? Label(World w, string key) => w.Dcs.Drawings.TryGetValue(key, out var d) ? d.Text : null;

    private static Vec2 Centre(World w, string key)
    {
        var points = w.Dcs.Drawings[key].Points;
        return new Vec2(points.Take(4).Average(p => p.X), points.Take(4).Average(p => p.Z));
    }

    [SkippableFact]
    public async Task A_flight_has_departed_only_when_its_last_member_is_airborne()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var spot = w!.Layout.Parking.Single(p => p.Index == 106).Position;
        var one = w.Put(Hangman(1, spot, 11));
        w.Put(Hangman(2, spot + new Vec2(25, 0), 12));
        await w.Flights.DeclareAsync(one, 2, w.Units);
        await w.Ground.RequestTaxiAsync(one, w.Units);
        var hold = w.Ground.Clearances[11].Route.Geometry[^1];

        // Both at the holding point, cleared together.
        w.Put(Hangman(1, hold, 11));
        w.Put(Hangman(2, hold + new Vec2(20, 20), 12));
        await w.Ground.OnUnitsAsync(w.Units);
        await w.Tower.ReadyForDepartureAsync(w.Units["Hangman 1-1"], w.Units);
        Assert.EndsWith("runway 31R, cleared for takeoff.", w.LastText);

        // Interval takeoff: the lead is airborne, number two still rolling. The runway stays the flight's.
        w.Put(Hangman(1, w.OnRunway31R(2500), 11, inAir: true, agl: 60, hdg: 308, speed: 90));
        w.Put(Hangman(2, w.OnRunway31R(400), 12, hdg: 308, speed: 50));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal(TowerPhase.ClearedForTakeoff, w.Tower.Flights[11].Phase);
        var eagle = w.Put(Aircraft("Eagle 1-1", 5, hold + new Vec2(-30, 30), player: "Eagle 1-1 | C"));
        await w.Tower.ReadyForDepartureAsync(eagle, w.Units);
        Assert.DoesNotContain("cleared for takeoff", w.LastText);

        // Number two airborne: the flight has departed.
        w.Put(Hangman(2, w.OnRunway31R(2200), 12, inAir: true, agl: 40, hdg: 308, speed: 85));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal(TowerPhase.Departed, w.Tower.Flights[11].Phase);
    }

    [SkippableFact]
    public async Task A_flight_taxiing_in_gets_a_parking_spot_for_every_member_side_by_side()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        // After landing, both near the 31R holding point.
        var hold = w!.Layout.RouteToRunway(w.Layout.Parking.Single(p => p.Index == 106).Position, "31R")!.Route.Geometry[^1];
        var one = w.Put(Hangman(1, hold, 11));
        w.Put(Hangman(2, hold + new Vec2(30, 0), 12));
        await w.Flights.DeclareAsync(one, 2, w.Units);

        await w.Ground.RequestTaxiToParkingAsync(one, w.Units);
        Assert.StartsWith("Hangman 1 flight, Al Dhafra Ground, taxi to ", w.LastText);
        var clearance = w.Ground.Clearances[11];
        Assert.Equal(new[] { "park-11-1-1", "park-11-1-2" }, w.Ground.SpotMarkKeys.Order());
        Assert.Equal(("1-1", "1-2"), (Label(w, "park-11-1-1"), Label(w, "park-11-1-2")));
        var leadSpot = Centre(w, "park-11-1-1");
        var wingSpot = Centre(w, "park-11-1-2");
        Assert.True(leadSpot.DistanceTo(clearance.Spot!.Position) < 1);
        Assert.InRange(wingSpot.DistanceTo(leadSpot), 5, GroundControl.MemberSpotRange);

        // Another aircraft taxiing in from the same place is not given either of them.
        var eagle = w.Put(Aircraft("Eagle 1-1", 5, hold + new Vec2(-30, 30), player: "Eagle 1-1 | C"));
        await w.Ground.RequestTaxiToParkingAsync(eagle, w.Units);
        var eagleSpot = w.Ground.Clearances[5].Spot!.Position;
        Assert.True(eagleSpot.DistanceTo(leadSpot) > 20 && eagleSpot.DistanceTo(wingSpot) > 20);
        await w.Ground.CancelTaxiAsync(eagle, w.Units);
        w.Units.Remove("Eagle 1-1");

        // The lead parks: parking reached, its mark goes; number two's stays until it is on its spot.
        w.Put(Hangman(1, clearance.Spot!.Position, 11));
        w.Put(Hangman(2, clearance.Spot.Position + new Vec2(60, 0), 12, speed: 5));
        await w.Ground.OnUnitsAsync(w.Units);
        Assert.Equal("Hangman 1 flight, Al Dhafra Ground, parking reached.", w.LastText);
        await w.Ground.OnUnitsAsync(w.Units);
        Assert.Equal(new[] { "park-11-1-2" }, w.Ground.SpotMarkKeys);
        w.Put(Hangman(2, wingSpot, 12));
        await w.Ground.OnUnitsAsync(w.Units);
        Assert.Empty(w.Ground.SpotMarkKeys);
        Assert.False(w.Dcs.Drawings.ContainsKey("park-11-1-2"));
    }

    [SkippableFact]
    public async Task A_helicopter_flight_gets_a_landing_spot_for_every_member()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var spot = HeliSpot(w!);
        var from = spot + Vec2.FromHeading(90) * 5 * Nm;
        var one = w!.Put(HangmanHeli(1, from, 11, inAir: true, agl: 150, hdg: 270, speed: 50));
        w.Put(HangmanHeli(2, from + new Vec2(60, 60), 12, inAir: true, agl: 150, hdg: 270, speed: 50));
        await w.Flights.DeclareAsync(one, 2, w.Units);

        await w.Tower.RequestLandingAsync(one, w.Units);
        Assert.Matches(@"^Hangman 1 flight, Al Dhafra Tower, make straight-in to .+, cleared to land\.$", w.LastText);
        Assert.Equal("LANDING SPOT 1-1", Label(w, "heli-11"));
        Assert.Equal("LANDING SPOT 1-2", Label(w, "heli-11-1-2"));
        var wingSpot = Centre(w, "heli-11-1-2");
        Assert.InRange(wingSpot.DistanceTo(spot), 5, TowerControl.MemberSpotRange);

        // The lead down: Tower is done with the flight, number two keeps its mark until it is down too.
        w.Put(HangmanHeli(1, spot, 11));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.False(w.Dcs.Drawings.ContainsKey("heli-11"));
        Assert.True(w.Dcs.Drawings.ContainsKey("heli-11-1-2"));
        w.Put(HangmanHeli(2, wingSpot, 12));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.False(w.Dcs.Drawings.ContainsKey("heli-11-1-2"));
    }

    [SkippableFact]
    public async Task A_helicopter_flight_gets_a_pad_for_every_member_at_a_farp()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var site = TestFob(w!);
        var from = Pad(site, 1) + Vec2.FromHeading(270) * 3 * Nm;
        var one = w!.Put(HangmanHeli(1, from, 11, inAir: true, agl: 150, hdg: 90, speed: 50));
        w.Put(HangmanHeli(2, from + new Vec2(60, -60), 12, inAir: true, agl: 150, hdg: 90, speed: 50));
        await w.Flights.DeclareAsync(one, 2, w.Units);

        await w.Farps.RequestLandingAsync(one, w.Units);
        Assert.Equal("Hangman 1 flight, FOB Test, make straight-in to pads 1 and 2, wind 310 degrees, 10 knots, cleared to land.", w.LastText);
        Assert.Equal("PAD 1 1-1", Label(w, "pad-11"));
        Assert.Equal("PAD 2 1-2", Label(w, "pad-11-1-2"));

        // Another helicopter asking now is not sent to either pad.
        w.Put(Heli2(from, inAir: true, agl: 150, hdg: 90, speed: 50));
        await w.Farps.RequestLandingAsync(w.Units["Viper 2-1"], w.Units);
        Assert.Contains("pad 3", w.LastText);

        // Number two keeps its pad after the lead is down.
        w.Put(HangmanHeli(1, Pad(site, 1), 11));
        await w.Farps.OnUnitsAsync(w.Units);
        Assert.True(w.Dcs.Drawings.ContainsKey("pad-11-1-2"));
        w.Put(HangmanHeli(2, Pad(site, 2), 12));
        await w.Farps.OnUnitsAsync(w.Units);
        Assert.False(w.Dcs.Drawings.ContainsKey("pad-11-1-2"));
    }
}
