using System.Globalization;
using DcsAtc.Airfields;
using DcsAtc.Airfields.Taxi;
using DcsAtc.Bridge;

namespace DcsAtc.Atc;

/// <summary>ATIS phraseology.</summary>
public static partial class Phraseology
{
    private static readonly string[] Letters =
    [
        "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel", "India", "Juliett", "Kilo", "Lima", "Mike",
        "November", "Oscar", "Papa", "Quebec", "Romeo", "Sierra", "Tango", "Uniform", "Victor", "Whiskey", "X-ray", "Yankee", "Zulu",
    ];

    /// <summary>The information letter: 0 → "Alpha", 25 → "Zulu".</summary>
    public static string LetterName(int index) => Letters[(index % 26 + 26) % 26];

    /// <summary>
    /// "Al Dhafra information Alpha, time 0750 Zulu. Runway in use 31L and 31R. Expect ILS approach. Wind 310 degrees,
    /// 10 knots. CAVOK. Temperature 30. QNH 1013, altimeter 29.92. Advise on initial contact you have information Alpha."
    /// </summary>
    public static string Atis(AtisContent c, string letter, TimeSpan? zulu) =>
        $"{c.Facility} information {letter}{(zulu is { } z ? $", time {z.Hours:00}{z.Minutes:00} Zulu" : "")}. " +
        $"Runway in use {RunwayList(c.Runways)}. Expect {c.Approach} approach. {Capitalize(Wind(c.Weather))}. " +
        (c.Sky is null ? "" : $"{c.Sky}. ") +
        $"{Temperature(c.TemperatureC)}. {Altimeter(c.Weather, UsTypes.First())}. Advise on initial contact you have information {letter}.";

    /// <summary>"31R", "31L and 31R", "09, 13 and 31".</summary>
    private static string RunwayList(IReadOnlyList<RunwayEnd> ends) =>
        ends.Count <= 1 ? ends.FirstOrDefault()?.Name ?? "" : $"{string.Join(", ", ends.SkipLast(1).Select(e => e.Name))} and {ends[^1].Name}";

    private static string Temperature(int celsius) => celsius < 0 ? $"Temperature minus {-celsius}" : $"Temperature {celsius}";

    /// <summary>Every piece an ATIS at this airfield can be made of (for preparing the clips).</summary>
    public static IEnumerable<string> AtisSamples(string facility, IReadOnlyList<Runway> runways, AirbaseWeather? weather)
    {
        foreach (var letter in Letters)
        {
            yield return $"{facility} information {letter}, time 0000 Zulu.";
            yield return $"Advise on initial contact you have information {letter}.";
        }
        for (int m = 0; m < 60; m++) yield return $"time {m % 24:00}{m:00} Zulu.";
        var ends = runways.SelectMany(r => new[] { r.EndA, r.EndB }).OrderBy(e => e.Name).ToList();
        foreach (var end in ends)
        {
            yield return $"Runway in use {end.Name}.";
            foreach (var other in ends.Where(o => o != end && Math.Abs(((o.TrueHeading - end.TrueHeading) % 360 + 540) % 360 - 180) < 20))
                yield return $"Runway in use {RunwayList([end, other])}.";
        }
        foreach (var approach in new[] { "ILS", "TACAN", "visual" }) yield return $"Expect {approach} approach.";
        if (weather is not null) yield return $"{Capitalize(Wind(weather))}. {Altimeter(weather, UsTypes.First())}.";
        yield return "CAVOK. No significant clouds. Visibility 10 kilometers or more.";
        for (int km = 5; km < 10; km++) yield return $"Visibility {km} kilometers.";
        for (int m = 100; m < 5000; m += 100) yield return $"Visibility {m} meters.";
        foreach (var w in new[] { "fog", "mist", "dust", "rain", "snow", "thunderstorm" })
        {
            yield return $"Visibility 10 kilometers or more, {w}.";
            foreach (var w2 in new[] { "fog", "mist", "dust" }) yield return $"Visibility 10 kilometers or more, {w}, {w2}.";
        }
        foreach (var amount in new[] { "few", "scattered", "broken", "overcast" })
        {
            for (int feet = 100; feet < 25000; feet += 100)
                yield return $"Clouds {amount} {feet.ToString(CultureInfo.InvariantCulture)} feet, {amount} {feet.ToString(CultureInfo.InvariantCulture)} feet.";
        }
        for (int t = -40; t <= 55; t++) yield return $"{Temperature(t)}.";
    }
}
