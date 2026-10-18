using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace DcsAtc.Srs;

public sealed record SrsOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5002;
    /// <summary>The name other SRS users see, e.g. "Al Dhafra Ground".</summary>
    public required string Name { get; init; }
    /// <summary>0 spectator, 1 red, 2 blue. With coalition security on, only this coalition hears us.</summary>
    public int Coalition { get; init; } = 2;
    /// <summary>Not a DCS unit: SRS only uses it for intercom matching.</summary>
    public uint UnitId { get; init; } = 100000;
    /// <summary>Frequencies to listen on (at most 10); players' transmissions on them arrive on <see cref="SrsClient.VoiceReceived"/>.</summary>
    public IReadOnlyList<SrsFrequency> Receive { get; init; } = [];
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Another client on the SRS server.</summary>
public sealed record SrsPeer(string Guid, string Name, int Coalition, uint UnitId, IReadOnlyList<SrsFrequency> Radios);

/// <summary>
/// A headless SRS client (a radio station): keeps a TCP sync connection and a UDP voice channel to the SRS
/// server, transmits 16 kHz mono audio as 40 ms Opus frames in real time, and reports received voice packets.
/// Reconnects when the server goes away.
/// </summary>
public sealed class SrsClient(SrsOptions options, Action<string>? log = null)
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan VoiceTimeout = TimeSpan.FromSeconds(42);

    private readonly ConcurrentDictionary<string, SrsPeer> _peers = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _transmitLock = new(1, 1);
    private NetworkStream? _stream;
    private UdpClient? _udp;
    private long _lastPong = -1;
    private volatile bool _synced;
    private long _packetNumber;
    private TaskCompletionSource _ready = NewReady();

    public SrsOptions Options => options;
    public string Guid { get; } = SrsProtocol.NewClientGuid();
    public bool IsConnected => _stream is not null;
    /// <summary>The server has our client and answers our UDP pings, so voice gets through both ways.</summary>
    public bool IsReady => _udp is not null && _lastPong >= 0 && Stopwatch.GetElapsedTime(_lastPong) < VoiceTimeout;
    public IReadOnlyDictionary<string, SrsPeer> Peers => _peers;
    public IReadOnlyDictionary<string, string> ServerSettings { get; private set; } = new Dictionary<string, string>();
    public string? ServerVersion { get; private set; }

    /// <summary>A voice packet from another client on one of our <see cref="SrsOptions.Receive"/> frequencies (or a test-frequency echo of our own).</summary>
    public event Action<SrsVoicePacket>? VoiceReceived;

    /// <summary>Connects, stays connected, and reconnects, until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        string? lastError = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or SocketException or SrsException or InvalidOperationException)
            {
                // While the server is down every attempt fails the same way: say it once.
                if (ex.Message != lastError) log?.Invoke($"SRS {options.Name}: {ex.Message}{(ServerVersion is null ? ", retrying" : "")}");
                lastError = ex.Message;
            }
            if (ServerVersion is not null) lastError = null;
            ServerVersion = null;
            try
            {
                await Task.Delay(options.ReconnectDelay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Waits until the voice channel is up.</summary>
    public async Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (IsReady) return true;
        try
        {
            await _ready.Task.WaitAsync(timeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Transmits 16 kHz mono PCM on <paramref name="frequencies"/> (all at once, like a radio with several transmitters),
    /// in real time: returns when the last frame has been sent. One transmission at a time per client.
    /// </summary>
    public async Task TransmitAsync(ReadOnlyMemory<short> pcm, IReadOnlyList<SrsFrequency> frequencies, CancellationToken ct = default)
    {
        if (frequencies.Count == 0) throw new ArgumentException("No frequency to transmit on.", nameof(frequencies));
        var frames = SrsOpus.Encode(pcm.Span);
        var freqs = frequencies.ToArray();
        await _transmitLock.WaitAsync(ct);
        try
        {
            if (!await WaitReadyAsync(TimeSpan.FromSeconds(5), ct)) throw new SrsException($"SRS {options.Name}: not connected to the SRS server.");
            using var timer = HighResolutionTimer.Begin();
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < frames.Count; i++)
            {
                var wait = TimeSpan.FromMilliseconds(i * SrsProtocol.FrameMilliseconds) - clock.Elapsed;
                if (wait > TimeSpan.FromMilliseconds(1)) await Task.Delay(wait, ct);
                var udp = _udp ?? throw new SrsException($"SRS {options.Name}: connection lost while transmitting.");
                var packet = new SrsVoicePacket(frames[i], freqs, options.UnitId, (ulong)Interlocked.Increment(ref _packetNumber), Guid);
                await udp.SendAsync(packet.Encode(), ct);
            }
        }
        finally
        {
            _transmitLock.Release();
        }
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        var address = (await Dns.GetHostAddressesAsync(options.Host, ct)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new SrsException($"no IPv4 address for {options.Host}");
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(address, options.Port, ct);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(address, options.Port);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _stream = tcp.GetStream();
        _udp = udp;
        log?.Invoke($"SRS {options.Name}: connected to {address}:{options.Port}");
        Task voice = Task.CompletedTask;
        try
        {
            await SendAsync(SrsProtocol.Message(SrsProtocol.MessageType.Sync, ClientJson()), session.Token);
            voice = Task.WhenAll(PingLoopAsync(udp, session.Token), ReceiveLoopAsync(udp, session.Token), KeepAliveAsync(session.Token));
            await ReadLoopAsync(_stream, session.Token);
            throw new SrsException("connection closed by the SRS server");
        }
        finally
        {
            session.Cancel();
            try
            {
                await voice;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
            _stream = null;
            _udp = null;
            _lastPong = -1;
            _synced = false;
            _ready = NewReady();
            _peers.Clear();
        }
    }

    private JsonObject ClientJson() => SrsProtocol.Client(Guid, options.Name, options.Coalition, options.UnitId, options.Receive);

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch (System.Text.Json.JsonException)
            {
                log?.Invoke($"SRS {options.Name}: unreadable message from the server");
                continue;
            }
            if (message is null) continue;
            var type = (SrsProtocol.MessageType)(message["MsgType"]?.GetValue<int>() ?? -1);
            switch (type)
            {
                case SrsProtocol.MessageType.Sync:
                    ServerVersion = message["Version"]?.GetValue<string>();
                    ReadSettings(message["ServerSettings"]);
                    if (message["Clients"] is JsonArray clients)
                        foreach (var c in clients) UpsertPeer(c, withRadios: true);
                    // Only now: readiness promises the client list too.
                    _synced = true;
                    log?.Invoke($"SRS {options.Name}: in sync with server {ServerVersion}, {_peers.Count} other clients");
                    break;
                case SrsProtocol.MessageType.Update:
                case SrsProtocol.MessageType.RadioUpdate:
                    ReadSettings(message["ServerSettings"]);
                    UpsertPeer(message["Client"], withRadios: type == SrsProtocol.MessageType.RadioUpdate);
                    break;
                case SrsProtocol.MessageType.ClientDisconnect:
                    if (message["Client"]?["ClientGuid"]?.GetValue<string>() is { } gone) _peers.TryRemove(gone, out _);
                    break;
                case SrsProtocol.MessageType.ServerSettings:
                    ReadSettings(message["ServerSettings"]);
                    break;
                case SrsProtocol.MessageType.VersionMismatch:
                    throw new SrsException($"the SRS server ({message["Version"]}) rejected protocol version {SrsProtocol.Version}");
            }
        }
    }

    private void ReadSettings(JsonNode? settings)
    {
        if (settings is not JsonObject o) return;
        ServerSettings = o.ToDictionary(p => p.Key, p => p.Value?.ToString() ?? "");
    }

    private void UpsertPeer(JsonNode? client, bool withRadios)
    {
        if (client?["ClientGuid"]?.GetValue<string>() is not { } guid || guid == Guid) return;
        _peers.TryGetValue(guid, out var old);
        var radioInfo = client["RadioInfo"];
        IReadOnlyList<SrsFrequency> radios = old?.Radios ?? [];
        uint unitId = old?.UnitId ?? 0;
        if (withRadios && radioInfo is not null)
        {
            unitId = radioInfo["unitId"]?.GetValue<uint>() ?? 0;
            radios = (radioInfo["radios"] as JsonArray ?? [])
                .Select(r => new SrsFrequency(r?["freq"]?.GetValue<double>() ?? 0, (SrsModulation)(r?["modulation"]?.GetValue<int>() ?? 3)))
                .Where(f => f.Modulation != SrsModulation.Disabled && f.Hz > 10000)
                .ToList();
        }
        _peers[guid] = new SrsPeer(guid, client["Name"]?.GetValue<string>() ?? old?.Name ?? "", client["Coalition"]?.GetValue<int>() ?? old?.Coalition ?? 0, unitId, radios);
    }

    /// <summary>
    /// The server answers a 22-byte GUID ping with the same bytes, and records our UDP endpoint if it already knows
    /// our client: only a pong after the SYNC reply means other clients' voice will reach us.
    /// </summary>
    private async Task PingLoopAsync(UdpClient udp, CancellationToken ct)
    {
        var ping = Encoding.ASCII.GetBytes(Guid);
        while (!ct.IsCancellationRequested)
        {
            await udp.SendAsync(ping, ct);
            // Quickly until ready, then at SRS's own pace.
            await Task.Delay(_lastPong < 0 ? TimeSpan.FromSeconds(1) : PingInterval, ct);
        }
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(ct);
            }
            catch (SocketException)
            {
                // ICMP "port unreachable" from an earlier send while the server was starting; keep listening.
                await Task.Delay(500, ct);
                continue;
            }
            var bytes = result.Buffer;
            if (bytes.Length == SrsProtocol.GuidLength)
            {
                if (!_synced) continue;
                if (_lastPong < 0) log?.Invoke($"SRS {options.Name}: voice channel ready");
                _lastPong = Stopwatch.GetTimestamp();
                _ready.TrySetResult();
            }
            else if (bytes.Length > SrsProtocol.GuidLength && SrsVoicePacket.Decode(bytes) is { } packet)
            {
                VoiceReceived?.Invoke(packet);
            }
        }
    }

    /// <summary>A periodic ping on the sync connection notices a dead server even when nothing else is sent.</summary>
    private async Task KeepAliveAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
            await SendAsync(SrsProtocol.Message(SrsProtocol.MessageType.Ping), ct);
        }
    }

    private async Task SendAsync(string line, CancellationToken ct)
    {
        var stream = _stream ?? throw new SrsException("not connected");
        var bytes = Encoding.UTF8.GetBytes(line);
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

    private static TaskCompletionSource NewReady() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class SrsException(string message) : Exception(message);

/// <summary>Raises the Windows timer resolution to 1 ms while frames are sent, so 40 ms frame timing holds.</summary>
internal sealed partial class HighResolutionTimer : IDisposable
{
    private readonly bool _active;

    private HighResolutionTimer() => _active = OperatingSystem.IsWindows() && TimeBeginPeriod(1) == 0;

    public static HighResolutionTimer Begin() => new();

    public void Dispose()
    {
        if (_active) TimeEndPeriod(1);
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint milliseconds);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint milliseconds);
}
