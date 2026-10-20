using DcsAtc.Atc;
using DcsAtc.Bridge;

namespace DcsAtc.Tests;

public class FarpSiteTests
{
    private static FarpInfo Farp(string name, int id, double x, double z, double? mhz = 127.5, params (double X, double Z)[] pads) =>
        new() { Name = name, Id = id, X = x, Z = z, Coalition = 2, Frequency = mhz, Pads = [.. pads.Select((p, i) => new FarpPadInfo(i + 1, p.X, p.Z))] };

    [Fact]
    public void Farps_of_one_FOB_or_close_together_are_one_site_with_numbered_pads()
    {
        var sites = FarpSite.Build([
            // Retribution: single-pad invisible FARPs named after the FOB (FOB Thailand in two groups 2.9 km apart).
            Farp("FOB Laos Invisible FARP 10", 12, 0, 300), Farp("FOB Laos Invisible FARP 2", 11, 0, 150), Farp("FOB Laos Invisible FARP 0", 10, 0, 0),
            Farp("FOB Thailand Invisible FARP 0", 20, 50000, 0), Farp("FOB Thailand Invisible FARP 1", 21, 50000, 2900),
            // An editor FARP with four pads, and another FARP 500 m from it.
            Farp("Alpha FARP", 30, 90000, 0, 128.0, (90000, 0), (90000, 40), (90040, 0), (90040, 40)),
            Farp("Bravo", 31, 90500, 0, 128.0),
            // Without a frequency (or an FM one): text messages only. Not in the mission now: left out.
            Farp("Charlie", 40, 120000, 0, null),
            new FarpInfo { Name = "Gone FARP", Id = null },
        ]);

        Assert.Equal(["FOB Laos", "FOB Thailand", "Alpha", "Charlie"], sites.Select(s => s.Name));
        var laos = sites[0];
        Assert.Equal(10, laos.Id);
        Assert.Equal(["FOB Laos Invisible FARP 0", "FOB Laos Invisible FARP 2", "FOB Laos Invisible FARP 10"], laos.Pads.Select(p => p.Farp));
        Assert.Equal([1, 2, 3], laos.Pads.Select(p => p.Number));
        Assert.Equal([127.5], laos.Frequencies.Shared);
        Assert.Equal(2, sites[1].Pads.Count);
        Assert.Equal(5, sites[2].Pads.Count);
        Assert.Empty(sites[3].Frequencies.All);
        Assert.True(laos.Covers(new(0, 400)));
        Assert.False(laos.Covers(new(0, 700)));
    }

    [Theory]
    [InlineData("FOB Laos Invisible FARP 3", "FOB Laos")]
    [InlineData("London FARP", "London")]
    [InlineData("Pad-2", "Pad")]
    [InlineData("FARP Alpha", "FARP Alpha")]
    [InlineData("FARP", "FARP")]
    public void A_site_is_named_after_its_FARPs_without_their_numbers(string farp, string site) => Assert.Equal(site, FarpSite.BaseName(farp));
}
