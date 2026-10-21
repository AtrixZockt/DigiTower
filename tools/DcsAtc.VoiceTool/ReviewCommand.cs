using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// review: goes through the calls DigiTower heard in flight (out/heard/&lt;day&gt;/heard.jsonl) so their texts can be
/// used to tune the recognizer: each call is played and its text shown; Enter keeps it, a typed text corrects it.
/// The recordings are only read; the result is out/train/flight/labels.jsonl (one line per call, resumable).
/// </summary>
static class ReviewCommand
{
    public static int Run(Options o)
    {
        string heard = o.Get("heard", Path.Combine("out", "heard"));
        string outDir = o.Get("out", Path.Combine("out", "train", "flight"));
        string? from = o.Find("from");
        Directory.CreateDirectory(outDir);
        string labels = Path.Combine(outDir, "labels.jsonl");
        var done = File.Exists(labels)
            ? File.ReadLines(labels).Select(l => JsonNode.Parse(l)?["file"]?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        var calls = new List<(string File, string Text, string Sender, double Seconds)>();
        foreach (var day in Directory.Exists(heard) ? Directory.GetDirectories(heard).Order().ToArray() : [])
        {
            string list = Path.Combine(day, "heard.jsonl");
            if (!File.Exists(list)) continue;
            foreach (var line in File.ReadLines(list))
            {
                if (JsonNode.Parse(line) is not { } n || n["file"]?.GetValue<string>() is not { } file) continue;
                string path = Path.GetFullPath(Path.Combine(day, file));
                string sender = n["sender"]?.GetValue<string>() ?? "";
                if (!File.Exists(path) || (from is not null && !sender.Contains(from, StringComparison.OrdinalIgnoreCase))) continue;
                calls.Add((path, n["text"]?.GetValue<string>() ?? "", sender, n["seconds"]?.GetValue<double>() ?? 0));
            }
        }
        var todo = calls.Where(c => !done.Contains(c.File)).ToList();
        Console.WriteLine($"""
            {calls.Count} calls heard in flight, {calls.Count - todo.Count} already reviewed; results go to {Path.GetFullPath(labels)}.
            Each call is played and its text shown. Enter: the text is right. Or type the right text and Enter.
            R: play again. S: skip it (not a call to ATC, someone else, unusable). Q: quit (run again to continue).

            """);
        int kept = 0, fixedCount = 0, skipped = 0;
        for (int i = 0; i < todo.Count; i++)
        {
            var c = todo[i];
            Console.WriteLine($"[{i + 1}/{todo.Count}] {c.Sender}, {c.Seconds:F1} s");
            Console.WriteLine($"    heard: {c.Text}");
            Play(c.File);
            while (true)
            {
                Console.Write("    > ");
                string answer = (Console.ReadLine() ?? "q").Trim();
                if (answer.Equals("r", StringComparison.OrdinalIgnoreCase))
                {
                    Play(c.File);
                    continue;
                }
                if (answer.Equals("q", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"stopped: {kept} kept, {fixedCount} corrected, {skipped} skipped this time.");
                    return 0;
                }
                bool skip = answer.Equals("s", StringComparison.OrdinalIgnoreCase);
                string text = answer.Length == 0 || skip ? c.Text : answer;
                var entry = new JsonObject { ["file"] = c.File, ["text"] = text, ["skip"] = skip, ["heard"] = c.Text, ["sender"] = c.Sender };
                File.AppendAllText(labels, entry.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
                if (skip) skipped++;
                else if (answer.Length == 0) kept++;
                else fixedCount++;
                break;
            }
        }
        Console.WriteLine($"done: {kept} kept, {fixedCount} corrected, {skipped} skipped this time.");
        return 0;
    }

    // Plays a WAV file through the default output and waits until it has finished.
    private static void Play(string path)
    {
        const uint SndFilename = 0x00020000, SndSync = 0x0000, SndNoDefault = 0x0002;
        if (OperatingSystem.IsWindows()) PlaySound(path, IntPtr.Zero, SndFilename | SndSync | SndNoDefault);
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string sound, IntPtr module, uint flags);
}
