using System.Text.RegularExpressions;

namespace DcsAtc.Atc;

/// <summary>Ground's traffic instructions (M7), ICAO style: the other aircraft by type and position.</summary>
public static partial class Phraseology
{
    /// <summary>DCS type names Ground says differently from their designation.</summary>
    private static readonly (string Prefix, string Spoken)[] TypeNames =
    [
        ("FA-18", "Hornet"), ("F/A-18", "Hornet"), ("F-14", "Tomcat"), ("F-15E", "Strike Eagle"), ("F-4E", "Phantom"),
        ("AV8B", "Harrier"), ("M-2000", "Mirage"), ("Mirage-F1", "Mirage F1"), ("AJS37", "Viggen"), ("Tornado", "Tornado"),
        ("C-130", "Hercules"), ("KC-130", "Hercules"), ("KC135", "KC-135"), ("TF-51", "Mustang"), ("P-51", "Mustang"),
        ("MQ-9", "Reaper"), ("Hawk", "Hawk"), ("Christen Eagle", "Christen Eagle"),
    ];

    /// <summary>
    /// How Ground names an aircraft type: "F-16C_50" → "F-16", "FA-18C_hornet" → "Hornet", "MiG-29S" → "MiG-29",
    /// anything else → "aircraft".
    /// </summary>
    public static string SpokenType(string dcsType)
    {
        foreach (var (prefix, spoken) in TypeNames)
            if (dcsType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return spoken;
        return Designation().Match(dcsType) is { Success: true } m ? m.Groups[1].Value : "aircraft";
    }

    [GeneratedRegex(@"^([A-Za-z]{1,4}-\d{1,3})")]
    private static partial Regex Designation();

    /// <summary>
    /// "Hangman 1-2, Al Dhafra Ground, hold short of B, give way to the F-16 from the left." Without a named taxiway to
    /// hold short of: "…, give way to the F-16 from the left."; too close for that: "…, hold position, give way to …".
    /// <paramref name="where"/> is "from the left", "from the right" or "opposite direction" (with "on A" when known).
    /// </summary>
    public static string GiveWay(string callsign, string facility, bool holdNow, string? holdShortOf, string type, string where) =>
        $"{callsign}, {facility} Ground, {(holdNow ? "hold position, " : holdShortOf is not null ? $"hold short of {holdShortOf}, " : "")}give way to the {type} {where}.";

    /// <summary>"Hangman 1-2, Al Dhafra Ground, follow the F-16 ahead."</summary>
    public static string FollowTraffic(string callsign, string facility, string type) =>
        $"{callsign}, {facility} Ground, follow the {type} ahead.";

    /// <summary>The traffic has passed: "Hangman 1-2, Al Dhafra Ground, continue taxi."</summary>
    public static string ContinueTaxi(string callsign, string facility) =>
        $"{callsign}, {facility} Ground, continue taxi.";

    /// <summary>Told to give way but still rolling: "Hangman 1-2, Al Dhafra Ground, hold position."</summary>
    public static string HoldPositionNow(string callsign, string facility) =>
        $"{callsign}, {facility} Ground, hold position.";

    /// <summary>Ground's traffic transmissions at an airfield with these taxiways (for preparing the clips).</summary>
    public static IEnumerable<string> GroundTrafficSamples(string callsign, string facility, IEnumerable<string> taxiways)
    {
        string[] where = ["from the left", "from the right", "opposite direction"];
        foreach (var w in where) yield return GiveWay(callsign, facility, holdNow: true, null, "aircraft", w);
        foreach (var t in taxiways)
        {
            yield return GiveWay(callsign, facility, holdNow: false, t, "aircraft", "from the left");
            yield return GiveWay(callsign, facility, holdNow: false, t, "aircraft", $"opposite direction on {t}");
        }
        yield return FollowTraffic(callsign, facility, "aircraft");
        yield return ContinueTaxi(callsign, facility);
        yield return HoldPositionNow(callsign, facility);
    }
}
