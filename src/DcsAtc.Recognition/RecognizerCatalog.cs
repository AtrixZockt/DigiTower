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

    /// <summary>Where the models are downloaded to: <c>models</c> beside recognition.json unless the app says otherwise.</summary>
    public string ModelsDirectory { get => _models ?? Path.Combine(Directory, "models"); init => _models = value; }
    private readonly string? _models;

    /// <summary>
    /// This user's own recognizers and models, such as one tuned on their voice (<c>tools/Train-Whisper.ps1</c>):
    /// <c>%APPDATA%\DigiTower\recognition.local.json</c>, beside the settings, so updates of DigiTower keep it.
    /// </summary>
    public static string UserFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DigiTower", LocalFile);

    /// <summary>
    /// Loads <c>recognition.json</c> from <paramref name="voiceDirectory"/> (normally <c>data/voice</c>), with
    /// <c>recognition.local.json</c> beside it and then <paramref name="userFile"/> on top (a default and threads, and
    /// recognizers and models of their own); the models are beside it unless <paramref name="modelsDirectory"/> says otherwise.
    /// </summary>
    public static RecognizerCatalog Load(string voiceDirectory, string? modelsDirectory = null, string? userFile = null)
    {
        var file = JsonSerializer.Deserialize<CatalogFile>(System.IO.File.ReadAllText(Path.Combine(voiceDirectory, File)), Json)
            ?? throw new InvalidDataException($"empty {File}");
        string? defaultId = file.Default;
        int? threads = file.Threads;
        var recognizers = file.Recognizers.ToList();
        var models = file.Models.ToList();
        foreach (var path in new[] { Path.Combine(voiceDirectory, LocalFile), userFile })
        {
            if (path is null || !System.IO.File.Exists(path)) continue;
            var local = JsonSerializer.Deserialize<LocalSettings>(System.IO.File.ReadAllText(path), Json);
            if (local is null) continue;
            defaultId = local.Default ?? defaultId;
            threads = local.Threads ?? threads;
            foreach (var r in local.Recognizers ?? [])
            {
                recognizers.RemoveAll(x => x.Id == r.Id);
                recognizers.Add(r);
            }
            foreach (var m in local.Models ?? [])
            {
                models.RemoveAll(x => x.Name == m.Name);
                models.Add(m);
            }
        }
        return new RecognizerCatalog
        {
            Directory = voiceDirectory,
            Default = defaultId ?? throw new InvalidDataException($"{File}: no default recognizer"),
            Recognizers = recognizers,
            Models = models,
            Vocabulary = file.Vocabulary ?? new SpeechVocabulary(),
            Aliases = file.Aliases ?? [],
            Threads = threads ?? 4,
            ModelsDirectory = modelsDirectory ?? Path.Combine(voiceDirectory, "models"),
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

    private sealed record LocalSettings(string? Default, int? Threads, List<RecognizerInfo>? Recognizers, List<RecognitionModel>? Models);
}
