using DcsAtc.Airfields;
using DcsAtc.Bridge;
using DcsAtc.Recognition;
using DcsAtc.Voice;

namespace DcsAtc.Host;

public enum Light
{
    /// <summary>Switched off.</summary>
    Off,
    On,
    /// <summary>Waiting, loading, or turned off somewhere else (the server).</summary>
    Warning,
    Error,
}

/// <summary>One status light of the control panel: "DCS BRIDGE · Connected · 127.0.0.1:42700"; <paramref name="Hint"/>: what to do about it.</summary>
public sealed record StatusLight(string Label, Light State, string Text, string? Hint = null);

/// <summary>Extras the console takes that are not settings.</summary>
public sealed record StartOptions
{
    /// <summary>Write these airbases' DCS data to data\dcs\&lt;name&gt;.json (for labelling airfields).</summary>
    public IReadOnlyList<string> DumpAirbases { get; init; } = [];
}

/// <summary>
/// Runs DigiTower: builds the radio, the speech recognizer, the bridge connection and the controllers from the settings,
/// starts and stops them, and tells the control panel (or the console) how it is going. One host per program.
/// </summary>
public sealed class AtcHost : IAsyncDisposable
{
    private readonly AtcLog _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _run;
    private BridgeClient? _client;
    private SrsRadio? _radio;
    private SrsProbe? _srsProbe;
    private Transcriber? _transcriber;
    private AtcApp? _app;
    private LogFile? _file;
    private DigiTowerSettings _settings = new();
    private volatile AtcSnapshot _snapshot = AtcSnapshot.Empty;

    public AtcHost(AtcLog log) => _log = log;

    public AtcLog Log => _log;

    public bool Running => _run is { IsCompleted: false };

    /// <summary>When the ATC was started (local time); null while stopped.</summary>
    public DateTime? StartedAt { get; private set; }

    /// <summary>The settings it runs with (as of the last start).</summary>
    public DigiTowerSettings Settings => _settings;

    /// <summary>The log file being written, if any.</summary>
    public string? LogFilePath => _file?.CurrentPath;

    /// <summary>Whether the SRS server answers, while the ATC runs with a voice (null: not known yet, or no SRS in use).</summary>
    public bool? SrsReachable => _radio is { } radio && radio.Connections.Connected > 0 ? true : _srsProbe?.Reachable;

    /// <summary>The latest strips; raised on the ATC's loop after every change (marshal to the UI thread).</summary>
    public AtcSnapshot Snapshot => _snapshot;
    public event Action<AtcSnapshot>? SnapshotChanged;

    /// <summary>Started or stopped.</summary>
    public event Action? RunningChanged;

    public async Task StartAsync(DigiTowerSettings settings, StartOptions? options = null)
    {
        await _lock.WaitAsync();
        try
        {
            if (Running) return;
            if (_cts is not null) await CleanUpAsync(); // the last run ended on its own
            _settings = settings.Clone();
            OpenLogFile();
            string? data = _settings.ResolvedDataDirectory, dcs = _settings.ResolvedDcsDirectory;
            Write(LogCategory.App, $"DigiTower starting. DCS install: {dcs ?? "not found (set it in the settings)"}; data: {data ?? "not found (set it in the settings)"}",
                dcs is null || data is null ? LogLevel.Warning : LogLevel.Info);

            _radio = _settings.Voice ? OpenRadio(data) : null;
            if (!_settings.Voice) Write(LogCategory.VoiceOut, "voice off: text messages only");
            if (_radio is { } radio) _srsProbe = new SrsProbe(radio.Server, () => radio.Connections.Connected > 0);
            RecognizerCatalog? recognition = null;
            if (_settings.VoiceRequests)
            {
                if (_radio is null) Write(LogCategory.VoiceIn, "voice requests need the voice on SRS (switch the voice on)", LogLevel.Warning);
                else if (data is null || !File.Exists(Path.Combine(data, "voice", RecognizerCatalog.File))) Write(LogCategory.VoiceIn, $"voice requests: no data\\voice\\{RecognizerCatalog.File}", LogLevel.Warning);
                else
                {
                    recognition = RecognizerCatalog.Load(Path.Combine(data, "voice"), _settings.ResolvedModelsDirectory, RecognizerCatalog.UserFile);
                    _transcriber = OpenTranscriber(recognition, _radio);
                }
            }

            _client = new BridgeClient(new BridgeOptions { Host = _settings.BridgeHost, Port = _settings.BridgePort, MapDrawings = _settings.MapDrawings });
            _app = new AtcApp(_client, new AppOptions
            {
                DcsDirectory = dcs,
                DataDirectory = data,
                DumpAirbases = options?.DumpAirbases ?? [],
                DumpDirectory = data is null ? Path.Combine("data", "dcs") : Path.Combine(data, "dcs"),
                SilenceAirbases = _settings.SilenceAirbases,
                KeepStockAtc = _settings.KeepStockAtc,
                Radio = _radio,
                VoiceAliases = recognition?.Aliases,
                ManageAi = _settings.ManageAi,
                CheckReadbacks = _settings.CheckReadbacks,
                Emergencies = _settings.Emergencies,
                ApproachDefaults = new ApproachProcedures { RequestRange = _settings.ApproachRequestRange, ServiceRadius = _settings.ApproachServiceRadius },
                Log = _log,
            });
            var app = _app;
            app.Changed += () =>
            {
                _snapshot = app.Snapshot();
                SnapshotChanged?.Invoke(_snapshot);
            };

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            if (_transcriber is not null)
                _transcriber.Recognized += (facility, t, text) => _ = app.OnVoiceAsync(facility, t.Frequency.Hz / 1e6, t.Sender?.Name, t.Sender?.UnitId, text, ct);
            Write(LogCategory.Bridge, $"waiting for the DCS bridge on {_settings.BridgeHost}:{_settings.BridgePort}");
            var connection = _client.RunAsync(ct);
            _run = RunAsync(app, connection, ct);
            StartedAt = DateTime.Now;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or DllNotFoundException)
        {
            Write(LogCategory.App, $"could not start: {ex.Message}", LogLevel.Error);
            await CleanUpAsync();
        }
        finally
        {
            _lock.Release();
        }
        RunningChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_cts is null) return;
            Write(LogCategory.App, "DigiTower stopping");
            await _cts.CancelAsync();
            if (_run is not null) await _run;
            await CleanUpAsync();
            Write(LogCategory.App, "DigiTower stopped");
        }
        finally
        {
            _lock.Release();
        }
        RunningChanged?.Invoke();
    }

    private async Task RunAsync(AtcApp app, Task connection, CancellationToken ct)
    {
        try
        {
            await app.RunAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Write(LogCategory.App, $"the ATC stopped: {ex.Message}", LogLevel.Error);
        }
        try
        {
            await connection;
        }
        catch (OperationCanceledException)
        {
        }
        if (!ct.IsCancellationRequested) RunningChanged?.Invoke();
    }

    private async Task CleanUpAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        _transcriber?.Dispose();
        if (_radio is not null) await _radio.DisposeAsync();
        _srsProbe?.Dispose();
        _cts?.Dispose();
        (_client, _transcriber, _radio, _srsProbe, _cts, _run, _app) = (null, null, null, null, null, null, null);
        StartedAt = null;
        _snapshot = AtcSnapshot.Empty;
        SnapshotChanged?.Invoke(_snapshot);
        CloseLogFile();
    }

    /// <summary>
    /// What is written to the log file, changed while running (the log settings apply at once; everything else at the
    /// next start).
    /// </summary>
    public async Task ApplyLogSettingsAsync(LogSettings log)
    {
        await _lock.WaitAsync();
        try
        {
            var old = _settings.Log;
            var copy = new DigiTowerSettings { Log = log }.Clone().Log;
            _settings.Log = copy; // one reference swap: the log is written from other threads
            if (!Running) return;
            if (_file is not null && (!copy.WriteFile || copy.Directory != old.Directory || copy.KeepDays != old.KeepDays)) CloseLogFile();
            if (_file is null && copy.WriteFile) OpenLogFile();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The status lights: bridge, SRS, voice, voice requests, AI handling, map drawings.</summary>
    public IReadOnlyList<StatusLight> Status()
    {
        var s = _settings;
        var app = _app;
        bool running = Running;
        var lights = new List<StatusLight>();
        string bridge = $"{s.BridgeHost}:{s.BridgePort}";
        lights.Add(!running ? new("DCS BRIDGE", Light.Off, "Stopped")
            : app?.Connected == true ? new("DCS BRIDGE", Light.On, $"Connected · {bridge}")
            : new("DCS BRIDGE", Light.Warning, $"Waiting for DCS · {bridge}"));

        if (!running || _radio is null)
            lights.Add(new("SRS", Light.Off, !running ? "Stopped" : "Not used (text only)"));
        else
        {
            var (clients, connected) = _radio.Connections;
            bool? reachable = SrsReachable;
            lights.Add(clients > 0 && connected == clients ? new("SRS", Light.On, $"Connected · {connected} stations")
                : reachable == false ? new("SRS", Light.Error, $"No server at {_radio.Server} · start your SRS server", SrsHint)
                : clients > 0 ? new("SRS", Light.Warning, $"Connecting · {connected} of {clients} · {_radio.Server}")
                : reachable == true ? new("SRS", Light.On, $"Ready · {_radio.Server}")
                : new("SRS", Light.Warning, $"Checking · {_radio.Server}"));
        }

        lights.Add(!s.Voice ? new("VOICE", Light.Off, "Off · text messages only")
            : !running ? new("VOICE", Light.Off, "Stopped")
            : _radio is null ? new("VOICE", Light.Warning, "No voice found · text only")
            : new("VOICE", Light.On, _aiVoiceLabel is { } ai ? $"{_voiceLabel} · AI: {ai}" : _voiceLabel ?? "On"));

        lights.Add(!s.VoiceRequests ? new("VOICE REQUESTS", Light.Off, "Off")
            : !running ? new("VOICE REQUESTS", Light.Off, "Stopped")
            : _transcriber is null ? new("VOICE REQUESTS", Light.Warning, "Not available (see the log)")
            : _transcriber.Ready switch
            {
                null => new("VOICE REQUESTS", Light.Warning, $"Loading {_transcriber.RecognizerId}"),
                true => new("VOICE REQUESTS", Light.On, _transcriber.RecognizerId),
                false => new("VOICE REQUESTS", Light.Error, $"{_transcriber.RecognizerId} could not load"),
            });

        int held = _snapshot.Airfields.Sum(f => f.Ground.Count(st => st.Ai && st.Clearance.StartsWith("Held", StringComparison.Ordinal)));
        lights.Add(!s.ManageAi ? new("AI HANDLING", Light.Off, "Off · AI left to DCS")
            : !running || app is null || !app.Connected ? new("AI HANDLING", Light.Off, running ? "Waiting for DCS" : "Stopped")
            : !app.ManagingAi ? new("AI HANDLING", Light.Warning, "Off on this server")
            : new("AI HANDLING", held > 0 ? Light.Warning : Light.On, held > 0 ? $"On · {held} held" : "On"));

        lights.Add(!s.MapDrawings ? new("MAP DRAWINGS", Light.Off, "Off")
            : app?.ServerMapDrawings == false ? new("MAP DRAWINGS", Light.Warning, "Off on this server")
            : !running ? new("MAP DRAWINGS", Light.Off, "Stopped")
            : new("MAP DRAWINGS", Light.On, "On · the whole side sees them"));
        return lights;
    }

    /// <summary>What to do when the SRS server does not answer.</summary>
    public const string SrsHint = "DigiTower joins your SRS server as the controllers; it does not start it. Start the SRS server on this PC " +
                                  "(in single player too, with your SRS client connected to it), or check its address in Settings.";

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lock.Dispose();
    }

    private string? _voiceLabel, _aiVoiceLabel;

    // The controllers' voice on SRS: the chosen voice if its model is there, else a Windows voice.
    private SrsRadio? OpenRadio(string? data)
    {
        if (data is null || !File.Exists(Path.Combine(data, "voice", "voices.json")))
        {
            Write(LogCategory.VoiceOut, "voice: no data\\voice\\voices.json, text messages only", LogLevel.Warning);
            return null;
        }
        var catalog = VoiceCatalog.Load(Path.Combine(data, "voice"), _settings.ResolvedModelsDirectory, _settings.ResolvedVoiceCacheDirectory);
        var wanted = catalog.Find(_settings.VoiceId ?? catalog.Default);
        if (wanted is null) Write(LogCategory.VoiceOut, $"voice: unknown voice {_settings.VoiceId}", LogLevel.Warning);
        var info = wanted is not null && catalog.IsAvailable(wanted) ? wanted : catalog.Voices.FirstOrDefault(v => v.Engine == "sapi" && catalog.IsAvailable(v));
        if (info is null)
        {
            Write(LogCategory.VoiceOut, "voice: no voice available, text messages only", LogLevel.Warning);
            return null;
        }
        if (wanted is not null && info != wanted)
            Write(LogCategory.VoiceOut, $"voice: {wanted.Id} is not downloaded (Install page, or VoiceTool fetch), using {info.Id}", LogLevel.Warning);
        var parts = _settings.Srs.Split(':');
        var options = new SrsRadioOptions { Host = parts[0], Port = parts.Length > 1 && int.TryParse(parts[1], out int port) ? port : 5002, Listen = _settings.VoiceRequests };
        Write(LogCategory.VoiceOut, $"voice: {info.Label} on SRS {options.Host}:{options.Port}");
        _voiceLabel = info.Label;
        var voice = catalog.Open(info, _log.For(LogCategory.VoiceOut));
        return new SrsRadio(options, voice, _log.For(LogCategory.Srs), OpenPilotVoice(catalog, info));
    }

    // AI pilots' voice: the one chosen if downloaded, else one that is not the controllers'; null when they do not talk.
    private AtcVoice? OpenPilotVoice(VoiceCatalog catalog, VoiceInfo controllers)
    {
        _aiVoiceLabel = null;
        if (!_settings.AiVoice || !_settings.ManageAi) return null;
        var wanted = _settings.AiVoiceId is { } id ? catalog.Find(id) : null;
        if (_settings.AiVoiceId is not null && wanted is null) Write(LogCategory.VoiceOut, $"AI pilot voice: unknown voice {_settings.AiVoiceId}", LogLevel.Warning);
        var info = wanted is not null && catalog.IsAvailable(wanted) ? wanted
            : DigiTowerSettings.AiVoiceChoices.Select(catalog.Find).OfType<VoiceInfo>().FirstOrDefault(v => v.Id != controllers.Id && catalog.IsAvailable(v));
        if (info is null)
        {
            Write(LogCategory.VoiceOut, "AI pilot voice: none available apart from the controllers' one, AI pilots stay silent", LogLevel.Warning);
            return null;
        }
        if (wanted is not null && info != wanted)
            Write(LogCategory.VoiceOut, $"AI pilot voice: {wanted.Id} is not downloaded (Install page), using {info.Id}", LogLevel.Warning);
        Write(LogCategory.VoiceOut, $"AI pilot voice: {info.Label} (when DigiTower handles AI traffic)");
        _aiVoiceLabel = info.Label;
        return catalog.Open(info, _log.For(LogCategory.VoiceOut));
    }

    // Speech recognition on what the stations hear: the recognizer loads in the background.
    private Transcriber? OpenTranscriber(RecognizerCatalog catalog, SrsRadio radio)
    {
        var info = catalog.Find(_settings.Recognizer ?? catalog.Default);
        if (info is null || !catalog.IsAvailable(info))
        {
            Write(LogCategory.VoiceIn, info is null ? $"voice requests: unknown recognizer {_settings.Recognizer}"
                : $"voice requests: {info.Id} is not downloaded (Install page, or VoiceTool fetch --recognizer {info.Id})", LogLevel.Warning);
            return null;
        }
        Write(LogCategory.VoiceIn, $"voice requests: {info.Label}, loading");
        var transcriber = new Transcriber(() => catalog.Create(info), info.Id, _settings.ResolvedRecordingsDirectory, _log.For(LogCategory.VoiceIn));
        radio.Heard += transcriber.OnHeard;
        return transcriber;
    }

    private void OpenLogFile()
    {
        if (!_settings.Log.WriteFile) return;
        try
        {
            _file = new LogFile(_settings.ResolvedLogDirectory, _settings.Log.KeepDays);
            _log.Written += WriteFile;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Write(LogCategory.App, $"no log file ({ex.Message})", LogLevel.Warning);
        }
    }

    private void CloseLogFile()
    {
        _log.Written -= WriteFile;
        _file?.Dispose();
        _file = null;
    }

    private void WriteFile(LogEntry e)
    {
        if (!_settings.Log.Files(e)) return;
        try
        {
            _file?.Write(e);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // A full disk or a locked file must not stop the controllers; the line is still in the window.
        }
    }

    private void Write(LogCategory category, string text, LogLevel level = LogLevel.Info) => _log.Write(category, text, level);
}
