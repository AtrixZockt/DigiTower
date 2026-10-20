using DcsAtc.Airfields;
using DcsAtc.Airfields.Rn5;
using DcsAtc.Atc;
using DcsAtc.Bridge;

namespace DcsAtc.Tests;

public class PhraseologyTests
{
    [Theory]
    [InlineData("Enfield11", "Enfield 1-1")]
    [InlineData("Viper 1-1", "Viper 1-1")]
    [InlineData("Colt12", "Colt 1-2")]
    [InlineData("201", "201")]
    public void Formats_dcs_callsigns(string raw, string expected) =>
        Assert.Equal(expected, Phraseology.Callsign(new UnitState { Callsign = raw, Name = "unit" }));

    [Theory]
    [InlineData("Hangman 1-1 | DiGii", "Hangman 1-1")]
    [InlineData("Hangman 1-1|DiGii", "Hangman 1-1")]
    [InlineData("DiGii", "DiGii")]
    public void Players_are_called_by_their_name_before_the_bar(string player, string expected) =>
        Assert.Equal(expected, Phraseology.Callsign(new UnitState { Player = player, Callsign = "", Name = "unit" }));

    [Fact]
    public void Falls_back_to_the_group_name_without_a_callsign()
    {
        // DCS reports an empty callsign for some client aircraft.
        Assert.Equal("Hawg 2", Phraseology.Callsign(new UnitState { Group = "Hawg 2", Callsign = "", Name = "unit" }));
        Assert.Equal("Hawg 2", Phraseology.Callsign(new UnitState { Group = "Hawg 2", Name = "unit" }));
    }

    [Theory]
    [InlineData("Al Dhafra AFB", "Al Dhafra")]
    [InlineData("Abu Dhabi International Airport", "Abu Dhabi")]
    [InlineData("Khasab", "Khasab")]
    public void Strips_airbase_suffixes(string name, string expected) => Assert.Equal(expected, Phraseology.Facility(name));

    [Theory]
    [InlineData("Hangman 1-2", "1-2")]
    [InlineData("Enfield 21-3", "21-3")]
    [InlineData("DiGii", "DiGii")]
    public void Flight_members_are_marked_by_their_number(string callsign, string expected) => Assert.Equal(expected, SpotMarks.Label(callsign));

    [Fact]
    public void A_flights_pads_are_listed_lead_first()
    {
        Assert.Equal("pad 4", Phraseology.Pads([4]));
        Assert.Equal("pads 4 and 5", Phraseology.Pads([4, 5]));
        Assert.Equal("pads 4, 5 and 2", Phraseology.Pads([4, 5, 2]));
    }

    [Theory]
    [InlineData("F-16C_50", "F-16")]
    [InlineData("FA-18C_hornet", "Hornet")]
    [InlineData("MiG-29S", "MiG-29")]
    [InlineData("AV8BNA", "Harrier")]
    [InlineData("F-15ESE", "Strike Eagle")]
    [InlineData("Yak-52", "Yak-52")]
    [InlineData("Bf-109K-4", "Bf-109")]
    [InlineData("something odd", "aircraft")]
    public void Ground_names_traffic_by_type(string dcsType, string expected) => Assert.Equal(expected, Phraseology.SpokenType(dcsType));

    /// <summary>Wind from <paramref name="from"/> degrees at <paramref name="knots"/>: DCS gives the vector it blows towards.</summary>
    private static AirbaseWeather Weather(double from, double knots, double pressure = 101325, double elevation = 0)
    {
        double ms = knots / 1.943844, towards = (from + 180) * Math.PI / 180;
        return new AirbaseWeather { WindX = ms * Math.Cos(towards), WindZ = ms * Math.Sin(towards), Pressure = pressure, Elevation = elevation, Temperature = 288.15 };
    }

    [Fact]
    public void Reads_wind_and_altimeter()
    {
        var w = Weather(310, 10);
        Assert.Equal(310, w.WindFrom, 6);
        Assert.Equal("wind 310 degrees, 10 knots", Phraseology.Wind(w));
        Assert.Equal("wind calm", Phraseology.Wind(Weather(200, 2)));
        Assert.Equal("wind 360 degrees, 5 knots", Phraseology.Wind(Weather(358, 5)));

        Assert.Equal("QNH 1013, altimeter 29.92", Phraseology.Altimeter(w, "F-16C_50"));
        Assert.Equal("QNH 1013", Phraseology.Altimeter(w, "Su-27"));
        // 16 m above sea level the station pressure is ~1.9 hPa lower than QNH.
        Assert.Equal(1013, Math.Floor(Weather(0, 0, pressure: 101135, elevation: 16).QnhHpa));
    }

    [Fact]
    public void Picks_the_runway_end_with_the_most_headwind()
    {
        var airfield = Airfield.Build(Rn5Network.Create("Synthetic", ["taxiway", "vpp"],
        [
            new Rn5Edge(0, 0, 0, 1, 0, 1, "vpp", [new Vec2(0, 0), new Vec2(0, 1000)]),
            new Rn5Edge(1, 1, 1, 2, 0, 1, "vpp", [new Vec2(0, 1000), new Vec2(0, 2000)]),
        ], ["09"]));
        Assert.Equal(["27"], airfield.ActiveEnds(270, 5).Select(e => e.End.Name));
        Assert.Equal(["09"], airfield.ActiveEnds(100, 5).Select(e => e.End.Name));
        Assert.Equal(["09"], airfield.ActiveEnds(0, 0).Select(e => e.End.Name));
    }
}
