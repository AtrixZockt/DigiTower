using Concentus;
using Concentus.Enums;

namespace DcsAtc.Srs;

/// <summary>Opus as SRS uses it: 16 kHz mono, 40 ms frames, VoIP mode.</summary>
public static class SrsOpus
{
    /// <summary>Splits PCM into 40 ms frames (the last one padded with silence) and encodes each.</summary>
    public static List<byte[]> Encode(ReadOnlySpan<short> pcm)
    {
        var encoder = OpusCodecFactory.CreateEncoder(SrsProtocol.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        var frames = new List<byte[]>(pcm.Length / SrsProtocol.FrameSamples + 1);
        var frame = new short[SrsProtocol.FrameSamples];
        var output = new byte[1275];
        for (int pos = 0; pos < pcm.Length; pos += SrsProtocol.FrameSamples)
        {
            var chunk = pcm.Slice(pos, Math.Min(SrsProtocol.FrameSamples, pcm.Length - pos));
            Array.Clear(frame);
            chunk.CopyTo(frame);
            int length = encoder.Encode(frame, SrsProtocol.FrameSamples, output, output.Length);
            frames.Add(output.AsSpan(0, length).ToArray());
        }
        return frames;
    }

    public static IOpusDecoder CreateDecoder() => OpusCodecFactory.CreateDecoder(SrsProtocol.SampleRate, 1);

    /// <summary>Decodes one frame; an empty frame asks the decoder to conceal a lost packet.</summary>
    public static short[] Decode(IOpusDecoder decoder, ReadOnlySpan<byte> frame)
    {
        var pcm = new short[SrsProtocol.FrameSamples];
        int samples = decoder.Decode(frame, pcm, SrsProtocol.FrameSamples, false);
        return samples == pcm.Length ? pcm : pcm[..Math.Max(0, samples)];
    }
}
