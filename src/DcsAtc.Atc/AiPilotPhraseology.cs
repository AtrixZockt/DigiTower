using DcsAtc.Airfields.Taxi;

namespace DcsAtc.Atc;

/// <summary>What AI pilots say: their calls to the controllers and their readbacks (callsign last, as pilots do).</summary>
public static partial class Phraseology
{
    /// <summary>"Al Dhafra Ground, Enfield 1-1, request taxi."</summary>
    public static string AiRequestTaxi(string facility, string callsign) => $"{facility} Ground, {callsign}, request taxi.";

    /// <summary>"Taxi to holding point runway 31R, Enfield 1-1."</summary>
    public static string AiTaxiReadback(string callsign, RunwayEnd end) => $"Taxi to holding point runway {end.Name}, {callsign}.";

    /// <summary>"Al Dhafra Tower, Enfield 1-1, ready for departure runway 31R."</summary>
    public static string AiReadyForDeparture(string facility, string callsign, RunwayEnd end) =>
        $"{facility} Tower, {callsign}, ready for departure runway {end.Name}.";

    /// <summary>"Runway 31R, cleared for takeoff, Enfield 1-1."</summary>
    public static string AiTakeoffReadback(string callsign, RunwayEnd end) => $"Runway {end.Name}, cleared for takeoff, {callsign}.";

    /// <summary>"Al Dhafra Approach, Colt 2-1, 14 miles north of Al Dhafra, inbound for landing."</summary>
    public static string AiInbound(string facility, string callsign, string position) =>
        $"{facility} Approach, {callsign}, {position}, inbound for landing.";

    /// <summary>"Straight in runway 31R, Colt 2-1."</summary>
    public static string AiStraightInReadback(string callsign, RunwayEnd end) => $"Straight in runway {end.Name}, {callsign}.";

    /// <summary>"Hold over the TACAN, 7000 feet, Colt 2-1."</summary>
    public static string AiHoldReadback(string callsign, bool tacan, double altitude) =>
        $"Hold over the {(tacan ? "TACAN" : "field")}, {altitude:0} feet, {callsign}.";

    /// <summary>"Leaving the hold, final runway 31R, 3000 feet, Colt 2-1."</summary>
    public static string AiLeaveHoldReadback(string callsign, RunwayEnd end, double altitude) =>
        $"Leaving the hold, final runway {end.Name}, {altitude:0} feet, {callsign}.";

    /// <summary>"Al Dhafra Tower, Colt 2-1, final runway 31R."</summary>
    public static string AiOnFinal(string facility, string callsign, RunwayEnd end) => $"{facility} Tower, {callsign}, final runway {end.Name}.";

    /// <summary>"Cleared to land runway 31R, Colt 2-1."</summary>
    public static string AiLandingReadback(string callsign, RunwayEnd end) => $"Cleared to land runway {end.Name}, {callsign}.";

    /// <summary>Everything AI pilots say at an airfield (for preparing the clips), apart from their callsigns and positions.</summary>
    public static IEnumerable<string> AiPilotSamples(string facility, IEnumerable<RunwayEnd> ends, IEnumerable<double> holdingLevels, double finalAltitude)
    {
        const string cs = "Enfield 1-1";
        yield return AiRequestTaxi(facility, cs);
        yield return $"{facility} Approach, {cs}, ";
        yield return "inbound for landing.";
        foreach (var end in ends)
        {
            yield return AiTaxiReadback(cs, end);
            yield return AiReadyForDeparture(facility, cs, end);
            yield return AiTakeoffReadback(cs, end);
            yield return AiStraightInReadback(cs, end);
            yield return AiLeaveHoldReadback(cs, end, finalAltitude);
            yield return AiOnFinal(facility, cs, end);
            yield return AiLandingReadback(cs, end);
        }
        foreach (var level in holdingLevels)
        {
            yield return AiHoldReadback(cs, true, level);
            yield return AiHoldReadback(cs, false, level);
        }
    }
}
