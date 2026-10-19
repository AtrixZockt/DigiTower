using System.Text.Json;

namespace DcsAtc.Voice;

/// <summary>A voice from <c>data/voice/voices.json</c>.</summary>
public sealed record VoiceInfo
{
    public required string Id { get; init; }
    public string Label { get; init; } = "";
    /// <summary>"kokoro", "piper" or "sapi".</summary>
    public required string Engine { get; init; }
    /// <summary>Model folder under <c>data/voice/models</c> (Kokoro, Piper).</summary>
    public string? Model { get; init; }
    /// <summary>Kokoro speaker id.</summary>
    public int Speaker { get; init; }
    /// <summary>Kokoro language: "en-us" or "en-gb".</summary>
    public string Lang { get; init; } = "en-us";
    /// <summary>SAPI voice name, e.g. "Microsoft David Desktop".</summary>
    public string? Name { get; init; }
    /// <summary>Speaking rate: controllers talk briskly.</summary>
    public float Speed { get; init; } = 1.2f;
}

/// <summary>A downloadable model archive (sherpa-onnx release asset, .tar.bz2 with one top-level folder of the same name).</summary>
public sealed record VoiceModel(string Name, string Url, string License);

/// <summary>The voices the app can use, where their models and clip caches live, and how to create them.</summary>
public sealed class VoiceCatalog
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    public required string Directory { get; init; }
    public required string Default { get; init; }
    public required IReadOnlyList<VoiceInfo> Voices { get; init; }
    public required IReadOnlyList<VoiceModel> Models { get; init; }
    /// <summary>CPU threads per neural TTS engine.</summary>
    public int Threads { get; init; } = 2;

    public string ModelsDirectory => Path.Combine(Directory, "models");
    public string CacheDirectory => Path.Combine(Directory, "cache");

    /// <summary>The file with this machine's own choices (default voice, threads); not in git.</summary>
    public const string LocalFile = "voices.local.json";

    /// <summary>
    /// Loads <c>voices.json</c> from <paramref name="voiceDirectory"/> (normally <c>data/voice</c>), with this machine's
    /// <c>voices.local.json</c> (if there is one) choosing the default voice and the threads.
    /// </summary>
    public static VoiceCatalog Load(string voiceDirectory)
    {
        var file = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(Path.Combine(voiceDirectory, "voices.json")), Json)
            ?? throw new InvalidDataException("empty voices.json");
        string localPath = Path.Combine(voiceDirectory, LocalFile);
        var local = File.Exists(localPath) ? JsonSerializer.Deserialize<LocalFileSettings>(File.ReadAllText(localPath), Json) : null;
        return new VoiceCatalog
        {
            Directory = voiceDirectory,
            Default = local?.Default ?? file.Default,
            Voices = file.Voices,
            Models = file.Models,
            Threads = local?.Threads ?? file.Threads ?? 2,
        };
    }

    /// <summary>The models a voice needs (none for the Windows voices).</summary>
    public IEnumerable<VoiceModel> ModelsFor(VoiceInfo voice) => Models.Where(m => m.Name == voice.Model);

    public VoiceInfo? Find(string id) => Voices.FirstOrDefault(v => v.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the voice can be created without a download.</summary>
    public bool IsAvailable(VoiceInfo voice) => voice.Engine switch
    {
        "sapi" => OperatingSystem.IsWindows(),
        _ => voice.Model is { } m && System.IO.Directory.Exists(Path.Combine(ModelsDirectory, m)),
    };

    public ISpeechEngine CreateEngine(VoiceInfo voice)
    {
        string ModelDir() => Path.Combine(ModelsDirectory, voice.Model ?? throw new InvalidDataException($"voice {voice.Id} has no model"));
        return voice.Engine switch
        {
            "kokoro" => SherpaEngine.Kokoro(ModelDir(), voice.Speaker, voice.Lang, voice.Speed, Threads),
            "piper" => SherpaEngine.Piper(ModelDir(), voice.Speed, Threads),
            "sapi" when OperatingSystem.IsWindows() => new SapiEngine(voice.Name, rate: (int)Math.Round((voice.Speed - 1) * 10)),
            _ => throw new NotSupportedException($"voice engine {voice.Engine} is not available here"),
        };
    }

    /// <summary>The voice with its clip cache (<c>cache/&lt;id&gt;</c>).</summary>
    public AtcVoice Open(VoiceInfo voice, Action<string>? log = null) =>
        new(voice, CreateEngine(voice), Path.Combine(CacheDirectory, voice.Id), log);

    private sealed record CatalogFile(string Default, List<VoiceInfo> Voices, List<VoiceModel> Models, int? Threads);

    private sealed record LocalFileSettings(string? Default, int? Threads);
}
