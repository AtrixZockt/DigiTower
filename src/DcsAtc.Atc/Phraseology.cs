using System.Globalization;
using System.Text.RegularExpressions;
using DcsAtc.Airfields;
using DcsAtc.Airfields.Taxi;
using DcsAtc.Bridge;

namespace DcsAtc.Atc;

/// <summary>ICAO-based phraseology (with military additions) for text and, later, voice.</summary>
public static partial class Phraseology
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>DCS type names of US aircraft; their crews also get the altimeter setting in inches of mercury.</summary>
    public static readonly IReadOnlySet<string> UsTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "F-16C_50", "F-16C bl.52d", "F-16A", "FA-18C_hornet", "F/A-18C", "F-15C", "F-15E", "F-15ESE", "A-10A", "A-10C", "A-10C_2",
        "F-14A", "F-14A-135-GR", "F-14B", "AV8BNA", "F-4E", "F-4E-45MC", "F-5E", "F-5E-3", "F-86F Sabre", "F-117A", "B-1B", "B-52H",
        "C-130", "C-130J-30", "C-17A", "KC-135", "KC135MPRS", "KC-10A", "E-3A", "E-2C", "S-3B", "S-3B Tanker", "P-51D", "P-51D-30-NA",
        "TF-51D", "AH-64D_BLK_II", "AH-64A", "AH-64D", "UH-1H", "OH58D", "CH-47D", "CH-47Fbl1", "UH-60A", "SH-60B", "MQ-9 Reaper",
    };

    /// <summary>
    /// The callsign to address a flight by:
    /// players by the part of their name before "|" ("Hangman 1-1 | DiGii" becomes "Hangman 1-1"), or the whole
    /// name without a "|"; AI by its DCS callsign, spoken style ("Enfield11" becomes "Enfield 1-1", numeric ones
    /// such as "201" stay); otherwise the group name, then the unit name.
    /// </summary>
    public static string Callsign(UnitState unit)
    {
        if (!string.IsNullOrWhiteSpace(unit.Player))
        {
            string fromName = unit.Player.Split('|')[0].Trim();
            if (fromName.Length > 0) return fromName;
        }
        if (!string.IsNullOrWhiteSpace(unit.Callsign))
        {
            string raw = unit.Callsign.Trim();
            var m = CompactCallsign().Match(raw);
            return m.Success ? $"{m.Groups[1].Value} {m.Groups[2].Value}-{m.Groups[3].Value}" : raw;
        }
        return !string.IsNullOrWhiteSpace(unit.Group) ? unit.Group.Trim() : unit.Name;
    }

    [GeneratedRegex(@"^([A-Za-z][A-Za-z ]*?)\s*(\d)(\d)$")]
    private static partial Regex CompactCallsign();

    /// <summary>"Al Dhafra AFB" → "Al Dhafra" (the name a controller uses on the radio).</summary>
    public static string Facility(string airbaseName) =>
        FacilitySuffix().Replace(airbaseName, "").Trim();

    [GeneratedRegex(@"\s+(AFB|AB|Air ?Base|Airbase|International Airport|Intl|Airport|Airfield|airfield|airport)$", RegexOptions.IgnoreCase)]
    private static partial Regex FacilitySuffix();

    /// <summary>"wind 310 degrees, 10 knots" (direction rounded to 10°), or "wind calm" below 3 knots.</summary>
    public static string Wind(AirbaseWeather w)
    {
        double knots = w.WindSpeedKnots;
        if (knots < 3) return "wind calm";
        int dir = (int)(Math.Round(w.WindFrom / 10) * 10);
        if (dir == 0) dir = 360;
        return $"wind {dir:000} degrees, {Math.Round(knots):0} knots";
    }

    /// <summary>"QNH 1013" (rounded down, as ICAO does), plus ", altimeter 29.92" for US aircraft types.</summary>
    public static string Altimeter(AirbaseWeather w, string unitType)
    {
        int hpa = (int)Math.Floor(w.QnhHpa);
        string text = $"QNH {hpa}";
        if (UsTypes.Contains(unitType)) text += $", altimeter {(w.QnhHpa * 0.0295300).ToString("0.00", Inv)}";
        return text;
    }

    /// <summary>"A, F" or "A, cross runway 13R, P, X0".</summary>
    public static string ViaText(IReadOnlyList<ViaItem> via, RunwayEnd departure) =>
        string.Join(", ", via.Select(v => v.IsRunwayCrossing ? $"cross runway {CrossingName(v.CrossedRunway!, departure)}" : v.Name));

    /// <summary>A crossed runway is named by the end that points the same way as the departure runway.</summary>
    public static string CrossingName(Runway crossed, RunwayEnd departure)
    {
        double Diff(RunwayEnd e) => Math.Abs(((e.TrueHeading - departure.TrueHeading) % 360 + 540) % 360 - 180);
        return Diff(crossed.EndA) <= Diff(crossed.EndB) ? crossed.EndA.Name : crossed.EndB.Name;
    }

    /// <summary>
    /// "Viper 1-1, Al Dhafra Ground, taxi to holding point runway 13L via A, F. Wind 310 degrees, 10 knots, QNH 1013, altimeter 29.91."
    /// </summary>
    public static string TaxiClearance(string callsign, string facility, RunwayEnd runway, IReadOnlyList<ViaItem> via, AirbaseWeather? weather, string unitType)
    {
        string clearance = $"{callsign}, {facility} Ground, taxi to holding point runway {runway.Name}";
        if (via.Count > 0) clearance += $" via {ViaText(via, runway)}";
        clearance += ".";
        if (weather is not null) clearance += $" {Capitalize(Wind(weather))}, {Altimeter(weather, unitType)}.";
        return clearance;
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
