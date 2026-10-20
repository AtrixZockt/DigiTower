using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DcsAtc.Host;

namespace DcsAtc.Gui.ViewModels;

/// <summary>A log category: its chip (shown here and in the console) and whether it goes to the log file.</summary>
public sealed partial class CategoryViewModel(LogCategory category, string name, LogViewModel owner) : ObservableObject
{
    public LogCategory Category { get; } = category;
    public string Label { get; } = AtcLog.Label(category);
    /// <summary>The longer name in the "what is logged" table.</summary>
    public string Name { get; } = name;
    public Brush Brush { get; } = Palette.Category(category);

    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _shown = true;
    [ObservableProperty] private bool _inFile = true;

    partial void OnShownChanged(bool value) => owner.CategoryChanged();
    partial void OnInFileChanged(bool value) => owner.CategoryChanged();
}

/// <summary>
/// The Log page. The chips, Debug and the "what is logged" table are settings (this window, the console and the log
/// file follow them at once); the search and "warnings only" only filter this view.
/// </summary>
public sealed partial class LogViewModel : ObservableObject
{
    private readonly AtcHost _host;
    private readonly LogFeed _feed;
    private readonly SettingsStore _store;
    private bool _loading, _saving;

    public LogViewModel(AtcHost host, LogFeed feed, SettingsStore store)
    {
        _host = host;
        _feed = feed;
        _store = store;
        (LogCategory, string)[] names =
        [
            (LogCategory.Ground, "Ground"), (LogCategory.Tower, "Tower"), (LogCategory.Approach, "Approach"), (LogCategory.Atis, "ATIS"),
            (LogCategory.Ai, "AI traffic"), (LogCategory.Farp, "FARPs"), (LogCategory.VoiceIn, "Voice requests (heard)"),
            (LogCategory.VoiceOut, "Voice (spoken)"), (LogCategory.Srs, "SRS"), (LogCategory.Bridge, "DCS bridge"), (LogCategory.App, "DigiTower"),
        ];
        foreach (var (c, name) in names) Categories.Add(new CategoryViewModel(c, name, this));
        LoadSettings();
        Rebuild();
        feed.Appended += Append;
        feed.Trimmed += dropped =>
        {
            var gone = new HashSet<LogEntry>(dropped, ReferenceEqualityComparer.Instance);
            while (Lines.Count > 0 && gone.Contains(Lines[0].Entry)) Lines.RemoveAt(0);
            Total = _feed.All.Count;
        };
        store.Changed += () =>
        {
            if (_saving) return;
            LoadSettings();
            Rebuild();
        };
    }

    public ObservableCollection<CategoryViewModel> Categories { get; } = [];
    /// <summary>The lines shown; replaced as a whole when a filter changes (thousands of single adds would be slow).</summary>
    [ObservableProperty] private ObservableCollection<LogLineViewModel> _lines = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _warningsOnly;
    [ObservableProperty] private bool _debug;
    [ObservableProperty] private bool _following = true;
    [ObservableProperty] private bool _writeFile = true;
    [ObservableProperty] private string _keepDays = "14";
    [ObservableProperty] private int _total;

    public string FollowText => Following ? "FOLLOWING" : "PAUSED";
    public string EmptyText => Total == 0 ? "Nothing logged yet." : "Nothing matches these filters.";
    public string ShownText => $"Showing {Lines.Count} of {Total} lines · the search and \"warnings only\" change this view only";
    public string LogFolder => _store.Current.ResolvedLogDirectory;
    public string LogFile => _host.LogFilePath ?? Path.Combine(LogFolder, Host.LogFile.NameFor(DateTime.Now));

    partial void OnSearchChanged(string value) => Rebuild();
    partial void OnWarningsOnlyChanged(bool value) => Rebuild();
    partial void OnFollowingChanged(bool value) => OnPropertyChanged(nameof(FollowText));
    partial void OnTotalChanged(int value)
    {
        OnPropertyChanged(nameof(ShownText));
        OnPropertyChanged(nameof(EmptyText));
    }

    partial void OnDebugChanged(bool value)
    {
        if (_loading) return;
        SaveSettings();
        Rebuild();
    }

    partial void OnWriteFileChanged(bool value) => SaveSettings();

    partial void OnKeepDaysChanged(string value)
    {
        if (int.TryParse(value, out int days) && days is >= 1 and <= 3650) SaveSettings();
    }

    internal void CategoryChanged()
    {
        if (_loading) return;
        SaveSettings();
        Rebuild();
    }

    /// <summary>The window scrolls to new lines while following (the view listens).</summary>
    public event Action? LinesAppended;

    [RelayCommand]
    private void Copy()
    {
        var text = string.Join(Environment.NewLine, Lines.Select(l => l.ToString()));
        if (text.Length > 0) Clipboard.SetText(text);
    }

    /// <summary>Copies some lines (the selected ones).</summary>
    public static void CopyLines(IEnumerable<LogLineViewModel> lines)
    {
        var text = string.Join(Environment.NewLine, lines.OrderBy(l => l.Entry.Time).Select(l => l.ToString()));
        if (text.Length > 0) Clipboard.SetText(text);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(LogFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogFolder}\"") { UseShellExecute = true });
    }

    private bool Matches(LogEntry e)
    {
        if (!_store.Current.Log.Shows(e)) return false;
        if (WarningsOnly && e.Level is not (LogLevel.Warning or LogLevel.Error)) return false;
        string q = Search.Trim();
        return q.Length == 0 || e.Text.Contains(q, StringComparison.OrdinalIgnoreCase) || AtcLog.Label(e.Category).Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void Rebuild()
    {
        Lines = new ObservableCollection<LogLineViewModel>(_feed.All.Where(Matches).Select(e => new LogLineViewModel(e)));
        Count();
        OnPropertyChanged(nameof(ShownText));
        OnPropertyChanged(nameof(LogFile));
        OnPropertyChanged(nameof(LogFolder));
        LinesAppended?.Invoke();
    }

    private void Append(IReadOnlyList<LogEntry> batch)
    {
        bool added = false;
        foreach (var e in batch)
        {
            if (!Matches(e)) continue;
            Lines.Add(new LogLineViewModel(e));
            added = true;
        }
        Count();
        OnPropertyChanged(nameof(ShownText));
        OnPropertyChanged(nameof(LogFile));
        if (added) LinesAppended?.Invoke();
    }

    private void Count()
    {
        bool debug = _store.Current.Log.Debug;
        var counts = _feed.All.Where(e => debug || e.Level != LogLevel.Debug).GroupBy(e => e.Category).ToDictionary(g => g.Key, g => g.Count());
        foreach (var c in Categories) c.Count = counts.GetValueOrDefault(c.Category);
        Total = _feed.All.Count;
    }

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            var log = _store.Current.Log;
            foreach (var c in Categories)
            {
                c.Shown = !log.Hidden.Contains(c.Category);
                c.InFile = !log.NotInFile.Contains(c.Category);
            }
            Debug = log.Debug;
            WriteFile = log.WriteFile;
            KeepDays = log.KeepDays.ToString();
        }
        finally
        {
            _loading = false;
        }
    }

    private void SaveSettings()
    {
        if (_loading) return;
        var log = _store.Current.Clone().Log;
        log.Hidden = Categories.Where(c => !c.Shown).Select(c => c.Category).ToHashSet();
        log.NotInFile = Categories.Where(c => !c.InFile).Select(c => c.Category).ToHashSet();
        log.Debug = Debug;
        log.WriteFile = WriteFile;
        if (int.TryParse(KeepDays, out int days) && days is >= 1 and <= 3650) log.KeepDays = days;
        _saving = true; // the store's Changed would reload what was just set
        try
        {
            _store.Update(s => s.Log = log);
        }
        finally
        {
            _saving = false;
        }
        _ = _host.ApplyLogSettingsAsync(log);
    }
}
