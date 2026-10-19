using System.Text.Json.Serialization;

namespace DcsAtc.Bridge;

/// <summary>A message received from the DCS bridge. Coordinates are DCS map coordinates: X = north, Z = east, Y = altitude (m).</summary>
public abstract record BridgeMessage;

/// <summary>The connection to the bridge hook was opened or lost.</summary>
public sealed record ConnectionChangedMessage(bool Connected, string? Reason = null) : BridgeMessage;

/// <summary>Sent by the hook right after the app connects.</summary>
public sealed record HelloMessage : BridgeMessage
{
    public int Protocol { get; init; }
    public string? Mission { get; init; }
    public bool MissionRunning { get; init; }
    public MissionInfo? Info { get; init; }
    public bool Multiplayer { get; init; }
    /// <summary>False when the server turned AI handling off (<c>manageAi = false</c> in DcsAtcConfig.lua); null from older bridges.</summary>
    public bool? ManageAi { get; init; }
    /// <summary>The hook's last error (e.g. a failed mission injection), if any.</summary>
    public string? Error { get; init; }
}

public sealed record MissionStartedMessage : BridgeMessage
{
    public string? Mission { get; init; }
    public MissionInfo? Info { get; init; }
}

public sealed record MissionStoppedMessage : BridgeMessage;

/// <summary>A snapshot of all active airplanes and helicopters.</summary>
public sealed record UnitsMessage(double Time, IReadOnlyList<UnitState> Units) : BridgeMessage;

/// <summary>A DCS world event, e.g. "birth", "engine_startup", "takeoff", "runway_touch".</summary>
public sealed record DcsEventMessage : BridgeMessage
{
    [JsonPropertyName("ev")] public string Event { get; init; } = "";
    [JsonPropertyName("t")] public double Time { get; init; }
    public string? Unit { get; init; }
    public int? UnitId { get; init; }
    public string? UnitType { get; init; }
    public int? Coal { get; init; }
    public string? Player { get; init; }
    public string? Group { get; init; }
    public int? GroupId { get; init; }
    public string? Place { get; init; }
}

/// <summary>A player selected one of the app's F10 menu entries.</summary>
public sealed record MenuSelectedMessage : BridgeMessage
{
    public int GroupId { get; init; }
    public string Id { get; init; } = "";
    [JsonPropertyName("t")] public double Time { get; init; }
}

/// <summary>An error reported by the hook or the mission side.</summary>
public sealed record BridgeErrorMessage : BridgeMessage
{
    public string Error { get; init; } = "";
    public string? Cmd { get; init; }
}

public sealed record MissionInfo
{
    public int Protocol { get; init; }
    public string? Theatre { get; init; }
    public MissionDate? Date { get; init; }
    public double StartTime { get; init; }
    public double AbsTime { get; init; }
    public double ModelTime { get; init; }
}

public sealed record MissionDate(int Year, int Month, int Day);

public sealed record UnitState
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    /// <summary>"plane" or "heli".</summary>
    public string Cat { get; init; } = "";
    public int Coal { get; init; }
    public string? Group { get; init; }
    public int? GroupId { get; init; }
    public string? Player { get; init; }
    public string? Callsign { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public double Vx { get; init; }
    public double Vy { get; init; }
    public double Vz { get; init; }
    /// <summary>True heading in degrees.</summary>
    public double Hdg { get; init; }
    /// <summary>Height above ground in metres.</summary>
    public double Agl { get; init; }
    public bool InAir { get; init; }

    [JsonIgnore] public bool IsHelicopter => Cat == "heli";
    [JsonIgnore] public bool IsPlayer => !string.IsNullOrEmpty(Player);
    /// <summary>Ground speed in m/s.</summary>
    [JsonIgnore] public double GroundSpeed => Math.Sqrt(Vx * Vx + Vz * Vz);
}

public sealed record AirbaseSummary
{
    public string Name { get; init; } = "";
    public int Id { get; init; }
    /// <summary>0 = airdrome, 1 = helipad/FARP, 2 = ship.</summary>
    public int? Category { get; init; }
    public int Coalition { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
}

/// <summary>An airdrome's ATC frequencies as DCS has them, the ones its briefing shows (bridge <c>airbase_radios</c>).</summary>
public sealed record AirbaseRadios
{
    /// <summary>The airdrome number, the same as the airbase id.</summary>
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public IReadOnlyList<AtcFrequency> Frequencies { get; init; } = [];
}

/// <param name="Modulation">0 AM, 1 FM.</param>
public sealed record AtcFrequency(double Hz, int Modulation)
{
    public double Mhz => Hz / 1e6;
    public bool IsAm => Modulation == 0;
}

public sealed record AirbaseInfo
{
    public string Name { get; init; } = "";
    public int Id { get; init; }
    public string? Callsign { get; init; }
    public int? Category { get; init; }
    public int Coalition { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public bool? RadioSilent { get; init; }
    public IReadOnlyList<RunwayInfo> Runways { get; init; } = [];
    public IReadOnlyList<ParkingInfo> Parking { get; init; } = [];
}

/// <summary>A runway as reported by DCS. DCS reports one entry per physical runway.</summary>
public sealed record RunwayInfo
{
    [JsonConverter(typeof(StringOrNumberConverter))] public string? Name { get; init; }
    /// <summary>DCS "course" in radians. The true heading is -course.</summary>
    public double Course { get; init; }
    public double Width { get; init; }
    public double Length { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }

    [JsonIgnore]
    public double TrueHeading => ((-Course * 180 / Math.PI) % 360 + 360) % 360;
}

public sealed record ParkingInfo
{
    /// <summary>Term_Index: the parking spot number used when spawning.</summary>
    public int Index { get; init; }
    public int Index0 { get; init; }
    /// <summary>Term_Type: kind of spot (16 runway, 40 helicopter only, 68 shelter, 72 open medium, 104 open big; see MOOSE AIRBASE.TerminalType).</summary>
    public int TermType { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public double DistToRunway { get; init; }
    public bool Occupied { get; init; }
}

/// <summary>Weather at an airbase: wind 10 m above the field, temperature and pressure at field elevation.</summary>
/// <summary>The mission's weather (as set in the editor) and the current fog; all distances in metres.</summary>
public sealed record MissionWeather
{
    /// <summary>Visibility (without fog or dust).</summary>
    public double? Visibility { get; init; }
    /// <summary>Visibility in dust, if dust is on (up to 1500 ft).</summary>
    public double? Dust { get; init; }
    /// <summary>Fog visibility and thickness (top above sea level); 0 or absent: no fog.</summary>
    public double? FogVisibility { get; init; }
    public double? FogThickness { get; init; }
    /// <summary>Cloud base and thickness (legacy clouds), density 0–10, or a preset ("Preset1", "RainyPreset2") whose layers move with the base.</summary>
    public double? CloudBase { get; init; }
    public double? CloudThickness { get; init; }
    public int? CloudDensity { get; init; }
    public string? CloudPreset { get; init; }
    /// <summary>0 none, 1 rain, 2 thunderstorm, 3 snow, 4 snowstorm (legacy clouds).</summary>
    public int? Precipitation { get; init; }
    /// <summary>The mission clock: seconds since midnight of the mission's start date (local time of the map).</summary>
    public double AbsTime { get; init; }
}

public sealed record AirbaseWeather
{
    /// <summary>Wind velocity north component (m/s), in the direction the wind blows towards.</summary>
    public double WindX { get; init; }
    /// <summary>Wind velocity east component (m/s), in the direction the wind blows towards.</summary>
    public double WindZ { get; init; }
    /// <summary>Kelvin.</summary>
    public double Temperature { get; init; }
    /// <summary>Station pressure in pascals.</summary>
    public double Pressure { get; init; }
    /// <summary>Field elevation in metres.</summary>
    public double Elevation { get; init; }

    [System.Text.Json.Serialization.JsonIgnore] public double WindSpeed => Math.Sqrt(WindX * WindX + WindZ * WindZ);
    [System.Text.Json.Serialization.JsonIgnore] public double WindSpeedKnots => WindSpeed * 1.943844;

    /// <summary>The true direction the wind comes from, in degrees (meteorological convention).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double WindFrom => ((Math.Atan2(-WindZ, -WindX) * 180 / Math.PI) + 360) % 360;

    /// <summary>QNH in hPa: station pressure reduced to sea level with the ISA lapse rate.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double QnhHpa => Pressure / 100 * Math.Pow(1 - 0.0065 * Elevation / 288.15, -5.25588);
}
