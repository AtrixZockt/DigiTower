using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DcsAtc.Voice;

/// <summary>
/// One controller voice: renders a transmission by stitching cached clips (<see cref="SpokenText"/>).
/// A clip that is not cached yet is synthesized once, cleaned up (resampled to 16 kHz, silence trimmed,
/// level normalized) and stored as a WAV file under the voice's cache folder.
/// </summary>
public sealed class AtcVoice(VoiceInfo info, ISpeechEngine engine, string cacheDirectory, Action<string>? log = null) : IDisposable
{
    public const int SampleRate = 16000;

    /// <summary>Part of every cache key (with the voice's speed): bump it when clip processing changes, so old clips are regenerated.</summary>
    private const string ProcessingVersion = "v2";

    private static readonly TimeSpan LeadIn = TimeSpan.FromMilliseconds(120);

    private readonly ConcurrentDictionary<string, AudioClip> _clips = new(StringComparer.Ordinal);
    private int _rendering;

    public VoiceInfo Info => info;
    public string CacheDirectory => cacheDirectory;

    /// <summary>The whole transmission for <paramref name="text"/>, 16 kHz mono.</summary>
    public AudioClip Render(string text) => Render(SpokenText.Units(text));

    /// <summary>A transmission being rendered has priority: <see cref="Prepare"/> waits for it.</summary>
    public AudioClip Render(IReadOnlyList<SpeechUnit> units)
    {
        Interlocked.Increment(ref _rendering);
        try
        {
            var parts = new List<(AudioClip, TimeSpan)> { (AudioClip.Silence(LeadIn, SampleRate), TimeSpan.Zero) };
            parts.AddRange(units.Select(u => (Clip(u.Text), u.PauseAfter)));
            return Audio.Concat(parts, SampleRate);
        }
        finally
        {
            Interlocked.Decrement(ref _rendering);
        }
    }

    /// <summary>Makes sure every clip of these texts is cached. Returns how many clips were synthesized.</summary>
    public int Prebake(IEnumerable<string> texts) =>
        texts.SelectMany(SpokenText.Units).Select(u => u.Text).Distinct().Count(Prepare);

    /// <summary>Makes one clip ahead of time, after any transmission being rendered. Returns whether it had to be synthesized.</summary>
    public bool Prepare(string unit)
    {
        if (IsCached(unit)) return false;
        while (Volatile.Read(ref _rendering) > 0) Thread.Sleep(20);
        bool made = false;
        _clips.GetOrAdd(unit, u =>
        {
            made = true;
            return Load(u, quiet: true);
        });
        return made;
    }

    public bool IsCached(string unit) => _clips.ContainsKey(unit) || File.Exists(PathOf(unit));

    /// <summary>One clip, from memory, the cache folder, or the TTS (then stored).</summary>
    public AudioClip Clip(string unit) => _clips.GetOrAdd(unit, u => Load(u, quiet: false));

    /// <param name="quiet">Prepared ahead of time: the caller reports, not every clip.</param>
    private AudioClip Load(string unit, bool quiet)
    {
        string path = PathOf(unit);
        if (File.Exists(path))
        {
            try
            {
                return Wav.Read(path);
            }
            catch (InvalidDataException)
            {
                // A damaged cache file is made again.
            }
        }
        var started = DateTime.UtcNow;
        AudioClip clip;
        try
        {
            clip = engine.Synthesize(unit);
        }
        catch (InvalidOperationException ex)
        {
            // Odd player names: try once more with plain letters, else leave the word out (not cached, so a better engine can try again).
            string plain = new(unit.Where(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-').ToArray());
            log?.Invoke($"voice {info.Id}: {ex.Message}");
            if (plain.Trim().Length == 0 || plain == unit) return new AudioClip([], SampleRate);
            try
            {
                clip = engine.Synthesize(plain);
            }
            catch (InvalidOperationException)
            {
                return new AudioClip([], SampleRate);
            }
        }
        clip = Audio.Normalize(Audio.TrimSilence(Audio.Resample(clip, SampleRate)));
        Directory.CreateDirectory(cacheDirectory);
        string temp = path + ".tmp";
        Wav.Write(temp, clip);
        File.Move(temp, path, overwrite: true);
        if (!quiet) log?.Invoke($"voice {info.Id}: new clip \"{unit}\" ({clip.Duration.TotalSeconds:F2} s, made in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms)");
        return clip;
    }

    private string PathOf(string unit)
    {
        var slug = new StringBuilder();
        foreach (char c in unit.ToLowerInvariant())
        {
            if (slug.Length >= 48) break;
            slug.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        }
        string key = $"{ProcessingVersion}|{info.Speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{unit}";
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..10];
        return Path.Combine(cacheDirectory, $"{slug.ToString().Trim('-')}-{hash}.wav");
    }

    public void Dispose() => engine.Dispose();
}
