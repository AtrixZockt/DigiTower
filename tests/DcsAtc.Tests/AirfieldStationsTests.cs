using DcsAtc.Airfields;
using DcsAtc.Atc;
using DcsAtc.Bridge;

namespace DcsAtc.Tests;

public class AirfieldStationsTests
{
    /// <summary>An airdrome's ATC frequencies (Hz, AM) as DCS reports them.</summary>
    private static AirbaseRadios Field(int id, string name, params double[] hz) =>
        new() { Id = id, Name = name, Frequencies = [.. hz.Select(h => new AtcFrequency(h, 0))] };

    /// <summary>Some Persian Gulf airdromes, as the probe on the real map returned them (HF, 30-40 MHz, VHF, UHF).</summary>
    private static readonly AirbaseRadios[] Gulf =
    [
        Field(4, "Al Dhafra AFB", 4300000, 39500000, 126500000, 251100000),
        Field(6, "Al Maktoum Intl", 4350000, 39600000, 118600000, 251200000),
        Field(8, "Tunb Island AFB"),
        Field(12, "Al Minhad AFB", 3800000, 38500000, 118550000, 250100000),
        Field(14, "Sharjah Intl", 3850000, 38600000, 118600000, 250200000),
        Field(27, "Jiroft", 4175000, 39250000, 136000000, 250850000),
    ];

    private static readonly AirfieldFrequencies DhafraChart = new(Atis: 270.5, Ground: 250.0, Tower: 270.0, Approach: 257.0);

    private static IReadOnlyDictionary<int, AirfieldStations> Map(AirfieldFrequencies? dhafraChart = null) =>
        AirfieldStations.ForMap(Gulf.Select(r => (r.Id, AirfieldStations.Usable(r.Frequencies.Select(f => (f.Mhz, f.IsAm))), r.Id == 4 ? dhafraChart : null)));

    private static (int, IReadOnlyList<double>, AirfieldFrequencies?) Shared(int id, params double[] mhz) => (id, mhz, null);

    [Fact]
    public void Every_position_works_on_the_VHF_and_UHF_frequencies_DCS_lists()
    {
        var minhad = Map()[12];
        Assert.Equal([118.55, 250.1], minhad.Shared);
        Assert.All(AirfieldStations.Roles, r => Assert.Equal([118.55, 250.1], minhad.For(r)));
        Assert.Null(minhad.RoleOn(118.55));
        Assert.Equal(new Contact(null), minhad.ContactFor("Approach"));
        Assert.Empty(AirfieldStations.Usable([(126.5, false)]));

        // No frequency in DCS: text messages only.
        var tunb = Map()[8];
        Assert.False(tunb.Serves("Ground"));
        Assert.Empty(tunb.Atis);
        Assert.Null(tunb.ContactFor("Approach"));
    }

    [Fact]
    public void Atis_is_on_ATC_UHF_plus_10_and_VHF_plus_1_moved_on_when_taken()
    {
        var map = Map();
        Assert.Equal([119.55, 260.1], map[12].Atis);
        // DCS gives Al Maktoum and Sharjah the same VHF: the first by id gets 119.6, the other the next free step.
        Assert.Equal([119.6, 261.2], map[6].Atis);
        Assert.Equal([119.625, 260.2], map[14].Atis);
        // At the top of the VHF band: 1 MHz below instead.
        Assert.Equal([135.0, 260.85], map[27].Atis);
    }

    [Fact]
    public void Atis_never_takes_an_ATC_or_a_guard_frequency()
    {
        var map = AirfieldStations.ForMap([Shared(1, 118.0), Shared(2, 119.0), Shared(3, 120.5, 233.0)]);
        Assert.Equal([119.025], map[1].Atis);
        Assert.Equal([120.0], map[2].Atis);
        Assert.Equal([121.525, 243.025], map[3].Atis);
    }

    [Fact]
    public void Chart_frequencies_come_first_and_the_shared_ones_carry_every_position()
    {
        var dhafra = Map(DhafraChart)[4];
        Assert.Equal([250.0, 126.5, 251.1], dhafra.For("Ground"));
        Assert.Equal([270.0, 126.5, 251.1], dhafra.For("Tower"));
        Assert.Equal([270.5], dhafra.Atis);
        Assert.Equal(5, dhafra.All.Count);
        Assert.Equal("Ground", dhafra.RoleOn(250.0));
        Assert.Null(dhafra.RoleOn(251.1));
        Assert.Equal(new Contact(257.0), dhafra.ContactFor("Approach"));
        Assert.Equal("ATIS 270.5, Ground 250.0, Tower 270.0, Approach 257.0, all positions 126.5 / 251.1", dhafra.Describe());
        Assert.Equal("ATIS 119.55 / 260.1, Ground/Tower/Approach 118.55 / 250.1", Map()[12].Describe());
    }

    [Fact]
    public void A_request_is_answered_by_its_position_or_sent_to_that_positions_frequency()
    {
        var dhafra = Map(DhafraChart)[4];
        // On Ground's own frequency, a departure request: Ground sends the pilot to Tower on 270.0.
        Assert.Equal(("Ground", (double?)270.0), dhafra.Route(250.0, "Ground", "Tower"));
        // On a shared frequency Tower answers it itself.
        Assert.Equal(("Tower", (double?)null), dhafra.Route(251.1, "Ground", "Tower"));
        Assert.Equal(("Ground", (double?)null), Map()[12].Route(118.55, "Tower", "Ground"));
        Assert.Equal(("Approach", (double?)null), dhafra.Route(257.0, "Approach", "Approach"));
    }
}
