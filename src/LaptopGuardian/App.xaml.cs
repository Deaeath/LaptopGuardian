using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LaptopGuardian.Core;

namespace LaptopGuardian;

public partial class App : Application
{
    public bool StartHidden { get; set; }

    Guardian? _engine;
    MainViewModel? _vm;
    MainWindow? _window;
    TrayIcon? _tray;
    EventWaitHandle? _showSignal;
    RegisteredWaitHandle? _showWait;

    public string? ScreenshotDir { get; set; }
    public bool? ForceDark { get; set; }
    public string? UpdatedFrom { get; set; }

    Timer? _updateTimer;
    string? _stagedUpdate;
    Version? _stagedVersion;
    int _checkingForUpdate;

    /// <summary>Checks GitHub for a newer release; downloads and verifies it; applies it when the window is not in use.</summary>
    async Task CheckForUpdateAsync(bool manual)
    {
        if (_engine == null || _vm == null) return;
        var exe = Environment.ProcessPath;
        if (!AppUpdater.CanSelfUpdate(exe))
        {
            if (manual) Dispatcher.Invoke(() => _vm.UpdateStatus = "This is a development build, so it does not update itself.");
            return;
        }
        if (!manual && !_engine.Settings.AutoUpdate) return;
        if (Interlocked.Exchange(ref _checkingForUpdate, 1) == 1) return;
        try
        {
            if (manual) Dispatcher.Invoke(() => _vm.UpdateStatus = "Checking...");
            if (_stagedUpdate == null)
            {
                var latest = await AppUpdater.LatestAsync();
                if (latest == null || latest.Version <= AppUpdater.Current)
                {
                    Dispatcher.Invoke(() => _vm.UpdateStatus = $"Up to date (version {AppUpdater.Current}). Checked {Fmt.WhenInSentence(DateTimeOffset.Now)}.");
                    return;
                }
                Dispatcher.Invoke(() => _vm.UpdateStatus = $"Downloading version {latest.Version}...");
                _stagedUpdate = await AppUpdater.DownloadAsync(latest, exe!);
                _stagedVersion = latest.Version;
            }
            Dispatcher.Invoke(() =>
            {
                _vm.UpdateStatus = $"Version {_stagedVersion} is ready. It installs when this window closes.";
                TryApplyUpdate();
            });
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            // Being offline is normal, not an error to log every twelve hours.
            Dispatcher.Invoke(() => _vm.UpdateStatus = "Could not reach GitHub to check for updates. It will try again later.");
        }
        catch (Exception ex)
        {
            Log(ex);
            Dispatcher.Invoke(() => _vm.UpdateStatus = "Could not check for updates: " + ex.Message);
        }
        finally { Interlocked.Exchange(ref _checkingForUpdate, 0); }
    }

    /// <summary>Restarting is invisible from the tray, so it waits until the window is not open.</summary>
    void TryApplyUpdate()
    {
        if (_stagedUpdate == null || _window?.IsVisible == true) return;
        var staged = _stagedUpdate;
        _stagedUpdate = null;
        try
        {
            // The keyboard filter must come off before the new copy puts its own on.
            AppUpdater.ApplyAndRestart(staged, Environment.ProcessPath!, AppUpdater.Current.ToString());
            Quit();
        }
        catch (Exception ex)
        {
            Log(ex);
            if (_vm != null) _vm.UpdateStatus = "Could not install the update: " + ex.Message;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (ScreenshotDir != null) { _ = RunScreenshotsAsync(ScreenshotDir); return; }
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);

        Theme.Apply();
        Theme.Watch();

        _engine = new Guardian(Guardian.DefaultDataDir);
        _vm = new MainViewModel(_engine, Dispatcher);

        Notifier.Init();
        Notifier.Activated = (page, id) => Dispatcher.BeginInvoke(() => ShowWindow(page));
        _engine.AlertRaised += a => Dispatcher.BeginInvoke(() =>
        {
            if (!Notifier.Show(a)) _tray?.Balloon(a);
        });

        _tray = new TrayIcon(() => ShowWindow(), BuildTrayMenu());
        _engine.Changed += () => Dispatcher.BeginInvoke(DispatcherPriority.Background, UpdateTray);
        Theme.Changed += UpdateTray;

        // A second launch (Start menu, desktop shortcut) brings this window forward.
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(() => ShowWindow()), null, -1, false);

        // After an update or a move, keep "start with Windows" pointing at this copy (never a development build).
        var exe = Environment.ProcessPath;
        if (exe != null && !exe.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
            try { Core.Startup.Repair(exe); } catch { }

        _engine.Start();
        if (!StartHidden) ShowWindow();

        // Self-update: remove what the last update replaced, say so once, then check after a minute and twice a day.
        AppUpdater.CleanUp(exe);
        if (Installer.IsInstalledCopy(exe)) Installer.Register(exe!);
        if (UpdatedFrom != null)
            Notifier.Show(new Alert(IncidentKind.GraphicsError, Severity.Info, $"LaptopGuardian updated to {AppUpdater.Current}", $"It was {UpdatedFrom}. Your settings and history are unchanged.", "settings"));
        _vm.CheckForUpdatesRequested += () => _ = CheckForUpdateAsync(manual: true);
        _updateTimer = new Timer(_ => _ = CheckForUpdateAsync(manual: false), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(12));
    }

    ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string text, Action act)
        {
            var mi = new MenuItem { Header = text };
            mi.Click += (_, _) => act();
            return mi;
        }
        menu.Items.Add(Item("Open LaptopGuardian", () => ShowWindow()));
        menu.Items.Add(Item("Shutdowns", () => ShowWindow("shutdowns")));
        menu.Items.Add(Item("Graphics", () => ShowWindow("graphics")));
        menu.Items.Add(Item("Check again", () => Task.Run(() => _engine?.Scan())));
        menu.Items.Add(new Separator());
        var keys = new MenuItem { Header = "Block repeated keys", IsCheckable = true };
        keys.Click += (_, _) => { if (_vm != null) _vm.KeyboardFilter = keys.IsChecked; };
        menu.Opened += (_, _) => keys.IsChecked = _engine?.Settings.KeyboardFilter == true;
        menu.Items.Add(keys);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit LaptopGuardian", Quit));
        return menu;
    }

    void UpdateTray()
    {
        if (_engine == null || _tray == null) return;
        var f = _engine.Findings();
        var (level, headline, summary) = Advisor.Status(f, _engine.Recommendations);
        _tray.SetStatus(level, $"LaptopGuardian: {headline}. {summary}");
    }

    public void ShowWindow(string? page = null)
    {
        if (_vm == null || _engine == null) return;
        if (_window == null)
        {
            _window = new MainWindow(_vm);
            _window.IsVisibleChanged += (_, _) =>
            {
                // Sample the graphics card every 2 seconds while you are looking, every 15 seconds otherwise.
                _engine.Gpu.Interval = _window.IsVisible ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(15);
                if (_window.IsVisible) _engine.Gpu.Poke();
                else Dispatcher.BeginInvoke(TryApplyUpdate);
            };
        }
        if (page != null) _vm.Navigate(page);
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;  // Windows only lets a background app come forward briefly; this makes it reliable.
        _window.Topmost = false;
        _window.Focus();
    }

    public void Quit()
    {
        if (_window != null) { _window.AllowClose = true; _window.Close(); }
        _updateTimer?.Dispose();
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        _tray?.Dispose();
        _engine?.Dispose();
        Shutdown();
    }

    async Task RunScreenshotsAsync(string dir)
    {
        int code = 0;
        try
        {
            Directory.CreateDirectory(dir);
            Theme.Apply(ForceDark);
            var g = new Guardian(dataDir: null);
            await Task.Run(() =>
            {
                g.Gpu.SampleOnce(); Thread.Sleep(1000);
                for (int i = 0; i < 12; i++) { g.Gpu.SampleOnce(); Thread.Sleep(250); }
                g.Scan();
            });
            await g.CheckDriversAsync();
            var vm = new MainViewModel(g, Dispatcher);
            var w = new MainWindow(vm)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000, Top = -30000, Width = 1180, Height = 900,
                ShowActivated = false, ShowInTaskbar = false,
            };
            w.Show();
            string suffix = Theme.IsDark ? "dark" : "light";
            foreach (var page in vm.Nav.Select(n => n.Key))
            {
                vm.Navigate(page);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var root = (FrameworkElement)w.Content;
                // Lay the page out at full height so the PNG shows all of it, not one screenful.
                root.Measure(new Size(w.Width - 16, double.PositiveInfinity));
                root.Arrange(new Rect(0, 0, w.Width - 16, Math.Max(800, root.DesiredSize.Height)));
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                var bg = new System.Windows.Shapes.Rectangle { Width = root.ActualWidth, Height = root.ActualHeight, Fill = Theme.Brush("Bg") };
                bg.Measure(new Size(root.ActualWidth, root.ActualHeight));
                bg.Arrange(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                bmp.Render(bg);
                bmp.Render(root);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var fs = File.Create(Path.Combine(dir, $"{page}-{suffix}.png"));
                enc.Save(fs);
            }
            w.AllowClose = true;
            w.Close();
            g.Dispose();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, "screenshot-error.txt"), ex.ToString());
            code = 1;
        }
        Shutdown(code);
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true; // stay alive: the keyboard filter and the watchers matter more than one broken view
    }

    static void Log(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(Guardian.DefaultDataDir);
            string path = Path.Combine(Guardian.DefaultDataDir, "error.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".1", overwrite: true);
            File.AppendAllText(path, $"[{DateTime.Now:s}] {ex}{Environment.NewLine}");
        }
        catch { }
    }
}
