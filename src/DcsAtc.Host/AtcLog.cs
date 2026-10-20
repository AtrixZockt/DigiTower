namespace DcsAtc.Host;

/// <summary>What a log line is about: the control panel filters on these, and each can go to the console and the log file or not.</summary>
public enum LogCategory
{
    /// <summary>Starting and stopping, settings, airfield data, flights.</summary>
    App,
    /// <summary>The connection to DCS, the mission, DCS events, F10 menu picks.</summary>
    Bridge,
    Ground,
    Tower,
    Approach,
    Atis,
    /// <summary>AI departures held and released.</summary>
    Ai,
    Farp,
    /// <summary>What pilots said on SRS and how it was understood.</summary>
    VoiceIn,
    /// <summary>The controllers' voice: clips prepared and spoken.</summary>
    VoiceOut,
    /// <summary>The SRS connection and the frequencies listened on.</summary>
    Srs,
}

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public sealed record LogEntry(DateTime Time, LogCategory Category, LogLevel Level, string Text);

/// <summary>
/// The log every part of DigiTower writes to: entries go to whoever listens (the console, the log file, the control
/// panel), and the last <see cref="Capacity"/> are kept so a window opened later shows them too.
/// </summary>
public sealed class AtcLog
{
    public const int Capacity = 5000;

    private readonly LinkedList<LogEntry> _recent = new();
    private readonly Lock _lock = new();
    private readonly Func<DateTime> _now;

    public AtcLog(Func<DateTime>? clock = null) => _now = clock ?? (() => DateTime.Now);

    /// <summary>Raised for every entry, on the thread that wrote it.</summary>
    public event Action<LogEntry>? Written;

    public void Write(LogCategory category, string text, LogLevel level = LogLevel.Info)
    {
        var entry = new LogEntry(_now(), category, level, text);
        lock (_lock)
        {
            _recent.AddLast(entry);
            if (_recent.Count > Capacity) _recent.RemoveFirst();
        }
        Written?.Invoke(entry);
    }

    /// <summary>A writer for one category, for the parts that take an <c>Action&lt;string&gt;</c>.</summary>
    public Action<string> For(LogCategory category, LogLevel level = LogLevel.Info) => text => Write(category, text, level);

    /// <summary>The entries kept, oldest first.</summary>
    public IReadOnlyList<LogEntry> Recent()
    {
        lock (_lock) return [.. _recent];
    }

    /// <summary>The category a log line is shown under: "VOICE IN", "GROUND", ….</summary>
    public static string Label(LogCategory category) => category switch
    {
        LogCategory.VoiceIn => "VOICE IN",
        LogCategory.VoiceOut => "VOICE OUT",
        LogCategory.Ai => "AI",
        LogCategory.Atis => "ATIS",
        LogCategory.Farp => "FARP",
        LogCategory.Srs => "SRS",
        LogCategory.App => "DIGITOWER", // not "APP": that is Approach on the radio
        _ => category.ToString().ToUpperInvariant(),
    };
}
