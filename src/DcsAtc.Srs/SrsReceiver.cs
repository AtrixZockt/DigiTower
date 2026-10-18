using System.Diagnostics;
using Concentus;

namespace DcsAtc.Srs;

/// <summary>One complete received transmission, decoded to 16 kHz mono PCM.</summary>
/// <param name="Sender">The SRS client that transmitted, if known (its name is the player name, its unit id the DCS unit).</param>
/// <param name="LostPackets">Packets missing from the sequence (concealed by the decoder).</param>
public sealed record SrsTransmission(string SenderGuid, SrsPeer? Sender, SrsFrequency Frequency, short[] Pcm, int Packets, int LostPackets, DateTimeOffset Started)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(Pcm.Length / (double)SrsProtocol.SampleRate);
}

/// <summary>
/// Assembles voice packets into transmissions: per sender, frames are decoded in packet order and a transmission
/// ends when no packet has arrived for <see cref="EndGap"/>.
/// </summary>
public sealed class SrsReceiver : IDisposable
{
    public static readonly TimeSpan EndGap = TimeSpan.FromMilliseconds(400);

    private readonly SrsClient _client;
    private readonly Dictionary<string, Pending> _pending = [];
    private readonly Lock _lock = new();
    private readonly Timer _timer;

    public SrsReceiver(SrsClient client)
    {
        _client = client;
        _client.VoiceReceived += OnPacket;
        _timer = new Timer(_ => Flush(force: false), null, 100, 100);
    }

    public event Action<SrsTransmission>? TransmissionReceived;

    public void Dispose()
    {
        _client.VoiceReceived -= OnPacket;
        _timer.Dispose();
        Flush(force: true);
    }

    private void OnPacket(SrsVoicePacket packet)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(packet.SenderGuid, out var p))
            {
                p = new Pending(packet.SenderGuid, packet.Frequencies.FirstOrDefault(), SrsOpus.CreateDecoder(), DateTimeOffset.Now);
                _pending[packet.SenderGuid] = p;
            }
            p.Add(packet);
        }
    }

    private void Flush(bool force)
    {
        List<SrsTransmission> done = [];
        lock (_lock)
        {
            foreach (var p in _pending.Values.ToList())
            {
                if (!force && Stopwatch.GetElapsedTime(p.LastPacket) < EndGap) continue;
                _pending.Remove(p.Sender);
                _client.Peers.TryGetValue(p.Sender, out var peer);
                done.Add(p.Finish(peer));
            }
        }
        foreach (var t in done) TransmissionReceived?.Invoke(t);
    }

    private sealed class Pending(string sender, SrsFrequency frequency, IOpusDecoder decoder, DateTimeOffset started)
    {
        private readonly SortedDictionary<ulong, byte[]> _frames = [];

        public string Sender => sender;
        public long LastPacket { get; private set; } = Stopwatch.GetTimestamp();

        public void Add(SrsVoicePacket packet)
        {
            _frames.TryAdd(packet.PacketNumber, packet.Audio);
            LastPacket = Stopwatch.GetTimestamp();
        }

        public SrsTransmission Finish(SrsPeer? peer)
        {
            var pcm = new List<short>();
            int lost = 0;
            ulong? previous = null;
            foreach (var (number, frame) in _frames)
            {
                // Conceal short gaps; a long gap is more likely a counter jump than lost audio.
                if (previous is { } prev && number - prev is > 1 and <= 5)
                {
                    for (ulong i = prev + 1; i < number; i++, lost++) pcm.AddRange(SrsOpus.Decode(decoder, []));
                }
                pcm.AddRange(SrsOpus.Decode(decoder, frame));
                previous = number;
            }
            return new SrsTransmission(sender, peer, frequency, pcm.ToArray(), _frames.Count, lost, started);
        }
    }
}
