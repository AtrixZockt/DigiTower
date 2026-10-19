using DcsAtc.Atc;
using DcsAtc.Bridge;

namespace DcsAtc.Tests;

public partial class TowerControlTests
{
    private static AirbaseRadios DcsRadios(int id, params double[] mhz) =>
        new() { Id = id, Frequencies = [new AtcFrequency(4_300_000, 0), .. mhz.Select(m => new AtcFrequency(m * 1e6, 0))] };

    private static string Frequencies(RadioCall call) => string.Join(" ", call.Frequencies);

    /// <summary>Taxi out, depart, hand off: the calls a departure hears.</summary>
    private static async Task DepartAsync(World w)
    {
        await TaxiToHoldingPointAsync(w);
        await w.Tower.ReadyForDepartureAsync(w.Units["Viper 1-1"], w.Units);
        w.Put(Viper(w.OnRunway31R(2500), inAir: true, agl: 60, hdg: 308, speed: 90));
        await w.Tower.OnUnitsAsync(w.Units);
        w.Put(Viper(w.OnRunway31R(9000), inAir: true, agl: 1200, hdg: 308, speed: 150));
        await w.Tower.OnUnitsAsync(w.Units);
    }

    [SkippableFact]
    public async Task Without_chart_data_every_position_talks_on_the_frequencies_DCS_gives_the_airfield()
    {
        // Al Dhafra's layout under a name no labels file has: no chart frequencies, only DCS's.
        var w = await CreateAsync("Test AFB", 99, [DcsRadios(99, 118.55, 250.1)]);
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        await DepartAsync(w!);

        var calls = w!.Radio.Calls;
        Assert.Contains(calls, c => c.Station == "Test Ground" && c.Text.StartsWith("Viper 1-1, Test Ground, taxi to holding point runway 31R"));
        Assert.All(calls, c => Assert.Equal("118.55 250.1", Frequencies(c)));
        // Handoffs stay on the frequency: no number to say.
        Assert.Contains(calls, c => c.Text == "Viper 1-1, Test Ground, hold short runway 31R, contact Tower when ready for departure.");
        Assert.Equal("Viper 1-1, contact Approach.", w.LastText);
        Assert.Equal("Test Tower", calls[^1].Station);

        // Approach works it too, and the ATIS broadcasts on its own pair.
        Assert.NotNull(await w.Approach.NearestAsync(w.OnRunway31R(0), 60));
        var atis = new AtisService(w.Dcs, w.Catalog, w.Radio);
        await atis.UpdateAsync();
        Assert.Equal("119.55 260.1", string.Join(" ", w.Radio.Broadcasts["Test ATIS"].Frequencies));
        var field = (await w.Catalog.GetAsync(w.Catalog.Airbases[0]))!;
        Assert.StartsWith("Test: ATIS 119.55 / 260.1, Ground/Tower/Approach 118.55 / 250.1.", Phraseology.FrequencyList(field));
    }

    [SkippableFact]
    public async Task Al_Dhafra_keeps_its_chart_frequencies_and_also_talks_on_the_DCS_ones()
    {
        var w = await CreateAsync("Al Dhafra AFB", 4, [DcsRadios(4, 126.5, 251.1)]);
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        await DepartAsync(w!);

        var calls = w!.Radio.Calls;
        Assert.All(calls.Where(c => c.Station == "Al Dhafra Ground"), c => Assert.Equal("250 126.5 251.1", Frequencies(c)));
        Assert.All(calls.Where(c => c.Station == "Al Dhafra Tower"), c => Assert.Equal("270 126.5 251.1", Frequencies(c)));
        Assert.Contains(calls, c => c.Text.EndsWith("contact Tower on 270.0 when ready for departure."));
        Assert.Equal("Viper 1-1, contact Approach on 257.0, good day.", w.LastText);

        var atis = new AtisService(w.Dcs, w.Catalog, w.Radio);
        await atis.UpdateAsync();
        Assert.Equal("270.5", string.Join(" ", w.Radio.Broadcasts["Al Dhafra ATIS"].Frequencies));
    }

    [SkippableFact]
    public async Task Atis_broadcasts_only_where_the_app_put_the_airfield_on_the_air()
    {
        var w = await CreateAsync("Test AFB", 99, [DcsRadios(99, 118.55, 250.1)]);
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        bool onAir = false;
        var atis = new AtisService(w!.Dcs, w.Catalog, w.Radio, onAir: _ => onAir);
        await atis.UpdateAsync();
        Assert.Empty(w.Radio.Broadcasts);
        onAir = true;
        await atis.UpdateAsync();
        Assert.True(w.Radio.Broadcasts.ContainsKey("Test ATIS"));
    }
}
