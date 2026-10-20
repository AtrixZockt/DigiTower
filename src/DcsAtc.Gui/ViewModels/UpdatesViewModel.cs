using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DcsAtc.Host;
using Velopack;
using Velopack.Sources;

namespace DcsAtc.Gui.ViewModels;

/// <summary>
/// Updates from the GitHub releases, for an installed DigiTower (not for a build from the repository): looked for at
/// start and every six hours; one click downloads it, stops the ATC (held AI is started) and restarts with the new
/// version.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    public const string Repository = "https://github.com/AtrixZockt/DigiTower";
    private readonly AtcHost _host;
    private readonly SettingsStore _store;
    private readonly Func<Task> _beforeRestart;
    private readonly UpdateManager? _manager;
    private UpdateInfo? _update;

    public UpdatesViewModel(AtcHost host, SettingsStore store, Func<Task> beforeRestart)
    {
        _host = host;
        _store = store;
        _beforeRestart = beforeRestart;
        try
        {
            var manager = new UpdateManager(new GithubSource(Repository, null, false));
            _manager = manager.IsInstalled ? manager : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
            _manager = null;
        }
        if (_manager is null) return;
        if (_manager.UpdatePendingRestart is { } pending)
        {
            Version = pending.Version.ToString();
            Ready = true;
        }
        new DispatcherTimer(TimeSpan.FromHours(6), DispatcherPriority.Background, async (_, _) => await CheckAsync(), Dispatcher.CurrentDispatcher).Start();
        _ = CheckAsync();
    }

    /// <summary>A newer version is there (or downloaded).</summary>
    [ObservableProperty] private string? _version;
    [ObservableProperty] private bool _downloading;
    [ObservableProperty] private int _progress;
    /// <summary>Downloaded: restarting applies it.</summary>
    [ObservableProperty] private bool _ready;
    [ObservableProperty] private string? _problem;

    public bool Available => Version is not null;
    public string ButtonText => Downloading ? $"Downloading {Progress} %" : Ready ? "Restart to update" : "Update";

    partial void OnVersionChanged(string? value) => OnPropertyChanged(nameof(Available));
    partial void OnDownloadingChanged(bool value) => OnPropertyChanged(nameof(ButtonText));
    partial void OnProgressChanged(int value) => OnPropertyChanged(nameof(ButtonText));
    partial void OnReadyChanged(bool value) => OnPropertyChanged(nameof(ButtonText));

    private async Task CheckAsync()
    {
        if (_manager is null || Ready || Downloading || !_store.Current.CheckForUpdates) return;
        try
        {
            _update = await _manager.CheckForUpdatesAsync();
            Version = _update?.TargetFullRelease.Version.ToString();
            Problem = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Problem = $"Could not look for updates: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Update()
    {
        if (_manager is null || Downloading) return;
        if (!Ready)
        {
            if (_update is null) return;
            Downloading = true;
            try
            {
                await _manager.DownloadUpdatesAsync(_update, p => Application.Current.Dispatcher.BeginInvoke(() => Progress = p));
                Ready = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException or UnauthorizedAccessException)
            {
                Problem = $"The update could not be downloaded: {ex.Message}";
                return;
            }
            finally
            {
                Downloading = false;
            }
        }
        if (_host.Running && MessageBox.Show($"Update to DigiTower {Version} now? The ATC stops for a moment and starts again with the new version.",
                "DigiTower", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await _beforeRestart();
        _manager.ApplyUpdatesAndRestart(_update?.TargetFullRelease ?? _manager.UpdatePendingRestart);
    }
}
