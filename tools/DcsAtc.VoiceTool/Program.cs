using System.Diagnostics;
using DcsAtc.Recognition;
using DcsAtc.Srs;
using DcsAtc.Voice;

// DigiTower voice tool.
//
//   srs-test [--host 127.0.0.1] [--port 5002] [--freq 247.2] [--wav in.wav] [--out echo.wav]
//       Joins the SRS server, transmits on an SRS test frequency (the server echoes those back to the sender),
//       records the echo and reports packets, losses and timing. Without --wav a test signal is sent.
//       --pair: a second client listens on --freq (default then 250.0) instead, as ATC listens to a player;
//       checks that the transmission arrives and that its sender is identified.
//
//   units "text"
//       Shows the clips a transmission is made of: [Hangman] [one one] | [Al Dafra Ground] ‖ ...
//
//   say "text" [--voice id] [--out say.wav] [--srs] [--host] [--port 5002] [--freq 250.0] [--name "DigiTower say"]
//       Renders a transmission with a voice (default: the catalogue default) and writes it as WAV; with --srs it is
//       also transmitted. New clips are generated once into data/voice/cache/<voice>/.
//
//   audition [--voices id,id,...] [--out out/audition]
//       Renders the same sample transmissions with every available voice, clean and with a rough radio effect,
//       and reports how fast each voice makes new clips.
//
//   listen [--freq 250.0,270.0] [--port 5002] [--coalition 2] [--seconds n] [--out out/heard]
//       Listens like a pilot and records every transmission heard on those frequencies as WAV.
//
//   fetch [--voice id | --model name | --all | --recognition | --recognizer id]
//       Downloads voice models into data/voice/models: those of the default voice (or of --voice), one model, or all;
//       --recognition: every speech-recognition model, --recognizer: the model of one recognizer.
//
//   record [--calls data/voice/test-calls.txt] [--out out/s5/calls] [--freq 250.0,270.0,257.0] [--port 5002] [--from name]
//       Records the test calls for the recognition bake-off: shows one call at a time, you say it on SRS (any of the
//       frequencies), the transmission is saved. Resumes where it stopped. --synthetic [--voice id]: makes the calls
//       with a TTS voice through the SRS codec instead (a native-speaker baseline, and a check without SRS).
//
//   transcribe <file.wav ...> | --corpus out/s5/calls [--recognizer id] [--threads n] [--json results.json] [--loop [--interval 5]]
//       Speech to text with one recognizer; --loop repeats the corpus (load test while flying in VR).
//
//   review [--heard out/heard] [--out out/train/flight] [--from name]
//       Goes through the calls heard in flight for tuning the recognizer: plays each, shows its text; Enter keeps it,
//       a typed text corrects it, S skips it. Writes out/train/flight/labels.jsonl (recordings are only read).
//   bakeoff [--corpus out/s5/calls] [--recognizers id,id,...] [--threads n] [--out out/s5] [--rescore] [--misses]
//       Runs every recognizer (each in a process of its own, below-normal priority) over the recorded calls and
//       writes report.html: what ATC understood, word errors, time per call, CPU, memory. --rescore: score the
//       results of the last run again without recognizing; --misses: list the calls each recognizer got wrong.
//
//   Common: [--data <dir>] the repository's data folder (default: found upwards from here);
//           [--threads n] CPU threads per TTS engine (default: voices.json).

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: DcsAtc.VoiceTool srs-test|units|say|audition|fetch|listen|record|transcribe|bakeoff|review [options]");
    return 1;
}

var options = Options.Parse(args.Skip(1));
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

switch (args[0])
{
    case "srs-test":
        return await SrsTestAsync(options, cts.Token);
    case "units":
        Console.WriteLine(SpokenText.Describe(SpokenText.Units(options.Text)));
        return 0;
    case "say":
        return await SayAsync(options, cts.Token);
    case "audition":
        return Audition(options);
    case "fetch":
        return await FetchAsync(options, cts.Token);
    case "listen":
        return await ListenAsync(options, cts.Token);
    case "record":
        return await RecognitionCommands.RecordAsync(options, cts.Token);
    case "transcribe":
        return await RecognitionCommands.TranscribeAsync(options, cts.Token);
    case "bakeoff":
        return await RecognitionCommands.BakeoffAsync(options, cts.Token);
    case "review":
        return ReviewCommand.Run(options);
    default:
        Console.Error.WriteLine($"unknown command: {args[0]}");
        return 1;
}

static async Task<int> SrsTestAsync(Options o, CancellationToken ct)
{
    bool pair = o.Find("pair") is not null;
    var frequency = SrsFrequency.Mhz(double.Parse(o.Get("freq", pair ? "250.0" : "247.2"), System.Globalization.CultureInfo.InvariantCulture));
    var audio = o.Find("wav") is { } wav ? Audio.Resample(Wav.Read(wav), SrsProtocol.SampleRate) : TestSignal();
    var client = new SrsClient(new SrsOptions
    {
        Host = o.Get("host", "127.0.0.1"),
        Port = int.Parse(o.Get("port", "5002")),
        Name = "DigiTower test",
        Coalition = 2,
    }, Console.WriteLine);
    var listener = !pair ? client : new SrsClient(new SrsOptions
    {
        Host = client.Options.Host,
        Port = client.Options.Port,
        Name = "DigiTower listener",
        Coalition = 2,
        Receive = [frequency],
    }, Console.WriteLine);
    using var receiver = new SrsReceiver(listener);
    var echo = new TaskCompletionSource<SrsTransmission>(TaskCreationOptions.RunContinuationsAsynchronously);
    receiver.TransmissionReceived += t => echo.TrySetResult(t);

    using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var run = Task.WhenAll(new[] { client, listener }.Distinct().Select(c => c.RunAsync(session.Token)));
    try
    {
        if (!await client.WaitReadyAsync(TimeSpan.FromSeconds(10), ct) || !await listener.WaitReadyAsync(TimeSpan.FromSeconds(10), ct))
        {
            Console.Error.WriteLine("SRS server not reachable (is it running, and is the port right?)");
            return 2;
        }
        Console.WriteLine($"server {client.ServerVersion}, test frequencies: {client.ServerSettings.GetValueOrDefault("TEST_FREQUENCIES", "?")}, peers: {client.Peers.Count}");
        var clock = Stopwatch.StartNew();
        await client.TransmitAsync(Audio.ToPcm16(audio.Samples), [frequency], ct);
        var sent = clock.Elapsed;
        Console.WriteLine($"sent {audio.Duration.TotalSeconds:F2} s of audio on {frequency} in {sent.TotalSeconds:F2} s");

        var received = await echo.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Console.WriteLine($"{(pair ? "received" : "echo")}: {received.Packets} packets, {received.LostPackets} lost, {received.Duration.TotalSeconds:F2} s, on {received.Frequency}, from {received.Sender?.Name ?? received.SenderGuid}");
        if (pair && received.Sender?.Name != client.Options.Name)
        {
            Console.Error.WriteLine("sender not identified");
            return 3;
        }
        string output = o.Get("out", Path.Combine("out", "srs-echo.wav"));
        Wav.Write(output, new AudioClip(Audio.FromPcm16(received.Pcm), SrsProtocol.SampleRate));
        Console.WriteLine($"echo written to {Path.GetFullPath(output)}");
        return received.LostPackets == 0 && Math.Abs((received.Duration - audio.Duration).TotalMilliseconds) <= SrsProtocol.FrameMilliseconds ? 0 : 3;
    }
    catch (TimeoutException)
    {
        Console.Error.WriteLine($"no echo: is {frequency} one of the server's TEST_FREQUENCIES?");
        return 3;
    }
    finally
    {
        session.Cancel();
        await run;
    }
}

// Three rising beeps: easy to recognise by ear and to check for gaps.
static AudioClip TestSignal()
{
    int rate = SrsProtocol.SampleRate;
    var samples = new List<float>();
    foreach (var hz in new[] { 600.0, 800.0, 1000.0 })
    {
        for (int i = 0; i < rate * 0.4; i++) samples.Add((float)(0.5 * Math.Sin(2 * Math.PI * hz * i / rate)));
        samples.AddRange(new float[rate / 5]);
    }
    return new AudioClip(samples.ToArray(), rate);
}

static VoiceCatalog Catalog(Options o) => o.Voices();

static async Task<int> SayAsync(Options o, CancellationToken ct)
{
    var catalog = Catalog(o);
    var info = catalog.Find(o.Get("voice", catalog.Default)) ?? throw new ArgumentException($"unknown voice {o.Find("voice")}");
    if (!catalog.IsAvailable(info))
    {
        Console.Error.WriteLine($"voice {info.Id} is not downloaded (run: fetch --model {info.Model})");
        return 2;
    }
    using var voice = catalog.Open(info, Console.WriteLine);
    Console.WriteLine(SpokenText.Describe(SpokenText.Units(o.Text)));
    var clock = Stopwatch.StartNew();
    var audio = voice.Render(o.Text);
    Console.WriteLine($"rendered {audio.Duration.TotalSeconds:F2} s in {clock.ElapsedMilliseconds} ms");
    string output = o.Get("out", Path.Combine("out", "say.wav"));
    Wav.Write(output, audio);
    Console.WriteLine($"written to {Path.GetFullPath(output)}");
    if (o.Find("srs") is null) return 0;

    // --name: talk as that SRS client, e.g. a pilot's player name to test voice requests.
    var client = new SrsClient(new SrsOptions { Host = o.Get("host", "127.0.0.1"), Port = int.Parse(o.Get("port", "5002")), Name = o.Get("name", "DigiTower say") }, Console.WriteLine);
    using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var run = client.RunAsync(session.Token);
    try
    {
        var frequency = SrsFrequency.Mhz(double.Parse(o.Get("freq", "250.0"), System.Globalization.CultureInfo.InvariantCulture));
        await client.TransmitAsync(Audio.ToPcm16(audio.Samples), [frequency], ct);
        Console.WriteLine($"transmitted on {frequency}");
        return 0;
    }
    finally
    {
        session.Cancel();
        await run;
    }
}

static int Audition(Options o)
{
    string[] samples =
    [
        "Hangman 1-1, Al Dhafra Ground, taxi to holding point runway 31R via A, B. Wind 310 degrees, 10 knots, QNH 1013, altimeter 29.92.",
        "Hangman 1-1, Al Dhafra Tower, runway 31R, wind 310 degrees, 10 knots, cleared for takeoff.",
        "Hangman 1 flight, go around, I say again, go around, runway occupied.",
        "Viper 2-3, welcome to Al Dhafra, vacate runway when able, contact Ground on 250.0.",
    ];
    var catalog = Catalog(o);
    string outDir = o.Get("out", Path.Combine("out", "audition"));
    var wanted = o.Find("voices")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    var results = new List<string>();
    foreach (var info in catalog.Voices.Where(v => wanted is null || wanted.Contains(v.Id, StringComparer.OrdinalIgnoreCase)))
    {
        if (!catalog.IsAvailable(info))
        {
            Console.WriteLine($"{info.Id}: not downloaded, skipped");
            continue;
        }
        var clock = Stopwatch.StartNew();
        using var voice = catalog.Open(info);
        var load = clock.Elapsed;
        clock.Restart();
        int made = voice.Prebake(samples);
        double perClip = made == 0 ? 0 : clock.Elapsed.TotalMilliseconds / made;
        for (int i = 0; i < samples.Length; i++)
        {
            var audio = voice.Render(samples[i]);
            Wav.Write(Path.Combine(outDir, "clean", $"{info.Id}-{i + 1}.wav"), audio);
            Wav.Write(Path.Combine(outDir, "radio", $"{info.Id}-{i + 1}.wav"), RadioEffect.Apply(audio));
        }
        string line = $"{info.Id,-20} {info.Label,-32} loaded in {load.TotalMilliseconds,5:F0} ms, {made,3} new clips, {perClip,5:F0} ms per clip";
        Console.WriteLine(line);
        results.Add(line);
    }
    Directory.CreateDirectory(outDir);
    File.WriteAllLines(Path.Combine(outDir, "voices.txt"), results.Prepend("Voices:").Append("").Append("Samples:").Concat(samples.Select((s, i) => $"{i + 1}: {s}")));
    File.WriteAllText(Path.Combine(outDir, "index.html"), AuditionPage(catalog.Voices.Where(v => File.Exists(Path.Combine(outDir, "clean", $"{v.Id}-1.wav"))).ToList(), samples));
    Console.WriteLine($"written to {Path.GetFullPath(outDir)}: open index.html to listen");
    return 0;
}

// A local page to compare the voices: one row per voice, one player per sample, radio or clean.
static string AuditionPage(IReadOnlyList<VoiceInfo> voices, IReadOnlyList<string> samples)
{
    static string H(string s) => System.Net.WebUtility.HtmlEncode(s);
    var rows = string.Concat(voices.Select(v =>
        $"<tr><th>{H(v.Label)}<small>{H(v.Id)}</small></th>" +
        string.Concat(Enumerable.Range(1, samples.Count).Select(i => $"<td><audio controls preload=\"none\" data-file=\"{H(v.Id)}-{i}.wav\"></audio></td>")) + "</tr>"));
    var heads = string.Concat(samples.Select((s, i) => $"<th title=\"{H(s)}\">{i + 1}</th>"));
    var list = string.Concat(samples.Select((s, i) => $"<li>{H(s)}</li>"));
    return $$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>ATC Voice Audition</title>
        <style>
          :root { --bg: #f6f7f9; --fg: #1d2330; --muted: #5b6475; --line: #d9dde5; --card: #fff; }
          @media (prefers-color-scheme: dark) { :root { --bg: #15181e; --fg: #e6e9ef; --muted: #9aa3b5; --line: #2c323d; --card: #1c2028; } }
          body { margin: 0; padding: 24px 16px; background: var(--bg); color: var(--fg); font: 15px/1.45 system-ui, sans-serif; }
          main { max-width: 1200px; margin: 0 auto; }
          h1 { font-size: 22px; margin: 0 0 4px; } p, li { color: var(--muted); }
          .wrap { overflow-x: auto; background: var(--card); border: 1px solid var(--line); border-radius: 8px; }
          table { border-collapse: collapse; width: 100%; } th, td { padding: 8px 10px; border-bottom: 1px solid var(--line); text-align: left; }
          th small { display: block; font-weight: 400; color: var(--muted); font-size: 12px; } audio { width: 210px; height: 32px; }
          label { margin-right: 16px; }
        </style></head>
        <body><main>
        <h1>ATC voice audition</h1>
        <p>The same transmissions in every voice, stitched from cached clips exactly as the app sends them. In the game, SRS adds its own radio sound; "radio" here is a rough imitation.</p>
        <p><label><input type="radio" name="kind" value="radio" checked> radio</label><label><input type="radio" name="kind" value="clean"> clean</label></p>
        <ol>{{list}}</ol>
        <div class="wrap"><table><thead><tr><th>Voice</th>{{heads}}</tr></thead><tbody>{{rows}}</tbody></table></div>
        </main>
        <script>
          function load(kind) { document.querySelectorAll('audio').forEach(a => { a.src = kind + '/' + a.dataset.file; }); }
          document.querySelectorAll('input[name=kind]').forEach(r => r.addEventListener('change', () => load(r.value)));
          document.addEventListener('play', e => document.querySelectorAll('audio').forEach(a => { if (a !== e.target) a.pause(); }), true);
          load('radio');
        </script>
        </body></html>
        """;
}

// Listens like a pilot and records every transmission heard (until Ctrl+C or --seconds).
static async Task<int> ListenAsync(Options o, CancellationToken ct)
{
    var frequencies = o.Get("freq", "250.0,270.0").Split(',')
        .Select(f => SrsFrequency.Mhz(double.Parse(f, System.Globalization.CultureInfo.InvariantCulture))).ToList();
    string outDir = o.Get("out", Path.Combine("out", "heard"));
    var client = new SrsClient(new SrsOptions
    {
        Host = o.Get("host", "127.0.0.1"),
        Port = int.Parse(o.Get("port", "5002")),
        Name = "DigiTower listener",
        Coalition = int.Parse(o.Get("coalition", "2")),
        Receive = frequencies,
    }, Console.WriteLine);
    using var receiver = new SrsReceiver(client);
    int count = 0;
    receiver.TransmissionReceived += t =>
    {
        string file = Path.Combine(outDir, $"{DateTime.Now:HHmmss}-{t.Frequency.Hz / 1e6:0.000}-{Interlocked.Increment(ref count)}.wav");
        Wav.Write(file, new AudioClip(Audio.FromPcm16(t.Pcm), SrsProtocol.SampleRate));
        Console.WriteLine($"heard {t.Duration.TotalSeconds:F1} s on {t.Frequency} from {t.Sender?.Name ?? t.SenderGuid} ({t.Packets} packets, {t.LostPackets} lost) -> {file}");
    };
    using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
    if (o.Find("seconds") is { } seconds) session.CancelAfter(TimeSpan.FromSeconds(double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)));
    Console.WriteLine($"listening on {string.Join(", ", frequencies)}");
    await client.RunAsync(session.Token);
    return 0;
}

static async Task<int> FetchAsync(Options o, CancellationToken ct)
{
    if (o.Find("recognition") is not null || o.Find("recognizer") is not null) return await FetchRecognitionAsync(o, ct);
    var catalog = Catalog(o);
    List<VoiceModel> models;
    if (o.Find("all") is not null)
    {
        models = catalog.Models.ToList();
    }
    else if (o.Find("model") is { } only)
    {
        models = catalog.Models.Where(m => m.Name.Equals(only, StringComparison.OrdinalIgnoreCase)).ToList();
    }
    else
    {
        var voice = catalog.Find(o.Get("voice", catalog.Default)) ?? throw new ArgumentException($"unknown voice {o.Get("voice", catalog.Default)}");
        models = catalog.ModelsFor(voice).ToList();
        Console.WriteLine($"voice {voice.Id} ({voice.Label}): {(models.Count == 0 ? "nothing to download" : string.Join(", ", models.Select(m => m.Name)))}");
    }
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    foreach (var model in models) await DownloadAsync(http, catalog.ModelsDirectory, model.Name, model.Url, model.License, archive: true, ct);
    return 0;
}

static async Task<int> FetchRecognitionAsync(Options o, CancellationToken ct)
{
    var catalog = o.Recognizers();
    List<RecognitionModel> models;
    if (o.Find("recognizer") is { } id)
    {
        var info = catalog.Find(id) ?? throw new ArgumentException($"unknown recognizer {id}");
        models = catalog.ModelFor(info) is { } m ? [m] : [];
    }
    else models = catalog.Models.ToList();
    Console.WriteLine($"speech recognition: {string.Join(", ", models.Select(m => $"{m.Name} ({m.SizeMb} MB)"))}");
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    foreach (var model in models)
    {
        if (model.Url is { } url)
        {
            await DownloadAsync(http, catalog.ModelsDirectory, model.Name, url, model.License, model.IsArchive, ct);
        }
        else if (model.Convert is { } repo && !File.Exists(catalog.PathOf(model)))
        {
            // Made from a Hugging Face model (an ATC fine-tune) with the conversion script.
            Console.WriteLine($"{model.Name}: converting {repo} ({model.License})");
            string script = Path.Combine(Path.GetDirectoryName(catalog.Directory)!, "..", "tools", "Convert-WhisperModel.ps1");
            using var convert = Process.Start(new ProcessStartInfo("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.GetFullPath(script), "-Repo", repo, "-Name", model.Name]) { UseShellExecute = false })!;
            await convert.WaitForExitAsync(ct);
            if (convert.ExitCode != 0) Console.Error.WriteLine($"{model.Name}: conversion failed");
        }
        else Console.WriteLine($"{model.Name}: present");
    }
    return 0;
}

// A model into the models folder: an archive unpacks to models/<name>/, a single file becomes models/<name>.bin.
// Everything is downloaded into a fresh folder of its own and moved in place only when complete.
static async Task DownloadAsync(HttpClient http, string modelsDirectory, string name, string url, string license, bool archive, CancellationToken ct)
{
    string target = Path.Combine(modelsDirectory, archive ? name : name + ".bin");
    if (Directory.Exists(target) || File.Exists(target))
    {
        Console.WriteLine($"{name}: present");
        return;
    }
    string staging = Path.Combine(modelsDirectory, $"_download-{Guid.NewGuid():N}");
    Directory.CreateDirectory(staging);
    try
    {
        string download = Path.Combine(staging, archive ? name + ".tar.bz2" : name + ".bin");
        Console.WriteLine($"{name}: downloading ({license})");
        await using (var source = await http.GetStreamAsync(url, ct))
        await using (var file = File.Create(download))
            await source.CopyToAsync(file, ct);
        if (!archive)
        {
            File.Move(download, target);
        }
        else
        {
            using var tar = Process.Start(new ProcessStartInfo("tar", ["-xjf", download, "-C", staging]) { UseShellExecute = false })!;
            await tar.WaitForExitAsync(ct);
            if (tar.ExitCode != 0 || !Directory.Exists(Path.Combine(staging, name)))
                throw new InvalidDataException($"{name}: the archive did not unpack to {name}/");
            Directory.Move(Path.Combine(staging, name), target);
        }
        Console.WriteLine($"{name}: ready");
    }
    finally
    {
        Directory.Delete(staging, recursive: true);
    }
}

sealed class Options
{
    private readonly Dictionary<string, string> _values = [];
    private readonly List<string> _positional = [];

    public static Options Parse(IEnumerable<string> args)
    {
        var o = new Options();
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--"))
            {
                o._positional.Add(list[i]);
                continue;
            }
            string key = list[i][2..];
            o._values[key] = i + 1 < list.Count && !list[i + 1].StartsWith("--") ? list[++i] : "true";
        }
        return o;
    }

    /// <summary>The positional text of "say" and "units".</summary>
    public string Text => _positional.Count > 0 ? string.Join(" ", _positional) : throw new ArgumentException("missing text");

    /// <summary>Every positional argument (the files of "transcribe").</summary>
    public IReadOnlyList<string> Positional => _positional;

    public string? Find(string key) => _values.GetValueOrDefault(key);
    public string Get(string key, string fallback) => _values.GetValueOrDefault(key, fallback);

    /// <summary>The repository's data folder: --data, or found upwards from here.</summary>
    public string DataDirectory() => Find("data") ?? FindDataDirectory() ?? throw new DirectoryNotFoundException("data folder not found (use --data)");

    /// <summary>The voices (data/voice/voices.json), with --threads applied.</summary>
    public VoiceCatalog Voices()
    {
        var catalog = VoiceCatalog.Load(Path.Combine(DataDirectory(), "voice"));
        return Find("threads") is { } threads ? new VoiceCatalog
        {
            Directory = catalog.Directory, Default = catalog.Default, Voices = catalog.Voices, Models = catalog.Models, Threads = int.Parse(threads),
        } : catalog;
    }

    /// <summary>The speech recognizers (data/voice/recognition.json).</summary>
    public RecognizerCatalog Recognizers() => RecognizerCatalog.Load(Path.Combine(DataDirectory(), "voice"), userFile: RecognizerCatalog.UserFile);

    private static string? FindDataDirectory()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "data");
                if (File.Exists(Path.Combine(candidate, "voice", "voices.json"))) return candidate;
            }
        }
        return null;
    }
}
