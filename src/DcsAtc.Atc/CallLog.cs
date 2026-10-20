namespace DcsAtc.Atc;

/// <summary>
/// The radio, remembering what was said to whom: every transmission by the callsign it starts with ("Hangman 1-2, …"),
/// for <see cref="Keep"/>, so that a readback can be checked against the instruction it answers.
/// </summary>
public sealed class CallLog(IAtcRadio inner, Func<DateTime>? clock = null) : IAtcRadio
{
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(2);

    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly Dictionary<string, List<(DateTime At, RadioCall Call)>> _calls = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public void Transmit(RadioCall call)
    {
        int comma = call.Text.IndexOf(',');
        if (comma > 0)
        {
            var now = _now();
            lock (_lock)
            {
                string callsign = call.Text[..comma];
                if (!_calls.TryGetValue(callsign, out var list)) _calls[callsign] = list = [];
                list.RemoveAll(c => now - c.At > Keep);
                list.Add((now, call));
            }
        }
        inner.Transmit(call);
    }

    public void Prepare(IEnumerable<string> texts, bool soon = false) => inner.Prepare(texts, soon);

    public void Broadcast(string station, IReadOnlyList<double> frequencies, int coalition, string? text) => inner.Broadcast(station, frequencies, coalition, text);

    public void Listen(string facility, AirfieldStations stations, int coalition) => inner.Listen(facility, stations, coalition);

    /// <summary>The transmissions to a callsign within <paramref name="within"/>, newest first.</summary>
    public IReadOnlyList<RadioCall> RecentTo(string callsign, TimeSpan within)
    {
        var now = _now();
        lock (_lock)
        {
            return _calls.TryGetValue(callsign, out var list)
                ? list.Where(c => now - c.At <= within).OrderByDescending(c => c.At).Select(c => c.Call).ToList()
                : [];
        }
    }

    /// <summary>
    /// The correction for a readback, or null when there is nothing to correct: the newest transmission to
    /// <paramref name="callsign"/> from this airfield or FARP site within <paramref name="within"/> that has a value of
    /// a kind the readback has, checked with <see cref="Readbacks"/>. Said by the same station:
    /// "Hangman 1-2, negative, I say again, runway 31R."
    /// </summary>
    /// <param name="words">The readback's normalized words (as the pilot-call reader gives them).</param>
    public RadioCall? Correction(string callsign, string facility, IReadOnlyList<string> words, TimeSpan within)
    {
        var heard = Readbacks.Heard(words);
        if (heard.Count == 0) return null;
        foreach (var call in RecentTo(callsign, within).Where(c => c.Facility == facility))
        {
            var instructed = Readbacks.Instructed(call.Text);
            if (!instructed.Any(i => heard.Any(h => h.Kind == i.Kind))) continue;
            var mistakes = Readbacks.Mistakes(instructed, heard);
            return mistakes.Count == 0 ? null : call with { Text = Phraseology.NegativeISayAgain(callsign, mistakes.Select(m => m.Said)) };
        }
        return null;
    }
}
