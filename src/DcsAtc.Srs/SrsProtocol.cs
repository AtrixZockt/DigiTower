using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace DcsAtc.Srs;

/// <summary>SRS radio modulations (values as on the wire).</summary>
public enum SrsModulation : byte
{
    AM = 0,
    FM = 1,
    Intercom = 2,
    Disabled = 3,
    HaveQuick = 4,
    Satcom = 5,
    Mids = 6,
    Sincgars = 7,
}

/// <summary>A radio frequency in Hz and its modulation.</summary>
public readonly record struct SrsFrequency(double Hz, SrsModulation Modulation = SrsModulation.AM)
{
    public static SrsFrequency Mhz(double mhz, SrsModulation modulation = SrsModulation.AM) => new(Math.Round(mhz * 1e6), modulation);

    /// <summary>SRS treats frequencies within 500 Hz as the same channel.</summary>
    public bool SameChannel(SrsFrequency other) => Modulation == other.Modulation && Math.Abs(Hz - other.Hz) < 500;

    public override string ToString() => $"{Hz / 1e6:0.000} {Modulation}";
}

/// <summary>
/// Wire format of the SRS 2.4.1 protocol (from ciribob/DCS-SimpleRadioStandalone, Common/Models):
/// TCP carries newline-delimited JSON <c>NetworkMessage</c>s, UDP carries 22-byte pings and voice packets.
/// </summary>
public static class SrsProtocol
{
    /// <summary>The version we announce. The server accepts clients from protocol version 1.9.0.0 on.</summary>
    public const string Version = "2.4.1.0";

    public const int SampleRate = 16000;
    public const int FrameMilliseconds = 40;
    public const int FrameSamples = SampleRate / 1000 * FrameMilliseconds;
    public const int GuidLength = 22;
    public const int MaxRadios = 11;

    public enum MessageType
    {
        Update = 0,
        Ping = 1,
        Sync = 2,
        RadioUpdate = 3,
        ServerSettings = 4,
        ClientDisconnect = 5,
        VersionMismatch = 6,
        ExternalAwacsModePassword = 7,
        ExternalAwacsModeDisconnect = 8,
    }

    /// <summary>A 22-character URL-safe base64 GUID, as SRS uses for client ids.</summary>
    public static string NewClientGuid() =>
        Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Replace('/', '_').Replace('+', '-')[..GuidLength];

    /// <summary>The <c>SRClientBase</c> JSON for a station listening on <paramref name="receive"/> (at most 10 frequencies).</summary>
    public static JsonObject Client(string guid, string name, int coalition, uint unitId, IReadOnlyList<SrsFrequency> receive)
    {
        if (receive.Count > MaxRadios - 1) throw new ArgumentException($"SRS clients have at most {MaxRadios - 1} radios.", nameof(receive));
        var radios = new JsonArray();
        for (int i = 0; i < MaxRadios; i++)
        {
            // Radio 0 is the intercom; the others listen on the station's frequencies.
            var f = i >= 1 && i <= receive.Count ? receive[i - 1] : (SrsFrequency?)null;
            radios.Add(new JsonObject
            {
                ["enc"] = false,
                ["encKey"] = 0,
                ["freq"] = f?.Hz ?? 1,
                ["modulation"] = (int)(f?.Modulation ?? SrsModulation.Disabled),
                ["retransmit"] = false,
                ["secFreq"] = 1,
                ["IntercomUnitId"] = 0,
                ["Model"] = "",
                ["Name"] = f is null ? "" : $"atc{i}",
            });
        }
        return new JsonObject
        {
            ["ClientGuid"] = guid,
            ["Name"] = name,
            ["Coalition"] = coalition,
            ["AllowRecord"] = false,
            ["Seat"] = 0,
            ["RadioInfo"] = new JsonObject
            {
                ["ambient"] = new JsonObject { ["abType"] = "", ["vol"] = 0 },
                ["iff"] = new JsonObject { ["control"] = 2, ["mic"] = -1, ["mode1"] = -1, ["mode2"] = -1, ["mode3"] = -1, ["mode4"] = false, ["status"] = 0 },
                ["radios"] = radios,
                ["unit"] = name,
                ["unitId"] = unitId,
            },
            // No position: SRS then never cuts us off by distance or line of sight.
            ["LatLngPosition"] = new JsonObject { ["alt"] = 0, ["lat"] = 0, ["lng"] = 0 },
            ["Gateway"] = false,
            ["DISEntityId"] = -1,
            ["GatewayClient"] = false,
        };
    }

    public static string Message(MessageType type, JsonObject? client = null)
    {
        var message = new JsonObject { ["MsgType"] = (int)type, ["Version"] = Version };
        if (client is not null) message["Client"] = client;
        return message.ToJsonString() + "\n";
    }
}

/// <summary>
/// One UDP voice packet: 40 ms of Opus audio for one or more frequencies.
/// Layout: u16 packet length, u16 audio length, u16 frequency-part length, audio, per frequency (f64 Hz, u8 modulation,
/// u8 encryption), u32 unit id, u64 packet number, u8 retransmission count, 22-byte transmission GUID, 22-byte sender GUID.
/// All little-endian.
/// </summary>
public sealed record SrsVoicePacket(
    byte[] Audio,
    SrsFrequency[] Frequencies,
    uint UnitId,
    ulong PacketNumber,
    string SenderGuid,
    string? TransmissionGuid = null,
    byte[]? Encryptions = null,
    byte Retransmissions = 0)
{
    private const int HeaderLength = 6;
    private const int FrequencyLength = 10;
    private const int FixedLength = 4 + 8 + 1 + SrsProtocol.GuidLength * 2;

    public byte[] Encode()
    {
        int frequencyPart = Frequencies.Length * FrequencyLength;
        int total = HeaderLength + Audio.Length + frequencyPart + FixedLength;
        var bytes = new byte[total];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)total);
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)Audio.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)frequencyPart);
        Audio.CopyTo(span[HeaderLength..]);
        int o = HeaderLength + Audio.Length;
        for (int i = 0; i < Frequencies.Length; i++, o += FrequencyLength)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(span[o..], Frequencies[i].Hz);
            span[o + 8] = (byte)Frequencies[i].Modulation;
            span[o + 9] = Encryptions is { } e && i < e.Length ? e[i] : (byte)0;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(span[o..], UnitId);
        BinaryPrimitives.WriteUInt64LittleEndian(span[(o + 4)..], PacketNumber);
        span[o + 12] = Retransmissions;
        Encoding.ASCII.GetBytes(TransmissionGuid ?? SenderGuid, span[(o + 13)..]);
        Encoding.ASCII.GetBytes(SenderGuid, span[(o + 13 + SrsProtocol.GuidLength)..]);
        return bytes;
    }

    public static SrsVoicePacket? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderLength + FrequencyLength + FixedLength) return null;
        int audioLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        int frequencyPart = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        if (HeaderLength + audioLength + frequencyPart + FixedLength > bytes.Length || frequencyPart % FrequencyLength != 0) return null;
        int count = frequencyPart / FrequencyLength;
        var frequencies = new SrsFrequency[count];
        var encryptions = new byte[count];
        int o = HeaderLength + audioLength;
        for (int i = 0; i < count; i++, o += FrequencyLength)
        {
            frequencies[i] = new SrsFrequency(BinaryPrimitives.ReadDoubleLittleEndian(bytes[o..]), (SrsModulation)bytes[o + 8]);
            encryptions[i] = bytes[o + 9];
        }
        // The fixed part is read from the end, as SRS does.
        int end = bytes.Length;
        string sender = Encoding.ASCII.GetString(bytes[(end - SrsProtocol.GuidLength)..]);
        string transmission = Encoding.ASCII.GetString(bytes.Slice(end - 2 * SrsProtocol.GuidLength, SrsProtocol.GuidLength));
        byte retransmissions = bytes[end - 2 * SrsProtocol.GuidLength - 1];
        return new SrsVoicePacket(
            bytes.Slice(HeaderLength, audioLength).ToArray(),
            frequencies,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[o..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[(o + 4)..]),
            sender,
            transmission,
            encryptions,
            retransmissions);
    }
}
