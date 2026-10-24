using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DcsAtc.Host;

namespace DcsAtc.Gui.ViewModels;

/// <summary>One line of the checklist: a mark, what to do, and a button to the page where it is done.</summary>
public sealed partial class SetupStepViewModel : ObservableObject
{
    public SetupStepViewModel(SetupStep step)
    {
        Title = step.Title;
        Update(step);
    }

    public string Title { get; }

    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private bool _done;
    [ObservableProperty] private string _mark = "";
    [ObservableProperty] private Brush _markBrush = Brushes.Transparent;
    [ObservableProperty] private Brush _titleBrush = Brushes.Transparent;
    [ObservableProperty] private string _action = "";
    public SetupPlace Place { get; private set; }

    public void Update(SetupStep s)
    {
        Hint = s.Hint;
        Done = s.Done;
        Place = s.Place;
        (Mark, MarkBrush) = s.Done ? ("✓", Palette.Light(Light.On).Label) : s.Optional ? ("○", Palette.TextDim) : ("●", Palette.WarnText);
        TitleBrush = s.Done ? Palette.TextDim : Palette.TextBright;
        Action = s.Place == SetupPlace.Install ? "INSTALL PAGE" : "SETTINGS";
    }
}

/// <summary>
/// The getting-started checklist on Operations: shown while something essential is missing (the bridge, the voice, a
/// running SRS server), gone for good once all of it was done or the user hides it (<see cref="DigiTowerSettings.GettingStarted"/>).
/// </summary>
public sealed partial class GettingStartedViewModel : ObservableObject
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);
    private readonly AtcHost _host;
    private readonly SettingsStore _store;
    private readonly MainViewModel _main;
    private SrsProbe? _probe;
    private DateTime _last = DateTime.MinValue;

    public GettingStartedViewModel(AtcHost host, SettingsStore store, MainViewModel main)
    {
        _host = host;
        _store = store;
        _main = main;
        store.Changed += Check;
        Check();
    }

    public ObservableCollection<SetupStepViewModel> Steps { get; } = [];

    [ObservableProperty] private bool _visible;
    [ObservableProperty] private string _summary = "";

    /// <summary>Every second: a new look every few seconds (an SRS server started, a voice downloaded).</summary>
    public void Tick()
    {
        if (DateTime.Now - _last >= Every) Check();
    }

    public void Check()
    {
        _last = DateTime.Now;
        var settings = _store.Current;
        if (!settings.GettingStarted)
        {
            Close();
            return;
        }

        bool? srs = null;
        if (settings.Voice)
        {
            // The running ATC knows best; while it is stopped (or not sure yet), look ourselves.
            srs = _host.Running ? _host.SrsReachable : null;
            if (srs is null)
            {
                var (host, port) = SrsProbe.Parse(settings.Srs);
                if (_probe is null || _probe.Host != host || _probe.Port != port)
                {
                    _probe?.Dispose();
                    _probe = new SrsProbe(settings.Srs);
                }
                srs = _probe.Reachable;
            }
            else DropProbe();
        }
        else DropProbe();

        ModelStore models;
        try
        {
            models = ModelStore.Load(settings);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            models = new ModelStore(null, null);
        }
        bool bridge = _host.Snapshot.Connected || GettingStarted.BridgeInstalled(settings);
        var steps = GettingStarted.Steps(settings, bridge, srs, models);
        if (GettingStarted.Complete(steps))
        {
            Finish();
            return;
        }
        Steps.Sync(steps, v => v.Title, s => s.Title, s => new SetupStepViewModel(s), (v, s) => v.Update(s));
        int essential = steps.Count(s => !s.Optional);
        Summary = $"{steps.Count(s => s.Done && !s.Optional)} of {essential} done. This goes away once they are.";
        Visible = true;
    }

    [RelayCommand]
    private void Go(SetupStepViewModel step) => _main.Page = step.Place == SetupPlace.Install ? Page.Install : Page.Settings;

    [RelayCommand]
    private void Hide() => Finish();

    // Done or hidden: not shown again (Settings → Start can bring it back).
    private void Finish()
    {
        Close();
        try
        {
            _store.Update(s => s.GettingStarted = false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not saved: it is shown again next time, nothing worse.
        }
    }

    private void Close()
    {
        Visible = false;
        Steps.Clear();
        DropProbe();
    }

    private void DropProbe()
    {
        _probe?.Dispose();
        _probe = null;
    }
}
