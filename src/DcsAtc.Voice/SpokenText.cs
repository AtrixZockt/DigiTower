using System.Text;
using System.Text.RegularExpressions;

namespace DcsAtc.Voice;

/// <summary>One clip of a transmission (the text the TTS says) and the silence after it.</summary>
public sealed record SpeechUnit(string Text, TimeSpan PauseAfter);

/// <summary>
/// Turns ATC text into what is said on the radio, as a sequence of clips. ICAO style: numbers digit by digit
/// ("wind 310 degrees" → "wind three one zero degrees", "niner" for 9), runway designators ("31R" → "three one right"),
/// frequencies with "decimal", altimeter settings without it, taxiways in the phonetic alphabet ("N4" → "November four").
/// <para>
/// Words between numbers form one clip ("taxi to holding point runway"); every number reading is a clip of its own.
/// Both come from small sets (the phrases of the phraseology, the numbers of a domain), so after a short warm-up every
/// clip is cached and only new callsign words ever need the TTS.
/// </para>
/// </summary>
public static partial class SpokenText
{
    public static readonly TimeSpan WordGap = TimeSpan.FromMilliseconds(30);
    public static readonly TimeSpan CommaPause = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan SentencePause = TimeSpan.FromMilliseconds(300);

    private static readonly string[] Digits = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "niner"];

    public static readonly IReadOnlyDictionary<char, string> Phonetic = new Dictionary<char, string>
    {
        ['A'] = "Alpha", ['B'] = "Bravo", ['C'] = "Charlie", ['D'] = "Delta", ['E'] = "Echo", ['F'] = "Foxtrot", ['G'] = "Golf",
        ['H'] = "Hotel", ['I'] = "India", ['J'] = "Juliett", ['K'] = "Kilo", ['L'] = "Lima", ['M'] = "Mike", ['N'] = "November",
        ['O'] = "Oscar", ['P'] = "Papa", ['Q'] = "Quebec", ['R'] = "Romeo", ['S'] = "Sierra", ['T'] = "Tango", ['U'] = "Uniform",
        ['V'] = "Victor", ['W'] = "Whiskey", ['X'] = "X-ray", ['Y'] = "Yankee", ['Z'] = "Zulu",
    };

    /// <summary>Words the TTS would read wrongly, as they should be said.</summary>
    public static readonly IReadOnlyDictionary<string, string> Lexicon = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["QNH"] = "Q N H",
        ["ILS"] = "I L S",
        ["VFR"] = "V F R",
        ["IFR"] = "I F R",
        ["ATIS"] = "ay-tis",
        ["TACAN"] = "tack-an",
        ["Dhafra"] = "Dafra",
    };

    /// <summary>The clips for <paramref name="text"/>, in order.</summary>
    public static IReadOnlyList<SpeechUnit> Units(string text)
    {
        var units = new List<SpeechUnit>();
        foreach (var sentence in Sentences().Split(text.Trim()).Where(s => s.Trim().Length > 0))
        {
            bool viaList = false;
            var clauses = sentence.Split([',', ';', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var clause in clauses)
            {
                var words = new List<string>();
                var tokens = clause.TrimEnd('.', '!', '?').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < tokens.Length; i++)
                {
                    string token = tokens[i];
                    string previous = i > 0 ? tokens[i - 1] : "";
                    string next = i + 1 < tokens.Length ? tokens[i + 1] : "";
                    if (Reading(token, previous, next, viaList) is { } value)
                    {
                        Flush(units, words);
                        units.Add(new SpeechUnit(value, WordGap));
                    }
                    else
                    {
                        words.Add(Lexicon.GetValueOrDefault(token, token));
                    }
                    if (token.Equals("via", StringComparison.OrdinalIgnoreCase)) viaList = true;
                }
                Flush(units, words);
                if (units.Count > 0) units[^1] = units[^1] with { PauseAfter = CommaPause };
            }
            if (units.Count > 0) units[^1] = units[^1] with { PauseAfter = SentencePause };
        }
        if (units.Count > 0) units[^1] = units[^1] with { PauseAfter = TimeSpan.Zero };
        return units;
    }

    /// <summary>The spoken form of a number-like token, or null for an ordinary word.</summary>
    private static string? Reading(string token, string previous, string next, bool viaList)
    {
        if (previous.Equals("runway", StringComparison.OrdinalIgnoreCase) && RunwayDesignator().Match(token) is { Success: true } rwy)
        {
            string side = rwy.Groups[2].Value switch { "L" => " left", "R" => " right", "C" => " center", _ => "" };
            return DigitWords(rwy.Groups[1].Value.PadLeft(2, '0')) + side;
        }
        if (previous.Equals("altimeter", StringComparison.OrdinalIgnoreCase) && Altimeter().IsMatch(token))
            return DigitWords(token.Replace(".", ""));
        if (Decimal().Match(token) is { Success: true } dec)
            return $"{DigitWords(dec.Groups[1].Value)} decimal {DigitWords(dec.Groups[2].Value)}";
        if (FlightNumber().Match(token) is { Success: true } flight)
            return $"{DigitWords(flight.Groups[1].Value)} {DigitWords(flight.Groups[2].Value)}";
        if (FlightLevel().Match(token) is { Success: true } fl)
            return $"flight level {DigitWords(fl.Groups[1].Value)}";
        if (Integer().IsMatch(token))
        {
            // Altitudes in whole hundreds are said in thousands and hundreds ("three thousand five hundred feet").
            if (next.Equals("feet", StringComparison.OrdinalIgnoreCase) && int.Parse(token) is var feet and >= 1000 && feet % 100 == 0)
                return Thousands(feet);
            return DigitWords(token);
        }
        // Taxiways: anywhere as letter plus digits ("N4"), as single letters in a "via" list ("via A, B").
        if (Taxiway().Match(token) is { Success: true } twy && (twy.Groups[2].Length > 0 || viaList) && token != "I")
        {
            string letters = string.Join(" ", twy.Groups[1].Value.Select(c => Phonetic[c]));
            return twy.Groups[2].Length > 0 ? $"{letters} {DigitWords(twy.Groups[2].Value)}" : letters;
        }
        return null;
    }

    public static string DigitWords(string digits) => string.Join(" ", digits.Where(char.IsAsciiDigit).Select(c => Digits[c - '0']));

    private static string Thousands(int feet)
    {
        var parts = new List<string>();
        if (feet >= 1000) parts.Add($"{DigitWords((feet / 1000).ToString())} thousand");
        if (feet % 1000 != 0) parts.Add($"{Digits[feet % 1000 / 100]} hundred");
        return string.Join(" ", parts);
    }

    private static void Flush(List<SpeechUnit> units, List<string> words)
    {
        if (words.Count == 0) return;
        units.Add(new SpeechUnit(string.Join(" ", words), WordGap));
        words.Clear();
    }

    /// <summary>Ends a sentence at ". " or a final "." (not inside "250.0").</summary>
    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex Sentences();

    [GeneratedRegex(@"^(\d{1,2})([LRC]?)$")]
    private static partial Regex RunwayDesignator();

    [GeneratedRegex(@"^\d{2}\.\d{2}$")]
    private static partial Regex Altimeter();

    [GeneratedRegex(@"^(\d+)\.(\d+)$")]
    private static partial Regex Decimal();

    [GeneratedRegex(@"^(\d+)-(\d+)$")]
    private static partial Regex FlightNumber();

    [GeneratedRegex(@"^FL(\d{2,3})$")]
    private static partial Regex FlightLevel();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex Integer();

    [GeneratedRegex(@"^([A-Z]{1,2})(\d{0,2})$")]
    private static partial Regex Taxiway();

    /// <summary>A readable rendering of the units, for logs and tests: "[Hangman] [one one] | [Al Dafra Ground] ‖ …".</summary>
    public static string Describe(IEnumerable<SpeechUnit> units)
    {
        var sb = new StringBuilder();
        foreach (var u in units)
        {
            sb.Append('[').Append(u.Text).Append(']');
            sb.Append(u.PauseAfter == SentencePause ? " ‖ " : u.PauseAfter == CommaPause ? " | " : u.PauseAfter == TimeSpan.Zero ? "" : " ");
        }
        return sb.ToString().TrimEnd();
    }
}
