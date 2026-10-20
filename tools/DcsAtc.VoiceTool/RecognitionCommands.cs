using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using DcsAtc.Recognition;
using DcsAtc.Srs;
using DcsAtc.Voice;

/// <summary>Spike S5: record test calls, run recognizers over them, compare.</summary>
static class RecognitionCommands
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private static readonly string DefaultCorpus = Path.Combine("out", "s5", "calls");

    // ---- record -------------------------------------------------------------------------------------------------

    public static async Task<int> RecordAsync(Options o, CancellationToken ct)
    {
        var script = TestCall.Load(o.Get("calls", Path.Combine(o.DataDirectory(), "voice", "test-calls.txt")));
        var corpus = CallCorpus.Load(o.Get("out", DefaultCorpus));
        if (o.Find("synthetic") is not null) return Synthetic(o, script, corpus);

        var frequencies = o.Get("freq", "250.0,270.0,257.0").Split(',').Select(f => SrsFrequency.Mhz(double.Parse(f, CultureInfo.InvariantCulture))).ToList();
        string? from = o.Find("from");
        var client = new SrsClient(new SrsOptions
        {
            Host = o.Get("host", "127.0.0.1"),
            Port = int.Parse(o.Get("port", "5002")),
            Name = "DigiTower recorder",
            Coalition = int.Parse(o.Get("coalition", "2")),
            Receive = frequencies,
        });
        using var receiver = new SrsReceiver(client);
        var heard = Channel.CreateUnbounded<SrsTransmission>();
        receiver.TransmissionReceived += t => heard.Writer.TryWrite(t);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = client.RunAsync(session.Token);
        try
        {
            if (!await client.WaitReadyAsync(TimeSpan.FromSeconds(10), ct))
            {
                Console.Error.WriteLine("SRS server not reachable (is it running, and is the port right?)");
                return 2;
            }
            int index = o.Find("start") is { } start ? int.Parse(start) - 1 : script.TakeWhile(c => corpus.Calls.Any(r => r.Number == c.Number)).Count();
            Console.WriteLine($"""
                Recording {script.Count} test calls into {Path.GetFullPath(corpus.Directory)} ({corpus.Calls.Count} already there).
                Tune a radio to {string.Join(" / ", frequencies)} and say each call with your normal push-to-talk.
                Keys: B = back (record the previous call again), S = skip, Q = quit (rerun to continue).

                """);
            while (index < script.Count)
            {
                var call = script[index];
                Console.WriteLine($"[{call.Number}/{script.Count}]  {call.Text}");
                var (transmission, key) = await NextAsync(heard.Reader, ct);
                switch (key)
                {
                    case ConsoleKey.Q:
                        return 0;
                    case ConsoleKey.S:
                        index++;
                        continue;
                    case ConsoleKey.B:
                        index = Math.Max(0, index - 1);
                        continue;
                }
                if (transmission is null) continue;
                string sender = transmission.Sender?.Name ?? transmission.SenderGuid;
                if (from is not null ? !sender.Contains(from, StringComparison.OrdinalIgnoreCase) : IsAtcStation(sender))
                {
                    Console.WriteLine($"    (ignored {transmission.Duration.TotalSeconds:F1} s from {sender})");
                    continue;
                }
                if (transmission.Duration < TimeSpan.FromSeconds(0.3))
                {
                    Console.WriteLine($"    too short ({transmission.Duration.TotalSeconds:F1} s), say it again");
                    continue;
                }
                var samples = Audio.FromPcm16(transmission.Pcm);
                var recorded = Save(corpus, call, samples, sender, transmission.Frequency.Hz / 1e6, transmission.LostPackets);
                Console.WriteLine($"    saved {recorded.Seconds:F1} s from {sender}, level {recorded.LevelDb:F0} dBFS{Warnings(recorded)}");
                index++;
            }
            Console.WriteLine($"All {script.Count} calls recorded. Next: dotnet run --project tools/DcsAtc.VoiceTool -- bakeoff");
            return 0;
        }
        finally
        {
            session.Cancel();
            await run;
        }
    }

    // The next transmission, or a key the user pressed meanwhile.
    private static async Task<(SrsTransmission? Transmission, ConsoleKey? Key)> NextAsync(ChannelReader<SrsTransmission> heard, CancellationToken ct)
    {
        bool keys = !Console.IsInputRedirected;
        while (true)
        {
            if (heard.TryRead(out var t)) return (t, null);
            if (keys && Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true).Key;
                if (key is ConsoleKey.Q or ConsoleKey.S or ConsoleKey.B) return (null, key);
            }
            await Task.Delay(50, ct);
        }
    }

    // The app's own stations ("Al Dhafra Ground") and test clients ("DigiTower …", before the rename "DCS-ATC …") talk on these frequencies too.
    private static bool IsAtcStation(string name) =>
        name.StartsWith("DigiTower", StringComparison.Ordinal) || name.StartsWith("DCS-ATC", StringComparison.Ordinal) || new[] { " Ground", " Tower", " Approach", " ATIS" }.Any(s => name.EndsWith(s, StringComparison.Ordinal));

    private static RecordedCall Save(CallCorpus corpus, TestCall call, float[] samples, string speaker, double frequencyMhz, int lost)
    {
        string file = $"{call.Number:000}.wav";
        Wav.Write(Path.Combine(corpus.Directory, file), new AudioClip(samples, SrsProtocol.SampleRate));
        var (level, peak) = Levels(samples);
        var recorded = new RecordedCall
        {
            Number = call.Number, Text = call.Text, Keys = call.Keys, File = file, Speaker = speaker, FrequencyMhz = frequencyMhz,
            Seconds = samples.Length / (double)SrsProtocol.SampleRate, LostPackets = lost, LevelDb = level, PeakDb = peak, Recorded = DateTimeOffset.Now,
        };
        corpus.Put(recorded);
        corpus.Save();
        return recorded;
    }

    private static string Warnings(RecordedCall c) =>
        (c.PeakDb > -0.5 ? "; clipped, turn the mic gain down" : "") +
        (c.LevelDb < -35 ? "; quiet, speak up or raise the mic gain" : "") +
        (c.LostPackets > 0 ? $"; {c.LostPackets} packets lost" : "");

    // Loudness of the speech parts (RMS of samples above -40 dBFS) and the peak, in dBFS.
    private static (double Level, double Peak) Levels(float[] samples)
    {
        double peak = 0, sum = 0;
        int loud = 0;
        foreach (var s in samples)
        {
            double a = Math.Abs(s);
            peak = Math.Max(peak, a);
            if (a > 0.01)
            {
                sum += a * a;
                loud++;
            }
        }
        static double Db(double x) => 20 * Math.Log10(Math.Max(x, 1e-6));
        return (loud == 0 ? -120 : Db(Math.Sqrt(sum / loud)), Db(peak));
    }

    // The test calls said by a TTS voice and sent through the SRS codec (Opus, 16 kHz, 40 ms frames) instead of SRS.
    private static int Synthetic(Options o, IReadOnlyList<TestCall> script, CallCorpus corpus)
    {
        var catalog = o.Voices();
        var info = catalog.Find(o.Get("voice", catalog.Default)) ?? throw new ArgumentException($"unknown voice {o.Find("voice")}");
        if (!catalog.IsAvailable(info))
        {
            Console.Error.WriteLine($"voice {info.Id} is not downloaded (run: fetch --voice {info.Id})");
            return 2;
        }
        using var voice = catalog.Open(info);
        foreach (var call in script)
        {
            var clip = Audio.Resample(voice.Render(call.Text), SrsProtocol.SampleRate);
            var decoder = SrsOpus.CreateDecoder();
            var pcm = SrsOpus.Encode(Audio.ToPcm16(clip.Samples)).SelectMany(frame => SrsOpus.Decode(decoder, frame)).ToArray();
            var recorded = Save(corpus, call, Audio.FromPcm16(pcm), $"tts:{info.Id}", 250.0, 0);
            Console.WriteLine($"[{call.Number}/{script.Count}] {recorded.Seconds:F1} s  {call.Text}");
        }
        Console.WriteLine($"written to {Path.GetFullPath(corpus.Directory)}");
        return 0;
    }

    // ---- transcribe ---------------------------------------------------------------------------------------------

    public static async Task<int> TranscribeAsync(Options o, CancellationToken ct)
    {
        var catalog = o.Recognizers();
        var info = catalog.Find(o.Get("recognizer", catalog.Default)) ?? throw new ArgumentException($"unknown recognizer {o.Find("recognizer")}");
        int threads = int.Parse(o.Get("threads", catalog.Threads.ToString(CultureInfo.InvariantCulture)));
        List<(string Name, int Number, float[] Samples)> inputs = o.Find("corpus") is { } dir
            ? CallCorpus.Load(dir) is var corpus && corpus.Calls.Count > 0
                ? corpus.Calls.Select(c => (c.File, c.Number, ReadSamples(corpus.PathOf(c)))).ToList()
                : throw new FileNotFoundException($"no recorded calls in {dir}")
            : o.Positional.Select((f, i) => (f, i + 1, ReadSamples(f))).ToList();
        if (inputs.Count == 0) throw new ArgumentException("nothing to transcribe (files or --corpus)");

        var run = new RecognizerRun { Recognizer = info.Id, Label = info.Label, Threads = threads };
        string? jsonPath = o.Find("json");
        try
        {
            var clock = Stopwatch.StartNew();
            using var recognizer = catalog.Create(info, threads);
            run.LoadSeconds = clock.Elapsed.TotalSeconds;
            run.Runtime = recognizer.Runtime;
            Console.WriteLine($"{info.Id}: {info.Label}, loaded in {run.LoadSeconds:F1} s on {recognizer.Runtime}, {threads} threads");
            // The first call pays for allocations (and shader compilation on the GPU); measured on its own.
            clock.Restart();
            recognizer.Recognize(inputs[0].Samples);
            run.FirstCallSeconds = clock.Elapsed.TotalSeconds;

            bool loop = o.Find("loop") is not null;
            var interval = TimeSpan.FromSeconds(double.Parse(o.Get("interval", "5"), CultureInfo.InvariantCulture));
            do
            {
                foreach (var (name, number, samples) in inputs)
                {
                    ct.ThrowIfCancellationRequested();
                    var cpu = Process.GetCurrentProcess().TotalProcessorTime;
                    clock.Restart();
                    string text = recognizer.Recognize(samples);
                    var result = new CallResult(number, text, clock.Elapsed.TotalSeconds, (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalSeconds, samples.Length / 16000.0);
                    if (!loop) run.Calls.Add(result);
                    Console.WriteLine($"{name,-12} {result.Seconds,5:F2} s  {text}");
                    if (loop) await Task.Delay(interval, ct);
                }
            }
            while (loop);
            run.PeakMemoryMb = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && jsonPath is not null)
        {
            // In a bake-off, one recognizer failing (a GPU runtime that will not load) must not stop the others.
            run.Error = ex.Message;
            Console.Error.WriteLine($"{info.Id}: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        if (jsonPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!);
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(run, Json), ct);
        }
        return run.Error is null ? 0 : 3;
    }

    private static float[] ReadSamples(string path) => Audio.Resample(Wav.Read(path), 16000).Samples;

    // ---- bakeoff ------------------------------------------------------------------------------------------------

    public static async Task<int> BakeoffAsync(Options o, CancellationToken ct)
    {
        var catalog = o.Recognizers();
        var corpus = CallCorpus.Load(o.Get("corpus", DefaultCorpus));
        if (corpus.Calls.Count == 0)
        {
            Console.Error.WriteLine($"no recorded calls in {corpus.Directory} (run: record)");
            return 2;
        }
        string outDir = o.Get("out", Path.Combine("out", "s5"));
        var wanted = o.Find("recognizers")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var runs = new List<RecognizerRun>();
        bool rescore = o.Find("rescore") is not null;
        foreach (var info in catalog.Recognizers.Where(r => wanted is null || wanted.Contains(r.Id, StringComparer.OrdinalIgnoreCase)))
        {
            if (rescore)
            {
                // Only score again (after a change to the scoring): the results of an earlier run.
                string earlier = Path.Combine(outDir, "results", $"{info.Id}.json");
                if (File.Exists(earlier)) runs.Add(JsonSerializer.Deserialize<RecognizerRun>(await File.ReadAllTextAsync(earlier, ct), Json)!);
                continue;
            }
            if (!catalog.IsAvailable(info))
            {
                Console.WriteLine($"{info.Id}: model not downloaded, skipped (fetch --recognizer {info.Id})");
                continue;
            }
            // A process per recognizer: Whisper loads one native runtime per process, a GPU crash stays contained,
            // and CPU time and memory are the recognizer's own. Below-normal priority, as it would run next to DCS.
            string json = Path.Combine(outDir, "results", $"{info.Id}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(json)!);
            File.Delete(json);
            var args = new List<string> { "transcribe", "--corpus", Path.GetFullPath(corpus.Directory), "--recognizer", info.Id, "--json", Path.GetFullPath(json), "--data", o.DataDirectory() };
            if (o.Find("threads") is { } threads) args.AddRange(["--threads", threads]);
            using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = false })!;
            try
            {
                child.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
            await child.WaitForExitAsync(ct);
            if (File.Exists(json)) runs.Add(JsonSerializer.Deserialize<RecognizerRun>(await File.ReadAllTextAsync(json, ct), Json)!);
            else runs.Add(new RecognizerRun { Recognizer = info.Id, Label = info.Label, Error = $"exited with code {child.ExitCode}" });
        }

        var reader = new PilotCallReader(catalog.Aliases);
        var scores = runs.Select(r => Score(r, corpus, reader)).ToList();
        Console.WriteLine();
        Console.WriteLine($"{"recognizer",-22} {"understood",10} {"tolerant",9} {"ATC right",9} {"keys",6} {"WER",6} {"median",7} {"p95",6} {"CPU/call",9} {"load",6} {"memory",8}  false matches");
        foreach (var s in scores.OrderByDescending(s => s.Understood).ThenBy(s => s.MedianSeconds))
        {
            if (s.Run.Error is not null)
            {
                Console.WriteLine($"{s.Run.Recognizer,-22} failed: {s.Run.Error}");
                continue;
            }
            Console.WriteLine($"{s.Run.Recognizer,-22} {s.Understood,4}/{s.Calls,-5} {s.UnderstoodTolerant,4}/{s.Calls,-4} {s.Handled,4}/{s.Calls,-4} {s.KeyRate,6:P0} {s.WordErrorRate,6:P0} {s.MedianSeconds,6:F2}s {s.P95Seconds,5:F2}s {s.CpuSecondsPerCall,8:F2}s {s.Run.LoadSeconds,5:F1}s {s.Run.PeakMemoryMb,6:F0} MB  {s.FalseMatches}");
        }
        if (o.Find("misses") is not null)
        {
            foreach (var s in scores.Where(s => s.Run.Error is null))
            {
                foreach (var c in s.CallScores.Where(c => c.MissingKeys.Count > 0 || c.Reaction != c.ExpectedReaction))
                {
                    if (c.Reaction != c.ExpectedReaction) Console.WriteLine($"{s.Run.Recognizer,-20} {c.Recorded.Number,3}  ATC: {c.Reaction}, should be: {c.ExpectedReaction}");
                    Console.WriteLine($"{s.Run.Recognizer,-20} {c.Recorded.Number,3}  missing {string.Join(" · ", c.MissingKeys)}");
                    Console.WriteLine($"{"",25}heard: {c.Result.Text}");
                    Console.WriteLine($"{"",25}plain: {string.Join(' ', c.Words)}");
                }
            }
        }
        string report = Path.Combine(outDir, "report.html");
        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(report, BakeoffReport.Html(corpus, scores, Path.GetDirectoryName(Path.GetFullPath(report))!), ct);
        Console.WriteLine($"\nreport: {Path.GetFullPath(report)}");
        return 0;
    }

    /// <summary>ATC's reaction to a reading: the call ("RequestApproach ils"), or "no answer" (readback, chatter), or "say again".</summary>
    internal static string Reaction(PilotCallReading r) =>
        r.Call is { } c ? $"{c.Intent}{(c.Option is { } o ? $" {o}" : "")}" : r.Readback || !r.AimedAtAtc ? "no answer" : "say again";

    /// <summary>Resemblance from which a key phrase counts as heard in the tolerant score (a parser that accepts near-misses).</summary>
    internal const double TolerantMatch = 0.8;

    /// <summary>How one recognizer did on the corpus.</summary>
    internal static RecognizerScore Score(RecognizerRun run, CallCorpus corpus, PilotCallReader reader)
    {
        var calls = new List<CallScore>();
        foreach (var result in run.Calls)
        {
            var recorded = corpus.Calls.FirstOrDefault(c => c.Number == result.Number);
            if (recorded is null) continue;
            var reference = Transcript.Normalize(recorded.Text);
            var words = Transcript.Normalize(result.Text);
            var (errors, correct) = Transcript.Compare(reference, words);
            var missing = recorded.Keys.Where(k => !Transcript.Contains(words, Transcript.Normalize(k))).ToList();
            var missingTolerant = missing.Where(k => Transcript.Resemblance(words, Transcript.Normalize(k)) < TolerantMatch).ToList();
            // The same tolerance applied to key phrases of the other calls that were not said: how often it is fooled.
            var notSaid = corpus.Calls.SelectMany(c => c.Keys).Distinct().Where(k => !Transcript.Contains(reference, Transcript.Normalize(k)));
            int falseMatches = notSaid.Count(k => Transcript.Resemblance(words, Transcript.Normalize(k)) >= TolerantMatch);
            // What ATC does with it, against what it would do with the call as written.
            string expected = Reaction(reader.Read(recorded.Text)), heard = Reaction(reader.Read(result.Text));
            calls.Add(new CallScore(recorded, result, words, correct, errors, reference.Length, missing, missingTolerant, falseMatches, expected, heard));
        }
        var times = calls.Select(c => c.Result.Seconds).Order().ToList();
        return new RecognizerScore(run, calls)
        {
            Calls = calls.Count,
            Understood = calls.Count(c => c.MissingKeys.Count == 0),
            UnderstoodTolerant = calls.Count(c => c.MissingTolerant.Count == 0),
            Handled = calls.Count(c => c.Reaction == c.ExpectedReaction),
            FalseMatches = calls.Sum(c => c.FalseMatches),
            KeyRate = calls.Sum(c => c.Recorded.Keys.Count) is var keys and > 0 ? 1 - calls.Sum(c => c.MissingKeys.Count) / (double)keys : 0,
            WordErrorRate = calls.Sum(c => c.ReferenceWords) is var refs and > 0 ? calls.Sum(c => c.Errors) / (double)refs : 0,
            MedianSeconds = times.Count > 0 ? times[times.Count / 2] : 0,
            P95Seconds = times.Count > 0 ? times[Math.Min(times.Count - 1, (int)Math.Ceiling(times.Count * 0.95) - 1)] : 0,
            CpuSecondsPerCall = calls.Count > 0 ? calls.Average(c => c.Result.CpuSeconds) : 0,
            RealTimeFactor = calls.Count > 0 ? calls.Sum(c => c.Result.Seconds) / Math.Max(1e-9, calls.Sum(c => c.Result.AudioSeconds)) : 0,
        };
    }
}

/// <summary>One recognizer over a corpus (written by "transcribe --json", read by "bakeoff").</summary>
sealed class RecognizerRun
{
    public string Recognizer { get; set; } = "";
    public string Label { get; set; } = "";
    public string Runtime { get; set; } = "";
    public int Threads { get; set; }
    public double LoadSeconds { get; set; }
    public double FirstCallSeconds { get; set; }
    public double PeakMemoryMb { get; set; }
    public List<CallResult> Calls { get; set; } = [];
    public string? Error { get; set; }
}

sealed record CallResult(int Number, string Text, double Seconds, double CpuSeconds, double AudioSeconds);

sealed record CallScore(RecordedCall Recorded, CallResult Result, string[] Words, bool[] Correct, int Errors, int ReferenceWords,
    IReadOnlyList<string> MissingKeys, IReadOnlyList<string> MissingTolerant, int FalseMatches, string ExpectedReaction, string Reaction);

sealed record RecognizerScore(RecognizerRun Run, IReadOnlyList<CallScore> CallScores)
{
    public int Calls { get; init; }
    /// <summary>Calls where every key phrase was recognized: ATC would have understood them.</summary>
    public int Understood { get; init; }
    /// <summary>The same with near-misses accepted (<see cref="RecognitionCommands.TolerantMatch"/>).</summary>
    public int UnderstoodTolerant { get; init; }
    /// <summary>Calls where ATC reacts as it would to the call as written (the parser: the request, or no answer to a readback).</summary>
    public int Handled { get; init; }
    /// <summary>Key phrases of other calls, not said, that the tolerance would have accepted.</summary>
    public int FalseMatches { get; init; }
    public double KeyRate { get; init; }
    public double WordErrorRate { get; init; }
    public double MedianSeconds { get; init; }
    public double P95Seconds { get; init; }
    public double CpuSecondsPerCall { get; init; }
    public double RealTimeFactor { get; init; }
}
