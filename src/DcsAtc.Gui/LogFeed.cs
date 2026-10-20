using System.Collections.Concurrent;
using System.Windows.Threading;
using DcsAtc.Host;

namespace DcsAtc.Gui;

/// <summary>
/// Brings the log to the UI thread in batches, five times a second: the controllers write from their own threads, and
/// a busy minute must not redraw the window for every line.
/// </summary>
public sealed class LogFeed
{
    private readonly ConcurrentQueue<LogEntry> _queue = new();
    private readonly List<LogEntry> _all;
    private readonly DispatcherTimer _timer;

    public LogFeed(AtcLog log)
    {
        log.Written += _queue.Enqueue;
        _all = [.. log.Recent()];
        var known = new HashSet<LogEntry>(_all, ReferenceEqualityComparer.Instance);
        while (_queue.TryPeek(out var e) && known.Contains(e)) _queue.TryDequeue(out _);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Drain(), Dispatcher.CurrentDispatcher);
    }

    /// <summary>Everything kept, oldest first (at most <see cref="AtcLog.Capacity"/>).</summary>
    public IReadOnlyList<LogEntry> All => _all;

    /// <summary>New entries, on the UI thread.</summary>
    public event Action<IReadOnlyList<LogEntry>>? Appended;

    /// <summary>The oldest entries, dropped from <see cref="All"/>.</summary>
    public event Action<IReadOnlyList<LogEntry>>? Trimmed;

    private void Drain()
    {
        if (_queue.IsEmpty) return;
        var batch = new List<LogEntry>();
        while (_queue.TryDequeue(out var e)) batch.Add(e);
        _all.AddRange(batch);
        int over = _all.Count - AtcLog.Capacity;
        if (over > 0)
        {
            var dropped = _all.GetRange(0, over);
            _all.RemoveRange(0, over);
            Trimmed?.Invoke(dropped);
        }
        Appended?.Invoke(batch);
    }
}
