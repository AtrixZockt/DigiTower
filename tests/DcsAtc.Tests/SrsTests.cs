using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DcsAtc.Atc;
using DcsAtc.Host;
using DcsAtc.Srs;
using DcsAtc.Voice;

namespace DcsAtc.Tests;

[Collection(TimingCollection.Name)]
public class SrsTests
{
    [Fact]
    public void Voice_packets_round_trip_in_the_SRS_layout()
    {
        string guid = SrsProtocol.NewClientGuid();
        Assert.Equal(22, guid.Length);
        var packet = new SrsVoicePacket([1, 2, 3, 4, 5], [SrsFrequency.Mhz(250.0), SrsFrequency.Mhz(30.0, SrsModulation.FM)], 100000, 42, guid);
        var bytes = packet.Encode();

        // Header: total length, audio length, frequency part length (2 × 10 bytes).
        Assert.Equal(bytes.Length, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(5, BitConverter.ToUInt16(bytes, 2));
        Assert.Equal(20, BitConverter.ToUInt16(bytes, 4));
        Assert.Equal(6 + 5 + 20 + 4 + 8 + 1 + 22 + 22, bytes.Length);
        Assert.Equal(guid, Encoding.ASCII.GetString(bytes, bytes.Length - 22, 22));

        var back = SrsVoicePacket.Decode(bytes)!;
        Assert.Equal(packet.Audio, back.Audio);
        Assert.Equal(packet.Frequencies, back.Frequencies);
        Assert.Equal((100000u, 42ul, guid, guid), (back.UnitId, back.PacketNumber, back.SenderGuid, back.TransmissionGuid));
        Assert.Null(SrsVoicePacket.Decode(bytes.AsSpan(0, 30)));
    }

    [Fact]
    public void Client_announces_a_station_with_its_receive_radios()
    {
        var json = SrsProtocol.Client("abcdefghijklmnopqrstuv", "Al Dhafra Ground", 2, 100000, [SrsFrequency.Mhz(250.0)]);
        Assert.Equal("Al Dhafra Ground", (string?)json["Name"]);
        var radios = json["RadioInfo"]!["radios"]!.AsArray();
        Assert.Equal(SrsProtocol.MaxRadios, radios.Count);
        Assert.Equal(250e6, (double)radios[1]!["freq"]!);
        Assert.Equal((int)SrsModulation.AM, (int)radios[1]!["modulation"]!);
        Assert.Equal((int)SrsModulation.Disabled, (int)radios[2]!["modulation"]!);
    }

    [Fact]
    public async Task Client_syncs_and_transmits_in_real_time()
    {
        await using var server = new FakeSrsServer();
        var client = new SrsClient(new SrsOptions { Port = server.Port, Name = "Al Dhafra Ground", Receive = [SrsFrequency.Mhz(250.0)] });
        using var receiver = new SrsReceiver(client);
        var echo = new TaskCompletionSource<SrsTransmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.TransmissionReceived += t => echo.TrySetResult(t);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = client.RunAsync(cts.Token);

        Assert.True(await client.WaitReadyAsync(TimeSpan.FromSeconds(5)));
        var sync = server.Messages.First(m => (int?)m["MsgType"] == (int)SrsProtocol.MessageType.Sync);
        Assert.Equal("Al Dhafra Ground", (string?)sync["Client"]!["Name"]);
        Assert.Equal(client.Guid, (string?)sync["Client"]!["ClientGuid"]);
        Assert.Equal("Hangman 1-1 | DiGii", client.Peers["PLAYERGUID000000000000"].Name);

        // One second of audio: 25 frames of 40 ms, sent at the pace they play.
        var clock = Stopwatch.StartNew();
        await client.TransmitAsync(new short[16000], [SrsFrequency.Mhz(247.2)], cts.Token);
        Assert.InRange(clock.ElapsedMilliseconds, 900, 1500);
        // The server takes packets in on a loop of its own: under load the last one is still on its way when
        // TransmitAsync returns. A packet that never comes still fails below.
        var arriving = Stopwatch.StartNew();
        while (server.VoicePackets.Count < 25 && arriving.ElapsedMilliseconds < 2000) await Task.Delay(10);
        var packets = server.VoicePackets.ToList();
        Assert.Equal(25, packets.Count);
        Assert.All(packets, p => Assert.Equal((client.Guid, SrsFrequency.Mhz(247.2)), (p.SenderGuid, p.Frequencies.Single())));
        Assert.Equal(Enumerable.Range(1, 25).Select(i => (ulong)i), packets.Select(p => p.PacketNumber).Order());

        // The server echoes the test frequency back: it arrives as one transmission.
        var received = await echo.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((25, 0), (received.Packets, received.LostPackets));
        Assert.Equal(TimeSpan.FromSeconds(1), received.Duration);

        cts.Cancel();
        await run;
    }

    /// <summary>
    /// Just enough of an SRS server: answers SYNC with one other client, answers UDP pings, and sends every voice packet
    /// to every client (back to the sender too, as SRS does on its test frequencies).
    /// </summary>
    internal sealed class FakeSrsServer : IAsyncDisposable
    {
        private readonly UdpClient _udp;
        private readonly TcpListener _tcp;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _tasks = [];

        public FakeSrsServer()
        {
            // SRS uses one port number for TCP and UDP: find one that is free for both.
            while (true)
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
                try
                {
                    _tcp = new TcpListener(IPAddress.Loopback, Port);
                    _tcp.Start();
                    break;
                }
                catch (SocketException)
                {
                    _udp.Dispose();
                }
            }
            _tasks.Add(AcceptAsync());
            _tasks.Add(UdpAsync());
        }

        public int Port { get; }
        public ConcurrentQueue<JsonNode> Messages { get; } = new();
        public ConcurrentQueue<SrsVoicePacket> VoicePackets { get; } = new();
        /// <summary>Every voice packet with when it arrived (ms since the server started).</summary>
        public ConcurrentQueue<(SrsVoicePacket Packet, long At)> Arrivals { get; } = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ConcurrentDictionary<IPEndPoint, bool> _endpoints = new();

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var socket = await _tcp.AcceptTcpClientAsync(_stop.Token);
                    _tasks.Add(SessionAsync(socket));
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task SessionAsync(TcpClient socket)
        {
            using var _ = socket;
            var stream = socket.GetStream();
            using var reader = new StreamReader(stream);
            try
            {
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    var message = JsonNode.Parse(line)!;
                    Messages.Enqueue(message);
                    if ((int?)message["MsgType"] != (int)SrsProtocol.MessageType.Sync) continue;
                    var reply = new JsonObject
                    {
                        ["MsgType"] = (int)SrsProtocol.MessageType.Sync,
                        ["Version"] = "2.4.1.0",
                        ["ServerSettings"] = new JsonObject { ["TEST_FREQUENCIES"] = "247.2" },
                        ["Clients"] = new JsonArray(new JsonObject
                        {
                            ["ClientGuid"] = "PLAYERGUID000000000000",
                            ["Name"] = "Hangman 1-1 | DiGii",
                            ["Coalition"] = 2,
                            ["RadioInfo"] = new JsonObject { ["unitId"] = 16777729, ["radios"] = new JsonArray() },
                        }),
                    };
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(reply.ToJsonString() + "\n"), _stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
            }
        }

        private async Task UdpAsync()
        {
            try
            {
                while (true)
                {
                    var r = await _udp.ReceiveAsync(_stop.Token);
                    _endpoints[r.RemoteEndPoint] = true;
                    if (r.Buffer.Length > 22 && SrsVoicePacket.Decode(r.Buffer) is { } packet)
                    {
                        VoicePackets.Enqueue(packet);
                        Arrivals.Enqueue((packet, _clock.ElapsedMilliseconds));
                        foreach (var other in _endpoints.Keys) await _udp.SendAsync(r.Buffer, other, _stop.Token);
                    }
                    else await _udp.SendAsync(r.Buffer, r.RemoteEndPoint, _stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _tcp.Stop();
            _udp.Dispose();
            try
            {
                await Task.WhenAll(_tasks);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }
    }
}

/// <summary>The controllers' radio on a (fake) SRS server: nobody is stepped on, AI exchanges go in one turn.</summary>
[Collection(TimingCollection.Name)]
public class SrsRadioTests
{
    private static readonly AirfieldStations Dhafra = new() { Ground = 250, Tower = 270 };

    /// <summary>"Says" every text as a tone, 60 ms per character.</summary>
    private sealed class ToneEngine : ISpeechEngine
    {
        public AudioClip Synthesize(string text) =>
            new(Enumerable.Range(0, 16 * 60 * text.Length).Select(i => (float)(0.3 * Math.Sin(i * 0.1))).ToArray(), 16000);

        public void Dispose()
        {
        }
    }

    private static AtcVoice Voice(string id, string dir) =>
        new(new VoiceInfo { Id = id, Engine = "test" }, new ToneEngine(), Path.Combine(dir, id));

    // The transmissions as the server saw them: sender, first and last packet (ms); a pause of 300 ms ends one.
    private static List<(string Sender, long From, long To)> Transmissions(SrsTests.FakeSrsServer server)
    {
        var list = new List<(string Sender, long From, long To)>();
        foreach (var sender in server.Arrivals.GroupBy(a => a.Packet.SenderGuid))
        {
            (string Sender, long From, long To)? current = null;
            foreach (var (_, at) in sender.OrderBy(a => a.At))
            {
                if (current is { } c && at - c.To <= 300) current = c with { To = at };
                else
                {
                    if (current is { } done) list.Add(done);
                    current = (sender.Key, at, at);
                }
            }
            if (current is { } last) list.Add(last);
        }
        return [.. list.OrderBy(t => t.From)];
    }

    [Fact]
    public async Task An_ai_exchange_is_one_turn_pilot_controller_pilot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "digitower-tests", Guid.NewGuid().ToString("N"));
        await using var server = new SrsTests.FakeSrsServer();
        var radio = new SrsRadio(new SrsRadioOptions { Port = server.Port }, Voice("atc", dir), _ => { }, Voice("pilot", dir));
        try
        {
            radio.Listen("Al Dhafra", Dhafra, 2);
            await Task.Delay(1500);   // connected
            var call = new RadioCall("Al Dhafra", "Tower", 2, "Colt 2-1, cleared to land.", Dhafra)
            {
                PilotCall = "Tower, Colt 2-1, final.", PilotReadback = "Cleared to land, Colt 2-1.", ToAi = true,
            };
            radio.Transmit(call);
            radio.Transmit(new RadioCall("Al Dhafra", "Ground", 2, "Hangman 1-2, taxi.", Dhafra));
            await WaitUntilAsync(() => Transmissions(server).Count >= 4, TimeSpan.FromSeconds(20));

            var t = Transmissions(server);
            // Pilot call, the controller's answer, the readback, then the next call (on its frequency, after the exchange).
            Assert.Equal(4, t.Count);
            Assert.InRange(t[1].From - t[0].To, 800, 1600);    // the controller answers after a second
            Assert.InRange(t[2].From - t[1].To, 600, 1400);    // the pilot reads back at once
            Assert.True(t[3].From > t[2].To, "the next call waits for the exchange");
        }
        finally
        {
            await radio.DisposeAsync();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task A_station_waits_while_a_player_talks()
    {
        string dir = Path.Combine(Path.GetTempPath(), "digitower-tests", Guid.NewGuid().ToString("N"));
        await using var server = new SrsTests.FakeSrsServer();
        var radio = new SrsRadio(new SrsRadioOptions { Port = server.Port }, Voice("atc", dir), _ => { });
        var player = new SrsClient(new SrsOptions { Port = server.Port, Name = "Hangman 1-2 | DiGii", Receive = [SrsFrequency.Mhz(250)] });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = player.RunAsync(cts.Token);
        try
        {
            radio.Listen("Al Dhafra", Dhafra, 2);
            Assert.True(await player.WaitReadyAsync(TimeSpan.FromSeconds(5)));
            await Task.Delay(1000);
            // The player talks for 2 s; ATC has something to say meanwhile.
            var talking = player.TransmitAsync(new short[32000], [SrsFrequency.Mhz(250)], cts.Token);
            await Task.Delay(300);
            radio.Transmit(new RadioCall("Al Dhafra", "Ground", 2, "Hangman 1-1, hold position.", Dhafra));
            await talking;
            await WaitUntilAsync(() => Transmissions(server).Count >= 2, TimeSpan.FromSeconds(15));

            var t = Transmissions(server);
            Assert.Equal(player.Guid, t[0].Sender);
            Assert.True(t[1].From >= t[0].To + 400, $"ATC started {t[1].From - t[0].To} ms after the player stopped");
        }
        finally
        {
            await cts.CancelAsync();
            await run;
            await radio.DisposeAsync();
            Directory.Delete(dir, true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < timeout) await Task.Delay(100);
        await Task.Delay(500);
    }
}

/// <summary>Tests that measure real-time behaviour, and tests heavy enough to disturb it, run one after another.</summary>
[CollectionDefinition(Name)]
public sealed class TimingCollection
{
    public const string Name = "Timing";
}
