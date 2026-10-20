using DcsAtc.Airfields;
using DcsAtc.Atc;
using DcsAtc.Bridge;

namespace DcsAtc.Tests;

public partial class TowerControlTests
{
    /// <summary>
    /// A FOB 15 km east of Al Dhafra as Retribution builds it: three single-pad FARPs on 127.5, pads 1 and 2 only 40 m
    /// apart (closer than 200 ft), pad 3 150 m away.
    /// </summary>
    private static FarpSite TestFob(World w)
    {
        var a = w.Catalog.Airbases[0];
        var first = new Vec2(a.X, a.Z + 15000);
        double[] offsets = [0, 40, 150];
        var farps = offsets.Select((dx, i) => new FarpInfo
        {
            Name = $"FOB Test Invisible FARP {i}", Id = 100 + i, X = first.X + dx, Z = first.Z, Coalition = 2, Frequency = 127.5,
            Pads = [new FarpPadInfo(1, first.X + dx, first.Z)],
        });
        w.Farps.SetSites(FarpSite.Build(farps));
        return w.Farps.Sites.Single();
    }

    private static Vec2 Pad(FarpSite site, int number) => site.Pads.Single(p => p.Number == number).Position;

    private static UnitState Heli2(Vec2 p, bool inAir = false, double agl = 0, double hdg = 0, double speed = 0) =>
        Aircraft("Viper 2-1", 2, p, inAir, agl, hdg, speed, player: "Viper 2-1 | B") with { Cat = "heli", Type = "UH-60L" };

    [SkippableFact]
    public async Task Farp_approves_startup_clears_a_departure_from_the_pad_and_lets_it_go()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var site = TestFob(w!);
        var heli = w!.Put(Heli(Pad(site, 1), hdg: 270));

        await w.Farps.RequestStartupAsync(heli, w.Units);
        Assert.Equal("Viper 1-1, FOB Test, start up approved, wind 310 degrees, 10 knots, QNH 1013, altimeter 29.92.", w.LastText);
        await w.Farps.RequestDepartureAsync(heli, w.Units, DepartureIntent.North);
        Assert.Equal("Viper 1-1, FOB Test, from present position, make right turn for north departure, wind 310 degrees, 10 knots, cleared for takeoff.", w.LastText);
        var call = w.Radio.Calls[^1];
        Assert.Equal(("FOB Test", "127.5"), (call.Station, string.Join(" ", call.Frequencies)));

        // A hover is not the departure; climbing away is; clear of the site: frequency change.
        w.Put(Heli(Pad(site, 1), inAir: true, agl: 10, hdg: 0));
        await w.Farps.OnUnitsAsync(w.Units);
        Assert.Equal(FarpPhase.ClearedForTakeoff, w.Farps.Flights[1].Phase);
        w.Put(Heli(Pad(site, 1) + Vec2.FromHeading(0) * 500, inAir: true, agl: 60, hdg: 0, speed: 40));
        await w.Farps.OnUnitsAsync(w.Units);
        w.Put(Heli(Pad(site, 1) + Vec2.FromHeading(0) * 5000, inAir: true, agl: 150, hdg: 0, speed: 60));
        await w.Farps.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, frequency change approved, good day.", w.LastText);
        Assert.Empty(w.Farps.Flights);
    }

    [SkippableFact]
    public async Task Farp_gives_landing_helicopters_free_pads_at_least_200_ft_apart()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var site = TestFob(w!);
        var east = Vec2.FromHeading(90) * 5 * Nm;

        // The first gets the nearest pad; the second not pad 2, which is 40 m from it.
        var other = w!.Put(Heli2(Pad(site, 1) + east, inAir: true, agl: 150, hdg: 270, speed: 50));
        await w.Farps.RequestLandingAsync(other, w.Units);
        Assert.Equal("Viper 2-1, FOB Test, make straight-in to pad 1, wind 310 degrees, 10 knots, cleared to land.", w.Dcs.Texts[^1].Text);
        await w.Farps.RequestLandingAsync(w.Put(Heli(Pad(site, 1) + east, inAir: true, agl: 150, hdg: 270, speed: 50)), w.Units);
        Assert.Equal("Viper 1-1, FOB Test, make straight-in to pad 3, wind 310 degrees, 10 knots, cleared to land.", w.LastText);
        Assert.True(w.Dcs.Drawings.ContainsKey("pad-1"));
        Assert.Equal("PAD 3", w.Dcs.Drawings["pad-1"].Text);

        // Down on its pad: done, and the pad is no longer marked.
        w.Put(Heli(Pad(site, 3)));
        await w.Farps.OnUnitsAsync(w.Units);
        Assert.False(w.Farps.Flights.ContainsKey(1));
        Assert.False(w.Dcs.Drawings.ContainsKey("pad-1"));
    }

    [SkippableFact]
    public async Task Farp_with_every_pad_occupied_lands_you_at_your_own_risk()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var site = TestFob(w!);
        for (int n = 1; n <= 3; n++) w!.Put(Aircraft($"Hawk {n}-1", 10 + n, Pad(site, n)) with { Cat = "heli", Type = "Mi-8MT" });
        await w!.Farps.RequestLandingAsync(w.Put(Heli(Pad(site, 1) + Vec2.FromHeading(90) * 3 * Nm, inAir: true, agl: 150, hdg: 270, speed: 50)), w.Units);
        Assert.Equal("Viper 1-1, FOB Test, all pads occupied, landing will be at your own risk.", w.LastText);
    }

    [SkippableFact]
    public async Task Farp_departure_waits_for_a_helicopter_landing_within_200_ft()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var site = TestFob(w!);
        var other = w!.Put(Heli2(Pad(site, 1) + Vec2.FromHeading(90) * 3 * Nm, inAir: true, agl: 150, hdg: 270, speed: 50));
        await w.Farps.RequestLandingAsync(other, w.Units);

        var heli = w.Put(Heli(Pad(site, 2)));
        await w.Farps.RequestDepartureAsync(heli, w.Units);
        Assert.Equal("Viper 1-1, FOB Test, hold position, helicopter traffic.", w.LastText);

        // The other has landed on pad 1: Viper 1-1 may go.
        w.Put(Heli2(Pad(site, 1)));
        await w.Farps.OnUnitsAsync(w.Units);
        await w.Farps.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, FOB Test, from present position, wind 310 degrees, 10 knots, cleared for takeoff.", w.LastText);
    }

    [SkippableFact]
    public async Task Farp_menu_offers_startup_and_departure_on_a_pad_and_landing_in_the_air()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var site = TestFob(w!);

        var entries = await w!.Menu.ForAsync(w.Put(Heli(Pad(site, 1))), w.Units);
        var fob = entries.Where(e => e.Path[1] == "FOB Test").ToList();
        Assert.Contains(fob, e => e.Label == "Request startup");
        Assert.Contains(fob, e => e.Path[^1] == "Request departure" && e.Label == "Departure north");
        Assert.DoesNotContain(fob, e => e.Label == "Closed traffic");
        Assert.Contains(fob, e => e.Label == "Frequencies");
        Assert.All(fob, e => Assert.StartsWith($"ab{site.Id}:farp.", e.Id));

        entries = await w.Menu.ForAsync(w.Put(Heli(Pad(site, 1) + Vec2.FromHeading(90) * 8 * Nm, inAir: true, agl: 300, hdg: 270, speed: 50)), w.Units);
        Assert.Contains(entries, e => e.Path[1] == "FOB Test" && e.Label == "Request landing");
    }
}
