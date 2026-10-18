using System.Runtime.Versioning;
using SherpaOnnx;

namespace DcsAtc.Voice;

/// <summary>A text-to-speech engine. Used once per new clip, never for whole transmissions.</summary>
public interface ISpeechEngine : IDisposable
{
    AudioClip Synthesize(string text);
}

/// <summary>Local neural TTS through sherpa-onnx: Kokoro (many voices in one model) or Piper (one voice per model). CPU only.</summary>
public sealed class SherpaEngine : ISpeechEngine
{
    private readonly OfflineTts _tts;
    private readonly int _speaker;
    private readonly float _speed;
    private readonly Lock _lock = new();

    private SherpaEngine(OfflineTtsConfig config, int speaker, float speed)
    {
        _tts = new OfflineTts(config);
        _speaker = speaker;
        _speed = speed;
    }

    public static SherpaEngine Kokoro(string modelDir, int speaker, string lang = "en-us", float speed = 1.0f, int threads = 2)
    {
        var config = new OfflineTtsConfig();
        config.Model.Kokoro.Model = FirstExisting(modelDir, "model.int8.onnx", "model.onnx");
        config.Model.Kokoro.Voices = Path.Combine(modelDir, "voices.bin");
        config.Model.Kokoro.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.Model.Kokoro.DataDir = Path.Combine(modelDir, "espeak-ng-data");
        string lexicon = Path.Combine(modelDir, lang.Equals("en-gb", StringComparison.OrdinalIgnoreCase) ? "lexicon-gb-en.txt" : "lexicon-us-en.txt");
        if (File.Exists(lexicon)) config.Model.Kokoro.Lexicon = lexicon;
        if (Directory.Exists(Path.Combine(modelDir, "dict"))) config.Model.Kokoro.DictDir = Path.Combine(modelDir, "dict");
        // espeak-ng names British English "en" and American "en-us".
        config.Model.Kokoro.Lang = lang.Equals("en-gb", StringComparison.OrdinalIgnoreCase) ? "en" : lang;
        config.Model.NumThreads = threads;
        config.Model.Provider = "cpu";
        config.MaxNumSentences = 1;
        return new SherpaEngine(config, speaker, speed);
    }

    public static SherpaEngine Piper(string modelDir, float speed = 1.0f, int threads = 2)
    {
        var config = new OfflineTtsConfig();
        config.Model.Vits.Model = Directory.GetFiles(modelDir, "*.onnx").FirstOrDefault()
            ?? throw new FileNotFoundException($"No .onnx model in {modelDir}");
        config.Model.Vits.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.Model.Vits.DataDir = Path.Combine(modelDir, "espeak-ng-data");
        config.Model.NumThreads = threads;
        config.Model.Provider = "cpu";
        config.MaxNumSentences = 1;
        return new SherpaEngine(config, 0, speed);
    }

    public AudioClip Synthesize(string text)
    {
        lock (_lock)
        {
            var audio = _tts.Generate(text, _speed, _speaker);
            // sherpa-onnx returns no audio (a null handle) when it cannot phonemize the text.
            if (audio.Handle == IntPtr.Zero) throw new InvalidOperationException($"the TTS could not say \"{text}\"");
            try
            {
                if (audio.NumSamples <= 0) throw new InvalidOperationException($"the TTS could not say \"{text}\"");
                return new AudioClip(audio.Samples, audio.SampleRate);
            }
            finally
            {
                audio.Dispose();
            }
        }
    }

    public void Dispose() => _tts.Dispose();

    private static string FirstExisting(string dir, params string[] names) =>
        names.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists) ?? throw new FileNotFoundException($"None of {string.Join(", ", names)} in {dir}");
}

/// <summary>The classic Windows (SAPI) voices: no download, robotic.</summary>
[SupportedOSPlatform("windows")]
public sealed class SapiEngine : ISpeechEngine
{
    private readonly System.Speech.Synthesis.SpeechSynthesizer _synth = new();
    private readonly Lock _lock = new();

    public SapiEngine(string? voiceName, int rate = 1)
    {
        if (!string.IsNullOrEmpty(voiceName)) _synth.SelectVoice(voiceName);
        _synth.Rate = rate;
    }

    public AudioClip Synthesize(string text)
    {
        lock (_lock)
        {
            using var stream = new MemoryStream();
            _synth.SetOutputToAudioStream(stream, new System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
            _synth.Speak(text);
            _synth.SetOutputToNull();
            var bytes = stream.ToArray();
            var pcm = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, pcm, 0, pcm.Length * 2);
            return new AudioClip(Audio.FromPcm16(pcm), 16000);
        }
    }

    public void Dispose() => _synth.Dispose();
}
