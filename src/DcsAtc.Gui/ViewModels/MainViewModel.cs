using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DcsAtc.Host;

namespace DcsAtc.Gui.ViewModels;

public enum Page
{
    Operations,
    Log,
    Settings,
    Install,
}

/// <summary>One status light over the strips: "DCS BRIDGE · Connected · 127.0.0.1:42700".</summary>
public sealed partial class LightViewModel : ObservableObject
{
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private Brush _back = Brushes.Transparent;
    [ObservableProperty] private Brush _edge = Brushes.Transparent;
    [ObservableProperty] private Brush _labelBrush = Brushes.Transparent;
    [ObservableProperty] private Brush _textBrush = Brushes.Transparent;

    public void Set(StatusLight light)
    {
        Label = light.Label;
        Text = light.Text;
        (Back, Edge, LabelBrush, TextBrush) = Palette.Light(light.State);
    }
}

/// <summary>The control panel: the pages, starting and stopping the ATC, the status lights.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AtcHost _host;
    private readonly SettingsStore _store;
    private readonly DispatcherTimer _clock;

    public MainViewModel(AtcHost host, SettingsStore store, LogFeed feed)
    {
        _host = host;
        _store = store;
        Operations = new OperationsViewModel(host, feed, store, this);
        Log = new LogViewModel(host, feed, store);
        Settings = new SettingsViewModel(host, store, this);
        Install = new InstallViewModel(store);
        Updates = new UpdatesViewModel(host, store, () => PrepareRestart?.Invoke() ?? StopAsync());
        _currentPage = Operations;
        _dispatcher = Dispatcher.CurrentDispatcher;
        host.RunningChanged += () => _dispatcher.BeginInvoke(Refresh);
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick(), _dispatcher);
        _clock.Start();
        Refresh();
    }

    private readonly Dispatcher _dispatcher;

    public OperationsViewModel Operations { get; }
    public LogViewModel Log { get; }
    public SettingsViewModel Settings { get; }
    public InstallViewModel Install { get; }
    public UpdatesViewModel Updates { get; }

    /// <summary>Before an update restarts DigiTower: the ATC stopped, the tray icon gone (set by the app).</summary>
    public Func<Task>? PrepareRestart { get; set; }

    [ObservableProperty] private Page _page = Page.Operations;
    [ObservableProperty] private ObservableObject _currentPage;

    partial void OnPageChanged(Page value)
    {
        CurrentPage = value switch
        {
            Page.Log => Log,
            Page.Settings => Settings,
            Page.Install => Install,
            _ => Operations,
        };
        if (value == Page.Install) Install.Refresh();
        if (value == Page.Settings) Settings.Refresh();
    }

    public ObservableCollection<LightViewModel> Lights { get; } = [];

    [ObservableProperty] private bool _running;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _elapsed = "--:--:--";

    public static string VersionNumber { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0-dev";

    public string Version { get; } = VersionNumber.StartsWith("0.0.0", StringComparison.Ordinal) ? "Development build" : $"Version {VersionNumber}";

    /// <summary>The window title and the tray's tooltip.</summary>
    public string StateText => Running ? "running" : "stopped";

    partial void OnRunningChanged(bool value) => OnPropertyChanged(nameof(StateText));

    [RelayCommand]
    private async Task StartStop()
    {
        if (Running) await StopAsync();
        else await StartAsync();
    }

    public async Task StartAsync()
    {
        if (Busy || _host.Running) return;
        Busy = true;
        try
        {
            var settings = _store.Current;
            // Loading voices and the recognizer takes a moment: not on the window's thread.
            await Task.Run(() => _host.StartAsync(settings));
        }
        catch (Exception ex)
        {
            // Whatever it was, the panel stays up and says so.
            _host.Log.Write(LogCategory.App, $"could not start: {ex.Message}", LogLevel.Error);
            await Task.Run(_host.StopAsync);
        }
        finally
        {
            Busy = false;
            Refresh();
        }
    }

    public async Task StopAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            await Task.Run(_host.StopAsync);
        }
        finally
        {
            Busy = false;
            Refresh();
        }
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    private void Tick()
    {
        var at = _host.StartedAt;
        Elapsed = at is { } t ? (DateTime.Now - t).ToString(@"hh\:mm\:ss") : "--:--:--";
        var lights = _host.Status();
        while (Lights.Count < lights.Count) Lights.Add(new LightViewModel());
        for (int i = 0; i < lights.Count; i++) Lights[i].Set(lights[i]);
        Operations.Tick();
    }

    private void Refresh()
    {
        Running = _host.Running;
        Settings.RefreshRestart();
        Tick();
    }
}
