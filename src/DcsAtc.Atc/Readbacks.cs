using System.Text.RegularExpressions;

namespace DcsAtc.Atc;

/// <summary>A value that is read back: its kind, how ATC said it ("runway 31R"), and a form to compare ("31R").</summary>
public sealed record ReadbackItem(string Kind, string Said, string Value);

/// <summary>
/// Readback checking (ICAO Doc 4444 §4.5.7.5: runway, heading, level, frequency and altimeter setting are read back):
/// the values of an ATC transmission against those in the pilot's readback. A readback value of a kind ATC gave that
/// matches none of ATC's values is a mistake; a missing value is not, since speech recognition drops words now and
/// then, and a runway read back without its side ("31") matches "31R".
/// </summary>
public static partial class Readbacks
{
    public const string Runway = "runway", Heading = "heading", Altitude = "altitude", Frequency = "frequency", Qnh = "QNH";

    private static readonly string[] DigitWords = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    /// <summary>The values in an ATC transmission, as written ("…, runway 31R, …, descend and maintain 3000 feet, …, contact Tower on 270.0").</summary>
    public static IReadOnlyList<ReadbackItem> Instructed(string text)
    {
        var items = new List<ReadbackItem>();
        foreach (Match m in RunwayText().Matches(text))
            items.Add(new(Runway, $"runway {m.Groups[1].Value}{m.Groups[2].Value}", RunwayValue(int.Parse(m.Groups[1].Value), m.Groups[2].Value)));
        foreach (Match m in HeadingText().Matches(text))
            items.Add(new(Heading, $"heading {m.Groups[1].Value}", int.Parse(m.Groups[1].Value).ToString()));
        foreach (Match m in AltitudeText().Matches(text))
            items.Add(new(Altitude, $"{m.Groups[1].Value} feet", int.Parse(m.Groups[1].Value).ToString()));
        foreach (Match m in FrequencyText().Matches(text))
            items.Add(new(Frequency, $"frequency {m.Groups[1].Value}", FrequencyValue(m.Groups[1].Value.Replace(".", ""))));
        foreach (Match m in QnhText().Matches(text))
            items.Add(new(Qnh, $"QNH {m.Groups[1].Value}", int.Parse(m.Groups[1].Value).ToString()));
        return items;
    }

    /// <summary>
    /// The values in a readback, from its normalized words (numbers digit by digit, whole thousands as "N thousand"):
    /// "runway three one right", "heading three one zero", "three thousand (feet)", "contact tower two seven zero
    /// zero", "qnh one zero one three".
    /// </summary>
    public static IReadOnlyList<ReadbackItem> Heard(IReadOnlyList<string> words)
    {
        var items = new List<ReadbackItem>();
        var used = new HashSet<int>();
        for (int i = 0; i < words.Count; i++)
        {
            int start = words[i] == "contact" ? FirstDigit(words, i + 1, 3) : i + 1;
            var (digits, next) = DigitRun(words, start);
            if (digits.Length == 0) continue;
            switch (words[i])
            {
                case "runway" when digits.Length <= 2 && int.Parse(digits) is >= 1 and <= 36:
                    string side = next < words.Count ? words[next] switch { "left" => "L", "right" => "R", "center" => "C", _ => "" } : "";
                    items.Add(new(Runway, $"runway {int.Parse(digits)}{side}", RunwayValue(int.Parse(digits), side)));
                    break;
                case "heading" when digits.Length == 3:
                    items.Add(new(Heading, $"heading {digits}", int.Parse(digits).ToString()));
                    break;
                case "qnh" when digits.Length is 3 or 4:
                    items.Add(new(Qnh, $"QNH {digits}", int.Parse(digits).ToString()));
                    break;
                case "contact" when digits.Length >= 3:
                    items.Add(new(Frequency, $"frequency {digits}", FrequencyValue(digits)));
                    break;
                default:
                    continue;
            }
            for (int j = start; j < next; j++) used.Add(j);
        }

        // Altitudes: a number with "thousand" or "feet" after it, not already part of one of the above.
        for (int i = 0; i < words.Count; i++)
        {
            if (!IsDigit(words[i]) || (i > 0 && IsDigit(words[i - 1])) || used.Contains(i)) continue;
            var (digits, next) = DigitRun(words, i);
            bool thousand = next < words.Count && words[next] == "thousand";
            int after = thousand ? next + 1 : next;
            if (!thousand && !(after < words.Count && words[after] == "feet")) continue;
            long value = long.Parse(digits) * (thousand ? 1000 : 1);
            if (value is >= 100 and <= 60000) items.Add(new(Altitude, $"{value} feet", value.ToString()));
        }
        return items;
    }

    /// <summary>
    /// The values ATC has to say again: for each kind in the readback, all of ATC's values of that kind when one read
    /// back matches none of them.
    /// </summary>
    public static IReadOnlyList<ReadbackItem> Mistakes(IReadOnlyList<ReadbackItem> instructed, IReadOnlyList<ReadbackItem> heard)
    {
        var mistakes = new List<ReadbackItem>();
        foreach (var kind in heard.Select(h => h.Kind).Distinct())
        {
            var said = instructed.Where(i => i.Kind == kind).ToList();
            if (said.Count == 0) continue;
            if (heard.Where(h => h.Kind == kind).All(h => said.Any(s => Same(kind, s.Value, h.Value)))) continue;
            mistakes.AddRange(said);
        }
        return mistakes;
    }

    private static bool Same(string kind, string a, string b)
    {
        if (kind != Runway) return a == b;
        string na = a.TrimEnd('L', 'R', 'C'), nb = b.TrimEnd('L', 'R', 'C');
        string sa = a[na.Length..], sb = b[nb.Length..];
        return na == nb && (sa.Length == 0 || sb.Length == 0 || sa == sb);
    }

    private static string RunwayValue(int number, string side) => $"{number}{side}";

    /// <summary>Frequencies compare by their digits without trailing zeros ("270.0", "two seven zero", "two seventy").</summary>
    private static string FrequencyValue(string digits) => digits.TrimEnd('0');

    private static bool IsDigit(string word) => Array.IndexOf(DigitWords, word) >= 0;

    private static int FirstDigit(IReadOnlyList<string> words, int from, int within)
    {
        for (int j = from; j < words.Count && j < from + within; j++)
            if (IsDigit(words[j])) return j;
        return from;
    }

    private static (string Digits, int Next) DigitRun(IReadOnlyList<string> words, int from)
    {
        var digits = new System.Text.StringBuilder();
        int i = from;
        for (; i < words.Count && IsDigit(words[i]); i++) digits.Append(Array.IndexOf(DigitWords, words[i]));
        return (digits.ToString(), i);
    }

    [GeneratedRegex(@"\brunway (\d{1,2})([LRC]?)\b")]
    private static partial Regex RunwayText();

    [GeneratedRegex(@"\bheading (\d{3})\b")]
    private static partial Regex HeadingText();

    [GeneratedRegex(@"\b(\d{3,5}) feet\b")]
    private static partial Regex AltitudeText();

    [GeneratedRegex(@"\bon (\d{3}\.\d{1,3})\b")]
    private static partial Regex FrequencyText();

    [GeneratedRegex(@"\bQNH (\d{3,4})\b")]
    private static partial Regex QnhText();
}
