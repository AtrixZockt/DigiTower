using System.Text.Json;
using System.Text.RegularExpressions;

namespace DcsAtc.Recognition;

/// <summary>
/// A call from the test script: what the pilot says and the phrases ATC must understand from it (the request,
/// runway, numbers, taxiways), written in [brackets] in the script.
/// </summary>
public sealed partial record TestCall(int Number, string Text, IReadOnlyList<string> Keys)
{
    /// <summary>One call per line; [brackets] mark the key phrases; lines starting with # and blank lines are skipped.</summary>
    public static IReadOnlyList<TestCall> Load(string path) => Parse(File.ReadLines(path));

    public static IReadOnlyList<TestCall> Parse(IEnumerable<string> lines)
    {
        var calls = new List<TestCall>();
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var keys = Key().Matches(line).Select(m => m.Groups[1].Value.Trim()).ToList();
            calls.Add(new TestCall(calls.Count + 1, Key().Replace(line, "$1"), keys));
        }
        return calls;
    }

    [GeneratedRegex(@"\[([^\]]+)\]")]
    private static partial Regex Key();
}

/// <summary>A recorded test call: its WAV file (next to the manifest) and how it was heard.</summary>
public sealed record RecordedCall
{
    public required int Number { get; init; }
    public required string Text { get; init; }
    public IReadOnlyList<string> Keys { get; init; } = [];
    /// <summary>The WAV file, relative to the corpus folder.</summary>
    public required string File { get; init; }
    /// <summary>Who said it: the SRS client name, or "tts:&lt;voice&gt;" for a synthetic call.</summary>
    public string? Speaker { get; init; }
    public double FrequencyMhz { get; init; }
    public double Seconds { get; init; }
    public int LostPackets { get; init; }
    /// <summary>Loudness of the speech (RMS, dBFS) and the highest peak (dBFS): too quiet or clipped shows here.</summary>
    public double LevelDb { get; init; }
    public double PeakDb { get; init; }
    public DateTimeOffset Recorded { get; init; }
}

/// <summary>A folder of recorded test calls with a manifest (<c>calls.json</c>).</summary>
public sealed class CallCorpus
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public const string ManifestFile = "calls.json";

    public required string Directory { get; init; }
    public List<RecordedCall> Calls { get; init; } = [];

    /// <summary>The corpus in <paramref name="directory"/> (empty if it has no manifest yet).</summary>
    public static CallCorpus Load(string directory)
    {
        string manifest = Path.Combine(directory, ManifestFile);
        var calls = System.IO.File.Exists(manifest) ? JsonSerializer.Deserialize<List<RecordedCall>>(System.IO.File.ReadAllText(manifest), Json) ?? [] : [];
        return new CallCorpus { Directory = directory, Calls = calls.OrderBy(c => c.Number).ToList() };
    }

    public string PathOf(RecordedCall call) => Path.Combine(Directory, call.File);

    /// <summary>Adds the call, replacing an earlier recording of the same number.</summary>
    public void Put(RecordedCall call)
    {
        Calls.RemoveAll(c => c.Number == call.Number);
        Calls.Add(call);
        Calls.Sort((a, b) => a.Number.CompareTo(b.Number));
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        string manifest = Path.Combine(Directory, ManifestFile);
        System.IO.File.WriteAllText(manifest + ".tmp", JsonSerializer.Serialize(Calls, Json));
        System.IO.File.Move(manifest + ".tmp", manifest, overwrite: true);
    }
}
