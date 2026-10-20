using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DcsAtc.Gui.ViewModels;
using DcsAtc.Host;
using Forms = System.Windows.Forms;

namespace DcsAtc.Gui;

/// <summary>
/// DigiTower's control panel. One per user session: opening it again brings the running one to the front. It runs the
/// ATC itself; closing the window while the ATC runs keeps it in the notification area (a setting).
///
///   DigiTower.exe [--settings &lt;file&gt;] [--minimized] [--render &lt;folder&gt; [--render-after &lt;seconds&gt;]]
///     --minimized     start in the notification area (for starting with Windows)
///     --render        write each page as a PNG to the folder and quit (screenshots for the docs); with "start the ATC
///                     when the control panel opens", --render-after waits that long first
/// </summary>
public partial class App : Application
{
    private const string InstanceName = "DigiTower.ControlPanel";
    private Mutex? _single;
    private EventWaitHandle? _show;
    private AtcHost? _host;
    private SettingsStore? _store;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private Forms.NotifyIcon? _tray;
    private bool _quitting, _toldAboutTray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? settingsPath = null, render = null;
        bool minimized = false;
        double renderAfter = 0;
        for (int i = 0; i < e.Args.Length; i++)
        {
            switch (e.Args[i])
            {
                case "--settings" when i + 1 < e.Args.Length: settingsPath = e.Args[++i]; break;
                case "--render" when i + 1 < e.Args.Length: render = e.Args[++i]; break;
                case "--render-after" when i + 1 < e.Args.Length: renderAfter = double.Parse(e.Args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--minimized": minimized = true; break;
            }
        }

        if (render is null && !FirstInstance())
        {
            Shutdown();
            return;
        }

        _store = new SettingsStore(settingsPath);
        var log = new AtcLog();
        _host = new AtcHost(log);
        _vm = new MainViewModel(_host, _store, new LogFeed(log));
        _window = new MainWindow { DataContext = _vm };
        _window.Closing += OnClosing;
        _vm.PrepareRestart = async () =>
        {
            _quitting = true;
            _show?.Set();
            if (_tray is not null) _tray.Visible = false;
            if (_host is not null) await Task.Run(_host.StopAsync);
        };
        _vm.PropertyChanged += (_, p) =>
        {
            if (p.PropertyName == nameof(MainViewModel.Running)) UpdateTray();
        };

        if (render is not null)
        {
            _ = RenderAsync(render, renderAfter);
            return;
        }

        CreateTray();
        if (minimized) _toldAboutTray = true;
        else _window.Show();
        if (_store.Current.StartAtcOnOpen) _ = _vm.StartAsync();
        _vm.Updates.PropertyChanged += (_, p) =>
        {
            if (p.PropertyName == nameof(UpdatesViewModel.Version)) AnnounceUpdate();
        };
        AnnounceUpdate();
    }

    private string? _announced;

    // A new version: the sidebar shows it; with the window in the notification area (or minimized), the icon says so too.
    private void AnnounceUpdate()
    {
        if (_vm!.Updates.Version is not { } version || version == _announced || _tray is null) return;
        if (_window!.IsVisible && _window.WindowState != WindowState.Minimized) return;
        _announced = version;
        _tray.ShowBalloonTip(8000, "DigiTower update", $"DigiTower {version} is available. Open DigiTower to update.", Forms.ToolTipIcon.Info);
    }

    // One control panel per session: a second start asks the first one to show itself.
    private bool FirstInstance()
    {
        _single = new Mutex(true, $@"Local\{InstanceName}", out bool created);
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{InstanceName}.Show");
        if (!created)
        {
            _show.Set();
            return false;
        }
        var thread = new Thread(() =>
        {
            while (_show.WaitOne())
            {
                if (_quitting) return;
                Dispatcher.BeginInvoke(ShowWindow);
            }
        })
        { IsBackground = true, Name = "DigiTower show" };
        thread.Start();
        return true;
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void CreateTray()
    {
        var stream = GetResourceStream(new Uri("pack://application:,,,/DigiTower;component/Assets/DigiTower.ico"))!.Stream;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open DigiTower", null, (_, _) => ShowWindow());
        var startStop = menu.Items.Add("Start ATC", null, (_, _) => _vm!.StartStopCommand.Execute(null));
        var update = menu.Items.Add("Update", null, (_, _) =>
        {
            ShowWindow();
            _vm!.Updates.UpdateCommand.Execute(null);
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit DigiTower", null, (_, _) => Dispatcher.BeginInvoke(QuitAsync));
        menu.Opening += (_, _) =>
        {
            startStop.Text = _vm!.Running ? "Stop ATC" : "Start ATC";
            update.Visible = _vm.Updates.Available;
            update.Text = $"Update to {_vm.Updates.Version}";
        };
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.BalloonTipClicked += (_, _) => ShowWindow();
        _tray.MouseClick += (_, a) =>
        {
            if (a.Button == Forms.MouseButtons.Left) ShowWindow();
        };
        UpdateTray();
    }

    private void UpdateTray()
    {
        if (_tray is not null) _tray.Text = $"DigiTower · ATC {_vm!.StateText}";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_quitting) return;
        e.Cancel = true;
        if (_host!.Running && _store!.Current.MinimizeToTray)
        {
            _window!.Hide();
            if (!_toldAboutTray)
            {
                _toldAboutTray = true;
                _tray?.ShowBalloonTip(4000, "DigiTower keeps running", "The ATC runs on. Open or quit DigiTower from this icon.", Forms.ToolTipIcon.Info);
            }
            return;
        }
        Dispatcher.BeginInvoke(QuitAsync);
    }

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        _show?.Set(); // ends the "show" thread
        _window?.Hide();
        if (_tray is not null) _tray.Visible = false;
        if (_host is not null) await Task.Run(_host.DisposeAsync().AsTask);
        _tray?.Dispose();
        _window?.Close();
        Shutdown();
    }

    // The pages as PNGs, for the docs and for checking the look without starting anything.
    private async Task RenderAsync(string folder, double after)
    {
        Directory.CreateDirectory(folder);
        var w = _window!;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -20000;
        w.ShowActivated = false;
        w.Show();
        try
        {
            if (_store!.Current.StartAtcOnOpen)
            {
                await _vm!.StartAsync();
                await Task.Delay(TimeSpan.FromSeconds(after));
            }
            RenderPages(folder);
        }
        finally
        {
            _quitting = true;
            if (_host is not null) await Task.Run(_host.DisposeAsync().AsTask);
            w.Close();
            Shutdown();
        }
    }

    private void RenderPages(string folder)
    {
        var w = _window!;
        foreach (var page in Enum.GetValues<Page>())
        {
            _vm!.Page = page;
            w.UpdateLayout();
            Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var content = (FrameworkElement)w.Content;
            double scale = VisualTreeHelper.GetDpi(w).DpiScaleX;
            var bitmap = new RenderTargetBitmap((int)(content.ActualWidth * scale), (int)(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(folder, $"{page}.png"));
            png.Save(file);
        }
    }
}
