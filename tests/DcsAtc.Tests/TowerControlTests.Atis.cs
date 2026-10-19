using DcsAtc.Airfields;
using DcsAtc.Atc;
using DcsAtc.Bridge;
using DcsAtc.Voice;

namespace DcsAtc.Tests;

/// <summary>ATIS: wording from the DCS weather, letters, the broadcast.</summary>
public partial class TowerControlTests
{
    private static IReadOnlyDictionary<string, CloudPreset>? DcsPresets()
    {
        if (TestPaths.Terrains is null) return null;
        string path = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(TestPaths.Terrains))!, "Config", "Effects", "clouds.lua");
        return File.Exists(path) ? CloudPresets.Read(path) : null;
    }

    [SkippableFact]
    public void Cloud_presets_are_read_from_the_DCS_install_and_move_with_the_mission_base()
    {
        var presets = DcsPresets();
        Skip.If(presets is null, "DCS install not available");

        Assert.True(presets!.Count >= 40, $"{presets.Count} presets");
        // Preset 7, "BKN 7.5/12 SCT/BKN 21/23": broken from 2520 m, scattered from 6720 m at its default base.
        var preset7 = presets["Preset7"];
        Assert.Equal([("broken", 2520.0), ("scattered", 6720.0)], preset7.At(2520).Select(l => (l.Amount, l.Base)));
        // A mission base of 1680 m moves the whole stack down by 840 m.
        Assert.Equal([1680.0, 5880.0], preset7.At(1680).Select(l => l.Base));
        Assert.True(presets["RainyPreset1"].Precipitation);
        Assert.False(preset7.Precipitation);
    }

    [SkippableFact]
    public void The_sky_is_reported_the_ICAO_way()
    {
        var presets = DcsPresets() ?? new Dictionary<string, CloudPreset>();
        const double field = 16;
        string Sky(MissionWeather m, int temperature = 30) => AtisService.Sky(m, field, temperature, presets);

        Assert.Equal("CAVOK", Sky(new MissionWeather { Visibility = 80000 }));
        // Legacy clouds: density 8 of 10 is broken; heights above the field, rounded to 100 ft.
        Assert.Equal("Visibility 10 kilometers or more. Clouds broken 2900 feet", Sky(new MissionWeather { Visibility = 80000, CloudBase = 900, CloudDensity = 8 }));
        Assert.Equal("Visibility 600 meters, fog. No significant clouds", Sky(new MissionWeather { Visibility = 80000, FogVisibility = 600, FogThickness = 300 }));
        Assert.Equal("Visibility 3000 meters, mist. No significant clouds", Sky(new MissionWeather { Visibility = 80000, FogVisibility = 3000, FogThickness = 300 }));
        // Fog that lies below the field does not count.
        Assert.Equal("CAVOK", Sky(new MissionWeather { Visibility = 80000, FogVisibility = 600, FogThickness = 30 }));
        Assert.Equal("Visibility 6 kilometers, dust. No significant clouds", Sky(new MissionWeather { Visibility = 80000, Dust = 6500 }));
        Assert.Equal("Visibility 10 kilometers or more, snow. Clouds overcast 1600 feet",
            Sky(new MissionWeather { Visibility = 80000, CloudBase = 500, CloudDensity = 10, Precipitation = 3 }, temperature: -2));
        if (presets.ContainsKey("RainyPreset1"))
            Assert.StartsWith("Visibility 10 kilometers or more, rain. Clouds ", Sky(new MissionWeather { Visibility = 80000, CloudPreset = "RainyPreset1", CloudBase = 1000 }));
    }

    [Fact]
    public void Zulu_time_comes_from_the_mission_clock_and_the_map()
    {
        // 08:50 local at the Gulf (UTC+4) is 04:50 Zulu; the day wraps.
        Assert.Equal(new TimeSpan(4, 50, 0), AtisService.Zulu(8 * 3600 + 50 * 60 + 30, "PersianGulf"));
        Assert.Equal(new TimeSpan(22, 0, 0), AtisService.Zulu(2 * 3600, "PersianGulf"));
        Assert.Equal(new TimeSpan(16, 0, 0), AtisService.Zulu(8 * 3600, "Nevada"));
        Assert.Equal(new TimeSpan(10, 30, 0), AtisService.Zulu(15 * 3600, "Afghanistan"));
    }

    [SkippableFact]
    public async Task Atis_broadcasts_on_its_frequency_and_takes_a_new_letter_with_significant_changes()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        w!.Dcs.MissionWeather = new MissionWeather { Visibility = 80000, CloudPreset = "Preset1", CloudBase = 2520, AbsTime = 8 * 3600 + 50 * 60 };
        var atis = new AtisService(w.Dcs, w.Catalog, w.Radio);

        await atis.UpdateAsync();
        var (mhz, coalition, text) = w.Radio.Broadcasts["Al Dhafra ATIS"];
        Assert.Equal((270.5, 2), (mhz, coalition));
        Assert.Equal("Al Dhafra information Alpha, time 0450 Zulu. Runway in use 31L and 31R. Expect ILS approach. Wind 310 degrees, 10 knots. " +
                     "CAVOK. Temperature 30. QNH 1013, altimeter 29.92. Advise on initial contact you have information Alpha.", text);

        // Same weather a minute later: same letter, and the time stays the time of the letter.
        w.Dcs.MissionWeather = w.Dcs.MissionWeather with { AbsTime = 8 * 3600 + 51 * 60 };
        await atis.UpdateAsync();
        Assert.Equal(text, w.Radio.Broadcasts["Al Dhafra ATIS"].Text);

        // The wind backs 40°: Bravo. Fog comes in: Charlie. A new hour: Delta.
        w.Dcs.Weather = w.Weather with { WindX = -5.14 * Math.Cos(270 * Math.PI / 180), WindZ = -5.14 * Math.Sin(270 * Math.PI / 180) };
        await atis.UpdateAsync();
        Assert.StartsWith("Al Dhafra information Bravo, time 0451 Zulu.", w.Radio.Broadcasts["Al Dhafra ATIS"].Text);
        Assert.Contains("Wind 270 degrees, 10 knots.", w.Radio.Broadcasts["Al Dhafra ATIS"].Text);
        w.Dcs.MissionWeather = w.Dcs.MissionWeather with { FogVisibility = 800, FogThickness = 200 };
        await atis.UpdateAsync();
        Assert.Contains("information Charlie", w.Radio.Broadcasts["Al Dhafra ATIS"].Text);
        // Preset 1 at its default base: scattered at about 8200 feet above the field, above that a second scattered layer.
        Assert.Contains("Visibility 800 meters, fog. Clouds scattered 8200 feet, scattered 16500 feet.", w.Radio.Broadcasts["Al Dhafra ATIS"].Text);
        w.Dcs.MissionWeather = w.Dcs.MissionWeather with { AbsTime = 9 * 3600 + 2 * 60 };
        await atis.UpdateAsync();
        Assert.StartsWith("Al Dhafra information Delta, time 0502 Zulu.", w.Radio.Broadcasts["Al Dhafra ATIS"].Text);

        // The menu reads the same text; the mission ending stops the broadcast.
        Assert.Equal(w.Radio.Broadcasts["Al Dhafra ATIS"].Text, await atis.TextAsync((await w.Catalog.GetAsync(w.Catalog.Airbases[0]))!));
        atis.Reset();
        Assert.False(w.Radio.Broadcasts.ContainsKey("Al Dhafra ATIS"));
    }

    [SkippableFact]
    public async Task With_an_older_bridge_the_atis_leaves_out_time_and_sky()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        w!.Dcs.OldBridge = true;
        var log = new List<string>();
        var atis = new AtisService(w.Dcs, w.Catalog, w.Radio, log.Add);
        await atis.UpdateAsync();
        Assert.Equal("Al Dhafra information Alpha. Runway in use 31L and 31R. Expect ILS approach. Wind 310 degrees, 10 knots. " +
                     "Temperature 30. QNH 1013, altimeter 29.92. Advise on initial contact you have information Alpha.", w.Radio.Broadcasts["Al Dhafra ATIS"].Text);
        Assert.Contains(log, l => l.Contains("install.ps1"));
    }

    [SkippableFact]
    public async Task Every_atis_clip_is_prepared_in_advance()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var presets = DcsPresets() ?? new Dictionary<string, CloudPreset>();
        var prepared = Phraseology.AtisSamples("Al Dhafra", w!.Layout.Runways, w.Weather).SelectMany(SpokenText.Units).Select(u => u.Text).ToHashSet();
        var field = (await w.Catalog.GetAsync(w.Catalog.Airbases[0]))!;

        // A spread of weathers, letters and times.
        var weathers = new[]
        {
            new MissionWeather { Visibility = 80000 },
            new MissionWeather { Visibility = 80000, CloudBase = 900, CloudDensity = 8 },
            new MissionWeather { Visibility = 80000, FogVisibility = 600, FogThickness = 300 },
            new MissionWeather { Visibility = 80000, Dust = 3500 },
            new MissionWeather { Visibility = 80000, CloudPreset = "Preset7", CloudBase = 1800 },
            new MissionWeather { Visibility = 80000, CloudPreset = "RainyPreset2", CloudBase = 900 },
        };
        var missing = new HashSet<string>();
        int i = 0;
        foreach (var mission in weathers)
        {
            var content = AtisService.Content(field, w.Weather, mission, presets);
            string text = Phraseology.Atis(content, Phraseology.LetterName(i * 5), new TimeSpan(i * 4, i * 11, 0));
            missing.UnionWith(SpokenText.Units(text).Select(u => u.Text).Where(u => !prepared.Contains(u)));
            i++;
        }
        Assert.True(missing.Count == 0, string.Join(" | ", missing));
    }
}
