using DcsAtc.Airfields;
using DcsAtc.Airfields.Taxi;
using DcsAtc.Bridge;

namespace DcsAtc.Atc;

public enum ApproachType
{
    Ils,
    Tacan,
    Visual,
    Overhead,
}

/// <summary>Approach control phraseology: radar contact, vectors, approach clearances, holding, radar service, traffic.</summary>
public static partial class Phraseology
{
    private static readonly string[] Compass = ["north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest"];

    /// <summary>"northwest": the 8-point compass direction of a true bearing.</summary>
    public static string CompassPoint(double bearing) => Compass[(int)Math.Round(((bearing % 360) + 360) % 360 / 45) % 8];

    /// <summary>"35 miles northwest of Al Dhafra".</summary>
    public static string Position(double miles, double bearingFromField, string facility)
    {
        double distance = Math.Max(1, Math.Round(miles));
        return $"{distance:0} {(distance == 1 ? "mile" : "miles")} {CompassPoint(bearingFromField)} of {facility}";
    }

    /// <summary>A heading as said: three digits, north as 360.</summary>
    public static int Heading(double degrees)
    {
        int h = (int)Math.Round((((degrees % 360) + 360) % 360) / 10) * 10;
        return h == 0 ? 360 : h;
    }

    /// <summary>"turn left heading 130" (or "fly heading 130" for a change under 10°).</summary>
    public static string Vector(double currentHeading, int heading)
    {
        double change = ((heading - currentHeading) % 360 + 540) % 360 - 180;
        return Math.Abs(change) < 10 ? $"fly heading {heading:000}" : $"turn {(change > 0 ? "right" : "left")} heading {heading:000}";
    }

    /// <summary>"descend and maintain 4000 feet", "climb and maintain 4000 feet" or "maintain 4000 feet".</summary>
    public static string AltitudeChange(double currentFeet, double assignedFeet) =>
        currentFeet > assignedFeet + 300 ? $"descend and maintain {assignedFeet:0} feet"
        : currentFeet < assignedFeet - 300 ? $"climb and maintain {assignedFeet:0} feet"
        : $"maintain {assignedFeet:0} feet";

    public static string ApproachName(ApproachType type) => type switch
    {
        ApproachType.Ils => "ILS approach",
        ApproachType.Tacan => "TACAN approach",
        ApproachType.Visual => "visual approach",
        _ => "overhead",
    };

    /// <summary>"Expect ILS approach runway 31R, localizer 108.7." and the like.</summary>
    public static string ExpectApproach(ApproachType type, RunwayEnd end, Navaid? guidance) => type switch
    {
        ApproachType.Ils => $"Expect ILS approach runway {end.Name}{(guidance is null ? "" : $", localizer {guidance.FrequencyMhz:0.0#}")}.",
        ApproachType.Tacan => $"Expect TACAN approach runway {end.Name}{(guidance?.TacanChannel is { } ch ? $", TACAN channel {ch}" : "")}.",
        ApproachType.Visual => $"Expect visual approach runway {end.Name}, report field in sight.",
        _ => $"Expect overhead runway {end.Name}, proceed to initial.",
    };

    /// <summary>
    /// The first call to an arrival: "Viper 1-1, Al Dhafra Approach, radar contact, 35 miles northwest of Al Dhafra.
    /// Descend and maintain 6000 feet, QNH 1013. Expect ILS approach runway 31R, localizer 108.7. Turn left heading 130."
    /// Without a position the aircraft is already in radar contact (a departure coming back): "Viper 1-1, roger. Descend …".
    /// </summary>
    public static string ApproachContact(string callsign, string facility, string? position, string altitude, AirbaseWeather? weather, string unitType, string expect, string? vector) =>
        $"{Identified(callsign, facility, position)} {Capitalize(altitude)}{(weather is null ? "" : $", {Altimeter(weather, unitType)}")}. {expect}" +
        (vector is null ? "" : $" {Capitalize(vector)}.");

    /// <summary>"Viper 1-1, Al Dhafra Approach, radar contact, 35 miles northwest of Al Dhafra." or, already identified, "Viper 1-1, roger."</summary>
    private static string Identified(string callsign, string facility, string? position) =>
        position is null ? $"{callsign}, roger." : $"{callsign}, {facility} Approach, radar contact, {position}.";

    /// <summary>"Viper 1-1, turn left heading 130, descend and maintain 4000 feet."</summary>
    public static string VectorCall(string callsign, string vector, string? altitude = null) =>
        $"{callsign}, {vector}{(altitude is null ? "" : $", {altitude}")}.";

    /// <summary>"Viper 1-1, extend downwind, number 2."</summary>
    public static string ExtendDownwind(string callsign, int number) => $"{callsign}, extend downwind, number {number}.";

    /// <summary>
    /// The approach clearance: "Viper 1-1, 12 miles from touchdown, turn right heading 340, maintain 3000 feet until established
    /// on the localizer, cleared ILS approach runway 31R." (visual without the field in sight: "…, maintain 3000 feet, report field in sight.")
    /// </summary>
    public static string ApproachClearance(string callsign, double milesToTouchdown, string vector, double altitude, ApproachType type, RunwayEnd end) => type switch
    {
        ApproachType.Ils => $"{callsign}, {Math.Round(milesToTouchdown):0} miles from touchdown, {vector}, maintain {altitude:0} feet until established on the localizer, cleared ILS approach runway {end.Name}.",
        ApproachType.Tacan => $"{callsign}, {Math.Round(milesToTouchdown):0} miles from touchdown, {vector}, maintain {altitude:0} feet until established on final, cleared TACAN approach runway {end.Name}.",
        _ => $"{callsign}, {Math.Round(milesToTouchdown):0} miles from touchdown, {vector}, maintain {altitude:0} feet, report field in sight.",
    };

    public static string ClearedVisualApproach(string callsign, RunwayEnd end, double? tower) =>
        $"{callsign}, cleared visual approach runway {end.Name}, {ContactTower(tower)}.";

    /// <summary>"contact Tower on 270.0" (or "contact Tower").</summary>
    private static string ContactTower(double? tower) => Frequency(tower) is { } f ? $"contact Tower on {f}" : "contact Tower";

    /// <summary>Arrival to Tower: "Viper 1-1, contact Tower on 270.0." (overhead: "…, report initial.")</summary>
    public static string ApproachToTower(string callsign, double? tower, bool overhead) =>
        $"{callsign}, {ContactTower(tower)}{(overhead ? ", report initial" : "")}.";

    /// <summary>Overhead arrival nearing the field: "Viper 1-1, descend and maintain 1500 feet, proceed to initial runway 31R."</summary>
    public static string ProceedToInitial(string callsign, string altitude, RunwayEnd end) => $"{callsign}, {altitude}, proceed to initial runway {end.Name}.";

    /// <summary>
    /// Into the hold: "Viper 1-3, Al Dhafra Approach, radar contact, 35 miles northwest of Al Dhafra. Hold over the Al Dhafra TACAN,
    /// right turns, maintain 7000 feet, QNH 1013. Expect further clearance in 10 minutes."
    /// </summary>
    public static string Hold(string callsign, string facility, string? position, bool tacan, double altitude, AirbaseWeather? weather, string unitType, int minutes) =>
        $"{Identified(callsign, facility, position)} Hold over the {facility} {(tacan ? "TACAN" : "field")}, right turns, " +
        $"maintain {altitude:0} feet{(weather is null ? "" : $", {Altimeter(weather, unitType)}")}. " +
        $"Expect further clearance in {minutes} minutes.";

    /// <summary>"Viper 1-3, leave the hold, turn left heading 220, descend and maintain 6000 feet. Expect ILS approach runway 31R, localizer 108.7."</summary>
    public static string LeaveHold(string callsign, string vector, string altitude, string expect) => $"{callsign}, leave the hold, {vector}, {altitude}. {expect}";

    public static string ApproachCancelled(string callsign) => $"{callsign}, approach cancelled, resume own navigation.";

    public static string NoApproach(string callsign, double miles) => $"{callsign}, no approach control within {miles:0} miles.";

    /// <summary>Departure check-in: "Viper 1-1, Al Dhafra Approach, radar contact, 6 miles northwest of Al Dhafra." (and "Maintain 6000 feet until advised.")</summary>
    public static string DepartureContact(string callsign, string facility, string position, double? restriction) =>
        $"{callsign}, {facility} Approach, radar contact, {position}.{(restriction is double r ? $" Maintain {r:0} feet until advised." : "")}";

    public static string MaintainUntilAdvised(string callsign, double altitude) => $"{callsign}, maintain {altitude:0} feet until advised, arrival traffic.";

    public static string ApproachClimbUnrestricted(string callsign) => $"{callsign}, climb unrestricted.";

    public static string RadarServiceTerminated(string callsign) => $"{callsign}, radar service terminated, frequency change approved, good day.";

    public static string CheckAltitude(string callsign, double altitude) => $"{callsign}, check altitude, maintain {altitude:0} feet.";

    public static string CheckHeading(string callsign, int heading) => $"{callsign}, check heading, fly heading {heading:000}.";

    /// <summary>"Viper 1-1, traffic, 2 o'clock, 5 miles, opposite direction, 2000 feet above."</summary>
    public static string Traffic(string callsign, int clock, double miles, string direction, double feetAbove)
    {
        double rounded = Math.Round(feetAbove / 100) * 100;
        string height = Math.Abs(rounded) < 300 ? "same altitude" : $"{Math.Abs(rounded):0} feet {(rounded > 0 ? "above" : "below")}";
        double distance = Math.Max(1, Math.Round(miles));
        return $"{callsign}, traffic, {clock} o'clock, {distance:0} {(distance == 1 ? "mile" : "miles")}, {direction}, {height}.";
    }

    /// <summary>
    /// "Al Minhad: ATIS 119.55 / 260.1, Ground/Tower/Approach 118.55 / 250.1. ILS 27 110.75 (IMNW), …, TACAN 99X (MIN)."
    /// (Al Dhafra: "ATIS 270.5, Ground 250.0, Tower 270.0, Approach 257.0, all positions 126.5 / 251.1. …")
    /// </summary>
    public static string FrequencyList(GroundAirfield airfield)
    {
        string radio = airfield.Frequencies.Describe();
        var navaids = airfield.Layout.Runways.SelectMany(r => new[] { r.EndA, r.EndB }.Select(e => (End: e, Loc: airfield.Radio.LocalizerFor(r, e))))
            .Where(x => x.Loc is not null).Select(x => $"ILS {x.End.Name} {x.Loc!.FrequencyMhz:0.0#} ({x.Loc.Callsign})").ToList();
        if (airfield.Radio.Tacan is { } tacan) navaids.Add($"TACAN {tacan.TacanChannel} ({tacan.Callsign})");
        return $"{airfield.Facility}: {(radio.Length > 0 ? radio : "no ATC frequencies, text messages only")}." +
               (navaids.Count > 0 ? $" {string.Join(", ", navaids)}." : "");
    }

    // Tower, after Approach.

    /// <summary>Overhead at the initial point: "Viper 1-1, Al Dhafra Tower, runway 31R, wind 310 degrees, 10 knots, break approved."</summary>
    public static string BreakApproved(string callsign, string facility, RunwayEnd runway, AirbaseWeather? weather) =>
        $"{callsign}, {facility} Tower, runway {runway.Name}, {(weather is null ? "" : Wind(weather) + ", ")}break approved.";

    /// <summary>Tower calling an arrival from Approach that has not checked in: "Viper 1-1, Al Dhafra Tower, runway 31R, wind 310 degrees, 10 knots, cleared to land."</summary>
    public static string ClearedToLandFirstCall(string callsign, string facility, RunwayEnd runway, AirbaseWeather? weather) =>
        $"{callsign}, {facility} Tower, runway {runway.Name}, {(weather is null ? "" : Wind(weather) + ", ")}cleared to land.";

    /// <summary>Checking in on final: "Viper 1-1, Al Dhafra Tower, runway 31R, continue approach, wind 310 degrees, 10 knots."</summary>
    public static string ContinueOnFinal(string callsign, string facility, RunwayEnd runway, AirbaseWeather? weather) =>
        $"{callsign}, {facility} Tower, runway {runway.Name}, continue approach{(weather is null ? "" : ", " + Wind(weather))}.";

    /// <summary>Everything Approach can say at an airfield (for preparing the clips), apart from the pilots' callsigns.</summary>
    public static IEnumerable<string> ApproachSamples(string facility, IReadOnlyList<Runway> runways, AirfieldNavaids navaids, AirfieldStations frequencies,
        AirbaseWeather? weather, ApproachProcedures p)
    {
        const string cs = "Viper 1-1";
        // Positions, headings, altitudes and distances: the number readings Approach uses.
        foreach (var bearing in Enumerable.Range(0, 8).Select(i => i * 45.0))
            for (int miles = 1; miles <= p.RequestRange; miles++)
                yield return $"{cs}, {facility} Approach, radar contact, {Position(miles, bearing, facility)}.";
        for (int heading = 10; heading <= 360; heading += 10)
        {
            yield return VectorCall(cs, $"turn left heading {heading:000}");
            yield return VectorCall(cs, $"turn right heading {heading:000}");
            yield return VectorCall(cs, $"fly heading {heading:000}");
            yield return CheckHeading(cs, heading);
        }
        var altitudes = new[] { p.InitialAltitude, p.InterceptAltitude, p.InterceptAltitude + 1000, p.ArrivalAltitude }
            .Concat(Enumerable.Range(0, Math.Max(1, p.MaxVectored + 3)).Select(i => p.HoldingAltitude + i * p.HoldingStep))
            .Concat(Enumerable.Range(2, 12).Select(k => k * 1000.0)).Distinct();
        foreach (var altitude in altitudes)
        {
            yield return $"{cs}, descend and maintain {altitude:0} feet. Climb and maintain {altitude:0} feet. Maintain {altitude:0} feet.";
            yield return CheckAltitude(cs, altitude);
            yield return MaintainUntilAdvised(cs, altitude);
            yield return DepartureContact(cs, facility, Position(10, 0, facility), altitude);
            yield return Hold(cs, facility, Position(30, 0, facility), navaids.Tacan is not null, altitude, weather, "", 5);
            yield return Hold(cs, facility, Position(30, 0, facility), navaids.Tacan is not null, altitude, weather, UsTypes.First(), 5);
        }
        for (int minutes = 5; minutes <= 30; minutes += 5) yield return $"Expect further clearance in {minutes} minutes.";
        for (int miles = 1; miles <= 30; miles++) yield return $"{cs}, {miles} miles from touchdown.";
        for (int n = 2; n <= 9; n++) yield return ExtendDownwind(cs, n);
        foreach (var end in runways.SelectMany(r => new[] { r.EndA, r.EndB }))
        {
            var runway = runways.First(r => r.EndA == end || r.EndB == end);
            foreach (var type in Enum.GetValues<ApproachType>())
            {
                var guidance = type == ApproachType.Ils ? navaids.LocalizerFor(runway, end) : type == ApproachType.Tacan ? navaids.Tacan : null;
                yield return ExpectApproach(type, end, guidance);
                if (type != ApproachType.Overhead) yield return ApproachClearance(cs, 12, "turn right heading 340", p.InterceptAltitude, type, end);
            }
            yield return ClearedVisualApproach(cs, end, frequencies.Own("Tower"));
            yield return ProceedToInitial(cs, $"descend and maintain {p.InitialAltitude:0} feet", end);
            yield return BreakApproved(cs, facility, end, weather);
            yield return ContinueOnFinal(cs, facility, end, weather);
            yield return ClearedToLandFirstCall(cs, facility, end, weather);
        }
        yield return $"{cs}, {AltitudeChange(10000, p.ArrivalAltitude)}, {Altimeter(weather ?? new AirbaseWeather(), "")}. {AltitudeChange(10000, p.ArrivalAltitude)}, {Altimeter(weather ?? new AirbaseWeather(), UsTypes.First())}.";
        yield return ApproachToTower(cs, frequencies.Own("Tower"), overhead: false);
        yield return ApproachToTower(cs, frequencies.Own("Tower"), overhead: true);
        yield return LeaveHold(cs, "turn left heading 220", "descend and maintain 6000 feet", "");
        yield return ApproachCancelled(cs);
        yield return $"{cs}, roger.";
        yield return ApproachClimbUnrestricted(cs);
        yield return RadarServiceTerminated(cs);
        foreach (var direction in new[] { "same direction", "opposite direction" }.Concat(Compass.Select(c => $"{c}bound")))
            for (int clock = 1; clock <= 12; clock++)
                yield return Traffic(cs, clock, 5, direction, 0);
        for (int miles = 1; miles <= p.TrafficRange + 1; miles++)
            for (int feet = 300; feet <= p.TrafficVertical; feet += 100)
            {
                yield return Traffic(cs, 12, miles, "same direction", feet);
                yield return Traffic(cs, 12, miles, "same direction", -feet);
            }
    }
}
