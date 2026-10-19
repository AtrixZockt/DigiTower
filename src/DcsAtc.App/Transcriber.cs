using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using DcsAtc.Recognition;
using DcsAtc.Srs;
using DcsAtc.Voice;

namespace DcsAtc.App;

/// <summary>
/// Voice requests, first step (spike S5, <c>--transcribe</c>): what pilots say to a station is turned into text and
/// logged, and the recording is kept, to judge recognition in real flights. ATC does not act on it yet.
/// </summary>
public sealed class Transcriber : IDisposable
{
    private static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(0.4);
    // Less than this much audible audio is a key click, not speech (Whisper makes up words for silence).
    private static readonly TimeSpan MinSpeech = TimeSpan.FromSeconds(0.2);

    private readonly Func<ISpeechRecognizer> _create;
    private readonly string _recognizerId;
    private readonly string _directory;
    private readonly Action<string> _log;
    private readonly BlockingCollection<(string Station, SrsTransmission Transmission)> _queue = new(boundedCapacity: 20);
    private readonly Thread _thread;
    // A transmission reaches every station on its frequency (one per coalition): transcribed once.
    private readonly Dictionary<string, DateTimeOffset> _recent = [];
    private int _count;

    /// <param name="create">Loads the recognizer (on the transcriber's own thread: a model can take seconds to load).</param>
    /// <param name="directory">Where recordings and <c>heard.jsonl</c> go, one folder per day.</param>
    public Transcriber(Func<ISpeechRecognizer> create, string recognizerId, string directory, Action<string> log)
    {
        _create = create;
        _recognizerId = recognizerId;
        _directory = directory;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "ATC transcriber" };
        _thread.Start();
    }

    /// <summary>A transmission turned into text: the station that heard it, the transmission, the text.</summary>
    public event Action<string, SrsTransmission, string>? Recognized;

    public void OnHeard(string station, SrsTransmission t)
    {
        if (t.Duration < MinDuration || _queue.IsAddingCompleted) return;
        lock (_recent)
        {
            string key = $"{t.SenderGuid}@{t.Frequency.Hz}";
            if (_recent.TryGetValue(key, out var at) && (t.Started - at).Duration() < TimeSpan.FromSeconds(2)) return;
            _recent[key] = t.Started;
        }
        try
        {
            if (!_queue.TryAdd((station, t))) _log("transcribe: busy, a transmission was skipped");
        }
        catch (InvalidOperationException)
        {
            // Stopping.
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(30));
    }

    private void Run()
    {
        ISpeechRecognizer recognizer;
        try
        {
            var clock = Stopwatch.StartNew();
            recognizer = _create();
            var loaded = clock.Elapsed;
            // The first recognition sets the engine up (on the GPU it compiles shaders: seconds); done now, not on a pilot's call.
            recognizer.Recognize(new float[SrsProtocol.SampleRate]);
            _log($"transcribe: {_recognizerId} ready on {recognizer.Runtime} in {loaded.TotalSeconds:F1} s (+{(clock.Elapsed - loaded).TotalSeconds:F1} s warm-up); recordings go to {Path.GetFullPath(_directory)}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or DllNotFoundException or InvalidDataException)
        {
            _log($"transcribe: {_recognizerId} could not be loaded: {ex.Message}");
            return;
        }
        using (recognizer)
        {
            foreach (var (station, t) in _queue.GetConsumingEnumerable())
            {
                var samples = Audio.FromPcm16(t.Pcm);
                string who = t.Sender?.Name ?? t.SenderGuid;
                if (AudibleSeconds(samples) < MinSpeech.TotalSeconds)
                {
                    _log($"heard {who} on {station} {t.Frequency} ({t.Duration.TotalSeconds:F1} s): no speech");
                    continue;
                }
                var clock = Stopwatch.StartNew();
                string text;
                try
                {
                    text = recognizer.Recognize(samples);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    _log($"transcribe: {ex.Message}");
                    continue;
                }
                var elapsed = clock.Elapsed;
                _log($"heard {who} on {station} {t.Frequency} ({t.Duration.TotalSeconds:F1} s): \"{text}\" [{_recognizerId}, {elapsed.TotalSeconds:F2} s]");
                Save(station, t, who, samples, text, elapsed);
                Recognized?.Invoke(station, t, text);
            }
        }
    }

    private void Save(string station, SrsTransmission t, string who, float[] samples, string text, TimeSpan elapsed)
    {
        try
        {
            string dir = Path.Combine(_directory, t.Started.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dir);
            string file = $"{t.Started:HHmmss}-{string.Concat(station.Where(char.IsLetterOrDigit))}-{Interlocked.Increment(ref _count)}.wav";
            Wav.Write(Path.Combine(dir, file), new AudioClip(samples, SrsProtocol.SampleRate));
            var entry = new
            {
                time = t.Started, station, frequencyMhz = t.Frequency.Hz / 1e6, sender = who, unitId = t.Sender?.UnitId, seconds = t.Duration.TotalSeconds,
                lostPackets = t.LostPackets, text, recognizer = _recognizerId, recognitionSeconds = Math.Round(elapsed.TotalSeconds, 3), file,
            };
            File.AppendAllText(Path.Combine(dir, "heard.jsonl"), JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch (IOException ex)
        {
            _log($"transcribe: recording not saved ({ex.Message})");
        }
    }

    // Seconds of audio above -40 dBFS, judged in 10 ms windows.
    private static double AudibleSeconds(float[] samples)
    {
        const int window = SrsProtocol.SampleRate / 100;
        int loud = 0;
        for (int start = 0; start + window <= samples.Length; start += window)
        {
            double sum = 0;
            for (int i = start; i < start + window; i++) sum += samples[i] * samples[i];
            if (Math.Sqrt(sum / window) > 0.01) loud++;
        }
        return loud / 100.0;
    }
}
