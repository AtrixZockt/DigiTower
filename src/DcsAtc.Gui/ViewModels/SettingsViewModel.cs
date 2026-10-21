using System.ComponentModel;
using System.Globalization;
using System.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DcsAtc.Host;
using DcsAtc.Voice;
using Microsoft.Win32;

namespace DcsAtc.Gui.ViewModels;

/// <summary>A choice in the voice or recognizer list; a null id is "the default".</summary>
public sealed record Choice(string? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Settings page: a copy of the settings to edit, saved to the file on Save. The ATC uses them from its next start
/// (Restart the ATC applies them now). The log settings are on the Log page, the bridge's on the Install page.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AtcHost _host;
    private readonly SettingsStore _store;
    private readonly MainViewModel _main;
    private bool _loading;
    private ModelStore? _models;

    public SettingsViewModel(AtcHost host, SettingsStore store, MainViewModel main)
    {
        _host = host;
        _store = store;
        _main = main;
        Load(store.Current);
        store.Changed += () =>
        {
            if (!Dirty) Load(store.Current);
            RefreshRestart();
        };
    }

    public string FilePath => _store.Path;

    // ---- start
    [ObservableProperty] private bool _startAtcOnOpen;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _checkForUpdates;
    /// <summary>Not in the settings file: Windows' own sign-in entry (<see cref="WindowsStartup"/>).</summary>
    [ObservableProperty] private bool _startWithWindows;
    private readonly WindowsStartup _startup = new();

    // ---- DCS
    [ObservableProperty] private string _dcsDirectory = "";
    [ObservableProperty] private string _bridgeHost = "";
    [ObservableProperty] private string _bridgePort = "";

    // ---- radio
    [ObservableProperty] private string _srs = "";
    [ObservableProperty] private bool _textOnly;
    [ObservableProperty] private Choice? _voice;
    [ObservableProperty] private IReadOnlyList<Choice> _voices = [];
    [ObservableProperty] private string? _voiceTestState;
    [ObservableProperty] private bool _aiVoice;
    [ObservableProperty] private Choice? _aiVoiceChoice;
    [ObservableProperty] private IReadOnlyList<Choice> _aiVoices = [];
    [ObservableProperty] private string? _aiVoiceTestState;

    // ---- voice requests
    [ObservableProperty] private bool _voiceRequests;
    [ObservableProperty] private Choice? _recognizer;
    [ObservableProperty] private IReadOnlyList<Choice> _recognizers = [];
    [ObservableProperty] private string _recordingsDirectory = "";
    [ObservableProperty] private bool _checkReadbacks;

    // ---- traffic
    [ObservableProperty] private bool _manageAi;
    [ObservableProperty] private bool _mapDrawings;
    [ObservableProperty] private bool _keepStockAtc;
    [ObservableProperty] private string _silenceAirbases = "";

    // ---- approach
    [ObservableProperty] private double _approachRequestRange;
    [ObservableProperty] private double _approachServiceRadius;

    // ---- chart data
    [ObservableProperty] private string _dataDirectory = "";
    [ObservableProperty] private string _dataSummary = "";

    // ---- state
    [ObservableProperty] private bool _dirty;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _saved;
    [ObservableProperty] private bool _needsRestart;

    public string? LoadError => _store.LoadError is { } e ? $"The settings file could not be read, so the defaults are shown: {e}. Save replaces it." : null;

    /// <summary>What the empty fields mean: the folders found on their own.</summary>
    public string DcsDirectoryHint => DigiTowerSettings.FindDcsDirectory() is { } d ? $"Empty: found on its own ({d})." : "Not found on its own: set the DCS install folder.";
    public string DataDirectoryHint => DigiTowerSettings.FindDataDirectory() is { } d ? $"Empty: the one beside DigiTower ({d})." : "Not found beside DigiTower: set the folder.";
    public string RecordingsHint => $"Empty: {new DigiTowerSettings { DataDirectory = NullIfEmpty(DataDirectory) }.ResolvedRecordingsDirectory}";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(Dirty) or nameof(Error) or nameof(Saved) or nameof(NeedsRestart) or nameof(VoiceTestState)
            or nameof(AiVoiceTestState) or nameof(Voices) or nameof(AiVoices) or nameof(Recognizers) or nameof(DataSummary)) return;
        Dirty = true;
        Saved = null;
        if (e.PropertyName == nameof(DataDirectory))
        {
            Catalogs();
            OnPropertyChanged(nameof(RecordingsHint));
        }
    }

    /// <summary>The page is shown: the voice lists again (a download on the Install page adds to them).</summary>
    public void Refresh()
    {
        if (!Dirty) Load(_store.Current);
        else Catalogs();
        RefreshRestart();
    }

    public void RefreshRestart() =>
        NeedsRestart = _host.Running && !Same(_host.Settings, _store.Current);

    [RelayCommand]
    private void Revert()
    {
        Load(_store.Current);
        Saved = null;
    }

    [RelayCommand]
    private void Save()
    {
        Error = Validate();
        if (Error is not null) return;
        var s = _store.Current.Clone();
        Apply(s);
        try
        {
            _store.Save(s);
            if (StartWithWindows)
                _startup.Enable(WindowsStartup.LauncherFor(Environment.ProcessPath!), _store.Path == DigiTowerSettings.DefaultPath ? null : _store.Path);
            else _startup.Disable();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Error = $"Could not save: {ex.Message}";
            return;
        }
        Load(_store.Current);
        OnPropertyChanged(nameof(LoadError));
        RefreshRestart();
        Saved = NeedsRestart ? "Saved. The ATC uses them from its next start." : "Saved.";
    }

    [RelayCommand]
    private async Task Restart()
    {
        await _main.RestartAsync();
        RefreshRestart();
        Saved = null;
    }

    [RelayCommand]
    private void BrowseDcs()
    {
        var dialog = new OpenFolderDialog { Title = "The DCS install folder (with Mods\\terrains)" };
        if (DigiTowerSettings.FindDcsDirectory() is { } found) dialog.InitialDirectory = found;
        if (dialog.ShowDialog() == true) DcsDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private void BrowseData()
    {
        var dialog = new OpenFolderDialog { Title = "DigiTower's data folder (with airfields and voice)" };
        if (dialog.ShowDialog() == true) DataDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private void BrowseRecordings()
    {
        var dialog = new OpenFolderDialog { Title = "Where to keep what was heard" };
        if (dialog.ShowDialog() == true) RecordingsDirectory = dialog.FolderName;
    }

    /// <summary>Says a sample on this PC's speakers with the chosen voice.</summary>
    [RelayCommand]
    private async Task TestVoice() => VoiceTestState = await SpeakAsync(Voice?.Id,
        "Hangman 1-2, Al Dhafra Ground, taxi to holding point runway 31 right via Alpha, Bravo. QNH 1013.", cockpit: false, s => VoiceTestState = s);

    /// <summary>The same with the AI pilots' voice, as they sound (with the cockpit sound).</summary>
    [RelayCommand]
    private async Task TestAiVoice() => AiVoiceTestState = await SpeakAsync(AiVoiceChoice?.Id ?? AutomaticAiVoice()?.Id,
        "Al Dhafra Approach, Colt 2-1, 14 miles north of Al Dhafra, inbound for landing.", cockpit: true, s => AiVoiceTestState = s);

    // Plays a sample; returns what went wrong (null: fine).
    private async Task<string?> SpeakAsync(string? id, string text, bool cockpit, Action<string> state)
    {
        var catalog = _models?.Voices;
        if (catalog is null) return "No voices (data\\voice\\voices.json not found).";
        var info = catalog.Find(id ?? catalog.Default);
        if (info is null || !catalog.IsAvailable(info)) return "Not downloaded: download it on the Install page.";
        state("Speaking…");
        try
        {
            string wav = await Task.Run(() =>
            {
                using var voice = catalog.Open(info);
                var clip = voice.Render(text);
                if (cockpit) clip = Audio.Cockpit(clip);
                string path = Path.Combine(Path.GetTempPath(), "digitower-voice-test.wav");
                Wav.Write(path, clip);
                return path;
            });
            using var player = new SoundPlayer(wav);
            await Task.Run(player.PlaySync);
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or NotSupportedException or DllNotFoundException)
        {
            return $"Could not speak: {ex.Message}";
        }
    }

    /// <summary>The AI pilots' voice when none is chosen: the first downloaded of the usual ones that is not the controllers'.</summary>
    private VoiceInfo? AutomaticAiVoice()
    {
        if (_models?.Voices is not { } v) return null;
        string controllers = Voice?.Id ?? v.Default;
        return DigiTowerSettings.AiVoiceChoices.Select(v.Find).OfType<VoiceInfo>().FirstOrDefault(x => x.Id != controllers && v.IsAvailable(x));
    }

    private void Load(DigiTowerSettings s)
    {
        _loading = true;
        try
        {
            StartAtcOnOpen = s.StartAtcOnOpen;
            MinimizeToTray = s.MinimizeToTray;
            CheckForUpdates = s.CheckForUpdates;
            StartWithWindows = _startup.IsEnabled;
            DcsDirectory = s.DcsDirectory ?? "";
            BridgeHost = s.BridgeHost;
            BridgePort = s.BridgePort.ToString(CultureInfo.InvariantCulture);
            Srs = s.Srs;
            TextOnly = !s.Voice;
            AiVoice = s.AiVoice;
            VoiceRequests = s.VoiceRequests;
            RecordingsDirectory = s.RecordingsDirectory ?? "";
            CheckReadbacks = s.CheckReadbacks;
            ManageAi = s.ManageAi;
            MapDrawings = s.MapDrawings;
            KeepStockAtc = s.KeepStockAtc;
            SilenceAirbases = string.Join(Environment.NewLine, s.SilenceAirbases);
            ApproachRequestRange = s.ApproachRequestRange;
            ApproachServiceRadius = s.ApproachServiceRadius;
            DataDirectory = s.DataDirectory ?? "";
            Catalogs(s.VoiceId, s.Recognizer, s.AiVoiceId);
            Dirty = false;
            Error = null;
            OnPropertyChanged(nameof(RecordingsHint));
        }
        finally
        {
            _loading = false;
        }
    }

    // The voices and recognizers to choose from, from the data folder's catalogs.
    private void Catalogs(string? voiceId = null, string? recognizerId = null, string? aiVoiceId = null)
    {
        bool loading = _loading;
        _loading = true;
        try
        {
            voiceId ??= Voice?.Id;
            aiVoiceId ??= AiVoiceChoice?.Id;
            recognizerId ??= Recognizer?.Id;
            try
            {
                _models = ModelStore.Load(new DigiTowerSettings { DataDirectory = NullIfEmpty(DataDirectory) });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                _models = null;
            }
            var v = _models?.Voices;
            Voices = v is null ? [new Choice(null, "Default")]
                : [new Choice(null, $"Default ({v.Find(v.Default)?.Label ?? v.Default})"),
                   .. v.Voices.Select(x => new Choice(x.Id, v.IsAvailable(x) ? x.Label : $"{x.Label} (not downloaded)"))];
            Voice = Voices.FirstOrDefault(c => c.Id is not null && c.Id.Equals(voiceId, StringComparison.OrdinalIgnoreCase)) ?? Voices[0];
            AiVoices = v is null ? [new Choice(null, "Automatic")]
                : [new Choice(null, $"Automatic ({AutomaticAiVoice()?.Label ?? "none downloaded"})"), .. Voices.Skip(1)];
            AiVoiceChoice = AiVoices.FirstOrDefault(c => c.Id is not null && c.Id.Equals(aiVoiceId, StringComparison.OrdinalIgnoreCase)) ?? AiVoices[0];
            var r = _models?.Recognizers;
            Recognizers = r is null ? [new Choice(null, "Default")]
                : [new Choice(null, $"Default ({r.Find(r.Default)?.Label ?? r.Default})"),
                   .. r.Recognizers.Select(x => new Choice(x.Id, r.IsAvailable(x) ? x.Label : $"{x.Label} (not downloaded)"))];
            Recognizer = Recognizers.FirstOrDefault(c => c.Id is not null && c.Id.Equals(recognizerId, StringComparison.OrdinalIgnoreCase)) ?? Recognizers[0];

            string? data = NullIfEmpty(DataDirectory) ?? DigiTowerSettings.FindDataDirectory();
            int labels = data is not null && Directory.Exists(Path.Combine(data, "airfields"))
                ? Directory.EnumerateFiles(Path.Combine(data, "airfields"), "*.labels.json", SearchOption.AllDirectories).Count() : 0;
            DataSummary = data is null ? "No data folder found." : $"{labels} airfields with taxiway names.";
        }
        finally
        {
            _loading = loading;
        }
    }

    private string? Validate()
    {
        if (!int.TryParse(BridgePort, out int port) || port is < 1 or > 65535) return "The bridge port must be a number from 1 to 65535.";
        if (string.IsNullOrWhiteSpace(BridgeHost)) return "The bridge address is empty.";
        var srs = Srs.Split(':');
        if (srs[0].Trim().Length == 0 || srs.Length > 2 || (srs.Length == 2 && (!int.TryParse(srs[1], out int p) || p is < 1 or > 65535)))
            return "The SRS server is \"address:port\", e.g. 127.0.0.1:5002.";
        if (NullIfEmpty(DcsDirectory) is { } dcs && !Directory.Exists(Path.Combine(dcs, "Mods", "terrains"))) return $"{dcs} is not a DCS install (no Mods\\terrains).";
        if (NullIfEmpty(DataDirectory) is { } data && !Directory.Exists(Path.Combine(data, "airfields"))) return $"{data} is not DigiTower's data folder (no airfields).";
        return null;
    }

    private void Apply(DigiTowerSettings s)
    {
        s.StartAtcOnOpen = StartAtcOnOpen;
        s.MinimizeToTray = MinimizeToTray;
        s.CheckForUpdates = CheckForUpdates;
        s.DcsDirectory = NullIfEmpty(DcsDirectory);
        s.BridgeHost = BridgeHost.Trim();
        s.BridgePort = int.Parse(BridgePort, CultureInfo.InvariantCulture);
        s.Srs = Srs.Trim();
        s.Voice = !TextOnly;
        s.VoiceId = Voice?.Id;
        s.AiVoice = AiVoice;
        s.AiVoiceId = AiVoiceChoice?.Id;
        s.VoiceRequests = VoiceRequests;
        s.Recognizer = Recognizer?.Id;
        s.RecordingsDirectory = NullIfEmpty(RecordingsDirectory);
        s.CheckReadbacks = CheckReadbacks;
        s.ManageAi = ManageAi;
        s.MapDrawings = MapDrawings;
        s.KeepStockAtc = KeepStockAtc;
        s.SilenceAirbases = SilenceAirbases.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        s.ApproachRequestRange = ApproachRequestRange;
        s.ApproachServiceRadius = ApproachServiceRadius;
        s.DataDirectory = NullIfEmpty(DataDirectory);
    }

    /// <summary>Whether the ATC would run the same (the log settings apply at once and do not count).</summary>
    private static bool Same(DigiTowerSettings a, DigiTowerSettings b)
    {
        var x = a.Clone();
        var y = b.Clone();
        x.Log = y.Log = new LogSettings();
        x.StartAtcOnOpen = y.StartAtcOnOpen;
        x.MinimizeToTray = y.MinimizeToTray;
        x.CheckForUpdates = y.CheckForUpdates;
        x.DcsFolders = y.DcsFolders;
        return x.ToJson() == y.ToJson();
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
