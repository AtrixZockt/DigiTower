using DcsAtc.Airfields;
using DcsAtc.Atc;
using DcsAtc.Bridge;

namespace DcsAtc.Tests;

public partial class TowerControlTests
{
    /// <summary>
    /// Fujairah (real DCS layout and chart names) with two parking spots by link D and the wind from 113° (runway 11):
    /// no taxiway leads to either threshold, D joins the runway 1.4 km from the 11 threshold.
    /// </summary>
    private static Task<World?> FujairahAsync()
    {
        var info = new AirbaseInfo
        {
            Name = "Fujairah Intl", Id = 7, Category = 0, Coalition = 2, X = -117499, Z = 7860,
            Runways = [new RunwayInfo { Name = "11" }],
            Parking = [new ParkingInfo { Index = 1, TermType = 104, X = -117254, Z = 8562 }, new ParkingInfo { Index = 2, TermType = 104, X = -117377, Z = 8492 }],
        };
        return CreateInAsync(Path.Combine(TestPaths.RepoRoot, "data"), info.Name, info.Id, null, [], info, windFrom: 113);
    }

    private static Vec2 OnRunway11(World w, double metres)
    {
        var end = w.Layout.FindRunwayEnd("11")!.Value.End;
        return end.Threshold + Vec2.FromHeading(end.TrueHeading) * metres;
    }

    [SkippableFact]
    public async Task Without_a_taxiway_to_the_threshold_tower_has_the_departure_backtrack()
    {
        var w = await FujairahAsync();
        Skip.If(w is null, "DCS install not available");

        // Ground: only to D's holding point, not along the runway.
        await w!.Ground.RequestTaxiAsync(w.Put(Viper(new Vec2(-117254, 8562))), w.Units);
        Assert.Contains("taxi to holding point runway 11 via D.", w.LastText);
        var hold = w.Ground.Clearances[1].Route.Geometry[^1];
        w.Put(Viper(hold));
        await w.Ground.OnUnitsAsync(w.Units);

        // Tower: backtrack and line up, not yet the takeoff.
        await w.Tower.ReadyForDepartureAsync(w.Units["Viper 1-1"], w.Units);
        Assert.Equal("Viper 1-1, runway 11, backtrack and line up.", w.LastText);
        Assert.Equal(TowerPhase.LineUpAndWait, w.Tower.Flights[1].Phase);
        Assert.True(w.Tower.Flights[1].Backtrack);

        // Backtracking along the runway: nothing yet.
        int texts = w.Dcs.Texts.Count;
        w.Put(Viper(OnRunway11(w, 800), hdg: 293, speed: 8));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal(texts, w.Dcs.Texts.Count);

        // Lined up at the threshold: cleared for takeoff.
        w.Put(Viper(OnRunway11(w, 120), hdg: 113));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.EndsWith("runway 11, cleared for takeoff.", w.LastText);
        Assert.Equal(TowerPhase.ClearedForTakeoff, w.Tower.Flights[1].Phase);
    }

    [SkippableFact]
    public async Task A_backtrack_waits_for_landing_traffic_further_out()
    {
        var w = await FujairahAsync();
        Skip.If(w is null, "DCS install not available");

        await w!.Ground.RequestTaxiAsync(w.Put(Viper(new Vec2(-117254, 8562))), w.Units);
        w.Put(Viper(w.Ground.Clearances[1].Route.Geometry[^1]));
        await w.Ground.OnUnitsAsync(w.Units);
        // 8 NM on final for 11: a normal departure would go, a backtrack of 1.4 km waits.
        var end = w.Layout.FindRunwayEnd("11")!.Value.End;
        w.Put(Aircraft("Tanker", 3, end.Threshold - Vec2.FromHeading(end.TrueHeading) * (8 * Nm), inAir: true, agl: 2000 * Ft, hdg: 113, speed: 120));
        await w.Tower.ReadyForDepartureAsync(w.Units["Viper 1-1"], w.Units);
        Assert.Contains("hold short runway 11", w.LastText);
        Assert.Equal(TowerPhase.WaitingForDeparture, w.Tower.Flights[1].Phase);
    }

    [SkippableFact]
    public async Task After_landing_past_the_way_off_tower_has_the_arrival_backtrack()
    {
        var w = await FujairahAsync();
        Skip.If(w is null, "DCS install not available");

        var end = w!.Layout.FindRunwayEnd("11")!.Value.End;
        w.Put(Viper(end.Threshold - Vec2.FromHeading(end.TrueHeading) * (4 * Nm), inAir: true, agl: 1200 * Ft, hdg: 113, speed: 80));
        await w.Tower.RequestLandingAsync(w.Units["Viper 1-1"], w.Units);
        Assert.EndsWith("cleared to land.", w.LastText);

        // Down before D: D is still ahead.
        w.Put(Viper(OnRunway11(w, 500), hdg: 113, speed: 60));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Contains("vacate runway when able", w.LastText);

        // Rolled past D, slow: back to it.
        w.Put(Viper(OnRunway11(w, 2600), hdg: 113, speed: 8));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, backtrack and vacate via D.", w.LastText);
        int texts = w.Dcs.Texts.Count;
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal(texts, w.Dcs.Texts.Count);
    }
}
