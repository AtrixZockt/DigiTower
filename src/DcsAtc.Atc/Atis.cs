using System.Globalization;
using DcsAtc.Airfields;
using DcsAtc.Airfields.Taxi;
using DcsAtc.Bridge;

namespace DcsAtc.Atc;

/// <summary>What an ATIS reports, apart from its letter and time (it is compared to decide when a new letter is due).</summary>
/// <param name="Sky">Visibility, weather and clouds as said ("CAVOK", "Visibility 6 kilometers, mist. Clouds broken 1200 feet"); null without mission weather.</param>
public sealed record AtisContent(string Facility, IReadOnlyList<RunwayEnd> Runways, string Approach, int WindFrom, int WindKnots,
    string? Sky, int TemperatureC, AirbaseWeather Weather)
{
    public int Qnh => (int)Math.Floor(Weather.QnhHpa);
}

/// <summary>
/// The ATIS of each airfield with an ATIS frequency: a recorded broadcast, looping on that frequency while someone
/// listens, with runway, approach, wind, visibility, weather, clouds, temperature and QNH. A new information letter
/// comes with every significant change (runway, approach, sky, QNH, wind by 30° or 5 knots) and every hour; the time
/// is the time of that letter, in Zulu. Every airfield's ATIS can also be read from the menu.
/// </summary>
public sealed class AtisService(IDcsCommands dcs, AirfieldCatalog airfields, IAtcRadio? radio = null, Action<string>? log = null)
{
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromMinutes(1);
    private const double Ft = 0.3048;

    /// <summary>The maps' time zones (local time in DCS minus UTC, hours), as MOOSE has them.</summary>
    public static readonly IReadOnlyDictionary<string, double> UtcOffsets = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["Caucasus"] = 4, ["PersianGulf"] = 4, ["Nevada"] = -8, ["Normandy"] = 0, ["TheChannel"] = 2, ["Syria"] = 3,
        ["MarianaIslands"] = 10, ["Falklands"] = -3, ["SinaiMap"] = 2, ["Kola"] = 3, ["Afghanistan"] = 4.5, ["Iraq"] = 3, ["GermanyCW"] = 1,
    };

    private readonly Dictionary<int, State> _states = [];
    private DateTime _lastUpdate = DateTime.MinValue;
    private bool _warned;

    /// <summary>The current information of an airfield, by its airbase id.</summary>
    public IReadOnlyDictionary<int, (string Letter, string Text)> Current =>
        _states.ToDictionary(s => s.Key, s => (Phraseology.LetterName(s.Value.Letter), s.Value.Text));

    /// <summary>Called with every unit update: brings the ATIS up to date once a minute.</summary>
    public async Task OnUnitsAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _lastUpdate < UpdateInterval) return;
        await UpdateAsync(ct);
    }

    /// <summary>Checks the weather and updates (and broadcasts) the ATIS of every airfield with an ATIS frequency.</summary>
    public async Task UpdateAsync(CancellationToken ct = default)
    {
        _lastUpdate = DateTime.UtcNow;
        var mission = await MissionWeatherAsync(ct);
        foreach (var airbase in airfields.Airbases.Where(a => a.Category == 0 && airfields.LabelsFor(a.Name)?.Frequencies?.Atis is not null).ToList())
        {
            if (await airfields.GetAsync(airbase, ct) is not { } airfield) continue;
            var (text, changed) = await UpdateAsync(airfield, mission, ct);
            if (!changed || radio is null || airfield.Layout.Labels?.Frequencies?.Atis is not double mhz) continue;
            radio.Prepare([text], soon: true);
            radio.Broadcast($"{airfield.Facility} ATIS", mhz, airbase.Coalition is 1 or 2 ? airbase.Coalition : 2, text);
            log?.Invoke($"ATIS {airfield.Facility}: {text}");
        }
    }

    /// <summary>The current ATIS of any airfield (made now if it has none yet), for the menu.</summary>
    public async Task<string> TextAsync(GroundAirfield airfield, CancellationToken ct = default) =>
        (await UpdateAsync(airfield, await MissionWeatherAsync(ct), ct)).Text;

    /// <summary>The mission ended: stop the broadcasts and start the letters again.</summary>
    public void Reset()
    {
        foreach (var airbase in airfields.Airbases)
        {
            if (radio is not null && airfields.LabelsFor(airbase.Name)?.Frequencies?.Atis is double mhz)
                radio.Broadcast($"{Phraseology.Facility(airbase.Name)} ATIS", mhz, airbase.Coalition is 1 or 2 ? airbase.Coalition : 2, null);
        }
        _states.Clear();
        _lastUpdate = DateTime.MinValue;
    }

    private async Task<(string Text, bool Changed)> UpdateAsync(GroundAirfield airfield, MissionWeather? mission, CancellationToken ct)
    {
        var weather = await dcs.GetAirbaseWeatherAsync(airfield.Airbase.Name, ct) ?? new AirbaseWeather();
        var content = Content(airfield, weather, mission, airfields.CloudPresets);
        TimeSpan? zulu = mission is null ? null : Zulu(mission.AbsTime, airfields.Theatre);
        if (!_states.TryGetValue(airfield.Airbase.Id, out var state)) _states[airfield.Airbase.Id] = state = new State();
        bool newHour = zulu is { } z && state.Time is { } t && (int)z.TotalHours != (int)t.TotalHours;
        if (state.Published is null || Significant(state.Published, content) || newHour)
        {
            state.Letter = state.Published is null ? 0 : (state.Letter + 1) % 26;
            state.Published = content;
            state.Time = zulu;
            state.Text = Phraseology.Atis(content, Phraseology.LetterName(state.Letter), zulu);
            return (state.Text, true);
        }
        return (state.Text, false);
    }

    private async Task<MissionWeather?> MissionWeatherAsync(CancellationToken ct)
    {
        try
        {
            return await dcs.GetMissionWeatherAsync(ct);
        }
        catch (BridgeCommandException)
        {
            if (!_warned) log?.Invoke("ATIS: the bridge in DCS is older than the app (no mission weather): no clouds, visibility or time. Run bridge/install.ps1 again.");
            _warned = true;
            return null;
        }
    }

    /// <summary>A change that needs a new letter.</summary>
    private static bool Significant(AtisContent was, AtisContent now) =>
        !was.Runways.Select(r => r.Name).SequenceEqual(now.Runways.Select(r => r.Name)) || was.Approach != now.Approach || was.Sky != now.Sky
        || was.Qnh != now.Qnh || (was.WindKnots < 3) != (now.WindKnots < 3) || Math.Abs(was.WindKnots - now.WindKnots) >= 5
        || (now.WindKnots >= 3 && Math.Abs(((now.WindFrom - was.WindFrom) % 360 + 540) % 360 - 180) >= 30);

    /// <summary>Zulu time from the mission clock (local time of the map).</summary>
    public static TimeSpan Zulu(double absTime, string? theatre)
    {
        double offset = theatre is not null && UtcOffsets.TryGetValue(theatre, out var o) ? o : 0;
        double seconds = ((absTime - offset * 3600) % 86400 + 86400) % 86400;
        return TimeSpan.FromSeconds(Math.Floor(seconds / 60) * 60);
    }

    /// <summary>What the ATIS of an airfield says now.</summary>
    public static AtisContent Content(GroundAirfield airfield, AirbaseWeather weather, MissionWeather? mission, IReadOnlyDictionary<string, CloudPreset> presets)
    {
        var active = airfield.Layout.ActiveEnds(weather.WindFrom, weather.WindSpeed).Select(e => e.End).OrderBy(e => e.Name).ToList();
        string approach = airfield.Layout.Runways.Any(r => active.Any(e => (r.EndA == e || r.EndB == e) && airfield.Radio.LocalizerFor(r, e) is not null)) ? "ILS"
            : airfield.Radio.Tacan is not null ? "TACAN" : "visual";
        int temperature = (int)Math.Round(weather.Temperature - 273.15);
        return new AtisContent(airfield.Facility, active, approach, (int)(Math.Round(weather.WindFrom / 10) * 10), (int)Math.Round(weather.WindSpeedKnots),
            mission is null ? null : Sky(mission, weather.Elevation, temperature, presets), temperature, weather);
    }

    /// <summary>"CAVOK", or "Visibility 6 kilometers, mist. Clouds scattered 2500 feet, broken 8000 feet" (heights above the field).</summary>
    public static string Sky(MissionWeather mission, double elevation, int temperatureC, IReadOnlyDictionary<string, CloudPreset> presets)
    {
        double visibility = mission.Visibility ?? 80000;
        var weather = new List<string>();
        if (mission.FogVisibility is > 0 && mission.FogThickness > elevation + 25)
        {
            visibility = Math.Min(visibility, mission.FogVisibility.Value);
            weather.Add(mission.FogVisibility < 1000 ? "fog" : "mist");
        }
        else if (mission.Dust is > 0 && elevation < 1500 * Ft)
        {
            visibility = Math.Min(visibility, mission.Dust.Value);
            weather.Add("dust");
        }

        var layers = new List<CloudLayer>();
        bool precipitation;
        if (mission.CloudPreset is { Length: > 0 } name && presets.TryGetValue(name, out var preset))
        {
            layers.AddRange(preset.At(mission.CloudBase ?? preset.Layers.FirstOrDefault()?.Base ?? 0));
            precipitation = preset.Precipitation;
        }
        else
        {
            if (mission.CloudDensity is > 0 && mission.CloudBase is double b)
                layers.Add(new CloudLayer(b, b + (mission.CloudThickness ?? 200), mission.CloudDensity.Value / 10.0));
            precipitation = mission.Precipitation is > 0;
        }
        if (precipitation)
        {
            weather.Insert(0, mission.Precipitation switch
            {
                2 => "thunderstorm",
                3 or 4 => "snow",
                _ => temperatureC <= 0 ? "snow" : "rain",
            });
        }

        var reported = layers.Select(l => (l.Amount, Feet: (int)(Math.Round((l.Base - elevation) / Ft / 100) * 100))).Where(l => l.Feet < 25000).Take(3).ToList();
        if (visibility >= 10000 && weather.Count == 0 && reported.All(l => l.Feet >= 5000)) return "CAVOK";

        string sky = visibility >= 10000 ? "Visibility 10 kilometers or more"
            : visibility >= 5000 ? $"Visibility {Math.Floor(visibility / 1000):0} kilometers"
            : $"Visibility {Math.Max(100, Math.Floor(visibility / 100) * 100):0} meters";
        if (weather.Count > 0) sky += ", " + string.Join(", ", weather);
        sky += ". " + (reported.Count == 0 ? "No significant clouds"
            : "Clouds " + string.Join(", ", reported.Select(l => $"{l.Amount} {Math.Max(100, l.Feet).ToString(CultureInfo.InvariantCulture)} feet")));
        return sky;
    }

    private sealed class State
    {
        public int Letter { get; set; }
        public AtisContent? Published { get; set; }
        public TimeSpan? Time { get; set; }
        public string Text { get; set; } = "";
    }
}
