namespace DcsAtc.Host;

/// <summary>
/// Writes the log to one file per day (digitower-2026-10-20.log) and deletes files older than the days to keep.
/// </summary>
public sealed class LogFile : IDisposable
{
    private readonly string _directory;
    private readonly Lock _lock = new();
    private StreamWriter? _writer;
    private DateOnly _day;
    private bool _disposed;

    public LogFile(string directory, int keepDays, Func<DateTime>? clock = null)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        var now = (clock ?? (() => DateTime.Now))();
        foreach (var file in Directory.EnumerateFiles(directory, "digitower-*.log"))
        {
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file)["digitower-".Length..], "yyyy-MM-dd", out var day)
                && day < DateOnly.FromDateTime(now).AddDays(-Math.Max(keepDays, 1)))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Open elsewhere: next time.
                }
            }
        }
    }

    public static string NameFor(DateTime time) => $"digitower-{time:yyyy-MM-dd}.log";

    /// <summary>The file written today.</summary>
    public string CurrentPath => Path.Combine(_directory, NameFor(DateTime.Now));

    public void Write(LogEntry e)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var day = DateOnly.FromDateTime(e.Time);
            if (_writer is null || day != _day)
            {
                _writer?.Dispose();
                _writer = new StreamWriter(new FileStream(Path.Combine(_directory, NameFor(e.Time)), FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                _day = day;
            }
            string level = e.Level switch { LogLevel.Warning => "WARNING  ", LogLevel.Error => "ERROR  ", LogLevel.Debug => "debug  ", _ => "" };
            _writer.WriteLine($"{e.Time:HH:mm:ss}  {AtcLog.Label(e.Category),-9}  {level}{e.Text}");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
