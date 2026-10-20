using DcsAtc.Airfields;
using DcsAtc.Bridge;

namespace DcsAtc.Atc;

/// <summary>
/// Helicopters at an airfield (FAA JO 7110.65 §3-11): they air-taxi (in a hover, or flying below 100 ft), depart from
/// where they are and land at a spot instead of a runway. DCS reports a hovering helicopter as in the air, so near an
/// airfield a low helicopter still counts as on the field.
/// </summary>
public static class Helicopters
{
    private const double Ft = 0.3048;

    /// <summary>Air-taxi stays at or below this (normally below 100 ft AGL).</summary>
    public const double AirTaxiHeight = 100 * Ft;

    /// <summary>Air-taxiing higher than this (100 ft and a margin) is told to stay low.</summary>
    public const double AirTaxiWarning = 150 * Ft;

    /// <summary>A helicopter higher than this has left the field: its taxi clearance or startup ends.</summary>
    public const double LeftField = 300 * Ft;

    /// <summary>Helicopters cut corners: further than this from the cleared route counts as off route (metres).</summary>
    public const double OffRouteDistance = 100;

    /// <summary>Helicopter landing and takeoff points at least this far apart may be used at once (200 ft).</summary>
    public const double Separation = 200 * Ft;

    /// <summary>A present-position departure is checked for runway crossings this far along its direction (metres).</summary>
    public const double DeparturePath = 2 * 1852;

    /// <summary>On the ground, or a helicopter hovering or air-taxiing below <see cref="LeftField"/>.</summary>
    public static bool OnField(UnitState unit) => !unit.InAir || (unit.IsHelicopter && unit.Agl < LeftField);

    /// <summary>Whether a parking spot suits a helicopter landing, best first (0); null: unsuitable (runway, shelter).</summary>
    public static int? SpotPreference(ParkingSpot spot) => spot.TermType switch
    {
        40 => 0,
        104 => 1,
        72 or 176 or 216 or 244 => 2,
        _ => null,
    };

    /// <summary>The compass direction of a departure intent (degrees true); null for own navigation and closed traffic.</summary>
    public static double? Bearing(DepartureIntent intent) => intent switch
    {
        DepartureIntent.North => 0,
        DepartureIntent.East => 90,
        DepartureIntent.South => 180,
        DepartureIntent.West => 270,
        _ => null,
    };
}
