using DcsAtc.Airfields;
using DcsAtc.Atc;
using DcsAtc.Bridge;

namespace DcsAtc.Tests;

public partial class TowerControlTests
{
    private static AiTraffic Ai(World w, List<string>? log = null) =>
        new(w.Dcs, w.Catalog, w.Ground, w.Tower, w.Approach, log is null ? null : log.Add, () => w.Clock.Now, w.Radio);

    /// <summary>An AI F-16 of group "Enfield n" ("Enfield n-1"), on the ground unless said otherwise.</summary>
    private static UnitState Enfield(int n, Vec2 p, bool inAir = false, double speed = 0) =>
        Aircraft($"Enfield {n}-1", 20 + n, p, inAir, agl: inAir ? 60 : 0, hdg: 308, speed: speed) with { Group = $"Enfield {n}", Callsign = $"Enfield{n}1" };

    private static DcsEventMessage Event(string name, int n, string? place = "Al Dhafra AFB") =>
        new() { Event = name, Unit = $"Enfield {n}-1", Group = $"Enfield {n}", Place = place };

    /// <summary>Parking spots for AI, away from spot 106 (the player's).</summary>
    private static Vec2 AiSpot(World w, int n) => w.Layout.Parking.Where(p => p.Index != 106 && p.TermType != 40).OrderBy(p => p.Index).ElementAt(n).Position;

    /// <summary>Enfield n starts its engines at a parking spot (how every AI departure begins).</summary>
    private static async Task EnginesAsync(World w, AiTraffic ai, int n, Vec2 spot)
    {
        w.Put(Enfield(n, spot));
        ai.OnEvent(Event("engine_startup", n));
        await ai.OnUnitsAsync(w.Units);
    }

    [SkippableFact]
    public async Task Ai_departure_with_nobody_in_the_way_goes_and_is_called_by_Ground_and_Tower()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var ai = Ai(w!);
        var spot = w!.Layout.Parking.Single(p => p.Index == 106).Position;

        await EnginesAsync(w, ai, 1, spot);
        Assert.Equal(AiDeparturePhase.Taxiing, ai.Departures["Enfield 1"].Phase);
        Assert.Empty(w.Dcs.Held);
        Assert.Empty(w.Radio.Calls);

        // It rolls: Ground tells it where to go.
        w.Put(Enfield(1, spot, speed: 5));
        await ai.OnUnitsAsync(w.Units);
        var call = w.Radio.Calls[^1];
        Assert.Equal(("Al Dhafra Ground", "250", 2, "Enfield 1-1, Al Dhafra Ground, taxi to holding point runway 31R."), (call.Station, string.Join(" ", call.Frequencies), call.Coalition, call.Text));
        Assert.Equal(("Al Dhafra Ground, Enfield 1-1, request taxi.", "Taxi to holding point runway 31R, Enfield 1-1.", true), (call.PilotCall, call.PilotReadback, call.ToAi));

        // At the runway: Tower clears it. Airborne: done.
        w.Put(Enfield(1, w.OnRunway31R(10)));
        await ai.OnUnitsAsync(w.Units);
        Assert.Equal(("Al Dhafra Tower", "Enfield 1-1, wind 310 degrees, 10 knots, runway 31R, cleared for takeoff."), (w.Radio.Calls[^1].Station, w.Radio.Calls[^1].Text));
        Assert.Equal(("Al Dhafra Tower, Enfield 1-1, ready for departure runway 31R.", "Runway 31R, cleared for takeoff, Enfield 1-1."), (w.Radio.Calls[^1].PilotCall, w.Radio.Calls[^1].PilotReadback));
        w.Put(Enfield(1, w.OnRunway31R(2500), inAir: true, speed: 90));
        await ai.OnUnitsAsync(w.Units);
        Assert.Empty(ai.Departures);
        Assert.Equal(2, w.Radio.Calls.Count);
    }

    [SkippableFact]
    public async Task Ai_is_held_for_a_player_taxiing_out_but_not_more_than_five_minutes()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var log = new List<string>();
        var ai = Ai(w!, log);

        await w!.Ground.RequestTaxiAsync(w.Put(Viper(w.Layout.Parking.Single(p => p.Index == 106).Position)), w.Units);
        await EnginesAsync(w, ai, 1, AiSpot(w, 0));
        Assert.Equal(AiDeparturePhase.Held, ai.Departures["Enfield 1"].Phase);
        Assert.Equal(["Enfield 1"], w.Dcs.Held);
        Assert.Contains("AI Enfield 1-1 (F-16C_50) at Al Dhafra: held, Viper 1-1 taxiing out", log);

        w.Clock.Now += AiTraffic.MaxWaitForPlayers - TimeSpan.FromSeconds(1);
        await ai.OnUnitsAsync(w.Units);
        Assert.Empty(w.Dcs.Released);

        w.Clock.Now += TimeSpan.FromSeconds(1);
        await ai.OnUnitsAsync(w.Units);
        Assert.Equal(["Enfield 1"], w.Dcs.Released);
        Assert.Equal(AiDeparturePhase.Taxiing, ai.Departures["Enfield 1"].Phase);
        Assert.Contains("AI Enfield 1-1: released after 300 s (had waited: Viper 1-1 taxiing out)", log);

        // Started again by DCS: its second engine start changes nothing.
        ai.OnEvent(Event("engine_startup", 1));
        await ai.OnUnitsAsync(w.Units);
        Assert.Equal(AiDeparturePhase.Taxiing, ai.Departures["Enfield 1"].Phase);
        Assert.Empty(w.Dcs.Held);
        Assert.Single(w.Dcs.Released);
    }

    [SkippableFact]
    public async Task Ai_is_held_as_long_as_a_player_takes_off()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var log = new List<string>();
        var ai = Ai(w!, log);

        await TaxiToHoldingPointAsync(w!);
        await w!.Tower.ReadyForDepartureAsync(w.Units["Viper 1-1"], w.Units);
        Assert.Equal(TowerPhase.ClearedForTakeoff, w.Tower.Flights[1].Phase);
        await EnginesAsync(w, ai, 1, AiSpot(w, 0));
        w.Clock.Now += TimeSpan.FromMinutes(10);
        await ai.OnUnitsAsync(w.Units);
        Assert.Equal(AiDeparturePhase.Held, ai.Departures["Enfield 1"].Phase);
        Assert.Contains("AI Enfield 1-1 (F-16C_50) at Al Dhafra: held, Viper 1-1 taking off", log);
        Assert.Empty(w.Dcs.Released);

        // Airborne and handed off: the AI may go.
        w.Put(Viper(w.OnRunway31R(2500), inAir: true, agl: 60, hdg: 308, speed: 90, climb: 10));
        await w.Tower.OnUnitsAsync(w.Units);
        w.Put(Viper(w.OnRunway31R(9000), inAir: true, agl: 1200, hdg: 308, speed: 150));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Empty(w.Tower.Flights);
        await ai.OnUnitsAsync(w.Units);
        Assert.Equal(["Enfield 1"], w.Dcs.Released);
        Assert.Equal(AiDeparturePhase.Taxiing, ai.Departures["Enfield 1"].Phase);
    }

    [SkippableFact]
    public async Task At_most_two_AI_groups_taxi_at_once()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var log = new List<string>();
        var ai = Ai(w!, log);

        for (int n = 1; n <= 3; n++) await EnginesAsync(w!, ai, n, AiSpot(w!, n));
        Assert.Equal([AiDeparturePhase.Taxiing, AiDeparturePhase.Taxiing, AiDeparturePhase.Held], new[] { 1, 2, 3 }.Select(n => ai.Departures[$"Enfield {n}"].Phase));
        Assert.Contains("AI Enfield 3-1 (F-16C_50) at Al Dhafra: held, 2 AI taxiing", log);

        w!.Put(Enfield(1, w.OnRunway31R(2500), inAir: true, speed: 90));
        ai.OnEvent(Event("takeoff", 1));
        await ai.OnUnitsAsync(w.Units);
        Assert.Equal(["Enfield 3"], w.Dcs.Released);
        Assert.Equal(AiDeparturePhase.Taxiing, ai.Departures["Enfield 3"].Phase);
    }

    [SkippableFact]
    public async Task Ai_that_cannot_be_held_is_left_to_DCS()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var log = new List<string>();
        var ai = Ai(w!, log);
        await w!.Ground.RequestTaxiAsync(w.Put(Viper(w.Layout.Parking.Single(p => p.Index == 106).Position)), w.Units);

        // Spawned by a script: not in the mission file.
        w.Dcs.NotInMission.Add("Enfield 1");
        await EnginesAsync(w, ai, 1, AiSpot(w, 0));
        Assert.Equal(AiDeparturePhase.Taxiing, ai.Departures["Enfield 1"].Phase);
        Assert.Contains("AI Enfield 1-1: cannot be held (not in the mission file: Enfield 1), left to DCS", log);

        // A bridge from before AI traffic: AI traffic is switched off.
        w.Dcs.OldBridge = true;
        await EnginesAsync(w, ai, 2, AiSpot(w, 1));
        Assert.Contains(log, l => l.StartsWith("AI traffic off: the DCS bridge does not support it"));
        await EnginesAsync(w, ai, 3, AiSpot(w, 2));
        Assert.False(ai.Departures.ContainsKey("Enfield 3"));
        Assert.Empty(w.Dcs.Held);
    }

    [SkippableFact]
    public async Task Leaves_alone_players_helicopters_and_AI_away_from_parking_or_at_other_airfields()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var ai = Ai(w!);
        await w!.Ground.RequestTaxiAsync(w.Put(Viper(w.Layout.Parking.Single(p => p.Index == 106).Position)), w.Units);

        // On the runway (a runway start), and already rolling.
        await EnginesAsync(w, ai, 1, w.OnRunway31R(10));
        w.Put(Enfield(2, AiSpot(w, 1), speed: 5));
        ai.OnEvent(Event("engine_startup", 2));
        await ai.OnUnitsAsync(w.Units);
        // A helicopter, and an airfield without chart data.
        w.Put(Enfield(3, AiSpot(w, 2)) with { Cat = "heli" });
        ai.OnEvent(Event("engine_startup", 3));
        w.Put(Enfield(4, AiSpot(w, 3)));
        ai.OnEvent(Event("engine_startup", 4, place: "Al Minhad AFB"));
        await ai.OnUnitsAsync(w.Units);
        // A player.
        ai.OnEvent(new DcsEventMessage { Event = "engine_startup", Unit = "Viper 1-1", Group = "Viper 1-1", Player = "Viper 1-1 | DiGii", Place = "Al Dhafra AFB" });
        await ai.OnUnitsAsync(w.Units);

        Assert.Empty(ai.Departures);
        Assert.Empty(w.Dcs.Held);
    }
}
