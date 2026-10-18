namespace DcsAtc.Voice;

/// <summary>
/// A rough AM radio sound for listening tests outside the game (in the game, SRS adds its own radio effect):
/// band-pass 300–3000 Hz, light saturation and a little noise.
/// </summary>
public static class RadioEffect
{
    public static AudioClip Apply(AudioClip clip, int seed = 1)
    {
        var s = (float[])clip.Samples.Clone();
        Biquad.HighPass(clip.SampleRate, 300).Process(s);
        Biquad.HighPass(clip.SampleRate, 300).Process(s);
        Biquad.LowPass(clip.SampleRate, 3000).Process(s);
        Biquad.LowPass(clip.SampleRate, 3000).Process(s);
        var noise = new Random(seed);
        for (int i = 0; i < s.Length; i++)
        {
            double x = Math.Tanh(s[i] * 2.5) / Math.Tanh(2.5);
            s[i] = (float)(0.85 * x + 0.015 * (noise.NextDouble() * 2 - 1));
        }
        return clip with { Samples = s };
    }

    /// <summary>RBJ cookbook biquad filter.</summary>
    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        public static Biquad LowPass(int rate, double hz, double q = 0.707) => Make(rate, hz, q, low: true);
        public static Biquad HighPass(int rate, double hz, double q = 0.707) => Make(rate, hz, q, low: false);

        private static Biquad Make(int rate, double hz, double q, bool low)
        {
            double w = 2 * Math.PI * hz / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
            double b0 = low ? (1 - cos) / 2 : (1 + cos) / 2, b1 = low ? 1 - cos : -(1 + cos);
            return new Biquad(b0 / a0, b1 / a0, b0 / a0, -2 * cos / a0, (1 - alpha) / a0);
        }

        public void Process(float[] s)
        {
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < s.Length; i++)
            {
                double x = s[i], y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1;
                x1 = x;
                y2 = y1;
                y1 = y;
                s[i] = (float)y;
            }
        }
    }
}
