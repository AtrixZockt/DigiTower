using System.Globalization;
using System.Text.RegularExpressions;

namespace DcsAtc.Airfields;

/// <summary>A cloud layer: base and top in metres above sea level, coverage 0–1.</summary>
public sealed record CloudLayer(double Base, double Top, double Coverage)
{
    /// <summary>The ICAO amount: few (up to 2/8), scattered (3–4/8), broken (5–7/8), overcast.</summary>
    public string Amount => Coverage < 0.25 ? "few" : Coverage < 0.5 ? "scattered" : Coverage < 0.875 ? "broken" : "overcast";
}

/// <summary>
/// A DCS weather preset ("Preset7", "RainyPreset2") from <c>Config/Effects/clouds.lua</c>: its layers at the default base;
/// the mission's cloud base moves the whole stack.
/// </summary>
public sealed record CloudPreset(string Name, IReadOnlyList<CloudLayer> Layers, bool Precipitation)
{
    /// <summary>The layers that are there (coverage above 5%) for the mission's cloud base, lowest first.</summary>
    public IReadOnlyList<CloudLayer> At(double missionBase)
    {
        if (Layers.Count == 0) return [];
        double shift = missionBase - Layers[0].Base;
        return Layers.Where(l => l.Coverage > 0.05).Select(l => l with { Base = l.Base + shift, Top = l.Top + shift }).OrderBy(l => l.Base).ToList();
    }
}

/// <summary>Reads the weather presets of a DCS install. Only the fields are read; nothing is executed.</summary>
public static partial class CloudPresets
{
    public static IReadOnlyDictionary<string, CloudPreset> Read(string path) => Parse(File.ReadAllText(path));

    public static IReadOnlyDictionary<string, CloudPreset> Parse(string text)
    {
        var result = new Dictionary<string, CloudPreset>(StringComparer.OrdinalIgnoreCase);
        var starts = PresetStart().Matches(text);
        for (int i = 0; i < starts.Count; i++)
        {
            string name = starts[i].Groups[1].Value;
            int end = i + 1 < starts.Count ? starts[i + 1].Index : text.Length;
            string block = text[starts[i].Index..end];
            var layers = Layer().Matches(block).Select(m => new CloudLayer(D(m.Groups[1].Value), D(m.Groups[2].Value), D(m.Groups[3].Value))).ToList();
            bool rain = PrecipitationPower().Match(block) is { Success: true } p && D(p.Groups[1].Value) > 0 || name.Contains("rain", StringComparison.OrdinalIgnoreCase);
            result[name] = new CloudPreset(name, layers, rain);
        }
        return result;
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>"Preset7 =" followed by "{" and "visibleInGUI": the start of a preset.</summary>
    [GeneratedRegex(@"(?m)^\s*(\w+)\s*=\s*\r?\n\s*\{\s*\r?\n\s*visibleInGUI")]
    private static partial Regex PresetStart();

    [GeneratedRegex(@"altitudeMin\s*=\s*([-\d.]+)[\s\S]*?altitudeMax\s*=\s*([-\d.]+)[\s\S]*?coverage\s*=\s*([-\d.]+)")]
    private static partial Regex Layer();

    [GeneratedRegex(@"precipitationPower\s*=\s*([-\d.]+)")]
    private static partial Regex PrecipitationPower();
}
