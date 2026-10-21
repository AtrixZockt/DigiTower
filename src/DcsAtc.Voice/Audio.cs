using System.Buffers.Binary;
using System.Text;

namespace DcsAtc.Voice;

/// <summary>Mono audio as float samples in [-1, 1].</summary>
public sealed record AudioClip(float[] Samples, int SampleRate)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)SampleRate);

    public static AudioClip Silence(TimeSpan duration, int sampleRate) => new(new float[(int)(duration.TotalSeconds * sampleRate)], sampleRate);
}

public static class Audio
{
    /// <summary>Converts to 16-bit PCM with clipping.</summary>
    public static short[] ToPcm16(ReadOnlySpan<float> samples)
    {
        var pcm = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++) pcm[i] = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
        return pcm;
    }

    public static float[] FromPcm16(ReadOnlySpan<short> pcm)
    {
        var samples = new float[pcm.Length];
        for (int i = 0; i < pcm.Length; i++) samples[i] = pcm[i] / (float)short.MaxValue;
        return samples;
    }

    /// <summary>Windowed-sinc resampling (Blackman window, 16 zero crossings per side) with an anti-alias low-pass when downsampling.</summary>
    public static AudioClip Resample(AudioClip clip, int sampleRate)
    {
        if (clip.SampleRate == sampleRate || clip.Samples.Length == 0) return clip with { SampleRate = sampleRate };
        var input = clip.Samples;
        double ratio = (double)sampleRate / clip.SampleRate;
        double cutoff = Math.Min(1.0, ratio) * 0.95;
        int half = (int)Math.Ceiling(16 / cutoff);
        var output = new float[(int)(input.Length * ratio)];
        for (int n = 0; n < output.Length; n++)
        {
            double t = n / ratio;
            int center = (int)t;
            double sum = 0, weights = 0;
            for (int k = Math.Max(0, center - half + 1); k <= Math.Min(input.Length - 1, center + half); k++)
            {
                double x = t - k;
                double w = cutoff * Sinc(cutoff * x) * Blackman(x / half);
                sum += input[k] * w;
                weights += w;
            }
            output[n] = weights > 1e-9 ? (float)(sum / weights) : 0f;
        }
        return new AudioClip(output, sampleRate);

        static double Sinc(double x) => Math.Abs(x) < 1e-9 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
        static double Blackman(double x) => Math.Abs(x) >= 1 ? 0 : 0.42 + 0.5 * Math.Cos(Math.PI * x) + 0.08 * Math.Cos(2 * Math.PI * x);
    }

    /// <summary>Cuts leading and trailing silence (below <paramref name="thresholdDb"/> of full scale, judged in 10 ms windows), keeping <paramref name="keep"/> of margin.</summary>
    public static AudioClip TrimSilence(AudioClip clip, double thresholdDb = -40, TimeSpan? keep = null)
    {
        var s = clip.Samples;
        int window = Math.Max(1, clip.SampleRate / 100);
        double threshold = Math.Pow(10, thresholdDb / 20);
        bool Loud(int start)
        {
            double sum = 0;
            int end = Math.Min(s.Length, start + window);
            for (int i = start; i < end; i++) sum += s[i] * s[i];
            return Math.Sqrt(sum / Math.Max(1, end - start)) > threshold;
        }
        int first = 0;
        while (first < s.Length && !Loud(first)) first += window;
        if (first >= s.Length) return clip with { Samples = [] };
        int last = s.Length - window;
        while (last > first && !Loud(Math.Max(0, last))) last -= window;
        int margin = (int)((keep ?? TimeSpan.FromMilliseconds(15)).TotalSeconds * clip.SampleRate);
        int from = Math.Max(0, first - margin);
        int to = Math.Min(s.Length, last + window + margin);
        return clip with { Samples = s[from..to] };
    }

    /// <summary>Scales to an RMS level (speech parts only), limited so peaks stay below -1 dBFS.</summary>
    public static AudioClip Normalize(AudioClip clip, double rmsDb = -18)
    {
        var s = clip.Samples;
        if (s.Length == 0) return clip;
        double peak = 0, sum = 0;
        int loud = 0;
        foreach (var x in s)
        {
            peak = Math.Max(peak, Math.Abs(x));
            if (Math.Abs(x) > 0.01)
            {
                sum += x * x;
                loud++;
            }
        }
        if (loud == 0 || peak < 1e-6) return clip;
        double rms = Math.Sqrt(sum / loud);
        double gain = Math.Min(Math.Pow(10, rmsDb / 20) / rms, Math.Pow(10, -1 / 20.0) / peak);
        return clip with { Samples = s.Select(x => (float)(x * gain)).ToArray() };
    }

    /// <summary>
    /// A pilot in a cockpit: the voice narrowed to a headset microphone's band (300–3400 Hz), lightly driven, over a
    /// steady low hiss (mask and engine) from the moment the microphone is keyed. SRS's radio effect comes on top when it
    /// is received, as for the controllers, whose voices stay clean: the difference tells pilots from controllers.
    /// </summary>
    public static AudioClip Cockpit(AudioClip clip, int seed = 1)
    {
        var s = (float[])clip.Samples.Clone();
        int rate = clip.SampleRate;
        Biquad.HighPass(rate, 300).Run(s);
        Biquad.LowPass(rate, 3400).Run(s);
        const double drive = 1.6;
        double norm = Math.Tanh(drive);
        for (int i = 0; i < s.Length; i++) s[i] = (float)(Math.Tanh(s[i] * drive) / norm * 0.9);

        // The hiss: white noise, low-passed, about 24 dB under the speech (-18 dB RMS).
        var random = new Random(seed);
        var noise = new float[s.Length];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(random.NextDouble() * 2 - 1);
        Biquad.LowPass(rate, 1200).Run(noise);
        Biquad.HighPass(rate, 150).Run(noise);
        double rms = Math.Sqrt(noise.Average(x => (double)x * x));
        float gain = rms > 0 ? (float)(Math.Pow(10, -42 / 20.0) / rms) : 0;
        for (int i = 0; i < s.Length; i++) s[i] = Math.Clamp(s[i] + noise[i] * gain, -1f, 1f);
        return clip with { Samples = s };
    }

    /// <summary>A second-order filter (RBJ cookbook), run in place.</summary>
    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        public static Biquad LowPass(int rate, double hz) => Make(rate, hz, low: true);
        public static Biquad HighPass(int rate, double hz) => Make(rate, hz, low: false);

        private static Biquad Make(int rate, double hz, bool low)
        {
            double w = 2 * Math.PI * hz / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * Math.Sqrt(0.5));
            double a0 = 1 + alpha;
            return low
                ? new Biquad((1 - cos) / 2 / a0, (1 - cos) / a0, (1 - cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0)
                : new Biquad((1 + cos) / 2 / a0, -(1 + cos) / a0, (1 + cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
        }

        public void Run(float[] s)
        {
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < s.Length; i++)
            {
                double x = s[i], y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x; y2 = y1; y1 = y;
                s[i] = (float)y;
            }
        }
    }

    /// <summary>Joins clips (same sample rate) with the given silence after each.</summary>
    public static AudioClip Concat(IEnumerable<(AudioClip Clip, TimeSpan GapAfter)> parts, int sampleRate)
    {
        var samples = new List<float>();
        foreach (var (clip, gap) in parts)
        {
            if (clip.SampleRate != sampleRate) throw new ArgumentException("Clips must share the sample rate.");
            samples.AddRange(clip.Samples);
            samples.AddRange(new float[(int)(gap.TotalSeconds * sampleRate)]);
        }
        return new AudioClip(samples.ToArray(), sampleRate);
    }
}

/// <summary>Minimal WAV reading (PCM 16-bit or float 32-bit, any channel count, mixed to mono) and writing (PCM 16-bit mono).</summary>
public static class Wav
{
    public static void Write(string path, AudioClip clip)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        Write(stream, clip);
    }

    public static void Write(Stream stream, AudioClip clip)
    {
        var pcm = Audio.ToPcm16(clip.Samples);
        int dataBytes = pcm.Length * 2;
        var header = new byte[44];
        var h = header.AsSpan();
        Encoding.ASCII.GetBytes("RIFF", h);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], 36 + dataBytes);
        Encoding.ASCII.GetBytes("WAVEfmt ", h[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(h[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(h[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], clip.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], clip.SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(h[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(h[34..], 16);
        Encoding.ASCII.GetBytes("data", h[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[40..], dataBytes);
        stream.Write(header);
        var data = new byte[dataBytes];
        for (int i = 0; i < pcm.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), pcm[i]);
        stream.Write(data);
    }

    public static AudioClip Read(string path) => Read(File.ReadAllBytes(path));

    public static AudioClip Read(byte[] bytes)
    {
        var b = bytes.AsSpan();
        if (b.Length < 12 || Encoding.ASCII.GetString(b[..4]) != "RIFF" || Encoding.ASCII.GetString(b[8..12]) != "WAVE")
            throw new InvalidDataException("Not a WAV file.");
        int format = 0, channels = 0, rate = 0, bits = 0;
        int pos = 12;
        while (pos + 8 <= b.Length)
        {
            string id = Encoding.ASCII.GetString(b.Slice(pos, 4));
            int size = BinaryPrimitives.ReadInt32LittleEndian(b[(pos + 4)..]);
            var body = b.Slice(pos + 8, Math.Min(size, b.Length - pos - 8));
            if (id == "fmt ")
            {
                format = BinaryPrimitives.ReadInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadInt16LittleEndian(body[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadInt16LittleEndian(body[14..]);
                if (format == unchecked((short)0xFFFE) && body.Length >= 26) format = BinaryPrimitives.ReadInt16LittleEndian(body[24..]);
            }
            else if (id == "data")
            {
                if (channels == 0) throw new InvalidDataException("WAV data before format.");
                int bytesPerSample = bits / 8;
                int frames = body.Length / (bytesPerSample * channels);
                var samples = new float[frames];
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        var s = body[((f * channels + c) * bytesPerSample)..];
                        sum += (format, bits) switch
                        {
                            (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                            (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(s),
                            (1, 8) => (s[0] - 128) / 128f,
                            _ => throw new InvalidDataException($"Unsupported WAV format {format}, {bits} bits."),
                        };
                    }
                    samples[f] = sum / channels;
                }
                return new AudioClip(samples, rate);
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException("WAV without data.");
    }
}
