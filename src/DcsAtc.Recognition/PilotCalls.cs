namespace DcsAtc.Recognition;

/// <summary>What a pilot asks ATC for. Which controller answers is decided by the caller (Ground, Tower, Approach).</summary>
public enum PilotIntent
{
    RequestTaxi, TaxiToParking, CancelTaxi, RequestStartup, RadioCheck, SayAgain,
    ReadyForDeparture, RequestLanding, CheckIn, ReportFinal, ReportInitial,
    RequestApproach, FieldInSight, CancelApproach,
}

/// <param name="Option">For a departure "own", "north", "east", "south", "west" or "closed"; for an approach "ils", "tacan", "visual" or "overhead"; null if not said.</param>
/// <param name="Match">How closely the words matched (0.8 to 1).</param>
public sealed record PilotCall(PilotIntent Intent, string? Option, double Match);

/// <summary>
/// How a transmission reads: the call it makes (if any), or else a readback (the pilot repeating an instruction), or
/// a call aimed at ATC that was not understood, or none of these (chatter on the frequency).
/// </summary>
public sealed record PilotCallReading(PilotCall? Call, bool Readback, bool AimedAtAtc, IReadOnlyList<string> Words);

/// <summary>
/// Reads what a pilot said, in plain code (no AI after the recognizer): the text is normalized
/// (<see cref="Transcript"/>), the pilot's known mishearings are replaced ("one way" → "runway"), and the result is
/// matched against a few ways of saying each call, accepting near-misses (<see cref="Transcript.Resemblance"/>).
/// Longer, closer matches win, so "request taxi to parking" is not read as "request taxi".
/// </summary>
public sealed class PilotCallReader
{
    /// <summary>
    /// Resemblance from which a phrase counts as said: about one letter in seven may differ ("stay again" for "say
    /// again"), but not two in a short phrase ("request take" is not "request taxi").
    /// </summary>
    public const double Threshold = 0.85;
    // Phrases shorter than this (in letters) must be said exactly: near-misses of short words are too often other words.
    private const int ShortPhrase = 8;

    private static readonly (PilotIntent Intent, string[] Phrases)[] Calls =
    [
        (PilotIntent.RequestTaxi, ["request taxi", "ready to taxi", "ready for taxi", "request to taxi"]),
        (PilotIntent.TaxiToParking, ["request taxi to parking", "taxi to parking", "request parking"]),
        (PilotIntent.CancelTaxi, ["cancel taxi", "cancel my taxi"]),
        (PilotIntent.RequestStartup, ["request start up", "ready to start", "ready for start up", "request start", "request engine start"]),
        (PilotIntent.RadioCheck, ["radio check", "how do you read", "how do you copy"]),
        (PilotIntent.SayAgain, ["say again"]),
        (PilotIntent.ReadyForDeparture, ["ready for departure", "ready for take off", "request take off", "request departure", "ready departure", "ready to go"]),
        (PilotIntent.RequestLanding, ["request landing", "inbound for landing", "for landing", "inbound"]),
        // "Airborne, passing 2000": a departure checking in with Approach.
        (PilotIntent.CheckIn, ["checking in", "check in", "with you", "airborne"]),
        (PilotIntent.ReportFinal, ["final", "short final", "mile final", "on final"]),
        (PilotIntent.ReportInitial, ["initial", "at initial"]),
        // Only with "request": "ILS approach runway 31R" alone is how a clearance is read back.
        (PilotIntent.RequestApproach, ["request approach", "request ils", "request tacan", "request visual", "request overhead", "request vectors",
            "request recovery"]),
        (PilotIntent.FieldInSight, ["field in sight", "airfield in sight", "runway in sight", "field insight"]),
        (PilotIntent.CancelApproach, ["cancel approach", "cancel the approach"]),
    ];

    private static readonly (string Option, string[] Phrases)[] Departures =
    [
        ("closed", ["closed traffic", "closed pattern", "request closed", "pattern work", "touch and go"]),
        ("own", ["own navigation"]),
        ("north", ["departure north", "north departure", "northbound", "to the north", "north"]),
        ("east", ["departure east", "east departure", "eastbound", "to the east", "east"]),
        ("south", ["departure south", "south departure", "southbound", "to the south", "south"]),
        ("west", ["departure west", "west departure", "westbound", "to the west", "west"]),
    ];

    private static readonly (string Option, string[] Phrases)[] Approaches =
    [
        ("ils", ["ils"]),
        ("tacan", ["tacan"]),
        ("visual", ["visual"]),
        ("overhead", ["overhead", "break"]),
    ];

    // Words of instructions a pilot reads back; with no request in the transmission, it is a readback.
    private static readonly string[][] ReadbackWords =
        [.. new[] { "cleared", "line up", "wilco", "roger", "copy", "contact", "descend", "descending", "climb", "climbing", "maintain",
            "heading", "qnh", "altimeter", "hold", "holding", "going around", "go around", "vacating", "via", "runway", "squawk", "taxi to",
            "full stop", "touch and go" }
            .Select(Transcript.Normalize)];

    private static readonly string[] Cleared = ["cleared"];

    // Words of a call to ATC (with the station's name): not understood, it gets "say again". "request" first.
    private static readonly string[][] RequestWords = [.. new[] { "request", "ready", "report", "inbound", "checking" }.Select(Transcript.Normalize)];

    private readonly (string[] From, string[] To)[] _aliases;
    private readonly (PilotIntent Intent, string[][] Phrases)[] _calls;

    /// <param name="aliases">The pilot's mishearings: "one way" → "runway" (normalized on both sides, longest first).</param>
    public PilotCallReader(IReadOnlyDictionary<string, string>? aliases = null)
    {
        _aliases = (aliases ?? new Dictionary<string, string>())
            .Select(a => (From: Transcript.Normalize(a.Key), To: Transcript.Normalize(a.Value)))
            .Where(a => a.From.Length > 0)
            .OrderByDescending(a => a.From.Length)
            .ToArray();
        _calls = Calls.Select(c => (c.Intent, c.Phrases.Select(Transcript.Normalize).ToArray())).ToArray();
    }

    /// <param name="station">The station's name ("Al Dhafra Ground"): a call naming it is aimed at ATC.</param>
    public PilotCallReading Read(string text, string? station = null)
    {
        var words = Replace(Transcript.Normalize(text));
        PilotCall? best = null;
        double bestWeight = 0;
        foreach (var (intent, phrases) in _calls)
        {
            foreach (var phrase in phrases)
            {
                double match = Match(words, phrase);
                double weight = match * phrase.Sum(w => w.Length);
                if (match >= Threshold && weight > bestWeight)
                {
                    bestWeight = weight;
                    best = new PilotCall(intent, null, match);
                }
            }
        }
        // "Cleared …" without a request is a clearance read back, whatever else it contains.
        if (best is not null && Match(words, Cleared) >= Threshold && !RequestWords.Take(1).Any(w => Match(words, w) >= Threshold))
            best = null;
        if (best is not null)
        {
            string? option = best.Intent switch
            {
                PilotIntent.ReadyForDeparture => Option(words, Departures),
                PilotIntent.RequestApproach => Option(words, Approaches),
                _ => null,
            };
            return new PilotCallReading(best with { Option = option }, false, true, words);
        }
        bool readback = ReadbackWords.Any(w => Match(words, w) >= Threshold);
        var stationWords = station is null ? [] : Transcript.Normalize(station).Where(w => w.Length >= 4).Select(w => new[] { w });
        bool aimed = RequestWords.Concat(stationWords).Any(w => Match(words, w) >= Threshold);
        return new PilotCallReading(null, readback, aimed, words);
    }

    private static double Match(IReadOnlyList<string> words, string[] phrase)
    {
        if (Transcript.Contains(words, phrase)) return 1;
        return phrase.Sum(w => w.Length) < ShortPhrase ? 0 : Transcript.Resemblance(words, phrase);
    }

    private static string? Option(IReadOnlyList<string> words, (string Option, string[] Phrases)[] options)
    {
        string? best = null;
        double bestWeight = 0;
        foreach (var (option, phrases) in options)
        {
            foreach (var phrase in phrases.Select(Transcript.Normalize))
            {
                double match = Match(words, phrase);
                double weight = match * phrase.Sum(w => w.Length);
                if (match >= Threshold && weight > bestWeight)
                {
                    bestWeight = weight;
                    best = option;
                }
            }
        }
        return best;
    }

    private string[] Replace(string[] words)
    {
        if (_aliases.Length == 0) return words;
        var result = new List<string>(words.Length);
        for (int i = 0; i < words.Length;)
        {
            var alias = _aliases.FirstOrDefault(a => i + a.From.Length <= words.Length && a.From.SequenceEqual(words.Skip(i).Take(a.From.Length)));
            if (alias.From is null)
            {
                result.Add(words[i++]);
                continue;
            }
            result.AddRange(alias.To);
            i += alias.From.Length;
        }
        return [.. result];
    }
}
