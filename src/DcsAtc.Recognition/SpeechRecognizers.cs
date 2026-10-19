using System.Text;
using SherpaOnnx;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace DcsAtc.Recognition;

/// <summary>
/// Speech to text for one finished transmission (16 kHz mono, samples in [-1, 1]). It only turns speech into text;
/// what the pilot asked for is decided by plain code afterwards.
/// </summary>
public interface ISpeechRecognizer : IDisposable
{
    /// <summary>Where it runs, as loaded ("Cpu", "Vulkan").</summary>
    string Runtime { get; }

    string Recognize(float[] samples);
}

public enum WhisperRuntime { Cpu, Vulkan }

/// <summary>
/// OpenAI Whisper through whisper.cpp (Whisper.net, GGML models). English only, beam search, no carry-over between
/// transmissions; an optional prompt carries the vocabulary (airfield, callsigns, ATC terms) to steer the spelling.
/// </summary>
public sealed class WhisperRecognizer : ISpeechRecognizer
{
    private const int SampleRate = 16000;
    // whisper.cpp ignores input shorter than a second; short calls ("wilco") are padded with silence.
    private static readonly int MinSamples = (int)(1.2 * SampleRate);

    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly Lock _lock = new();

    public WhisperRecognizer(string modelPath, WhisperRuntime runtime, int threads, string? prompt)
    {
        UseRuntime(runtime);
        _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = runtime == WhisperRuntime.Vulkan });
        var builder = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithThreads(threads)
            .WithNoContext()
            .WithSingleSegment()
            .WithBeamSearchSamplingStrategy(b => b.WithBeamSize(5));
        if (!string.IsNullOrWhiteSpace(prompt)) builder = builder.WithPrompt(prompt);
        _processor = builder.Build();
    }

    public string Runtime => RuntimeOptions.LoadedLibrary?.ToString() ?? "?";

    public string Recognize(float[] samples)
    {
        if (samples.Length < MinSamples) samples = [.. samples, .. new float[MinSamples - samples.Length]];
        lock (_lock)
        {
            var text = new StringBuilder();
            foreach (var segment in _processor.ProcessAsync(samples).ToBlockingEnumerable()) text.Append(segment.Text);
            return text.ToString().Trim();
        }
    }

    public void Dispose()
    {
        _processor.Dispose();
        _factory.Dispose();
    }

    // One native runtime per process, chosen before the first model loads; never a silent fall-back to another one.
    private static void UseRuntime(WhisperRuntime runtime)
    {
        var wanted = runtime == WhisperRuntime.Vulkan ? RuntimeLibrary.Vulkan : RuntimeLibrary.Cpu;
        if (RuntimeOptions.LoadedLibrary is { } loaded)
        {
            if (loaded != wanted) throw new InvalidOperationException($"Whisper already runs on {loaded} in this process (one runtime per process)");
            return;
        }
        RuntimeOptions.RuntimeLibraryOrder = [wanted];
    }
}

/// <summary>
/// NVIDIA Parakeet TDT (a NeMo transducer) through sherpa-onnx, int8 on the CPU. Its work grows with the length of the
/// call (Whisper always encodes 30 s). With hotwords it decodes with beam search and favours those words.
/// </summary>
public sealed class ParakeetRecognizer : ISpeechRecognizer
{
    private const int SampleRate = 16000;

    private readonly OfflineRecognizer _recognizer;
    private readonly Lock _lock = new();
    // The hotword files for sherpa-onnx (deleted on dispose).
    private readonly string? _files;

    public ParakeetRecognizer(string modelDir, int threads, IReadOnlyList<string>? hotwords = null, float hotwordScore = 1.5f)
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = FirstExisting(modelDir, "encoder.int8.onnx", "encoder.onnx");
        config.ModelConfig.Transducer.Decoder = FirstExisting(modelDir, "decoder.int8.onnx", "decoder.onnx");
        config.ModelConfig.Transducer.Joiner = FirstExisting(modelDir, "joiner.int8.onnx", "joiner.onnx");
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = threads;
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        if (hotwords is { Count: > 0 })
        {
            // sherpa-onnx reads hotwords from a file and spells them in the model's pieces with its own SentencePiece
            // encoder, which needs a vocabulary with scores. Every piece costs the same (ties go to the more common,
            // lower id), so a word is spelled in the fewest pieces, as this BPE model writes it.
            _files = Path.Combine(Path.GetTempPath(), $"dcsatc-hotwords-{Environment.ProcessId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_files);
            string vocab = Path.Combine(_files, "bpe.vocab");
            File.WriteAllLines(vocab, File.ReadLines(config.ModelConfig.Tokens).Select(l => l.Split(' ')).Where(f => f.Length == 2)
                .Select(f => FormattableString.Invariant($"{f[0]} {-1 - int.Parse(f[1]) * 1e-6:0.000000}")));
            string file = Path.Combine(_files, "hotwords.txt");
            File.WriteAllLines(file, hotwords.Distinct());
            config.ModelConfig.ModelingUnit = "bpe";
            config.ModelConfig.BpeVocab = vocab;
            config.DecodingMethod = "modified_beam_search";
            config.MaxActivePaths = 4;
            config.HotwordsFile = file;
            config.HotwordsScore = hotwordScore;
        }
        _recognizer = new OfflineRecognizer(config);
    }

    public string Runtime => "Cpu";

    public string Recognize(float[] samples)
    {
        lock (_lock)
        {
            using var stream = _recognizer.CreateStream();
            stream.AcceptWaveform(SampleRate, samples);
            _recognizer.Decode(stream);
            return stream.Result.Text.Trim();
        }
    }

    public void Dispose()
    {
        _recognizer.Dispose();
        if (_files is not null)
        {
            try
            {
                Directory.Delete(_files, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string FirstExisting(string dir, params string[] names) =>
        names.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists) ?? throw new FileNotFoundException($"None of {string.Join(", ", names)} in {dir}");
}
