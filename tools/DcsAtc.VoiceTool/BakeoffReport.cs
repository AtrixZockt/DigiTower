using System.Net;
using System.Text;
using DcsAtc.Recognition;

/// <summary>The bake-off as a local page: the recognizers side by side, then every call with what each one heard.</summary>
static class BakeoffReport
{
    public static string Html(CallCorpus corpus, IReadOnlyList<RecognizerScore> scores, string reportDirectory)
    {
        static string H(string s) => WebUtility.HtmlEncode(s);
        var ranked = scores.Where(s => s.Run.Error is null).OrderByDescending(s => s.Understood).ThenBy(s => s.MedianSeconds).ToList();

        var summary = new StringBuilder();
        foreach (var s in ranked)
        {
            summary.Append($"<tr><th>{H(s.Run.Label)}<small>{H(s.Run.Recognizer)} · {H(s.Run.Runtime)}, {s.Run.Threads} threads</small></th>" +
                           $"<td class=\"num strong\">{s.Understood}/{s.Calls}</td><td class=\"num\">{s.UnderstoodTolerant}/{s.Calls}</td><td class=\"num strong\">{s.Handled}/{s.Calls}</td><td class=\"num\">{s.KeyRate:P0}</td><td class=\"num\">{s.WordErrorRate:P1}</td>" +
                           $"<td class=\"num\">{s.MedianSeconds:F2} s</td><td class=\"num\">{s.P95Seconds:F2} s</td><td class=\"num\">{s.RealTimeFactor:F2}</td>" +
                           $"<td class=\"num\">{s.CpuSecondsPerCall:F2} s</td><td class=\"num\">{s.Run.FirstCallSeconds:F1} s</td><td class=\"num\">{s.Run.LoadSeconds:F1} s</td>" +
                           $"<td class=\"num\">{s.Run.PeakMemoryMb:F0} MB</td></tr>");
        }
        foreach (var s in scores.Where(s => s.Run.Error is not null))
            summary.Append($"<tr><th>{H(s.Run.Label)}<small>{H(s.Run.Recognizer)}</small></th><td colspan=\"12\" class=\"bad\">failed: {H(s.Run.Error!)}</td></tr>");

        var calls = new StringBuilder();
        foreach (var recorded in corpus.Calls)
        {
            var results = ranked.Select(s => (s, c: s.CallScores.FirstOrDefault(c => c.Recorded.Number == recorded.Number))).Where(x => x.c is not null).ToList();
            bool allUnderstood = results.All(x => x.c!.MissingKeys.Count == 0);
            string audio = Path.GetRelativePath(reportDirectory, corpus.PathOf(recorded)).Replace('\\', '/');
            calls.Append($"<section class=\"call{(allUnderstood ? " all-ok" : "")}\"><header><span class=\"n\">{recorded.Number}</span>" +
                         $"<span class=\"said\">{Expected(recorded)}</span><audio controls preload=\"none\" src=\"{H(audio)}\"></audio>" +
                         $"<small>{recorded.Seconds:F1} s · {H(recorded.Speaker ?? "")} · level {recorded.LevelDb:F0} dBFS</small></header><table>");
            foreach (var (s, c) in results)
            {
                var words = string.Join(' ', c!.Words.Select((w, i) => c.Correct[i] ? H(w) : $"<mark>{H(w)}</mark>"));
                string verdict = c.MissingKeys.Count == 0 ? "<td class=\"ok\">✓</td>" : c.MissingTolerant.Count == 0 ? "<td class=\"near\" title=\"near-miss: tolerant only\">≈</td>" : "<td class=\"bad\">✗</td>";
                string missing = (c.MissingKeys.Count == 0 ? "" : $"<div class=\"missing\">missing: {H(string.Join(" · ", c.MissingKeys))}</div>") +
                                 (c.Reaction == c.ExpectedReaction ? "" : $"<div class=\"missing\">ATC: {H(c.Reaction)} (should be: {H(c.ExpectedReaction)})</div>");
                calls.Append($"<tr>{verdict}<th>{H(s.Run.Recognizer)}</th><td class=\"heard\" title=\"{H(c.Result.Text)}\">{(words.Length == 0 ? "<em>(nothing)</em>" : words)}{missing}</td>" +
                             $"<td class=\"num\">{c.Result.Seconds:F2} s</td></tr>");
            }
            calls.Append("</table></section>");
        }

        string speakers = string.Join(", ", corpus.Calls.Select(c => c.Speaker).Distinct());
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Recognition Bake-off</title>
            <style>
              :root { --bg: #f6f7f9; --fg: #1d2330; --muted: #5b6475; --line: #d9dde5; --card: #fff; --ok: #1a7f37; --bad: #c62828; --mark: #ffd8d6; }
              @media (prefers-color-scheme: dark) { :root { --bg: #15181e; --fg: #e6e9ef; --muted: #9aa3b5; --line: #2c323d; --card: #1c2028; --ok: #4cc06a; --bad: #ff6b6b; --mark: #5c2626; } }
              body { margin: 0; padding: 24px 16px; background: var(--bg); color: var(--fg); font: 15px/1.45 system-ui, sans-serif; }
              main { max-width: 1200px; margin: 0 auto; }
              h1 { font-size: 22px; margin: 0 0 4px; } h2 { font-size: 17px; margin: 28px 0 8px; } p { color: var(--muted); margin: 4px 0; }
              .wrap { overflow-x: auto; background: var(--card); border: 1px solid var(--line); border-radius: 8px; }
              table { border-collapse: collapse; width: 100%; } th, td { padding: 6px 10px; border-bottom: 1px solid var(--line); text-align: left; vertical-align: top; }
              thead th { font-size: 13px; color: var(--muted); font-weight: 600; } th small { display: block; font-weight: 400; color: var(--muted); font-size: 12px; }
              .num { text-align: right; white-space: nowrap; font-variant-numeric: tabular-nums; } .strong { font-weight: 700; }
              .ok { color: var(--ok); } .bad { color: var(--bad); } .near { color: var(--muted); font-weight: 700; } mark { background: var(--mark); color: inherit; border-radius: 3px; padding: 0 2px; }
              .call { background: var(--card); border: 1px solid var(--line); border-radius: 8px; margin: 10px 0; overflow-x: auto; }
              .call header { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 12px; padding: 10px 12px; border-bottom: 1px solid var(--line); }
              .call header small { color: var(--muted); } .n { font-weight: 700; color: var(--muted); min-width: 2ch; } .said { flex: 1 1 320px; }
              .said b { background: color-mix(in srgb, var(--ok) 15%, transparent); border-radius: 3px; padding: 0 2px; font-weight: 600; }
              .call table th { width: 160px; font-weight: 500; font-size: 13px; } .call td.ok, .call td.bad { width: 1.5em; }
              .heard { font-family: ui-monospace, Consolas, monospace; font-size: 13px; } .missing { color: var(--bad); font-family: system-ui, sans-serif; font-size: 12px; }
              audio { height: 30px; max-width: 100%; } label { color: var(--muted); }
              body.only-misses .all-ok { display: none; }
            </style></head>
            <body><main>
            <h1>Speech recognition bake-off</h1>
            <p>{{corpus.Calls.Count}} calls by {{H(speakers)}}. <b>Understood</b>: calls where every key phrase (highlighted in the call) came through, so ATC could act on them.
            <b>Tolerant</b>: the same, accepting near-misses a parser could take ("one way three one right" for "runway three one right"; ≈ in the calls).
            <b>ATC right</b>: ATC's reaction (the parser) is the one the call as written gets: the request, no answer to a readback, "say again".
            <b>WER</b>: word error rate after normalising (numbers digit by digit, taxiways phonetic). Times are per call on this PC, recognizer at below-normal priority.</p>
            <h2>Recognizers</h2>
            <div class="wrap"><table><thead><tr><th>Recognizer</th><th class="num">Understood</th><th class="num">Tolerant</th><th class="num">ATC right</th><th class="num">Key phrases</th><th class="num">WER</th>
            <th class="num">Median</th><th class="num">95%</th><th class="num">× real time</th><th class="num">CPU / call</th><th class="num">First call</th><th class="num">Load</th><th class="num">Memory</th></tr></thead>
            <tbody>{{summary}}</tbody></table></div>
            <h2>Calls</h2>
            <p><label><input type="checkbox" id="misses"> only calls some recognizer missed</label> · <mark>marked</mark> words differ from what was said (hover for the raw text).</p>
            {{calls}}
            </main>
            <script>
              document.getElementById('misses').addEventListener('change', e => document.body.classList.toggle('only-misses', e.target.checked));
              document.addEventListener('play', e => document.querySelectorAll('audio').forEach(a => { if (a !== e.target) a.pause(); }), true);
            </script>
            </body></html>
            """;
    }

    // The call as said, key phrases in bold.
    private static string Expected(RecordedCall call)
    {
        string text = WebUtility.HtmlEncode(call.Text);
        foreach (var key in call.Keys.OrderByDescending(k => k.Length))
        {
            string encoded = WebUtility.HtmlEncode(key);
            int at = text.IndexOf(encoded, StringComparison.Ordinal);
            if (at >= 0 && !text[..at].EndsWith("<b>", StringComparison.Ordinal)) text = text[..at] + "<b>" + encoded + "</b>" + text[(at + encoded.Length)..];
        }
        return text;
    }
}
