using System.Text.Json;

namespace DcsAtc.Recognition;

/// <summary>A recognizer setup from <c>data/voice/recognition.json</c>: an engine, its model and how it is run.</summary>
public sealed record RecognizerInfo
{
    public required string Id { get; init; }
    public string Label { get; init; } = "";
    /// <summary>"whisper" or "parakeet".</summary>
    public required string Engine { get; init; }
    /// <summary>Model under <c>data/voice/models</c>: a file <c>&lt;name&gt;.bin</c> (Whisper) or a folder (Parakeet).</summary>
    public required string Model { get; init; }
    /// <summary>Whisper: "cpu" or "vulkan".</summary>
    public string Runtime { get; init; } = "cpu";
    /// <summary>Use the vocabulary: as the prompt (Whisper) or as hotwords (Parakeet).</summary>
    public bool Vocabulary { get; init; } = true;
}

/// <summary>
/// A recognition model: downloaded (<see cref="Url"/>: a single <c>.bin</c> file, or a sherpa-onnx <c>.tar.bz2</c>
/// archive with one folder of the same name), or made from a Hugging Face model with
/// <c>tools/Convert-WhisperModel.ps1</c> (<see cref="Convert"/>: its id).
/// </summary>
public sealed record RecognitionModel(string Name, string? Url, string License, int SizeMb, string? Convert = null)
{
    public bool IsArchive => Url?.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>The words a recognizer is told to expect: names it would not spell right on its own, and ATC terms.</summary>
public sealed record SpeechVocabulary
{
    public IReadOnlyList<string> Airfields { get; init; } = [];
    /// <summary>Callsign names ("Hangman", "Enfield"): the pilot says their own one first or last.</summary>
    public IReadOnlyList<string> Callsigns { get; init; } = [];
    public IReadOnlyList<string> Terms { get; init; } = [];

    /// <summary>
    /// A Whisper prompt: one short ATC-style sample (how numbers and taxiways should look) and the names.
    /// Kept short: long prompts make Whisper more likely to repeat them on unclear audio.
    /// </summary>
    public string Prompt()
    {
        string airfield = Airfields.FirstOrDefault() ?? "Tower";
        var callsign = Callsigns.FirstOrDefault() ?? "Viper";
        return $"{airfield} Ground, {callsign} 1-2, request taxi. Runway 31 right via Alpha, Bravo, QNH 1013. " +
               $"{string.Join(", ", Airfields.Skip(1).Concat(Callsigns.Skip(1)).Concat(Terms))}.";
    }

    /// <summary>The phrases to favour (Parakeet hotwords).</summary>
    public IReadOnlyList<string> Hotwords() => Airfields.Concat(Callsigns).Concat(Terms).Distinct().ToList();
}

/// <summary>The recognizers to choose from, where their models live, and how to create them.</summary>
public sealed class RecognizerCatalog
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public const string File = "recognition.json";
    /// <summary>This machine's own choices (default recognizer, threads); not in git.</summary>
    public const string LocalFile = "recognition.local.json";

    public required string Directory { get; init; }
    public required string Default { get; init; }
    public required IReadOnlyList<RecognizerInfo> Recognizers { get; init; }
    public required IReadOnlyList<RecognitionModel> Models { get; init; }
    public required SpeechVocabulary Vocabulary { get; init; }
    /// <summary>Mishearings to correct before a call is read: "one way" → "runway" (<see cref="PilotCallReader"/>).</summary>
    public IReadOnlyDictionary<string, string> Aliases { get; init; } = new Dictionary<string, string>();
    /// <summary>CPU threads per recognizer.</summary>
    public int Threads { get; init; } = 4;

    public string ModelsDirectory => Path.Combine(Directory, "models");

    /// <summary>Loads <c>recognition.json</c> from <paramref name="voiceDirectory"/> (normally <c>data/voice</c>), with <c>recognition.local.json</c> on top.</summary>
    public static RecognizerCatalog Load(string voiceDirectory)
    {
        var file = JsonSerializer.Deserialize<CatalogFile>(System.IO.File.ReadAllText(Path.Combine(voiceDirectory, File)), Json)
            ?? throw new InvalidDataException($"empty {File}");
        string localPath = Path.Combine(voiceDirectory, LocalFile);
        var local = System.IO.File.Exists(localPath) ? JsonSerializer.Deserialize<LocalSettings>(System.IO.File.ReadAllText(localPath), Json) : null;
        return new RecognizerCatalog
        {
            Directory = voiceDirectory,
            Default = local?.Default ?? file.Default,
            Recognizers = file.Recognizers,
            Models = file.Models,
            Vocabulary = file.Vocabulary ?? new SpeechVocabulary(),
            Aliases = file.Aliases ?? [],
            Threads = local?.Threads ?? file.Threads ?? 4,
        };
    }

    public RecognizerInfo? Find(string id) => Recognizers.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public RecognitionModel? ModelFor(RecognizerInfo info) => Models.FirstOrDefault(m => m.Name == info.Model);

    /// <summary>The model's file or folder on disk.</summary>
    public string PathOf(RecognitionModel model) => Path.Combine(ModelsDirectory, model.IsArchive ? model.Name : model.Name + ".bin");

    /// <summary>Whether the recognizer's model is downloaded.</summary>
    public bool IsAvailable(RecognizerInfo info) => ModelFor(info) is { } m && (m.IsArchive ? System.IO.Directory.Exists(PathOf(m)) : System.IO.File.Exists(PathOf(m)));

    public ISpeechRecognizer Create(RecognizerInfo info, int? threads = null, SpeechVocabulary? vocabulary = null)
    {
        var model = ModelFor(info) ?? throw new InvalidDataException($"recognizer {info.Id}: unknown model {info.Model}");
        if (!IsAvailable(info)) throw new FileNotFoundException($"recognizer {info.Id}: model {model.Name} is not downloaded (VoiceTool fetch --recognizer {info.Id})");
        var words = info.Vocabulary ? vocabulary ?? Vocabulary : null;
        int n = threads ?? Threads;
        return info.Engine switch
        {
            "whisper" => new WhisperRecognizer(PathOf(model), info.Runtime.Equals("vulkan", StringComparison.OrdinalIgnoreCase) ? WhisperRuntime.Vulkan : WhisperRuntime.Cpu, n, words?.Prompt()),
            "parakeet" => new ParakeetRecognizer(PathOf(model), n, words?.Hotwords()),
            _ => throw new NotSupportedException($"recognition engine {info.Engine}"),
        };
    }

    private sealed record CatalogFile(string Default, List<RecognizerInfo> Recognizers, List<RecognitionModel> Models, SpeechVocabulary? Vocabulary,
        Dictionary<string, string>? Aliases, int? Threads);

    private sealed record LocalSettings(string? Default, int? Threads);
}
