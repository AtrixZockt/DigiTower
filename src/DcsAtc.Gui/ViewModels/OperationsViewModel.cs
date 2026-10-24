using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DcsAtc.Host;

namespace DcsAtc.Gui.ViewModels;

/// <summary>A flight progress strip.</summary>
public sealed partial class StripViewModel : ObservableObject
{
    public StripViewModel(string key, Strip strip)
    {
        Key = key;
        Update(strip);
    }

    public string Key { get; }

    [ObservableProperty] private string _tag = "";
    [ObservableProperty] private Brush _tagBrush = Brushes.Transparent;
    [ObservableProperty] private Brush _tagTextBrush = Brushes.Transparent;
    [ObservableProperty] private string _callsign = "";
    [ObservableProperty] private string _type = "";
    [ObservableProperty] private string _clearance = "";
    [ObservableProperty] private string? _note;
    [ObservableProperty] private string _runway = "";
    [ObservableProperty] private string _elapsed = "";
    private DateTime _since;

    public void Update(Strip s)
    {
        Tag = s.Kind == StripKind.Departure ? "DEP" : "ARR";
        (TagBrush, TagTextBrush) = s.Ai ? (Palette.AiTag, Palette.AiTagText)
            : s.Kind == StripKind.Departure ? (Palette.DepTag, Palette.DepTagText) : (Palette.ArrTag, Palette.ArrTagText);
        Callsign = s.Callsign;
        Type = s.Ai ? $"{s.Type} · AI" : s.Type;
        Clearance = s.Clearance;
        Note = s.Note;
        Runway = s.Runway ?? "";
        _since = s.Since;
        Tick();
    }

    public void Tick()
    {
        var t = DateTime.Now - _since;
        Elapsed = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }
}

/// <summary>One position's strips: GROUND, TOWER, APPROACH (or the FARP's one controller).</summary>
public sealed class ColumnViewModel(string title, string? frequency)
{
    public string Title { get; } = title;
    public string? Frequency { get; set; } = frequency;
    public ObservableCollection<StripViewModel> Strips { get; } = [];
}

/// <summary>An airfield's tab: its name and how many strips it has.</summary>
public sealed partial class AirfieldTabViewModel(string name) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] private int _count;
    public AirfieldStrips? Strips { get; set; }
}

/// <summary>The Operations page: the mission, the airfields' strips, the latest log lines.</summary>
public sealed partial class OperationsViewModel : ObservableObject
{
    private const int RecentLines = 7;
    private readonly AtcHost _host;
    private readonly SettingsStore _store;
    private readonly MainViewModel _main;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private AtcSnapshot? _pending;

    public OperationsViewModel(AtcHost host, LogFeed feed, SettingsStore store, MainViewModel main)
    {
        _host = host;
        _store = store;
        _main = main;
        Setup = new GettingStartedViewModel(host, store, main);
        host.SnapshotChanged += s =>
        {
            // Many messages a second: show the latest one, once per redraw.
            if (Interlocked.Exchange(ref _pending, s) is null) _dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyPending);
        };
        foreach (var e in feed.All) AddRecent(e);
        feed.Appended += batch =>
        {
            foreach (var e in batch) AddRecent(e);
        };
        store.Changed += () =>
        {
            Recent.Clear();
            foreach (var e in feed.All) AddRecent(e);
        };
        Apply(AtcSnapshot.Empty);
    }

    /// <summary>Start / stop, how long it has run, the status lights.</summary>
    public MainViewModel Main => _main;

    /// <summary>The getting-started checklist, while something essential is missing.</summary>
    public GettingStartedViewModel Setup { get; }

    public ObservableCollection<AirfieldTabViewModel> Airfields { get; } = [];
    public ObservableCollection<ColumnViewModel> Columns { get; } = [];
    public ObservableCollection<LogLineViewModel> Recent { get; } = [];

    [ObservableProperty] private AirfieldTabViewModel? _selected;
    [ObservableProperty] private string _caption = "MISSION";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _atis;
    [ObservableProperty] private string? _emptyMessage;

    partial void OnSelectedChanged(AirfieldTabViewModel? value) => ShowColumns();

    [RelayCommand]
    private void OpenLog() => _main.Page = Page.Log;

    public void Tick()
    {
        Setup.Tick();
        foreach (var c in Columns)
            foreach (var s in c.Strips) s.Tick();
        if (_pending is null) Describe(_host.Snapshot); // started, stopped: before any message says so
    }

    private void ApplyPending()
    {
        if (Interlocked.Exchange(ref _pending, null) is { } s) Apply(s);
    }

    private void Apply(AtcSnapshot s)
    {
        Describe(s);
        string? keep = Selected?.Name;
        Airfields.Sync(s.Airfields, t => t.Name, f => f.Name, f => new AirfieldTabViewModel(f.Name), (_, _) => { });
        for (int i = 0; i < s.Airfields.Count; i++)
        {
            Airfields[i].Strips = s.Airfields[i];
            Airfields[i].Count = s.Airfields[i].Count;
        }
        var selected = Airfields.FirstOrDefault(a => a.Name == keep) ?? Airfields.FirstOrDefault();
        if (selected != Selected) Selected = selected;
        else ShowColumns();
    }

    private void Describe(AtcSnapshot s)
    {
        bool running = _host.Running;
        (Caption, Title) = s.Connected
            ? ("MISSION", $"{Theatre(s.Theatre)} · {s.Mission ?? "untitled"}")
            : running ? ("WAITING FOR DCS", "No mission yet") : ("ATC STOPPED", "No mission");
        EmptyMessage = s.Airfields.Count > 0 ? null
            : !running ? "The ATC is stopped. Start it to see the traffic."
            : !s.Connected ? $"Waiting for DCS on {_host.Settings.BridgeHost}:{_host.Settings.BridgePort}: start a mission on a DCS with the bridge installed (Install page)."
            : "No airfield on the air yet. An airfield gets its tab when a player is near it.";
    }

    private void ShowColumns()
    {
        var f = Selected?.Strips;
        Atis = f?.Atis;
        if (f is null)
        {
            Columns.Clear();
            return;
        }
        var wanted = f.IsFarp
            ? new[] { ("FARP", f.TowerFrequency, f.Tower) }
            : [("GROUND", f.GroundFrequency, f.Ground), ("TOWER", f.TowerFrequency, f.Tower), ("APPROACH", f.ApproachFrequency, f.Approach)];
        if (Columns.Count != wanted.Length || Columns.Select(c => c.Title).Where((t, i) => t != wanted[i].Item1).Any())
        {
            Columns.Clear();
            foreach (var (title, frequency, _) in wanted) Columns.Add(new ColumnViewModel(title, frequency));
        }
        for (int i = 0; i < wanted.Length; i++)
        {
            var column = Columns[i];
            if (column.Frequency != wanted[i].Item2)
            {
                Columns[i] = column = new ColumnViewModel(wanted[i].Item1, wanted[i].Item2);
            }
            column.Strips.Sync(wanted[i].Item3, v => v.Key, Key, s => new StripViewModel(Key(s), s), (v, s) => v.Update(s));
        }

        static string Key(Strip s) => $"{s.Kind}|{s.Callsign}";
    }

    private void AddRecent(LogEntry e)
    {
        if (!_store.Current.Log.Shows(e) || e.Level == LogLevel.Debug) return;
        Recent.Insert(0, new LogLineViewModel(e));
        while (Recent.Count > RecentLines) Recent.RemoveAt(Recent.Count - 1);
    }

    /// <summary>"PersianGulf" → "Persian Gulf".</summary>
    private static string Theatre(string? theatre) => theatre switch
    {
        null => "DCS",
        "PersianGulf" => "Persian Gulf",
        "MarianaIslands" => "Mariana Islands",
        "SinaiMap" => "Sinai",
        "GermanyCW" => "Germany (Cold War)",
        "Kola" => "Kola",
        _ => System.Text.RegularExpressions.Regex.Replace(theatre, "(?<=[a-z])(?=[A-Z])", " "),
    };
}
