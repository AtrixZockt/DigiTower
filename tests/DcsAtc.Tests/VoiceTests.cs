using DcsAtc.Voice;

namespace DcsAtc.Tests;

public class VoiceTests
{
    private static string Spoken(string text) => SpokenText.Describe(SpokenText.Units(text));

    [Fact]
    public void Taxi_clearance_is_said_the_ICAO_way()
    {
        Assert.Equal(
            "[Hangman] [one one] | [Al Dafra Ground] | [taxi to holding point runway] [three one right] [via] [Alpha] | [Bravo] ‖ " +
            "[Wind] [three one zero] [degrees] | [one zero] [knots] | [Q N H] [one zero one three] | [altimeter] [two niner niner two]",
            Spoken("Hangman 1-1, Al Dhafra Ground, taxi to holding point runway 31R via A, B. Wind 310 degrees, 10 knots, QNH 1013, altimeter 29.92."));
    }

    [Fact]
    public void Numbers_runways_taxiways_and_frequencies()
    {
        Assert.Equal("[contact Tower on] [two seven zero decimal zero] [when ready for departure]", Spoken("contact Tower on 270.0 when ready for departure."));
        Assert.Equal("[cross runway] [one three left] | [November four] | [X-ray zero]", Spoken("cross runway 13L, N4, X0."));
        Assert.Equal("[runway] [zero four]", Spoken("runway 04."));
        Assert.Equal("[climb and maintain] [three thousand five hundred] [feet] | [then] [flight level one two zero]", Spoken("climb and maintain 3500 feet, then FL120."));
        Assert.Equal("[no tower within] [three zero] [miles]", Spoken("no tower within 30 miles."));
    }

    [Fact]
    public void Words_are_not_mistaken_for_taxiways()
    {
        // "I" is a word, and single letters are taxiways only in a "via" list.
        Assert.Equal("[Viper] [one one] | [go around] | [I say again] | [go around]", Spoken("Viper 1-1, go around, I say again, go around."));
        Assert.Equal("[Hangman] [one] [flight] | [taxi to Parking X-Ray North via] [Whiskey]", Spoken("Hangman 1 flight, taxi to Parking X-Ray North via W."));
    }

    [Fact]
    public void Pauses_follow_the_punctuation()
    {
        var units = SpokenText.Units("Viper 1-1, cleared to land. Good day.");
        Assert.Equal([SpokenText.WordGap, SpokenText.CommaPause, SpokenText.SentencePause, TimeSpan.Zero], units.Select(u => u.PauseAfter));
    }

    [Fact]
    public void Resampling_keeps_pitch_and_length()
    {
        var tone = new AudioClip(Enumerable.Range(0, 24000).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 24000.0))).ToArray(), 24000);
        var resampled = Audio.Resample(tone, 16000);
        Assert.Equal(16000, resampled.Samples.Length);
        // Zero crossings in the steady middle part: 440 Hz → 880 per second.
        var middle = resampled.Samples[1000..15000];
        int crossings = middle.Zip(middle.Skip(1)).Count(p => p.First < 0 != p.Second < 0);
        Assert.InRange(crossings / (14000 / 16000.0), 870, 890);
        Assert.InRange(middle.Max(), 0.48f, 0.52f);
    }

    [Fact]
    public void Silence_is_trimmed_and_the_level_normalized()
    {
        var samples = new float[16000];
        for (int i = 4000; i < 8000; i++) samples[i] = (float)(0.05 * Math.Sin(i * 0.3));
        var clip = Audio.Normalize(Audio.TrimSilence(new AudioClip(samples, 16000)));
        Assert.InRange(clip.Duration.TotalMilliseconds, 250, 300);
        Assert.InRange(clip.Samples.Max(Math.Abs), 0.15f, 0.9f);
    }

    [Fact]
    public void Wav_round_trip()
    {
        var clip = new AudioClip([0f, 0.5f, -0.5f, 0.25f], 16000);
        using var stream = new MemoryStream();
        Wav.Write(stream, clip);
        var back = Wav.Read(stream.ToArray());
        Assert.Equal(16000, back.SampleRate);
        Assert.Equal(clip.Samples, back.Samples.Select(x => MathF.Round(x, 3)));
    }

    [Fact]
    public void Clips_are_made_once_and_then_come_from_the_cache()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dcsatc-voice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = new ToneEngine();
            var info = new VoiceInfo { Id = "test", Engine = "test" };
            using (var voice = new AtcVoice(info, engine, dir))
            {
                Assert.Equal(3, voice.Prebake(["Viper 1-1, cleared to land."]));
                var audio = voice.Render("Viper 1-1, cleared to land.");
                Assert.True(audio.Duration > TimeSpan.FromSeconds(1));
            }
            Assert.Equal(["Viper", "one one", "cleared to land"], engine.Said);

            // A new voice object finds the clips on disk: nothing is synthesized again.
            var again = new ToneEngine();
            using (var voice = new AtcVoice(info, again, dir))
            {
                voice.Render("Viper 1-1, cleared to land.");
                Assert.Equal(0, voice.Prebake(["Viper 1-1, cleared to land."]));
            }
            Assert.Empty(again.Said);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>"Says" every text as a short tone, 22.05 kHz like Piper.</summary>
    private sealed class ToneEngine : ISpeechEngine
    {
        public List<string> Said { get; } = [];

        public AudioClip Synthesize(string text)
        {
            Said.Add(text);
            int n = 22050 * (2 + text.Length) / 20;
            return new AudioClip(Enumerable.Range(0, n).Select(i => (float)(0.3 * Math.Sin(i * 0.1))).ToArray(), 22050);
        }

        public void Dispose()
        {
        }
    }
}
