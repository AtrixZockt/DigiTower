using DcsAtc.Atc;

namespace DcsAtc.Tests;

public partial class TowerControlTests
{
    private static Task<bool> DeclareAsync(World w, string unit, EmergencyKind kind) =>
        w.Tower.DeclareEmergencyAsync(w.Units[unit], w.Units, kind, null, null);

    private static List<string> Texts(World w) => w.Dcs.Texts.Select(t => t.Text).ToList();

    [SkippableFact]
    public async Task A_mayday_is_cleared_to_land_at_once_and_silences_the_frequency_until_down()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        w!.Put(Viper(w.OnFinal31R(20000), inAir: true, agl: 900, hdg: 308, speed: 120));
        Assert.True(await DeclareAsync(w, "Viper 1-1", EmergencyKind.Mayday));
        Assert.Contains("Viper 1-1, roger Mayday, nearest airfield Al Dhafra, heading 310, 11 miles, runway 31R, wind 310 degrees, 10 knots, cleared to land.", Texts(w));
        Assert.Equal("All stations, stop transmitting, Mayday.", w.LastText);
        Assert.Equal((TowerPhase.ClearedToLand, EmergencyKind.Mayday), (w.Tower.Flights[1].Phase, w.Tower.Flights[1].Emergency!.Value));
        Assert.True(w.Tower.Silenced(4));

        // Down, still fast: vacate; slowed down: the distress is over.
        w.Put(Viper(w.OnRunway31R(800), hdg: 308, speed: 40));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Contains("vacate runway when able", w.LastText);
        w.Put(Viper(w.OnRunway31R(1500), hdg: 308, speed: 10));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("All stations, distress traffic ended.", w.LastText);
        Assert.False(w.Tower.Silenced(4));
    }

    [SkippableFact]
    public async Task A_mayday_sends_traffic_behind_it_around_and_holds_departures()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        await TaxiToHoldingPointAsync(w!);
        w!.Put(Aircraft("Hornet 1-1", 2, w.OnFinal31R(9000), inAir: true, agl: 450, hdg: 308, speed: 80));
        await w.Tower.RequestLandingAsync(w.Units["Hornet 1-1"], w.Units);
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Hornet 1-1, runway 31R, wind 310 degrees, 10 knots, cleared to land.", w.LastText);

        // A Mayday 3 NM out, closer than the Hornet: the Hornet goes around.
        w.Put(Aircraft("Eagle 1-1", 3, w.OnFinal31R(6000), inAir: true, agl: 300, hdg: 308, speed: 75));
        Assert.True(await DeclareAsync(w, "Eagle 1-1", EmergencyKind.Mayday));
        var texts = Texts(w);
        Assert.Contains("Eagle 1-1, roger Mayday, runway 31R, wind 310 degrees, 10 knots, cleared to land.", texts);
        Assert.Contains("All stations, stop transmitting, Mayday.", texts);
        Assert.Equal("Hornet 1-1, go around, I say again, go around, emergency traffic.", w.LastText);
        Assert.Equal(TowerPhase.Inbound, w.Tower.Flights[2].Phase);

        // The departure waits.
        await w.Tower.ReadyForDepartureAsync(w.Units["Viper 1-1"], w.Units);
        Assert.Equal("Viper 1-1, Al Dhafra Tower, hold short runway 31R, emergency traffic.", w.LastText);
    }

    [SkippableFact]
    public async Task A_pan_pan_lands_next_without_sending_anyone_around()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        w!.Put(Aircraft("Hornet 1-1", 2, w.OnFinal31R(5000), inAir: true, agl: 250, hdg: 308, speed: 75));
        await w.Tower.RequestLandingAsync(w.Units["Hornet 1-1"], w.Units);
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.EndsWith("cleared to land.", w.LastText);

        w.Put(Aircraft("Eagle 1-1", 3, w.OnFinal31R(15000), inAir: true, agl: 700, hdg: 308, speed: 90));
        Assert.True(await DeclareAsync(w, "Eagle 1-1", EmergencyKind.PanPan));
        Assert.Equal("Eagle 1-1, roger Pan Pan, runway 31R, wind 310 degrees, 10 knots, continue approach, number two, follow the traffic ahead.", w.LastText);
        Assert.Equal(TowerPhase.ClearedToLand, w.Tower.Flights[2].Phase);
        Assert.False(w.Tower.Silenced(4));

        // The Hornet is down and off: the Pan Pan is cleared.
        w.Units.Remove("Hornet 1-1");
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Eagle 1-1, runway 31R, wind 310 degrees, 10 knots, cleared to land.", w.LastText);
    }

    [SkippableFact]
    public async Task A_cancelled_mayday_ends_the_silence_and_becomes_an_ordinary_arrival()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        w!.Put(Viper(w.OnFinal31R(20000), inAir: true, agl: 900, hdg: 308, speed: 120));
        await DeclareAsync(w, "Viper 1-1", EmergencyKind.Mayday);
        await w.Tower.CancelEmergencyAsync(w.Units["Viper 1-1"], w.Units);
        Assert.Contains("Viper 1-1, roger, Mayday cancelled.", Texts(w));
        Assert.Equal("All stations, distress traffic ended.", w.LastText);
        Assert.Equal(TowerPhase.Inbound, w.Tower.Flights[1].Phase);
        Assert.Null(w.Tower.Flights[1].Emergency);
        Assert.False(w.Tower.Silenced(4));
    }

    [SkippableFact]
    public async Task On_the_ground_an_emergency_is_only_acknowledged()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        await TaxiToHoldingPointAsync(w!);
        Assert.False(await DeclareAsync(w!, "Viper 1-1", EmergencyKind.Mayday));
        Assert.Equal("Viper 1-1, roger Mayday.", w!.LastText);
        Assert.Empty(w.Tower.Flights);
    }

    [SkippableFact]
    public async Task The_emergency_menu_is_there_in_the_air_only_when_turned_on()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        var viper = w!.Put(Viper(w.OnFinal31R(20000), inAir: true, agl: 900, hdg: 308, speed: 120));
        var menu = new AtcMenu(w.Catalog, w.Ground, w.Tower, w.Approach, w.Flights, w.Farps, emergencies: true);
        Assert.Equal(["Mayday", "Pan Pan"], (await menu.ForAsync(viper, w.Units)).Where(e => e.Path.SequenceEqual(AtcMenu.Emergency)).Select(e => e.Label));
        Assert.DoesNotContain(await w.Menu.ForAsync(viper, w.Units), e => e.Path.SequenceEqual(AtcMenu.Emergency));

        await DeclareAsync(w, "Viper 1-1", EmergencyKind.Mayday);
        Assert.Equal(["Cancel Mayday"], (await menu.ForAsync(viper, w.Units)).Where(e => e.Path.SequenceEqual(AtcMenu.Emergency)).Select(e => e.Label));

        var parked = w.Put(Viper(w.Layout.Parking.Single(p => p.Index == 106).Position));
        Assert.DoesNotContain(await menu.ForAsync(parked, w.Units), e => e.Path.SequenceEqual(AtcMenu.Emergency));
    }
}
