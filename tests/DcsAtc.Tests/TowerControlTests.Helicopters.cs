using DcsAtc.Airfields;
using DcsAtc.Atc;
using DcsAtc.Bridge;

namespace DcsAtc.Tests;

public partial class TowerControlTests
{
    /// <summary>Viper 1-1 as a helicopter (an Apache).</summary>
    private static UnitState Heli(Vec2 p, bool inAir = false, double agl = 0, double hdg = 0, double speed = 0) =>
        Viper(p, inAir, agl, hdg, speed) with { Cat = "heli", Type = "AH-64D_BLK_II" };

    /// <summary>Al Dhafra's helicopter-only spot.</summary>
    private static Vec2 HeliSpot(World w) => w.Layout.Parking.Single(p => p.Index == 177).Position;

    [SkippableFact]
    public async Task Helicopter_air_taxis_low_and_keeps_its_clearance_while_hovering()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        await w!.Ground.RequestTaxiAsync(w.Put(Heli(HeliSpot(w))), w.Units);
        Assert.Equal("Viper 1-1, Al Dhafra Ground, air-taxi to holding point runway 31L via U9. Wind 310 degrees, 10 knots, QNH 1013, altimeter 29.92.", w.LastText);
        var route = w.Ground.Clearances[1].Route.Geometry;
        var along = route[route.Count / 2];

        // In a hover DCS says "in the air" (and may report a takeoff): the clearance stays; air-taxi speed is its own business.
        w.Put(Heli(along, inAir: true, agl: 10, speed: 20));
        await w.Ground.OnEventAsync(new DcsEventMessage { Event = "takeoff", Unit = "Viper 1-1", Group = "Viper 1-1" });
        await w.Ground.OnUnitsAsync(w.Units);
        Assert.Single(w.Ground.Clearances);
        Assert.DoesNotContain(w.Dcs.Texts, t => t.Text.Contains("speed"));

        // Too high: told to stay low. Higher still it has left the field: the clearance ends.
        w.Put(Heli(along, inAir: true, agl: 60, speed: 20));
        await w.Ground.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, Al Dhafra Ground, remain at or below 100 feet.", w.LastText);
        w.Put(Heli(along, inAir: true, agl: 120, speed: 20));
        await w.Ground.OnUnitsAsync(w.Units);
        Assert.Empty(w.Ground.Clearances);
    }

    [SkippableFact]
    public async Task Every_parking_spot_counts_as_at_the_airfield_however_far_from_its_reference_point()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        // Spot 176 is 6.1 km from Al Dhafra's reference point (the helicopter spot 4.9 km).
        var far = w!.Layout.Parking.Single(p => p.Index == 176).Position;
        await w.Ground.RequestTaxiAsync(w.Put(Viper(far)), w.Units);
        Assert.StartsWith("Viper 1-1, Al Dhafra Ground, taxi to holding point runway", w.LastText);
        Assert.NotNull(await w.Catalog.AtAsync(HeliSpot(w)));
        Assert.Null(await w.Catalog.AtAsync(far + Vec2.FromHeading(0) * 8000));
    }

    [SkippableFact]
    public async Task Helicopter_departs_from_present_position_with_a_turn_for_its_direction()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var spot = HeliSpot(w!);
        w!.Put(Heli(spot, hdg: 270));
        await w.Tower.ReadyForDepartureAsync(w.Units["Viper 1-1"], w.Units, DepartureIntent.North);
        Assert.Equal("Viper 1-1, Al Dhafra Tower, from present position, make right turn for north departure, wind 310 degrees, 10 knots, cleared for takeoff.", w.LastText);
        Assert.True(w.Tower.Flights[1].FromPresentPosition);

        // A hover is not the departure; climbing away is; clear of the field it goes to Approach.
        w.Put(Heli(spot, inAir: true, agl: 10, hdg: 0));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal(TowerPhase.ClearedForTakeoff, w.Tower.Flights[1].Phase);
        w.Put(Heli(spot + Vec2.FromHeading(0) * 500, inAir: true, agl: 60, hdg: 0, speed: 40));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal(TowerPhase.Departed, w.Tower.Flights[1].Phase);
        w.Put(Heli(spot + Vec2.FromHeading(0) * 3000, inAir: true, agl: 1000, hdg: 0, speed: 60));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, contact Approach on 257.0, good day.", w.LastText);
    }

    [SkippableFact]
    public async Task Helicopter_parked_near_a_runway_still_departs_from_where_it_stands()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        // Spot 245 is 215 m from a runway (31 of Al Dhafra's spots are within 400 m of one).
        var spot = w!.Layout.Parking.Single(p => p.Index == 245).Position;
        Assert.True(w.Layout.Runways.Min(r => r.DistanceFrom(spot)) < TowerControl.HoldingPointRange);
        var heli = w.Put(Heli(spot, hdg: 0));

        var entries = await w.Menu.ForAsync(heli, w.Units);
        Assert.Contains(entries, e => e.Path[^1] == "Request departure");
        Assert.DoesNotContain(entries, e => e.Path[^1] == "Ready for departure");

        await w.Tower.ReadyForDepartureAsync(heli, w.Units);
        Assert.Equal("Viper 1-1, Al Dhafra Tower, from present position, wind 310 degrees, 10 knots, cleared for takeoff.", w.LastText);
        Assert.True(w.Tower.Flights[1].FromPresentPosition);
    }

    [SkippableFact]
    public async Task Helicopter_departure_across_a_runway_in_use_holds_position()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        // 800 m to the right of runway 31R (north-east of it, away from 31L): south crosses it, north does not.
        var beside = w!.OnRunway31R(1500) + Vec2.FromHeading(w.End31R.TrueHeading + 90) * 800;
        w.Put(Aircraft("Hornet 1-1", 7, w.OnFinal31R(3 * Nm), inAir: true, agl: 300, hdg: w.End31R.TrueHeading, speed: 80));
        w.Put(Heli(beside, hdg: 90));
        var other = w.Put(Aircraft("Viper 2-1", 2, beside + Vec2.FromHeading(w.End31R.TrueHeading) * 300, player: "Viper 2-1 | B", hdg: 0)
            with { Cat = "heli", Type = "UH-60L" });

        await w.Tower.ReadyForDepartureAsync(w.Units["Viper 1-1"], w.Units, DepartureIntent.South);
        Assert.Equal("Viper 1-1, Al Dhafra Tower, hold position, traffic on final.", w.LastText);
        await w.Tower.ReadyForDepartureAsync(other, w.Units, DepartureIntent.North);
        Assert.Equal("Viper 2-1, Al Dhafra Tower, from present position, north departure, wind 310 degrees, 10 knots, cleared for takeoff.", w.LastText);

        // The traffic has landed and gone: Viper 1-1 may cross.
        w.Units.Remove("Hornet 1-1");
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, Al Dhafra Tower, from present position, make right turn for south departure, wind 310 degrees, 10 knots, cleared for takeoff.",
            w.Dcs.Texts.Last(t => t.Text.StartsWith("Viper 1-1")).Text);
    }

    [SkippableFact]
    public async Task Helicopter_lands_straight_in_at_a_helicopter_spot_without_holding_up_the_runway()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var spot = HeliSpot(w!);
        w!.Put(Heli(spot + Vec2.FromHeading(90) * 5 * Nm, inAir: true, agl: 150, hdg: 270, speed: 50));
        await w.Tower.RequestLandingAsync(w.Units["Viper 1-1"], w.Units);
        Assert.Matches(@"^Viper 1-1, Al Dhafra Tower, make straight-in to .+, wind 310 degrees, 10 knots, cleared to land\.$", w.LastText);
        Assert.Equal((TowerPhase.ClearedToLand, 177), (w.Tower.Flights[1].Phase, w.Tower.Flights[1].Spot?.Index));
        Assert.True(w.Dcs.Drawings.ContainsKey("heli-1"));

        // A jet ready at the runway is cleared: the helicopter does not use it.
        var jet = w.Put(Aircraft("Eagle 1-1", 5, w.OnRunway31R(0) + Vec2.FromHeading(w.End31R.TrueHeading + 90) * 150, player: "Eagle 1-1 | C"));
        await w.Tower.ReadyForDepartureAsync(jet, w.Units);
        Assert.EndsWith("runway 31R, cleared for takeoff.", w.LastText);

        // Down at the spot: Tower is done with it.
        w.Put(Heli(spot));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.False(w.Tower.Flights.ContainsKey(1));
        Assert.False(w.Dcs.Drawings.ContainsKey("heli-1"));
    }

    [SkippableFact]
    public async Task Helicopter_menu_offers_departure_from_present_position_and_landing_while_hovering()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var spot = HeliSpot(w!);

        var entries = await w!.Menu.ForAsync(w.Put(Heli(spot)), w.Units);
        Assert.Contains(entries, e => e.Path[^1] == "Request departure" && e.Label == "Departure north");
        Assert.DoesNotContain(entries, e => e.Label == "Closed traffic");
        Assert.Contains(entries, e => e.Label == "Request taxi");

        // Hovering low over the field: still the ground calls, and landing; no instrument approaches.
        entries = await w.Menu.ForAsync(w.Put(Heli(spot, inAir: true, agl: 10)), w.Units);
        Assert.Contains(entries, e => e.Label == "Request taxi");
        Assert.Contains(entries, e => e.Label == "Request landing");
        Assert.DoesNotContain(entries, e => e.Label == "Request startup");
        Assert.DoesNotContain(entries, e => e.Path[^1] == "Request approach");

        // Arriving: landing, but no overhead initial.
        entries = await w.Menu.ForAsync(w.Put(Heli(spot + Vec2.FromHeading(90) * 10 * Nm, inAir: true, agl: 300, hdg: 270, speed: 50)), w.Units);
        Assert.Contains(entries, e => e.Label == "Request landing");
        Assert.DoesNotContain(entries, e => e.Label == "Report initial");
    }
}
