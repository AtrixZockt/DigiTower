using System.Text.RegularExpressions;

namespace DcsAtc.Recognition;

/// <summary>
/// Turns what a recognizer wrote, or what a pilot was meant to say, into the same plain words, so that two ways of
/// writing one call compare equal: lower case, no punctuation, numbers digit by digit as words ("31R", "thirty-one
/// right" and "three one right" all become "three one right"), whole thousands as "three thousand", decimals without
/// the point ("29.92" and "two niner niner two"), taxiways in the phonetic alphabet ("A", "D2" → "alpha", "delta two").
/// </summary>
public static partial class Transcript
{
    private static readonly string[] Digits = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    private static readonly string[] Phonetic =
    [
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliett", "kilo", "lima", "mike",
        "november", "oscar", "papa", "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whiskey", "xray", "yankee", "zulu",
    ];

    private static readonly Dictionary<string, int> Ones = new()
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["niner"] = 9,
    };

    private static readonly Dictionary<string, int> Teens = new()
    {
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15,
        ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, int> Tens = new()
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    // Spellings that mean the same word; names recognizers spell in several ways.
    private static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["dafra"] = ["dhafra"], ["dhafrah"] = ["dhafra"], ["dafrah"] = ["dhafra"], ["daffra"] = ["dhafra"],
        ["aldhafra"] = ["al", "dhafra"], ["aldafra"] = ["al", "dhafra"],
        ["rodger"] = ["roger"], ["ok"] = ["okay"], ["ft"] = ["feet"], ["alfa"] = ["alpha"], ["juliet"] = ["juliett"],
        ["whisky"] = ["whiskey"],
        ["northwest"] = ["north", "west"], ["northeast"] = ["north", "east"], ["southwest"] = ["south", "west"], ["southeast"] = ["south", "east"],
        // Spellings of one instruction that ATC treats alike.
        ["descent"] = ["descend"], ["startup"] = ["start", "up"], ["takeoff"] = ["take", "off"], ["lineup"] = ["line", "up"],
    };

    private static readonly HashSet<string> Fillers = ["uh", "um", "er", "erm", "ah", "hmm", "mm"];

    private static readonly string[] Acronyms = ["QNH", "ILS", "ATIS", "TACAN", "VFR", "IFR", "RTB", "VOR", "DME", "ATC"];

    /// <summary>The words of <paramref name="text"/> in their plain form.</summary>
    public static string[] Normalize(string text)
    {
        // Case-sensitive rules first: runway sides, acronyms, taxiway letters.
        text = text.Replace('’', '\'').Replace('‘', '\'');
        text = NinerTail().Replace(text, "");
        text = RunwaySide().Replace(text, m => $"{m.Groups[1].Value} {Side(m.Groups[2].Value)}");
        foreach (var acronym in Acronyms)
            text = Regex.Replace(text, @"\b" + string.Join(@"[\s.\-]*", acronym.ToCharArray()) + @"\b\.?", acronym, RegexOptions.IgnoreCase);
        text = TaxiwayWithNumber().Replace(text, m => $"{Phonetic[m.Groups[1].Value[0] - 'A']} {m.Groups[2].Value}");
        text = LetterDigitBoundary().Replace(text, " ");
        text = SingleCapital().Replace(text, m => Phonetic[m.Value[0] - 'A']);

        text = text.ToLowerInvariant();
        text = ThousandsSeparator().Replace(text, "$1$2");
        text = DecimalPoint().Replace(text, "$1 $2");
        text = text.Replace("x-ray", "xray").Replace("'", "");
        text = NotWordCharacter().Replace(text, " ");

        var words = new List<string>();
        foreach (var raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Fillers.Contains(raw)) continue;
            words.AddRange(Aliases.TryGetValue(raw, out var alias) ? alias : [raw]);
        }
        return Numbers(words).ToArray();
    }

    /// <summary>Normalized, joined with single spaces.</summary>
    public static string Plain(string text) => string.Join(' ', Normalize(text));

    /// <summary>
    /// Word errors (substitutions + deletions + insertions) between the reference and the recognized words, and the
    /// alignment: for each recognized word whether it matches the reference.
    /// </summary>
    public static (int Errors, bool[] Correct) Compare(IReadOnlyList<string> reference, IReadOnlyList<string> recognized)
    {
        int n = reference.Count, m = recognized.Count;
        var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;
        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int same = reference[i - 1] == recognized[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + same);
            }
        }
        var correct = new bool[m];
        for (int i = n, j = m; j > 0;)
        {
            if (i > 0 && d[i, j] == d[i - 1, j - 1] + (reference[i - 1] == recognized[j - 1] ? 0 : 1))
            {
                correct[j - 1] = reference[i - 1] == recognized[j - 1];
                i--;
                j--;
            }
            else if (d[i, j] == d[i, j - 1] + 1) j--;
            else i--;
        }
        return (d[n, m], correct);
    }

    /// <summary>Whether <paramref name="phrase"/> occurs in <paramref name="words"/> as consecutive words.</summary>
    public static bool Contains(IReadOnlyList<string> words, IReadOnlyList<string> phrase)
    {
        if (phrase.Count == 0) return true;
        for (int start = 0; start + phrase.Count <= words.Count; start++)
        {
            int k = 0;
            while (k < phrase.Count && words[start + k] == phrase[k]) k++;
            if (k == phrase.Count) return true;
        }
        return false;
    }

    /// <summary>
    /// How closely <paramref name="phrase"/> appears in <paramref name="words"/>: the best similarity (0..1, by letters,
    /// ignoring spaces) of any run of about as many words. Tolerates near-misses a parser would accept, such as
    /// "one way three one right" for "runway three one right".
    /// </summary>
    public static double Resemblance(IReadOnlyList<string> words, IReadOnlyList<string> phrase)
    {
        if (phrase.Count == 0) return 1;
        string target = string.Concat(phrase);
        double best = 0;
        for (int length = Math.Max(1, phrase.Count - 1); length <= phrase.Count + 1; length++)
        {
            for (int start = 0; start + length <= words.Count; start++)
            {
                string candidate = string.Concat(words.Skip(start).Take(length));
                best = Math.Max(best, 1 - Distance(candidate, target) / (double)Math.Max(candidate.Length, target.Length));
            }
        }
        return best;
    }

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    // Number words to digits, then every number digit by digit ("thirty one" → 31 → "three one"); whole thousands
    // stay "N thousand" (as said), and "decimal" / "point" between digits is dropped.
    private static List<string> Numbers(List<string> words)
    {
        var digits = new List<string>();
        for (int i = 0; i < words.Count; i++)
        {
            string w = words[i];
            if (Ones.TryGetValue(w, out int one)) digits.Add(one.ToString());
            else if (Teens.TryGetValue(w, out int teen)) digits.Add(teen.ToString());
            else if (Tens.TryGetValue(w, out int ten))
            {
                if (i + 1 < words.Count && Ones.TryGetValue(words[i + 1], out int unit) && unit > 0)
                {
                    digits.Add((ten + unit).ToString());
                    i++;
                }
                else digits.Add(ten.ToString());
            }
            else digits.Add(w);
        }

        var result = new List<string>();
        foreach (var w in digits)
        {
            if (!w.All(char.IsAsciiDigit))
            {
                result.Add(w);
                continue;
            }
            if (w.Length >= 4 && w.EndsWith("000") && w.TrimStart('0').Length > 3)
            {
                result.AddRange(w[..^3].Select(c => Digits[c - '0']));
                result.Add("thousand");
            }
            else result.AddRange(w.Select(c => Digits[c - '0']));
        }

        for (int i = result.Count - 2; i >= 1; i--)
        {
            if (result[i] is "decimal" or "point" && IsDigitWord(result[i - 1]) && IsDigitWord(result[i + 1])) result.RemoveAt(i);
        }

        // Hundreds as digits: "five hundred" is 500, "three thousand five hundred" is 3500.
        for (int i = 1; i < result.Count; i++)
        {
            if (result[i] != "hundred" || !IsDigitWord(result[i - 1])) continue;
            result[i] = "zero";
            result.Insert(i + 1, "zero");
            if (i >= 3 && result[i - 2] == "thousand" && IsDigitWord(result[i - 3])) result.RemoveAt(i - 2);
        }

        // A run of digits ending in three zeros is a whole thousand however it was written ("2-0-0-0" for 2000).
        for (int end = result.Count; end > 0;)
        {
            int start = end;
            while (start > 0 && IsDigitWord(result[start - 1])) start--;
            if (end - start >= 4 && result[start] != "zero" && result.Skip(end - 3).Take(3).All(w => w == "zero"))
            {
                result.RemoveRange(end - 3, 3);
                result.Insert(end - 3, "thousand");
            }
            end = start == end ? end - 1 : start;
        }
        return result;
    }

    private static bool IsDigitWord(string w) => Array.IndexOf(Digits, w) >= 0;

    private static string Side(string letter) => letter switch { "L" => "left", "R" => "right", _ => "center" };

    // "Niner" written as "9A" or "9 a" ("29A9A2" for "two niner niner two").
    [GeneratedRegex(@"(?<=9)[\s-]*[Aa](?=[\s-]*\d)")]
    private static partial Regex NinerTail();

    [GeneratedRegex(@"(?<=[A-Za-z])(?=\d)|(?<=\d)(?=[A-Za-z])")]
    private static partial Regex LetterDigitBoundary();

    [GeneratedRegex(@"\b(\d{1,2}) ?([LRC])\b")]
    private static partial Regex RunwaySide();

    [GeneratedRegex(@"\b([A-Z])(\d{1,2})\b")]
    private static partial Regex TaxiwayWithNumber();

    // A capital letter standing alone is a taxiway (or a spelled letter); "I" is the pronoun.
    [GeneratedRegex(@"(?<![\w'])[A-HJ-Z](?![\w'])")]
    private static partial Regex SingleCapital();

    [GeneratedRegex(@"(\d),(\d{3})\b")]
    private static partial Regex ThousandsSeparator();

    [GeneratedRegex(@"(\d)\.(\d)")]
    private static partial Regex DecimalPoint();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NotWordCharacter();
}
