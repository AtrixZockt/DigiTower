using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace DcsAtc.Bridge;

public sealed record BridgeOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 42700;
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>False: nothing is drawn on the F10 map (DCS shows drawings to a whole side, not one player).</summary>
    public bool MapDrawings { get; init; } = true;
}

/// <summary>A command to the bridge failed on the DCS side.</summary>
public sealed class BridgeCommandException(string message) : Exception(message);

/// <summary>
/// Connects to the DCS bridge hook (newline-delimited JSON over TCP), reconnects when the
/// connection drops, and publishes everything DCS sends on <see cref="Messages"/>.
/// </summary>
public sealed class BridgeClient : IAsyncDisposable, IDcsCommands
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private readonly BridgeOptions _options;
    private readonly Channel<BridgeMessage> _messages = Channel.CreateUnbounded<BridgeMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NetworkStream? _stream;
    private int _nextRequestId;

    public BridgeClient(BridgeOptions? options = null) => _options = options ?? new BridgeOptions();

    public ChannelReader<BridgeMessage> Messages => _messages.Reader;

    public bool IsConnected => _stream is not null;

    /// <summary>Connects, reads until the connection drops, and reconnects, until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var tcp = new TcpClient { NoDelay = true };
            try
            {
                await tcp.ConnectAsync(_options.Host, _options.Port, ct);
            }
            catch (SocketException)
            {
                await DelayAsync(_options.ReconnectDelay, ct);
                continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _stream = tcp.GetStream();
            Publish(new ConnectionChangedMessage(true));
            string reason = "closed by DCS";
            try
            {
                await ReadLoopAsync(_stream, ct);
            }
            catch (OperationCanceledException)
            {
                reason = "stopped";
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                reason = ex.Message;
            }
            finally
            {
                _stream = null;
                FailPending(reason);
                Publish(new ConnectionChangedMessage(false, reason));
            }
            await DelayAsync(_options.ReconnectDelay, ct);
        }
        _messages.Writer.TryComplete();
    }

    /// <summary>Sends a command without waiting for a reply.</summary>
    public async Task SendAsync(JsonObject command, CancellationToken ct = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected to DCS.");
        var bytes = Encoding.UTF8.GetBytes(command.ToJsonString() + "\n");
        await _writeLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(bytes, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Sends a command and waits for its reply. Returns the reply's data, if any.</summary>
    public async Task<JsonElement?> RequestAsync(JsonObject command, CancellationToken ct = default)
    {
        int req = Interlocked.Increment(ref _nextRequestId);
        command["req"] = req;
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[req] = tcs;
        try
        {
            await SendAsync(command, ct);
            return await tcs.Task.WaitAsync(_options.RequestTimeout, ct);
        }
        finally
        {
            _pending.TryRemove(req, out _);
        }
    }

    public Task<JsonElement?> PingAsync(CancellationToken ct = default) =>
        RequestAsync(new JsonObject { ["cmd"] = "ping" }, ct);

    /// <summary>Shows a text message to one group (top right of the screen).</summary>
    public Task ShowTextAsync(int groupId, string text, int seconds = 10, bool clear = false, CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["cmd"] = "text", ["groupId"] = groupId, ["text"] = text, ["seconds"] = seconds, ["clear"] = clear }, ct);

    /// <summary>Adds (or replaces) an F10 menu entry for a group. Selecting it produces a <see cref="MenuSelectedMessage"/> with <paramref name="id"/>.</summary>
    public Task AddMenuAsync(int groupId, IReadOnlyList<string> path, string label, string id, CancellationToken ct = default) =>
        SendAsync(new JsonObject
        {
            ["cmd"] = "menu_add",
            ["groupId"] = groupId,
            ["path"] = new JsonArray(path.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
            ["label"] = label,
            ["id"] = id,
        }, ct);

    public Task RemoveMenuAsync(int groupId, string id, CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["cmd"] = "menu_remove", ["groupId"] = groupId, ["id"] = id }, ct);

    public Task ClearMenusAsync(int groupId, CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["cmd"] = "menu_clear", ["groupId"] = groupId }, ct);

    public async Task<IReadOnlyList<AirbaseSummary>> GetAirbasesAsync(CancellationToken ct = default)
    {
        var data = await RequestAsync(new JsonObject { ["cmd"] = "airbase_list" }, ct);
        return data is { ValueKind: JsonValueKind.Array } d ? d.Deserialize<List<AirbaseSummary>>(Json) ?? [] : [];
    }

    /// <summary>Every airdrome's ATC frequencies as DCS's briefing reads them (answered by the hook, from the terrain).</summary>
    public async Task<IReadOnlyList<AirbaseRadios>> GetAirbaseRadiosAsync(CancellationToken ct = default)
    {
        var data = await RequestAsync(new JsonObject { ["cmd"] = "airbase_radios" }, ct);
        if (data is not { ValueKind: JsonValueKind.Array } list) return [];
        // Read by hand: Lua sends an empty frequency list as {}.
        return list.EnumerateArray().Select(a => new AirbaseRadios
        {
            Id = a.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
            Name = a.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : "",
            Frequencies = a.TryGetProperty("frequencies", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().Select(x => new AtcFrequency(x.GetProperty("hz").GetDouble(), x.TryGetProperty("modulation", out var m) ? m.GetInt32() : 0)).ToList()
                : [],
        }).ToList();
    }

    /// <summary>The FARPs of the mission file, with the frequency and callsign the mission gives them and their pads.</summary>
    public async Task<IReadOnlyList<FarpInfo>> GetFarpsAsync(CancellationToken ct = default)
    {
        var data = await RequestAsync(new JsonObject { ["cmd"] = "farp_list" }, ct);
        return data is { ValueKind: JsonValueKind.Array } d ? d.Deserialize<List<FarpInfo>>(Json) ?? [] : [];
    }

    public async Task<AirbaseInfo?> GetAirbaseAsync(string name, CancellationToken ct = default)
    {
        var data = await RequestAsync(new JsonObject { ["cmd"] = "airbase_info", ["name"] = name }, ct);
        return data?.Deserialize<AirbaseInfo>(Json);
    }

    public async Task<AirbaseWeather?> GetAirbaseWeatherAsync(string name, CancellationToken ct = default)
    {
        var data = await RequestAsync(new JsonObject { ["cmd"] = "airbase_weather", ["name"] = name }, ct);
        return data?.Deserialize<AirbaseWeather>(Json);
    }

    public async Task<MissionWeather?> GetMissionWeatherAsync(CancellationToken ct = default)
    {
        var data = await RequestAsync(new JsonObject { ["cmd"] = "mission_weather" }, ct);
        return data?.Deserialize<MissionWeather>(Json);
    }

    /// <summary>
    /// Draws a polyline on the F10 map for a coalition (-1 = all), optionally with a text at its end.
    /// Drawing again with the same <paramref name="key"/> replaces the previous drawing.
    /// </summary>
    public Task DrawRouteAsync(string key, int coalition, IEnumerable<(double X, double Z)> points, double[] rgba, string? text = null, CancellationToken ct = default)
    {
        if (!_options.MapDrawings) return Task.CompletedTask;
        var cmd = new JsonObject
        {
            ["cmd"] = "draw_route",
            ["key"] = key,
            ["coalition"] = coalition,
            ["points"] = new JsonArray(points.Select(p => (JsonNode?)new JsonArray(Math.Round(p.X, 1), Math.Round(p.Z, 1))).ToArray()),
            ["color"] = new JsonArray(rgba.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
        };
        if (text is not null) cmd["text"] = text;
        return SendAsync(cmd, ct);
    }

    /// <summary>
    /// A pin with a label on the F10 map meant only for one group (DCS markToGroup); removed with <see cref="ClearDrawingAsync"/>.
    /// For testing whether DCS really keeps it from other players.
    /// </summary>
    public Task MarkGroupAsync(string key, int groupId, double x, double z, string text, CancellationToken ct = default) =>
        RequestAsync(new JsonObject { ["cmd"] = "mark_group", ["key"] = key, ["groupId"] = groupId, ["x"] = Math.Round(x, 1), ["z"] = Math.Round(z, 1), ["text"] = text }, ct);

    public Task ClearDrawingAsync(string key, CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["cmd"] = "drawing_clear", ["key"] = key }, ct);

    /// <summary>
    /// Holds an AI group at its parking spots: DCS spawns it again from the mission file, uncontrolled (engines off).
    /// Fails (<see cref="BridgeCommandException"/>) for groups that are not in the mission file.
    /// </summary>
    public Task HoldGroupAsync(string group, CancellationToken ct = default) =>
        RequestAsync(new JsonObject { ["cmd"] = "group_hold", ["group"] = group }, ct);

    /// <summary>Releases a held AI group (DCS's Start command: it starts its engines and taxis).</summary>
    public Task ReleaseGroupAsync(string group, CancellationToken ct = default) =>
        RequestAsync(new JsonObject { ["cmd"] = "group_release", ["group"] = group }, ct);

    public async Task<GroupDestination> GetGroupDestinationAsync(string group, CancellationToken ct = default)
    {
        var data = await RequestAsync(new JsonObject { ["cmd"] = "group_destination", ["group"] = group }, ct);
        return data?.Deserialize<GroupDestination>(Json) ?? new GroupDestination();
    }

    public Task HoldArrivalAsync(string group, string flag, int airdromeId, HoldingFix hold, (double X, double Z, double Alt, double Speed) final,
        (double X, double Z, double Alt) land, CancellationToken ct = default)
    {
        static JsonNode N(double v) => JsonValue.Create(Math.Round(v, 1));
        var fix = new JsonObject { ["x"] = N(hold.X), ["z"] = N(hold.Z), ["alt"] = N(hold.Alt), ["speed"] = N(hold.Speed) };
        if (hold.X2 is double x2 && hold.Z2 is double z2)
        {
            fix["x2"] = N(x2);
            fix["z2"] = N(z2);
        }
        return RequestAsync(new JsonObject
        {
            ["cmd"] = "arrival_hold",
            ["group"] = group,
            ["flag"] = flag,
            ["airdromeId"] = airdromeId,
            ["hold"] = fix,
            ["final"] = new JsonObject { ["x"] = N(final.X), ["z"] = N(final.Z), ["alt"] = N(final.Alt), ["speed"] = N(final.Speed) },
            ["land"] = new JsonObject { ["x"] = N(land.X), ["z"] = N(land.Z), ["alt"] = N(land.Alt) },
        }, ct);
    }

    public Task ClearArrivalAsync(string flag, CancellationToken ct = default) =>
        RequestAsync(new JsonObject { ["cmd"] = "arrival_clear", ["flag"] = flag }, ct);

    /// <summary>Silences (or restores) the stock DCS ATC of an airbase.</summary>
    public async Task<bool> SetRadioSilentAsync(string name, bool silent, CancellationToken ct = default)
    {
        var data = await RequestAsync(new JsonObject { ["cmd"] = "radio_silent", ["name"] = name, ["silent"] = silent }, ct);
        return data is { } d && d.TryGetProperty("silent", out var s) && s.ValueKind == JsonValueKind.True;
    }

    public ValueTask DisposeAsync()
    {
        _messages.Writer.TryComplete();
        FailPending("disposed");
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                Dispatch(doc.RootElement);
            }
            catch (JsonException ex)
            {
                Publish(new BridgeErrorMessage { Error = $"Invalid JSON from DCS: {ex.Message}" });
            }
        }
    }

    internal void Dispatch(JsonElement msg)
    {
        string? type = msg.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "hello":
                Publish(msg.Deserialize<HelloMessage>(Json)!);
                break;
            case "mission_start":
                Publish(msg.Deserialize<MissionStartedMessage>(Json)!);
                break;
            case "mission_stop":
                Publish(new MissionStoppedMessage());
                break;
            case "poll":
                if (msg.TryGetProperty("msgs", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var inner in msgs.EnumerateArray()) Dispatch(inner);
                }
                if (msg.TryGetProperty("units", out var units) && units.ValueKind == JsonValueKind.Array)
                {
                    double time = msg.TryGetProperty("t", out var pt) ? pt.GetDouble() : 0;
                    Publish(new UnitsMessage(time, units.Deserialize<List<UnitState>>(Json) ?? []));
                }
                break;
            case "event":
                Publish(msg.Deserialize<DcsEventMessage>(Json)!);
                break;
            case "menu":
                Publish(msg.Deserialize<MenuSelectedMessage>(Json)!);
                break;
            case "reply":
                CompleteRequest(msg);
                break;
            case "error":
                Publish(msg.Deserialize<BridgeErrorMessage>(Json)!);
                break;
        }
    }

    private void CompleteRequest(JsonElement reply)
    {
        if (!reply.TryGetProperty("req", out var r) || !r.TryGetInt32(out int req)) return;
        if (!_pending.TryRemove(req, out var tcs)) return;
        bool ok = reply.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
        if (ok)
        {
            tcs.TrySetResult(reply.TryGetProperty("data", out var data) ? data.Clone() : null);
        }
        else
        {
            string error = reply.TryGetProperty("error", out var e) ? e.GetString() ?? "unknown error" : "unknown error";
            tcs.TrySetException(new BridgeCommandException(error));
        }
    }

    private void FailPending(string reason)
    {
        foreach (var (req, tcs) in _pending)
        {
            if (_pending.TryRemove(req, out _)) tcs.TrySetException(new BridgeCommandException($"Connection to DCS lost: {reason}"));
        }
    }

    private void Publish(BridgeMessage message) => _messages.Writer.TryWrite(message);

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
