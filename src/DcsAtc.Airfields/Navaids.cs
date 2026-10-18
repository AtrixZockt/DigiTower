using System.Globalization;
using System.Text.RegularExpressions;
using DcsAtc.Airfields.Taxi;

namespace DcsAtc.Airfields;

public enum NavaidType
{
    Other,
    Vor,
    Dme,
    VorDme,
    Tacan,
    Vortac,
    Ndb,
    IlsLocalizer,
    IlsGlideslope,
    IlsMarker,
}

/// <summary>A radio navigation aid from a DCS terrain's <c>beacons.lua</c>.</summary>
/// <param name="Id">The DCS beacon id, e.g. "airfield4_8": airdrome 4's beacons are its ILS, TACAN, …</param>
/// <param name="Direction">Course in true degrees (localizers: the landing direction).</param>
public sealed record Navaid(string Id, NavaidType Type, string Callsign, double FrequencyMhz, int? Channel, Vec2 Position, double Elevation, double Direction)
{
    /// <summary>The DCS airdrome the navaid belongs to (null for en-route beacons).</summary>
    public int? AirdromeId => Id.StartsWith("airfield", StringComparison.Ordinal) && Id.IndexOf('_') is int u and > 8
        && int.TryParse(Id.AsSpan(8, u - 8), out int id) ? id : null;

    public bool IsTacan => Type is NavaidType.Tacan or NavaidType.Vortac;

    /// <summary>"96X" (DCS ground TACANs are X channels).</summary>
    public string? TacanChannel => IsTacan && Channel is int c ? $"{c}X" : null;
}

/// <summary>The navaids of one airfield, matched to its runways.</summary>
public sealed class AirfieldNavaids(IReadOnlyList<Navaid> navaids)
{
    public static readonly AirfieldNavaids None = new([]);

    public IReadOnlyList<Navaid> All => navaids;

    public Navaid? Tacan => navaids.FirstOrDefault(n => n.IsTacan);

    public bool HasIls => navaids.Any(n => n.Type == NavaidType.IlsLocalizer);

    /// <summary>The localizer for landing on <paramref name="end"/>: same course, on that runway's centerline.</summary>
    public Navaid? LocalizerFor(Runway runway, RunwayEnd end) => navaids
        .Where(n => n.Type == NavaidType.IlsLocalizer && Math.Abs(((n.Direction - end.TrueHeading) % 360 + 540) % 360 - 180) < 5)
        .Where(n => runway.FromCenterline(n.Position) < 300 && n.Position.DistanceTo(runway.Center) < runway.Length / 2 + 3000)
        .MinBy(n => runway.FromCenterline(n.Position));
}

/// <summary>
/// Reads a terrain's <c>beacons.lua</c> (DCS "beaconsTableFormat = 2": one Lua table per beacon with type, callsign,
/// frequency in Hz, channel, position {x, y, z}, direction and beaconId). Only the fields are read; nothing is executed.
/// </summary>
public static partial class BeaconReader
{
    public static IReadOnlyList<Navaid> Read(string path) => Parse(File.ReadAllText(path));

    public static IReadOnlyList<Navaid> Parse(string text)
    {
        var result = new List<Navaid>();
        foreach (var block in BlockStart().Split(text).Skip(1))
        {
            if (Field(block, BeaconId()) is not { } id || Field(block, TypeField()) is not { } type) continue;
            var pos = Position().Match(block);
            if (!pos.Success) continue;
            double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
            double direction = Field(block, Direction()) is { } d ? D(d) : 0;
            result.Add(new Navaid(
                id,
                TypeOf(type),
                Field(block, Callsign()) ?? "",
                Field(block, Frequency()) is { } f ? D(f) / 1e6 : 0,
                Field(block, Channel()) is { } c ? int.Parse(c, CultureInfo.InvariantCulture) : null,
                new Vec2(D(pos.Groups[1].Value), D(pos.Groups[3].Value)),
                D(pos.Groups[2].Value),
                (direction % 360 + 360) % 360));
        }
        return result;
    }

    private static NavaidType TypeOf(string type) => type switch
    {
        "BEACON_TYPE_VOR" => NavaidType.Vor,
        "BEACON_TYPE_DME" => NavaidType.Dme,
        "BEACON_TYPE_VOR_DME" => NavaidType.VorDme,
        "BEACON_TYPE_TACAN" => NavaidType.Tacan,
        "BEACON_TYPE_VORTAC" => NavaidType.Vortac,
        "BEACON_TYPE_HOMER" or "BEACON_TYPE_AIRPORT_HOMER" or "BEACON_TYPE_AIRPORT_HOMER_WITH_MARKER" => NavaidType.Ndb,
        "BEACON_TYPE_ILS_LOCALIZER" => NavaidType.IlsLocalizer,
        "BEACON_TYPE_ILS_GLIDESLOPE" => NavaidType.IlsGlideslope,
        "BEACON_TYPE_ILS_FAR_HOMER" or "BEACON_TYPE_ILS_NEAR_HOMER" => NavaidType.IlsMarker,
        _ => NavaidType.Other,
    };

    private static string? Field(string block, Regex regex) => regex.Match(block) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"\n\s*\{\s*\r?\n")]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"beaconId\s*=\s*'([^']*)'")]
    private static partial Regex BeaconId();

    [GeneratedRegex(@"\btype\s*=\s*(BEACON_TYPE_\w+)")]
    private static partial Regex TypeField();

    [GeneratedRegex(@"callsign\s*=\s*'([^']*)'")]
    private static partial Regex Callsign();

    [GeneratedRegex(@"frequency\s*=\s*([-\d.]+)")]
    private static partial Regex Frequency();

    [GeneratedRegex(@"channel\s*=\s*(\d+)")]
    private static partial Regex Channel();

    [GeneratedRegex(@"\bposition\s*=\s*\{\s*([-\d.]+)\s*,\s*([-\d.]+)\s*,\s*([-\d.]+)")]
    private static partial Regex Position();

    [GeneratedRegex(@"direction\s*=\s*([-\d.]+)")]
    private static partial Regex Direction();
}
