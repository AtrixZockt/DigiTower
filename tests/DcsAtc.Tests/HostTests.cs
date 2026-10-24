using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DcsAtc.Host;

namespace DcsAtc.Tests;

public class HostTests
{
    private static string TempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "digitower-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Settings_round_trip_through_the_file()
    {
        string dir = TempDirectory();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            Assert.Equal(60, DigiTowerSettings.Load(path).ApproachRequestRange); // no file: the defaults

            var settings = new DigiTowerSettings
            {
                BridgePort = 42711,
                VoiceRequests = true,
                Recognizer = "whisper-turbo",
                ApproachServiceRadius = 25,
                SilenceAirbases = ["Al Minhad AFB"],
            };
            settings.Log.Hidden.Add(LogCategory.VoiceOut);
            settings.Log.KeepDays = 3;
            settings.Save(path);

            string json = File.ReadAllText(path);
            Assert.Contains("\"approachServiceRadius\": 25", json);
            Assert.Contains("\"VoiceOut\"", json); // enums as names, so the file can be edited by hand
            Assert.False(File.Exists(path + ".new"));

            var loaded = DigiTowerSettings.Load(path);
            Assert.Equal((42711, true, "whisper-turbo", 25.0, 60.0), (loaded.BridgePort, loaded.VoiceRequests, loaded.Recognizer, loaded.ApproachServiceRadius, loaded.ApproachRequestRange));
            Assert.Equal(["Al Minhad AFB"], loaded.SilenceAirbases);
            Assert.Equal([LogCategory.VoiceOut], loaded.Log.Hidden);
            Assert.Equal(3, loaded.Log.KeepDays);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Settings_file_written_by_hand_is_read_and_a_broken_one_reported()
    {
        string dir = TempDirectory();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """
                {
                  // comments and trailing commas are fine
                  "voice": false,
                  "log": { "hidden": ["Atis", "Srs"], },
                }
                """);
            var s = DigiTowerSettings.Load(path);
            Assert.False(s.Voice);
            Assert.Equal([LogCategory.Atis, LogCategory.Srs], s.Log.Hidden.Order());
            Assert.Equal(42700, s.BridgePort); // the rest: defaults

            File.WriteAllText(path, "{ \"voice\": ");
            var ex = Assert.Throws<InvalidDataException>(() => DigiTowerSettings.Load(path));
            Assert.Contains("settings.json", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Clone_is_a_separate_copy()
    {
        var s = new DigiTowerSettings();
        var copy = s.Clone();
        copy.Log.Hidden.Add(LogCategory.Ground);
        copy.SilenceAirbases.Add("Khasab");
        Assert.Empty(s.Log.Hidden);
        Assert.Empty(s.SilenceAirbases);
    }

    [Fact]
    public void Log_settings_choose_what_is_shown_and_written()
    {
        var log = new LogSettings { Hidden = [LogCategory.VoiceOut], NotInFile = [LogCategory.Srs] };
        var at = DateTime.Now;
        var voiceOut = new LogEntry(at, LogCategory.VoiceOut, LogLevel.Info, "prepared 12 clips");
        var srs = new LogEntry(at, LogCategory.Srs, LogLevel.Info, "connected");
        var debug = new LogEntry(at, LogCategory.Ground, LogLevel.Debug, "status");

        Assert.False(log.Shows(voiceOut));
        Assert.True(log.Files(voiceOut));
        Assert.True(log.Shows(srs));
        Assert.False(log.Files(srs));
        Assert.False(log.Shows(debug));
        log.Debug = true;
        Assert.True(log.Shows(debug));
        log.WriteFile = false;
        Assert.False(log.Files(voiceOut));
    }

    [Fact]
    public void Log_keeps_the_latest_entries_and_tells_listeners()
    {
        var log = new AtcLog();
        var heard = new List<LogEntry>();
        log.Written += heard.Add;
        log.For(LogCategory.Tower)("Viper 1-1 cleared for takeoff");
        for (int i = 0; i < AtcLog.Capacity + 10; i++) log.Write(LogCategory.Ground, $"line {i}", LogLevel.Debug);

        Assert.Equal((LogCategory.Tower, LogLevel.Info, "Viper 1-1 cleared for takeoff"), (heard[0].Category, heard[0].Level, heard[0].Text));
        var recent = log.Recent();
        Assert.Equal(AtcLog.Capacity, recent.Count);
        Assert.Equal($"line {AtcLog.Capacity + 9}", recent[^1].Text);
        Assert.Equal("VOICE IN", AtcLog.Label(LogCategory.VoiceIn));
    }

    [Fact]
    public void Log_file_writes_one_file_a_day_and_deletes_old_ones()
    {
        string dir = TempDirectory();
        try
        {
            var today = new DateTime(2026, 10, 20, 14, 5, 9);
            foreach (var days in new[] { 1, 14, 15, 40 })
                File.WriteAllText(Path.Combine(dir, LogFile.NameFor(today.AddDays(-days))), "old");
            File.WriteAllText(Path.Combine(dir, "notes.log"), "not ours");

            using (var file = new LogFile(dir, 14, () => today))
            {
                file.Write(new LogEntry(today, LogCategory.Tower, LogLevel.Info, "Viper 1-1 cleared for takeoff"));
                file.Write(new LogEntry(today, LogCategory.VoiceIn, LogLevel.Warning, "not understood"));
                file.Write(new LogEntry(today.AddDays(1).Date, LogCategory.App, LogLevel.Info, "past midnight"));
            }

            var names = Directory.GetFiles(dir).Select(Path.GetFileName).Order().ToList();
            Assert.Equal(["digitower-2026-10-06.log", "digitower-2026-10-19.log", "digitower-2026-10-20.log", "digitower-2026-10-21.log", "notes.log"], names);
            Assert.Equal(
                ["14:05:09  TOWER      Viper 1-1 cleared for takeoff", "14:05:09  VOICE IN   WARNING  not understood"],
                File.ReadAllLines(Path.Combine(dir, "digitower-2026-10-20.log")));
            Assert.Equal(["00:00:00  DIGITOWER  past midnight"], File.ReadAllLines(Path.Combine(dir, "digitower-2026-10-21.log")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// The whole host against the real hook in a fake DCS: it connects, the player's startup request shows up as a strip,
    /// the lights say so, and stopping clears it all.
    /// </summary>
    [SkippableFact]
    public async Task Host_runs_the_controllers_and_shows_their_strips()
    {
        string? lua = TestPaths.FindLua();
        Skip.If(lua is null, "Lua 5.1 with LuaSocket not found (set DIGITOWER_LUA)");
        Skip.If(TestPaths.Terrains is null, "DCS not installed (set DIGITOWER_DCS_DIR)");
        int port = FreePort();
        string logs = TempDirectory();
        string script = Path.Combine(TestPaths.RepoRoot, "bridge", "tests", "fake_dcs.lua");
        using var fake = Process.Start(new ProcessStartInfo(lua!, $"\"{script}\" --port {port} --duration 60 --static --click \"Request startup\" --quiet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            WorkingDirectory = TestPaths.RepoRoot,
        })!;
        var log = new AtcLog();
        await using var host = new AtcHost(log);
        try
        {
            var settings = new DigiTowerSettings
            {
                BridgePort = port,
                Voice = false,
                DataDirectory = Path.Combine(TestPaths.RepoRoot, "data"),
                DcsDirectory = Path.GetDirectoryName(Path.GetDirectoryName(TestPaths.Terrains))!,
                Log = { Directory = logs },
            };
            var startup = new TaskCompletionSource<AtcSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.SnapshotChanged += s =>
            {
                if (s.Airfields.Any(f => f.Ground.Any(st => st.Clearance == "Start up approved"))) startup.TrySetResult(s);
            };
            int changes = 0;
            host.RunningChanged += () => changes++;

            await host.StartAsync(settings);
            Assert.True(host.Running);
            Assert.NotNull(host.StartedAt);

            var snapshot = await startup.Task.WaitAsync(TimeSpan.FromSeconds(40));
            Assert.True(snapshot.Connected);
            var dhafra = Assert.Single(snapshot.Airfields, f => f.Name == "Al Dhafra AFB");
            var strip = Assert.Single(dhafra.Ground);
            Assert.Equal((StripKind.Departure, false), (strip.Kind, strip.Ai));
            Assert.NotNull(dhafra.GroundFrequency);

            var lights = host.Status().ToDictionary(l => l.Label);
            Assert.Equal(Light.On, lights["DCS BRIDGE"].State);
            Assert.Equal(Light.Off, lights["VOICE"].State);
            Assert.Equal(Light.Off, lights["SRS"].State);
            Assert.Contains(log.Recent(), e => e.Category == LogCategory.Ground);
            Assert.True(File.Exists(host.LogFilePath));

            await host.StopAsync();
            Assert.False(host.Running);
            Assert.Same(AtcSnapshot.Empty, host.Snapshot);
            Assert.Equal(Light.Off, host.Status().Single(l => l.Label == "DCS BRIDGE").State);
            Assert.Equal(2, changes);
            Assert.Contains(File.ReadAllLines(Directory.GetFiles(logs).Single()), l => l.Contains("GROUND"));
        }
        finally
        {
            await host.StopAsync();
            if (!fake.HasExited) fake.Kill();
            Directory.Delete(logs, true);
        }
    }

    /// <summary>
    /// Two AI F-16s come back to Al Dhafra in the fake DCS (their mission routes land there) while a player is parked there:
    /// the first comes straight in, the second holds over the TACAN behind it, both shown on Approach's strips.
    /// </summary>
    [SkippableFact]
    public async Task Host_takes_ai_arrivals_through_the_bridge()
    {
        string? lua = TestPaths.FindLua();
        Skip.If(lua is null, "Lua 5.1 with LuaSocket not found (set DIGITOWER_LUA)");
        Skip.If(TestPaths.Terrains is null, "DCS not installed (set DIGITOWER_DCS_DIR)");
        int port = FreePort();
        string script = Path.Combine(TestPaths.RepoRoot, "bridge", "tests", "fake_dcs.lua");
        using var fake = Process.Start(new ProcessStartInfo(lua!, $"\"{script}\" --port {port} --duration 60 --static --arrival --quiet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            WorkingDirectory = TestPaths.RepoRoot,
        })!;
        var log = new AtcLog();
        await using var host = new AtcHost(log);
        try
        {
            var holding = new TaskCompletionSource<AtcSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.SnapshotChanged += s =>
            {
                if (s.Airfields.Any(f => f.Approach.Any(st => st.Ai && st.Clearance.StartsWith("Holding", StringComparison.Ordinal)))) holding.TrySetResult(s);
            };
            await host.StartAsync(new DigiTowerSettings
            {
                BridgePort = port,
                Voice = false,
                DataDirectory = Path.Combine(TestPaths.RepoRoot, "data"),
                DcsDirectory = Path.GetDirectoryName(Path.GetDirectoryName(TestPaths.Terrains))!,
                Log = { WriteFile = false },
            });

            var snapshot = await holding.Task.WaitAsync(TimeSpan.FromSeconds(40));
            var approach = snapshot.Airfields.Single(f => f.Name == "Al Dhafra AFB").Approach;
            Assert.Contains(approach, st => st is { Callsign: "Colt 1-1", Clearance: "Straight in", Ai: true });
            Assert.Contains(approach, st => st is { Callsign: "Colt 2-1", Clearance: "Holding at 7000 ft", Note: "Colt 1-1 on approach" });
            Assert.Contains(log.Recent(), e => e.Category == LogCategory.Ai && e.Text.EndsWith("Hold over the Al Dhafra TACAN, right turns, maintain 7000 feet. Expect further clearance in 5 minutes.", StringComparison.Ordinal));
        }
        finally
        {
            await host.StopAsync();
            if (!fake.HasExited) fake.Kill();
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

public class ModelStoreTests
{
    [Fact]
    public void Lists_the_voice_and_recognition_models_with_their_use()
    {
        var settings = new DigiTowerSettings { DataDirectory = Path.Combine(TestPaths.RepoRoot, "data") };
        Assert.Equal(Path.Combine(TestPaths.RepoRoot, "data", "voice", "models"), settings.ResolvedModelsDirectory); // a developer build: as before
        var entries = ModelStore.Load(settings).Entries();

        var kokoro = Assert.Single(entries, e => e.Name == "kokoro-multi-lang-v1_0");
        Assert.Equal((ModelKind.Voice, true, true), (kokoro.Kind, kokoro.IsArchive, kokoro.Url is not null));
        Assert.StartsWith("10 voices (Michael, Adam", kokoro.Use);
        var turbo = Assert.Single(entries, e => e.Name == "ggml-large-v3-turbo-q5_0");
        Assert.Equal((ModelKind.Recognition, false, 574), (turbo.Kind, turbo.IsArchive, turbo.SizeMb));
        Assert.Equal("Voice requests: Whisper large-v3-turbo", turbo.Use);
        Assert.EndsWith(Path.Combine("voice", "models", "ggml-large-v3-turbo-q5_0.bin"), turbo.Path);
    }

    [Fact]
    public void Installed_programs_keep_models_and_recordings_outside_the_program_folder()
    {
        string data = Path.Combine(Path.GetTempPath(), "digitower-tests", Guid.NewGuid().ToString("N"), "app", "data");
        Directory.CreateDirectory(Path.Combine(data, "airfields"));
        try
        {
            var settings = new DigiTowerSettings { DataDirectory = data };
            Assert.Equal(Path.Combine(DigiTowerSettings.LocalDirectory, "models"), settings.ResolvedModelsDirectory);
            Assert.Equal(Path.Combine(DigiTowerSettings.LocalDirectory, "heard"), settings.ResolvedRecordingsDirectory);
            Assert.Equal(Path.Combine(DigiTowerSettings.LocalDirectory, "logs"), settings.ResolvedLogDirectory);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(data))!, true);
        }
    }

    [Fact]
    public async Task Downloads_a_model_file_into_place()
    {
        string dir = Path.Combine(Path.GetTempPath(), "digitower-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var body = new byte[300_000];
        new Random(7).NextBytes(body);
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        int port = ((IPEndPoint)server.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var request = new byte[4096];
            await stream.ReadAtLeastAsync(request, 1, throwOnEndOfStream: false);
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(body);
        });
        try
        {
            var model = new ModelEntry(ModelKind.Recognition, "ggml-test", "test", "MIT", 1, $"http://127.0.0.1:{port}/ggml-test.bin", false,
                Path.Combine(dir, "ggml-test.bin"), false, false);
            var reports = new List<double>();
            await ModelStore.DownloadAsync(model, new Progress<double>(reports.Add));
            await serve;
            Assert.Equal(body, File.ReadAllBytes(model.Path));
            Assert.Equal(["ggml-test.bin"], Directory.GetFileSystemEntries(dir).Select(Path.GetFileName)); // no staging folder left
            ModelStore.Remove(model with { Present = true });
            Assert.Empty(Directory.GetFileSystemEntries(dir));
        }
        finally
        {
            server.Stop();
            Directory.Delete(dir, true);
        }
    }

    // A pilot who only runs the SRS client got a green "Ready" and no voice: the probe tries the server itself.
    [Fact]
    public async Task Srs_probe_tells_a_running_server_from_none()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int open = ((IPEndPoint)listener.LocalEndpoint).Port;
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        int none = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        try
        {
            Assert.True(await SrsProbe.TryConnectAsync("127.0.0.1", open, TimeSpan.FromSeconds(2)));
            Assert.False(await SrsProbe.TryConnectAsync("127.0.0.1", none, TimeSpan.FromSeconds(2)));

            using var up = new SrsProbe($"127.0.0.1:{open}");
            using var down = new SrsProbe($"127.0.0.1:{none}");
            var until = DateTime.UtcNow.AddSeconds(5);
            while ((up.Reachable is null || down.Reachable is null) && DateTime.UtcNow < until) await Task.Delay(50);
            Assert.True(up.Reachable);
            Assert.False(down.Reachable);
            // Known to answer (a controller is connected): no try at all.
            using var known = new SrsProbe($"127.0.0.1:{none}", () => true);
            until = DateTime.UtcNow.AddSeconds(5);
            while (known.Reachable is null && DateTime.UtcNow < until) await Task.Delay(50);
            Assert.True(known.Reachable);
        }
        finally
        {
            listener.Stop();
        }
        Assert.Equal(("srs.example", 5002), SrsProbe.Parse("srs.example"));
        Assert.Equal(("10.0.0.2", 5010), SrsProbe.Parse("10.0.0.2:5010"));
    }

    [Fact]
    public void Getting_started_lists_what_is_missing_and_ends_when_the_essentials_are_there()
    {
        var none = new ModelStore(null, null);
        // Text only: just the bridge.
        var textOnly = new DigiTowerSettings { Voice = false };
        Assert.False(GettingStarted.Complete(GettingStarted.Steps(textOnly, bridgeInstalled: false, srsReachable: null, none)));
        Assert.True(GettingStarted.Complete(GettingStarted.Steps(textOnly, bridgeInstalled: true, srsReachable: null, none)));

        // With a voice: the voice and a running SRS server too; voice requests are optional.
        var steps = GettingStarted.Steps(new DigiTowerSettings(), bridgeInstalled: true, srsReachable: false, none);
        Assert.Equal(["Install the bridge into DCS", "Download the controllers' voice", "Start your SRS server", "Talk to the controllers (optional)"],
            steps.Select(s => s.Title));
        Assert.Contains("No SRS server answers at 127.0.0.1:5002", steps[2].Hint);
        Assert.Contains("does not start it", steps[2].Hint);
        Assert.False(GettingStarted.Complete(steps));
        Assert.True(GettingStarted.Complete(steps.Select(s => s.Optional ? s : s with { Done = true })));
        Assert.True(steps[3].Optional);
    }
}
