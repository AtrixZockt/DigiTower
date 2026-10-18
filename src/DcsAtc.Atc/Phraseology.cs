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

    /// <summary>"Viper 1-1, Al Dhafra Ground, taxi to Apron 1 East via B, A."</summary>
    public static string TaxiToParking(string callsign, string facility, string area, IReadOnlyList<ViaItem> via)
    {
        string text = $"{callsign}, {facility} Ground, taxi to {area}";
        if (via.Count > 0) text += $" via {string.Join(", ", via.Select(v => v.IsRunwayCrossing ? $"cross runway {v.CrossedRunway!.EndA.Name}" : v.Name))}";
        return text + ".";
    }

    /// <summary>A radio frequency as said: 270 → "270.0", 251.25 → "251.25".</summary>
    public static string? Frequency(double? mhz) => mhz?.ToString("0.0##", Inv);

    /// <summary>"Viper 1-1, unrestricted climb approved, wind 310 degrees, 10 knots, runway 31R, cleared for takeoff."</summary>
    public static string ClearedForTakeoff(string callsign, RunwayEnd runway, AirbaseWeather? weather, DepartureInstruction? departure = null) =>
        $"{callsign}, {(departure is null ? "" : Departure(departure) + ", ")}{(weather is null ? "" : Wind(weather) + ", ")}runway {runway.Name}, cleared for takeoff.";

    /// <summary>
    /// The climb-out part of a takeoff clearance: "unrestricted climb approved", "after departure turn right heading 360,
    /// unrestricted climb approved", "fly runway heading, maintain at or below 500 feet until 3 miles, then unrestricted climb",
    /// "fly runway heading, climb and maintain 3000 feet until advised", "right closed traffic approved".
    /// </summary>
    public static string Departure(DepartureInstruction d)
    {
        var p = d.Procedures;
        if (d.Intent == DepartureIntent.ClosedTraffic) return $"{d.ClosedTrafficSide} closed traffic approved";
        string? turnOut = TurnOut(d);
        if (d.StayLow)
        {
            string then = d.UntilAdvised ? $"climb and maintain {p.InitialAltitude:0} feet until advised"
                : turnOut is null ? "unrestricted climb" : $"{turnOut}, unrestricted climb";
            return $"fly runway heading, maintain at or below {p.StayLowAltitude:0} feet until {p.StayLowDistance:0} miles, then {then}";
        }
        if (d.UntilAdvised) return $"fly runway heading, climb and maintain {p.InitialAltitude:0} feet until advised";
        return turnOut switch
        {
            null => "unrestricted climb approved",
            "fly runway heading" => "fly runway heading, unrestricted climb approved",
            _ => $"after departure {turnOut}, unrestricted climb approved",
        };
    }

    /// <summary>"turn right heading 360", "fly runway heading" (heading close to the runway's), or null (own navigation).</summary>
    private static string? TurnOut(DepartureInstruction d) =>
        d.Heading is not int h ? null : d.Turn is { } turn ? $"turn {turn} heading {h:000}" : "fly runway heading";

    /// <summary>The departing traffic ahead is far enough away: "Viper 1-2, climb unrestricted, resume own navigation."</summary>
    public static string ClimbUnrestricted(string callsign, DepartureInstruction d) =>
        d.Heading is int h
            ? $"{callsign}, {(d.Turn is { } turn ? $"turn {turn} heading {h:000}" : $"fly heading {h:000}")}, climb unrestricted."
            : $"{callsign}, climb unrestricted, resume own navigation.";

    public static string CheckAltitudeStayLow(string callsign, DepartureProcedures p) =>
        $"{callsign}, check altitude, maintain at or below {p.StayLowAltitude:0} feet until {p.StayLowDistance:0} miles.";

    public static string CheckAltitudeMaintain(string callsign, DepartureProcedures p) =>
        $"{callsign}, check altitude, maintain {p.InitialAltitude:0} feet.";

    public static string CheckHeading(string callsign) => $"{callsign}, check heading, fly runway heading.";

    /// <summary>"Viper 1-1, runway 31L, wind 310 degrees, 10 knots, cleared to land."</summary>
    public static string ClearedToLand(string callsign, RunwayEnd runway, AirbaseWeather? weather) =>
        $"{callsign}, runway {runway.Name}, {(weather is null ? "" : Wind(weather) + ", ")}cleared to land.";

    /// <summary>"…, contact Approach on 257.0, good day." or "…, frequency change approved, good day."; after an assigned heading, "resume own navigation" first.</summary>
    public static string ContactAfterDeparture(string callsign, double? approach, bool resumeOwnNavigation = false) =>
        $"{callsign}, {(resumeOwnNavigation ? "resume own navigation, " : "")}" +
        (Frequency(approach) is { } f ? $"contact Approach on {f}, good day." : "frequency change approved, good day.");

    // Ground.

    public static string UnableToRoute(string callsign, string facility) =>
        $"{callsign}, {facility} Ground, unable to find a taxi route from your position.";

    public static string NoFreeParking(string callsign, string facility) =>
        $"{callsign}, {facility} Ground, no free parking found, hold position.";

    public static string TaxiCancelled(string callsign, string facility) =>
        $"{callsign}, {facility} Ground, taxi clearance cancelled, hold position.";

    public static string OffRoute(string callsign, string facility) =>
        $"{callsign}, {facility} Ground, you are off your cleared taxi route. Check route and hold position if unsure.";

    public static string ReduceTaxiSpeed(string callsign, string facility) =>
        $"{callsign}, {facility} Ground, reduce taxi speed.";

    /// <summary>"…, hold short runway 31R, contact Tower on 270.0 when ready for departure."</summary>
    public static string HoldShortContactTower(string callsign, string facility, RunwayEnd runway, double? tower) =>
        $"{callsign}, {facility} Ground, hold short runway {runway.Name}, contact Tower{(Frequency(tower) is { } f ? $" on {f}" : "")} when ready for departure.";

    public static string ParkingReached(string callsign, string facility) =>
        $"{callsign}, {facility} Ground, parking reached.";

    public static string GroundWhileAirborne(string callsign) =>
        $"{callsign}, you are airborne. Ground control is for aircraft on the ground.";

    public static string NoGroundControl(string callsign) =>
        $"{callsign}, no ground control available at your position.";

    // Tower.

    /// <summary>Why a departure holds short or a landing waits.</summary>
    public const string TrafficOnFinal = "traffic on final", LandingTraffic = "landing traffic", RunwayOccupied = "runway occupied",
        TrafficDeparting = "traffic departing", NumberTwoForDeparture = "number two for departure", NumberTwoFollowTraffic = "number two, follow the traffic ahead";

    public static readonly IReadOnlyList<string> HoldReasons = [TrafficOnFinal, LandingTraffic, RunwayOccupied, TrafficDeparting, NumberTwoForDeparture];
    public static readonly IReadOnlyList<string> ApproachReasons = [RunwayOccupied, TrafficDeparting, NumberTwoFollowTraffic];

    public static string AlreadyAirborne(string callsign) => $"{callsign}, you are already airborne.";

    public static string NoTower(string callsign) => $"{callsign}, no tower available at your position.";

    public static string NotAtHoldingPoint(string callsign, string facility, RunwayEnd runway) =>
        $"{callsign}, {facility} Tower, you are not at a holding point for runway {runway.Name}. Request taxi from Ground.";

    public static string OnTheGround(string callsign) => $"{callsign}, you are on the ground.";

    public static string NoTowerWithin(string callsign, double miles) => $"{callsign}, no tower within {miles:F0} miles.";

    /// <summary>"Viper 1-1, Al Dhafra Tower, runway 31R, wind 310 degrees, 10 knots, QNH 1013, report final."</summary>
    public static string ReportFinal(string callsign, string facility, RunwayEnd runway, AirbaseWeather? weather, string unitType) =>
        $"{callsign}, {facility} Tower, runway {runway.Name}{(weather is null ? "" : $", {Wind(weather)}, {Altimeter(weather, unitType)}")}, report final.";

    public static string RequestLandingFirst(string callsign) => $"{callsign}, request landing first.";

    public static string LineUpAndWait(string callsign, RunwayEnd runway) => $"{callsign}, runway {runway.Name}, line up and wait.";

    /// <summary>Closed traffic that flies off: "Viper 1-1, leaving the pattern, contact Approach on 257.0, good day."</summary>
    public static string LeavingPattern(string callsign, double? approach) =>
        $"{callsign}, leaving the pattern, " + (Frequency(approach) is { } f ? $"contact Approach on {f}, good day." : "frequency change approved, good day.");

    public static string HoldShort(string callsign, string facility, RunwayEnd runway, string reason) =>
        $"{callsign}, {facility} Tower, hold short runway {runway.Name}, {reason}.";

    public static string ContinueApproach(string callsign, string reason) => $"{callsign}, continue approach, {reason}.";

    public static string GoAround(string callsign, string reason) => $"{callsign}, go around, I say again, go around, {reason}.";

    /// <summary>"Viper 1-1, welcome to Al Dhafra, vacate runway when able, contact Ground on 250.0."</summary>
    public static string VacateContactGround(string callsign, string facility, double? ground) =>
        $"{callsign}, welcome to {facility}, vacate runway when able, contact Ground{(Frequency(ground) is { } f ? $" on {f}" : "")}.";

    /// <summary>
    /// One example of every transmission Ground and Tower make at an airfield, with its runways, taxiways, parking
    /// areas and frequencies, and the given weather: saying these once caches every clip a controller needs there,
    /// except the pilots' callsigns.
    /// </summary>
    public static IEnumerable<string> Samples(string facility, IReadOnlyList<Runway> runways, IEnumerable<string> taxiways, IEnumerable<string> areas,
        AirfieldFrequencies? frequencies, AirbaseWeather? weather, DepartureProcedures? procedures = null)
    {
        const string cs = "Viper 1-1";
        var via = taxiways.Select(t => new ViaItem(t)).ToList();
        // Every runway crossed, named for every direction of departure.
        var crossings = via.Take(1).Concat(runways.Select(r => new ViaItem(r.EndA.Name, r))).ToList();
        foreach (var end in runways.SelectMany(r => new[] { r.EndA, r.EndB }))
        {
            yield return TaxiClearance(cs, facility, end, crossings, null, "");
            // Altimeter in hPa only, and also in inches of mercury for US types.
            foreach (var type in new[] { "", UsTypes.First() })
            {
                yield return TaxiClearance(cs, facility, end, via, weather, type);
                yield return ReportFinal(cs, facility, end, weather, type);
            }
            yield return HoldShortContactTower(cs, facility, end, frequencies?.Tower);
            foreach (var d in DepartureVariants(end, procedures ?? DepartureProcedures.Default))
            {
                yield return ClearedForTakeoff(cs, end, weather, d);
                if (d.UntilAdvised) yield return ClimbUnrestricted(cs, d);
            }
            yield return ClearedToLand(cs, end, weather);
            yield return LineUpAndWait(cs, end);
            yield return NotAtHoldingPoint(cs, facility, end);
            foreach (var reason in HoldReasons) yield return HoldShort(cs, facility, end, reason);
        }
        foreach (var area in areas.Append("parking"))
        {
            yield return TaxiToParking(cs, facility, area, via);
            yield return TaxiToParking(cs, facility, area, []);
        }
        foreach (var reason in ApproachReasons)
        {
            yield return ContinueApproach(cs, reason);
            yield return GoAround(cs, reason);
        }
        yield return UnableToRoute(cs, facility);
        yield return NoFreeParking(cs, facility);
        yield return TaxiCancelled(cs, facility);
        yield return OffRoute(cs, facility);
        yield return ReduceTaxiSpeed(cs, facility);
        yield return ParkingReached(cs, facility);
        yield return ContactAfterDeparture(cs, frequencies?.Approach);
        yield return ContactAfterDeparture(cs, frequencies?.Approach, resumeOwnNavigation: true);
        yield return LeavingPattern(cs, frequencies?.Approach);
        yield return CheckAltitudeStayLow(cs, procedures ?? DepartureProcedures.Default);
        yield return CheckAltitudeMaintain(cs, procedures ?? DepartureProcedures.Default);
        yield return CheckHeading(cs);
        yield return VacateContactGround(cs, facility, frequencies?.Ground);
    }

    /// <summary>Every climb-out Tower can give from this runway end: each intent, with and without each restriction.</summary>
    private static IEnumerable<DepartureInstruction> DepartureVariants(RunwayEnd end, DepartureProcedures procedures)
    {
        foreach (var intent in Enum.GetValues<DepartureIntent>())
        {
            if (intent == DepartureIntent.ClosedTraffic)
            {
                yield return DepartureInstruction.Create(intent, end, procedures);
                continue;
            }
            foreach (var (stayLow, following) in new[] { (false, (string?)null), (true, null), (false, "traffic"), (true, "traffic") })
                yield return DepartureInstruction.Create(intent, end, procedures, stayLow, following);
        }
    }

    /// <summary>What a pilot's callsign is said as, alone and as a flight ("Hangman 1-1", "Hangman 1 flight").</summary>
    public static IEnumerable<string> CallsignSamples(string callsign)
    {
        yield return $"{callsign}, ";
        if (FlightRegistry.Split(callsign) is { } split) yield return $"{split.Prefix} flight, ";
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
