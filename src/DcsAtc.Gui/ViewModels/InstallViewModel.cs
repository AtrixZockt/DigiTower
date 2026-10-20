using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DcsAtc.Host;
using Microsoft.Win32;

namespace DcsAtc.Gui.ViewModels;

/// <summary>One DCS Saved Games folder: its bridge, and the bridge's settings there.</summary>
public sealed partial class InstanceViewModel : ObservableObject
{
    private readonly InstallViewModel _owner;

    public InstanceViewModel(DcsInstance instance, bool addedByHand, InstallViewModel owner)
    {
        Instance = instance;
        AddedByHand = addedByHand;
        _owner = owner;
    }

    public DcsInstance Instance { get; }
    public string Name => Instance.Name;
    public string Path => Instance.Path;
    public bool AddedByHand { get; }

    [ObservableProperty] private string _state = "";
    [ObservableProperty] private Brush _stateBack = Brushes.Transparent;
    [ObservableProperty] private Brush _stateEdge = Brushes.Transparent;
    [ObservableProperty] private Brush _stateText = Brushes.Transparent;
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string? _warning;
    [ObservableProperty] private string _primary = "INSTALL";
    [ObservableProperty] private bool _primaryIsAccent = true;
    [ObservableProperty] private bool _installed;
    [ObservableProperty] private string _port = "42700";
    [ObservableProperty] private bool _manageAi = true;
    [ObservableProperty] private bool _mapDrawings = true;
    [ObservableProperty] private bool _settingsChanged;
    [ObservableProperty] private string? _message;
    private BridgeConfig _saved = new();

    partial void OnPortChanged(string value) => SettingsChanged = Installed && Config() != _saved;
    partial void OnManageAiChanged(bool value) => SettingsChanged = Installed && Config() != _saved;
    partial void OnMapDrawingsChanged(bool value) => SettingsChanged = Installed && Config() != _saved;

    public BridgeConfig? Config() =>
        int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is >= 1 and <= 65535 ? new BridgeConfig(ManageAi, MapDrawings, port) : null;

    public void Show(BridgeStatus status, int appPort)
    {
        Installed = status.State != BridgeState.NotInstalled;
        (State, StateBack, StateEdge, StateText) = status.State switch
        {
            BridgeState.Installed => ("INSTALLED", Palette.Light(Light.On).Back, Palette.Light(Light.On).Edge, Palette.Light(Light.On).Label),
            BridgeState.UpdateAvailable => ("UPDATE AVAILABLE", Palette.Light(Light.Warning).Back, Palette.Light(Light.Warning).Edge, Palette.Light(Light.Warning).Label),
            _ => ("NOT INSTALLED", Palette.Light(Light.Off).Back, Palette.Light(Light.Off).Edge, Palette.Light(Light.Off).Label),
        };
        (Primary, PrimaryIsAccent) = status.State switch
        {
            BridgeState.Installed => ("REINSTALL", false),
            BridgeState.UpdateAvailable => ("UPDATE", true),
            _ => ("INSTALL", true),
        };
        Detail = status.Detail;
        _saved = status.Config;
        Port = status.Config.Port.ToString(CultureInfo.InvariantCulture);
        ManageAi = status.Config.ManageAi;
        MapDrawings = status.Config.MapDrawings;
        SettingsChanged = false;
        var warnings = new List<string>();
        if (Installed && status.Config.Port != appPort)
            warnings.Add($"This bridge listens on port {status.Config.Port}, DigiTower connects to {appPort} (Settings).");
        if (status.OtherHooks.Count > 0)
            warnings.Add($"Other hooks here can also run code in missions while the bridge is installed: {string.Join(", ", status.OtherHooks)}. Make sure none of them accepts code from the network.");
        Warning = warnings.Count > 0 ? string.Join(Environment.NewLine, warnings) : null;
    }

    [RelayCommand]
    private void Install() => _owner.Install(this);

    [RelayCommand]
    private void Uninstall() => _owner.Uninstall(this);

    [RelayCommand]
    private void ApplySettings() => _owner.ApplySettings(this);

    [RelayCommand]
    private void Forget() => _owner.Forget(this);
}

/// <summary>A voice or recognition model: download it, or remove it.</summary>
public sealed partial class ModelViewModel(ModelEntry entry, InstallViewModel owner) : ObservableObject
{
    public ModelEntry Entry { get; private set; } = entry;
    public string Name => Entry.Name;
    public string Use => Entry.Default ? $"{Entry.Use} · default" : Entry.Use;
    public string License => Entry.License;
    public string Size => (Entry.Present ? ModelStore.SizeOnDisk(Entry) : Entry.SizeMb) is int mb ? mb >= 1000 ? $"{mb / 1000.0:0.0} GB" : $"{mb} MB" : "";
    public string State => Busy ? (Progress < 0 ? "Unpacking…" : $"Downloading {Progress:P0}") : Entry.Present ? "Downloaded" : Entry.Url is null ? "Made by a script" : "Not downloaded";
    public Brush StateBrush => Entry.Present ? Palette.Light(Light.On).Label : Palette.TextDim;
    public string Action => Busy ? "Cancel" : Entry.Present ? "Remove" : Entry.Url is null ? "" : "Download";
    public bool HasAction => Action.Length > 0;
    public string? ToolTip => Entry.Url is null ? "Made from a Hugging Face model with tools\\Convert-WhisperModel.ps1 (see docs\\voice.md)." : null;

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private double _progress;
    internal CancellationTokenSource? Cancel;

    partial void OnBusyChanged(bool value) => Changed();
    /// <summary>Unpacking an archive: no progress to show.</summary>
    public bool Unpacking => Busy && Progress < 0;

    partial void OnProgressChanged(double value)
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Unpacking));
    }

    public void Update(ModelEntry entry)
    {
        Entry = entry;
        Changed();
    }

    private void Changed()
    {
        foreach (var p in new[] { nameof(Use), nameof(Size), nameof(State), nameof(StateBrush), nameof(Action), nameof(HasAction) }) OnPropertyChanged(p);
    }

    [RelayCommand]
    private Task Act() => owner.ActAsync(this);
}

/// <summary>The Install page: the bridge in each DCS, the voice and recognition models.</summary>
public sealed partial class InstallViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private BridgeInstaller? _installer;

    public InstallViewModel(SettingsStore store)
    {
        _store = store;
    }

    public ObservableCollection<InstanceViewModel> Instances { get; } = [];
    public ObservableCollection<ModelViewModel> Models { get; } = [];

    [ObservableProperty] private string? _problem;
    [ObservableProperty] private bool _dcsRunning;
    [ObservableProperty] private string _savedGames = "";
    [ObservableProperty] private string _modelsFolder = "";

    public void Refresh()
    {
        string? scripts = BridgeInstaller.FindScripts();
        _installer = scripts is null ? null : new BridgeInstaller(scripts);
        Problem = scripts is null ? "The bridge files (bridge\\Scripts) are not beside DigiTower: reinstall DigiTower." : null;
        DcsRunning = BridgeInstaller.DcsRunning();
        SavedGames = BridgeInstaller.SavedGames();

        var found = BridgeInstaller.FindInstances(SavedGames).Select(i => (i, false))
            .Concat(_store.Current.DcsFolders.Where(Directory.Exists).Select(p => (new DcsInstance(BridgeInstaller.NameOf(p), p), true)))
            .DistinctBy(x => Path.GetFullPath(x.Item1.Path).TrimEnd('\\').ToLowerInvariant())
            .ToList();
        var keep = Instances.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        Instances.Clear();
        foreach (var (instance, byHand) in found)
        {
            var vm = keep.TryGetValue(instance.Path, out var old) ? old : new InstanceViewModel(instance, byHand, this);
            Show(vm);
            Instances.Add(vm);
        }

        var store = ModelStore.Load(_store.Current);
        ModelsFolder = _store.Current.ResolvedModelsDirectory ?? "";
        var entries = store.Entries();
        Models.Sync(entries, m => m.Name, e => e.Name, e => new ModelViewModel(e, this), (m, e) => { if (!m.Busy) m.Update(e); });
    }

    private void Show(InstanceViewModel vm)
    {
        if (_installer is null) return;
        try
        {
            vm.Show(_installer.Status(vm.Path), _store.Current.BridgePort);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            vm.Message = $"Could not read it: {ex.Message}";
        }
    }

    internal void Install(InstanceViewModel vm)
    {
        if (_installer is null) return;
        if (vm.Config() is not { } config)
        {
            vm.Message = "The port must be a number from 1 to 65535.";
            return;
        }
        Run(vm, () => _installer.Install(vm.Path, config), "Installed");
    }

    internal void Uninstall(InstanceViewModel vm)
    {
        if (_installer is null) return;
        if (MessageBox.Show($"Remove the DigiTower bridge from {vm.Name} ({vm.Path})?\n\nIts settings file is kept.", "DigiTower",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Run(vm, () => _installer.Uninstall(vm.Path), "Removed");
    }

    internal void ApplySettings(InstanceViewModel vm)
    {
        if (vm.Config() is not { } config)
        {
            vm.Message = "The port must be a number from 1 to 65535.";
            return;
        }
        Run(vm, () =>
        {
            BridgeInstaller.WriteConfig(Path.Combine(vm.Path, "Scripts", BridgeInstaller.ConfigFile), config);
            return [];
        }, "Bridge settings saved");
    }

    internal void Forget(InstanceViewModel vm)
    {
        _store.Update(s => s.DcsFolders.RemoveAll(f => f.Equals(vm.Path, StringComparison.OrdinalIgnoreCase)));
        Refresh();
    }

    private void Run(InstanceViewModel vm, Func<IReadOnlyList<string>> action, string what)
    {
        try
        {
            action();
            DcsRunning = BridgeInstaller.DcsRunning();
            vm.Message = $"{what}. {(DcsRunning ? "DCS is running: restart it to load the change." : "DCS loads it when it starts.")}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            vm.Message = $"Failed: {ex.Message}";
        }
        Show(vm);
    }

    [RelayCommand]
    private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "A DCS Saved Games folder (e.g. Saved Games\\DCS.dcs_serverrelease)", InitialDirectory = SavedGames };
        if (dialog.ShowDialog() != true) return;
        string path = dialog.FolderName;
        if (!Directory.Exists(Path.Combine(path, "Config")) && !Directory.Exists(Path.Combine(path, "Scripts"))
            && MessageBox.Show($"{path} has no Config or Scripts folder: DCS has not used it yet. Add it anyway?", "DigiTower",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _store.Update(s =>
        {
            if (!s.DcsFolders.Contains(path, StringComparer.OrdinalIgnoreCase)) s.DcsFolders.Add(path);
        });
        Refresh();
    }

    internal async Task ActAsync(ModelViewModel m)
    {
        if (m.Busy)
        {
            m.Cancel?.Cancel();
            return;
        }
        if (m.Entry.Present)
        {
            if (MessageBox.Show($"Remove {m.Name} ({m.Size})? It can be downloaded again.", "DigiTower", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            try
            {
                ModelStore.Remove(m.Entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show($"Could not remove {m.Name}: {ex.Message}\n\nIf the ATC is using it, stop the ATC first.", "DigiTower", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Refresh();
            return;
        }
        if (m.Entry.Url is null) return;
        if (MessageBox.Show($"Download {m.Name}{(m.Size.Length > 0 ? $" ({m.Size})" : "")}?\n\nLicense: {m.License}\n\nFrom: {m.Entry.Url}", "DigiTower",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        m.Cancel = new CancellationTokenSource();
        m.Progress = 0;
        m.Busy = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(m.Entry.Path)!);
            await ModelStore.DownloadAsync(m.Entry, new Progress<double>(p => m.Progress = p), m.Cancel.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show($"Could not download {m.Name}: {ex.Message}", "DigiTower", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            m.Busy = false;
            m.Cancel.Dispose();
            m.Cancel = null;
        }
        Refresh();
    }
}
