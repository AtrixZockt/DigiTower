using DcsAtc.Airfields;
using DcsAtc.Atc;
using DcsAtc.Bridge;

namespace DcsAtc.Tests;

public partial class TowerControlTests
{
    private static AiArrivals Arrivals(World w, List<string>? log = null) =>
        new(w.Dcs, w.Catalog, w.Tower, w.Approach, w.Approach.Stacks, log is null ? null : log.Add, () => w.Clock.Now, w.Radio);

    /// <summary>An AI F-16 of group "Colt n" in the air, its mission route landing at Al Dhafra.</summary>
    private static UnitState Colt(World w, int n, Vec2 p, double feet = 6000, double hdg = 128, double speed = 150)
    {
        w.Dcs.Destinations[$"Colt {n}"] = new GroupDestination { InMission = true, Id = 4, Name = "Al Dhafra AFB" };
        return Aircraft($"Colt {n}-1", 60 + n, p, inAir: true, agl: feet * Ft - 16, hdg: hdg, speed: speed) with { Group = $"Colt {n}", Callsign = $"Colt{n}1" };
    }

    [SkippableFact]
    public async Task Ai_arrival_with_nobody_around_comes_straight_in_and_is_cleared_to_land()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var log = new List<string>();
        var arrivals = Arrivals(w!, log);

        // Spawned in the air 25 NM out: an arrival at once.
        w!.Put(Colt(w, 1, FromField(w, 128, 25)));
        await arrivals.OnUnitsAsync(w.Units);
        var a = arrivals.Arrivals["Colt 1"];
        Assert.Equal((AiArrivalPhase.Inbound, "31R"), (a.Phase, a.End!.Name));
        Assert.Empty(w.Dcs.Holding);
        Assert.Matches(@"^Colt 1-1, Al Dhafra Approach, radar contact, 2\d miles southeast of Al Dhafra\. Proceed straight in runway 31R\.$", w.Radio.Calls[^1].Text);
        // The AI pilot calls first and reads back (in the AI pilots' voice); no room is kept for a player's readback.
        Assert.Matches(@"^Al Dhafra Approach, Colt 1-1, 2\d miles southeast of Al Dhafra, inbound for landing\.$", w.Radio.Calls[^1].PilotCall);
        Assert.Equal(("Straight in runway 31R, Colt 1-1.", true), (w.Radio.Calls[^1].PilotReadback, w.Radio.Calls[^1].ToAi));

        // On final: Tower clears it; down: forgotten.
        w.Put(Colt(w, 1, FromField(w, 128, 7), feet: 2000, hdg: 308));
        await arrivals.OnUnitsAsync(w.Units);
        Assert.Equal(("Al Dhafra Tower", "Colt 1-1, runway 31R, wind 310 degrees, 10 knots, cleared to land."), (w.Radio.Calls[^1].Station, w.Radio.Calls[^1].Text));
        Assert.Equal(("Al Dhafra Tower, Colt 1-1, final runway 31R.", "Cleared to land runway 31R, Colt 1-1."), (w.Radio.Calls[^1].PilotCall, w.Radio.Calls[^1].PilotReadback));
        arrivals.OnEvent(new DcsEventMessage { Event = "land", Unit = "Colt 1-1", Group = "Colt 1", Place = "Al Dhafra AFB" });
        Assert.Empty(arrivals.Arrivals);
        Assert.Contains("AI Colt 1-1: landed at Al Dhafra", log);
    }

    [SkippableFact]
    public async Task Ai_departure_is_an_arrival_only_once_it_comes_back_from_far_out()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var arrivals = Arrivals(w!);

        w!.Put(Colt(w, 1, w.OnRunway31R(10)) with { InAir = false, Agl = 0, Vx = 0, Vz = 0 });
        await arrivals.OnUnitsAsync(w.Units);
        w.Put(Colt(w, 1, FromField(w, 308, 10), feet: 5000, hdg: 308));
        await arrivals.OnUnitsAsync(w.Units);
        Assert.Empty(arrivals.Arrivals);   // climbing out, not coming in

        w.Put(Colt(w, 1, FromField(w, 308, 45), feet: 20000, hdg: 308));
        await arrivals.OnUnitsAsync(w.Units);
        w.Put(Colt(w, 1, FromField(w, 308, 29), feet: 15000, hdg: 128));
        await arrivals.OnUnitsAsync(w.Units);
        Assert.True(arrivals.Arrivals.ContainsKey("Colt 1"));
    }

    [SkippableFact]
    public async Task Ai_arrival_holds_while_a_player_is_on_approach_and_leaves_the_hold_after_him()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var log = new List<string>();
        var arrivals = Arrivals(w!, log);

        var viper = w!.Put(Viper(FromField(w, 308, 30), inAir: true, agl: 8000 * Ft, hdg: 128, speed: 150));
        await w.Approach.RequestApproachAsync(viper, w.Units, ApproachType.Ils);
        w.Put(Colt(w, 1, FromField(w, 30, 28), feet: 16000, hdg: 210));
        await arrivals.OnUnitsAsync(w.Units);

        var a = arrivals.Arrivals["Colt 1"];
        Assert.Equal((AiArrivalPhase.Holding, 7000.0, "Viper 1-1 on approach"), (a.Phase, a.HoldAltitude, a.HeldFor));
        var hold = Assert.Single(w.Dcs.Holding);
        Assert.Equal(("Colt 1", 4), (hold.Value.Group, hold.Value.Airdrome));
        Assert.Equal(7000 * Ft, hold.Value.Hold.Alt, 1);
        Assert.NotNull(hold.Value.Hold.X2);   // a race track
        Assert.Equal(16 + 5 * Nm * Math.Tan(3 * Math.PI / 180), hold.Value.Land.Alt, 0);   // the landing point on the glide path, as measured
        Assert.Matches(@"^Colt 1-1, Al Dhafra Approach, radar contact, .*\. Hold over the Al Dhafra TACAN, right turns, maintain 7000 feet\. Expect further clearance in 5 minutes\.$", w.Radio.Calls[^1].Text);

        // The player is still flying the approach: the AI keeps holding.
        await arrivals.OnUnitsAsync(w.Units);
        Assert.Empty(w.Dcs.Cleared);

        // The player has landed and vacated the runway: the AI leaves the hold.
        w.Put(Viper(w.Layout.Parking.Single(p => p.Index == 106).Position, hdg: 308, speed: 5));
        await w.Approach.OnUnitsAsync(w.Units);
        await arrivals.OnUnitsAsync(w.Units);
        Assert.Equal([a.Flag], w.Dcs.Cleared);
        Assert.Equal(AiArrivalPhase.Cleared, a.Phase);
        Assert.Equal("Colt 1-1, leave the hold, proceed to final runway 31R, descend and maintain 3000 feet.", w.Radio.Calls[^1].Text);
        Assert.Equal((null, "Leaving the hold, final runway 31R, 3000 feet, Colt 1-1."), (w.Radio.Calls[^1].PilotCall, w.Radio.Calls[^1].PilotReadback));
        Assert.Empty(w.Approach.Stacks.At(4));

        // Out of the hold over the field it is near the runway, but not on final yet: no landing clearance until it is.
        w.Put(Colt(w, 1, FromField(w, 30, 2), feet: 6500, hdg: 308));
        await arrivals.OnUnitsAsync(w.Units);
        Assert.False(a.LandingCalled);
        w.Put(Colt(w, 1, FromField(w, 128, 7), feet: 2000, hdg: 308));
        await arrivals.OnUnitsAsync(w.Units);
        Assert.Equal("Colt 1-1, runway 31R, wind 310 degrees, 10 knots, cleared to land.", w.Radio.Calls[^1].Text);
    }

    [SkippableFact]
    public async Task Ai_arrival_never_holds_longer_than_ten_minutes()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var arrivals = Arrivals(w!);

        // A player is on approach the whole time.
        var viper = w!.Put(Viper(FromField(w, 308, 30), inAir: true, agl: 8000 * Ft, hdg: 128, speed: 150));
        await w.Approach.RequestApproachAsync(viper, w.Units, ApproachType.Ils);
        w.Put(Colt(w, 1, FromField(w, 30, 28), feet: 16000, hdg: 210));
        await arrivals.OnUnitsAsync(w.Units);
        Assert.Equal(AiArrivalPhase.Holding, arrivals.Arrivals["Colt 1"].Phase);

        w.Clock.Now += AiArrivals.MaxHold - TimeSpan.FromSeconds(1);
        await arrivals.OnUnitsAsync(w.Units);
        Assert.Empty(w.Dcs.Cleared);
        w.Clock.Now += TimeSpan.FromSeconds(1);
        await arrivals.OnUnitsAsync(w.Units);
        Assert.Single(w.Dcs.Cleared);
    }

    [SkippableFact]
    public async Task Ai_arrivals_come_in_one_at_a_time_and_share_the_stack_with_players()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var arrivals = Arrivals(w!);

        // Colt 1 comes straight in; Colt 2 and 3 arrive while it is still far out: they hold, stacked.
        w!.Put(Colt(w, 1, FromField(w, 128, 25)));
        await arrivals.OnUnitsAsync(w.Units);
        w.Put(Colt(w, 2, FromField(w, 30, 28), feet: 16000, hdg: 210));
        w.Put(Colt(w, 3, FromField(w, 250, 27), feet: 14000, hdg: 70));
        await arrivals.OnUnitsAsync(w.Units);
        Assert.Equal(AiArrivalPhase.Inbound, arrivals.Arrivals["Colt 1"].Phase);
        Assert.Equal((AiArrivalPhase.Holding, 7000.0, "Colt 1-1 on approach"),
            (arrivals.Arrivals["Colt 2"].Phase, arrivals.Arrivals["Colt 2"].HoldAltitude, arrivals.Arrivals["Colt 2"].HeldFor));
        Assert.Equal(8000.0, arrivals.Arrivals["Colt 3"].HoldAltitude);

        // A player told to hold gets the next level up.
        Assert.Equal(9000.0, w.Approach.Stacks.Take(4, "player:99", 7000, 1000));
        w.Approach.Stacks.Leave("player:99");

        // Colt 1 close in: the lowest holder goes, the other stays.
        w.Put(Colt(w, 1, FromField(w, 128, 3), feet: 1000, hdg: 308));
        await arrivals.OnUnitsAsync(w.Units);
        Assert.Equal(AiArrivalPhase.Cleared, arrivals.Arrivals["Colt 2"].Phase);
        Assert.Equal(AiArrivalPhase.Holding, arrivals.Arrivals["Colt 3"].Phase);
        Assert.Equal([(8000.0, "ai:Colt 3")], w.Approach.Stacks.At(4));
    }

    [SkippableFact]
    public async Task Ai_from_scripts_and_old_bridges_are_left_to_DCS()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var log = new List<string>();
        var arrivals = Arrivals(w!, log);

        w!.Put(Colt(w, 1, FromField(w, 128, 25)));
        w.Dcs.NotInMission.Add("Colt 1");
        await arrivals.OnUnitsAsync(w.Units);
        Assert.Empty(arrivals.Arrivals);

        w.Dcs.OldBridge = true;
        w.Put(Colt(w, 2, FromField(w, 30, 28)));
        await arrivals.OnUnitsAsync(w.Units);
        Assert.Empty(arrivals.Arrivals);
        Assert.Contains(log, l => l.StartsWith("AI arrivals off: the DCS bridge does not support them", StringComparison.Ordinal));
    }
}
