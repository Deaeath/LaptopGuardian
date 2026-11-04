using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LaptopGuardian.Core;

namespace LaptopGuardian;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class Command : ICommand
{
    readonly Action<object?> _run;
    public Command(Action<object?> run) => _run = run;
    public Command(Action run) : this(_ => run()) { }
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _run(parameter);
}

internal static class Look
{
    public static Brush For(Severity s) => Theme.Brush(s switch { Severity.Critical => "Bad", Severity.Warning => "Warn", _ => "Info" });
    public static Brush Tint(Severity s) => Theme.Brush(s switch { Severity.Critical => "BadTint", Severity.Warning => "WarnTint", _ => "InfoTint" });

    public static string Glyph(IncidentKind k) => k switch
    {
        IncidentKind.UnexpectedShutdown => "",
        IncidentKind.GraphicsError => "",
        IncidentKind.GpuMemoryFull => "",
        IncidentKind.BatteryLow => "",
        _ => "",
    };

    public static string Glyph(Severity s) => s switch { Severity.Critical => "", Severity.Warning => "", _ => "" };
}

public sealed class NavItem : Observable
{
    string? _badge;
    public NavItem(string key, string label, string glyph) { Key = key; Label = label; Glyph = glyph; }
    public string Key { get; }
    public string Label { get; }
    public string Glyph { get; }
    public string? Badge { get => _badge; set => Set(ref _badge, value); }
}

public sealed class IncidentVm
{
    public IncidentVm(Incident i, bool showDay)
    {
        Model = i;
        Title = i.Title;
        Summary = i.Summary;
        When = Fmt.When(i.Time);
        Clock = Fmt.Clock(i.Time);
        Day = Fmt.Day(i.Time);
        ShowDay = showDay;
        Glyph = Look.Glyph(i.Kind);
        Brush = Look.For(i.Severity);
        Tint = Look.Tint(i.Severity);
        Detail = i.Detail;
        Source = i.Source == null ? null
            : i.Source + (i.Data != null && !i.Source.StartsWith("Windows Error", StringComparison.Ordinal) ? $"  ·  “{i.Data}”" : "");
        Context = i.Gpu == null ? null : "At the time: " + i.Gpu.Describe();
        Note = i.UserNote == null ? null : "You saw: " + i.UserNote;
    }

    public Incident Model { get; }
    public string Title { get; }
    public string Summary { get; }
    public string When { get; }
    public string Clock { get; }
    public string Day { get; }
    public bool ShowDay { get; }
    public string Glyph { get; }
    public Brush Brush { get; }
    public Brush Tint { get; }
    public string? Detail { get; }
    public string? Source { get; }
    public string? Context { get; }
    public string? Note { get; }
}

public sealed record StepVm(string Time, string Label, Brush Brush);

public sealed class CrashVm : Observable
{
    public static IReadOnlyList<string> Answers { get; } = new[]
    {
        "Not sure",
        "It was off when I came back",
        "Black screen, it would not wake",
        "The picture was frozen",
        "It restarted on its own",
        "I held the power button",
    };

    readonly Action<string, string?> _save;
    string? _note;

    public CrashVm(Incident i, Action<string, string?> save)
    {
        _save = save;
        Id = i.Id;
        Title = i.Title;
        Summary = i.Summary;
        Detail = i.Detail;
        When = Fmt.When(i.Time);
        Brush = Look.For(i.Severity);
        Tint = Look.Tint(i.Severity);
        _note = i.UserNote;

        var c = i.Crash;
        var steps = new List<StepVm>();
        if (c != null)
        {
            var anchor = c.LastActivity ?? c.LastAlive ?? c.RestartedAt;
            string T(DateTimeOffset t) => t.ToLocalTime().Date == anchor.ToLocalTime().Date ? Fmt.Clock(t) : Fmt.When(t);
            if (c.AsleepSince is DateTimeOffset s) steps.Add(new StepVm(T(s), "Went to sleep", Theme.Brush("Info")));
            if (c.LastActivity is DateTimeOffset act) steps.Add(new StepVm(T(act), "Last activity", Theme.Brush("Bad")));
            else if (c.LastAlive is DateTimeOffset la) steps.Add(new StepVm(T(la), "Windows last noted it running", Theme.Brush("Faint")));
            if (c.PowerButtonPressedAt is DateTimeOffset pb) steps.Add(new StepVm(T(pb), "Power button pressed", Theme.Brush("Warn")));
            steps.Add(new StepVm(T(c.RestartedAt), "Started again", Theme.Brush("Good")));

            var facts = new List<string>();
            if (c.OnAc is bool ac) facts.Add(ac ? "Plugged in" : "On battery");
            facts.Add(c.BugcheckCode != 0 ? $"stop code 0x{c.BugcheckCode:X} {c.BugcheckName}" : "no error code");
            if (c.GraphicsErrorsBefore > 0) facts.Add(Fmt.Plural(c.GraphicsErrorsBefore, "graphics error", "graphics errors") + " in the hour before");
            if (c.LastActivity is DateTimeOffset act2 && c.RestartedAt > act2) facts.Add($"restarted {Fmt.Span(c.RestartedAt - act2)} later");
            Facts = string.Join("  ·  ", facts);
        }
        Steps = steps;
    }

    public string Id { get; }
    public string Title { get; }
    public string Summary { get; }
    public string? Detail { get; }
    public string When { get; }
    public Brush Brush { get; }
    public Brush Tint { get; }
    public List<StepVm> Steps { get; }
    public string? Facts { get; }
    public IReadOnlyList<string> AnswerOptions => Answers;

    public string? Note
    {
        get => _note;
        set
        {
            if (Set(ref _note, value)) _save(Id, value == Answers[0] ? null : value);
        }
    }
}

public sealed class RecommendationVm : Observable
{
    bool _busy;

    public RecommendationVm(Recommendation r, MainViewModel vm)
    {
        Model = r;
        Brush = Look.For(r.Severity);
        Glyph = Look.Glyph(r.Severity);
        ActCommand = new Command(async () =>
        {
            if (r.ActionId == null) return;
            if (r.ActionId.StartsWith("go:", StringComparison.Ordinal)) { vm.Navigate(r.ActionId[3..]); return; }
            Busy = true;
            try { await vm.ApplyAsync(r.ActionId); }
            finally { Busy = false; }
        });
        LinkCommand = new Command(() => MainViewModel.Open(r.LinkUrl));
        DismissCommand = new Command(() => vm.Dismiss(r.Id));
    }

    public Recommendation Model { get; }
    public string Title => Model.Title;
    public string Body => Model.Body;
    public string? ActionLabel => Busy ? "Working..." : Model.ActionLabel;
    public bool HasAction => Model.ActionId != null;
    public string? LinkLabel => Model.LinkLabel;
    public bool HasLink => Model.LinkUrl != null;
    public Brush Brush { get; }
    public string Glyph { get; }
    public ICommand ActCommand { get; }
    public ICommand LinkCommand { get; }
    public ICommand DismissCommand { get; }

    public bool Busy
    {
        get => _busy;
        set { if (Set(ref _busy, value)) { Raise(nameof(NotBusy)); Raise(nameof(ActionLabel)); } }
    }

    public bool NotBusy => !_busy;
}

public sealed record KernelReportVm(string Title, string Line, string Meaning, Brush Brush, bool Stuck);
public sealed record GuideActionVm(string Label, bool Primary, ICommand Command);
public sealed record GuideVm(string Title, string Status, Brush Brush, string Glyph, List<string> Steps, List<GuideActionVm> Actions);
public sealed record DriverRowVm(string Category, string Device, string Version, string Date);
public sealed record SettingRow(string Label, string Plugged, string Battery);
public sealed record ThresholdOption(int Ms, string Label);

public sealed class MainViewModel : Observable
{
    readonly Guardian _g;
    readonly Dispatcher _ui;
    readonly DispatcherTimer _clock;
    bool _refreshQueued, _gpuQueued, _keysQueued;
    string _signature = "";
    NavItem _selectedNav;
    string _timelineFilter = "Everything";
    string? _message;
    bool _messageIsError;
    CancellationTokenSource? _messageTimer;

    public MainViewModel(Guardian g, Dispatcher ui)
    {
        _g = g;
        _ui = ui;
        Nav = new List<NavItem>
        {
            new("overview", "Overview", ""),
            new("timeline", "Timeline", ""),
            new("shutdowns", "Shutdowns", ""),
            new("graphics", "Graphics", ""),
            new("power", "Power & battery", ""),
            new("drivers", "Drivers", ""),
            new("keyboard", "Keyboard", ""),
            new("settings", "Settings", ""),
        };
        _selectedNav = Nav[0];

        CopyReportCommand = new Command(CopyReport);
        SaveReportCommand = new Command(SaveReport);
        RescanCommand = new Command(() => Task.Run(_g.Scan));
        NavigateCommand = new Command(p => Navigate(p as string ?? "overview"));
        ApplyCommand = new Command(async p => { if (p is string id) await ApplyAsync(id); });
        OpenDataFolderCommand = new Command(() => Open(_g.DataDir));
        TestNotificationCommand = new Command(_g.RaiseTest);
        DismissMessageCommand = new Command(() => Message = null);
        OpenLinkCommand = new Command(p => Open(p as string));
        CheckDriversCommand = new Command(() => _ = _g.CheckDriversAsync());
        CheckUpdatesCommand = new Command(() => CheckForUpdatesRequested?.Invoke());

        AppIcon = LoadIcon();

        g.Changed += QueueRefresh;
        g.Gpu.Sampled += _ => QueueGpu();
        g.Keyboard.BounceSuppressed += _ => QueueKeys();
        Theme.Changed += () => { _signature = ""; Refresh(); RefreshGpu(); };

        // Relative times ("Today 9:24 PM") and live advice (heat, memory) need an occasional nudge.
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => _g.Recompute(), ui);
        Refresh();
        RefreshGpu();
    }

    // ------------------------------------------------------------ navigation and status

    public List<NavItem> Nav { get; }
    public NavItem SelectedNav
    {
        get => _selectedNav;
        set { if (value != null && Set(ref _selectedNav, value)) { Raise(nameof(Page)); PageChanged?.Invoke(); } }
    }
    public string Page => _selectedNav.Key;
    public event Action? PageChanged;

    public void Navigate(string key)
    {
        var item = Nav.FirstOrDefault(n => n.Key == key);
        if (item != null) SelectedNav = item;
    }

    public ImageSource AppIcon { get; }
    public Severity StatusLevel { get; private set; }
    public string StatusHeadline { get; private set; } = "Checking...";
    public string StatusSummary { get; private set; } = "Reading what Windows recorded.";
    public Brush StatusBrush => Theme.Brush(StatusLevel switch { Severity.Critical => "Bad", Severity.Warning => "Warn", _ => "Good" });
    public Brush StatusTint => Theme.Brush(StatusLevel switch { Severity.Critical => "BadTint", Severity.Warning => "WarnTint", _ => "GoodTint" });
    public string StatusGlyph => StatusLevel switch { Severity.Critical => "", Severity.Warning => "", _ => "" };
    public string MachineLine { get; private set; } = "";
    public string LastScanText { get; private set; } = "";
    public bool Scanning { get; private set; }
    public string Version => Cli.Version;
    public string DataFolder => _g.DataDir ?? "";
    public string InstallFolder => Path.GetDirectoryName(Environment.ProcessPath) ?? "";

    // ------------------------------------------------------------ overview cards

    public string Shutdowns7 { get; private set; } = "0";
    public Brush Shutdowns7Brush { get; private set; } = Brushes.Gray;
    public string Graphics7 { get; private set; } = "0";
    public Brush Graphics7Brush { get; private set; } = Brushes.Gray;
    public string Stability30 { get; private set; } = "";
    public string LastShutdownText { get; private set; } = "";

    public string BatteryPctText { get; private set; } = "";
    public string BatterySourceText { get; private set; } = "";
    public double BatteryHealth { get; private set; }
    public Brush BatteryHealthBrush { get; private set; } = Brushes.Gray;
    public string BatteryHealthText { get; private set; } = "";
    public string BatteryDetailText { get; private set; } = "";
    public bool HasBattery { get; private set; }

    public List<RecommendationVm> Recommendations { get; private set; } = new();
    public List<IncidentVm> Recent { get; private set; } = new();

    // ------------------------------------------------------------ graphics (live)

    public string GpuName { get; private set; } = "Graphics card";
    public string GpuTempText { get; private set; } = "--";
    public Brush GpuTempBrush { get; private set; } = Brushes.Gray;
    public string GpuLimitText { get; private set; } = "";
    public double GpuUtil { get; private set; }
    public string GpuUtilText { get; private set; } = "--";
    public double GpuMem { get; private set; }
    public string GpuMemText { get; private set; } = "--";
    public Brush GpuMemBrush { get; private set; } = Brushes.Gray;
    public string GpuPowerText { get; private set; } = "--";
    public string GpuThrottleText { get; private set; } = "";
    public Brush GpuThrottleBrush { get; private set; } = Brushes.Gray;
    public string GpuAppsText { get; private set; } = "";
    public List<GpuApp> GpuApps { get; private set; } = new();
    public IReadOnlyList<double> TempSeries { get; private set; } = Array.Empty<double>();
    public IReadOnlyList<double> UtilSeries { get; private set; } = Array.Empty<double>();
    public IReadOnlyList<double> MemSeries { get; private set; } = Array.Empty<double>();
    public string GpuDriverText { get; private set; } = "";
    public bool GpuAsleep { get; private set; }

    public List<IncidentVm> GraphicsErrors { get; private set; } = new();
    public List<KernelReportVm> KernelReports { get; private set; } = new();
    public string KernelSummary { get; private set; } = "";
    public bool HasStuckReports { get; private set; }

    // ------------------------------------------------------------ timeline and shutdowns

    public IReadOnlyList<string> TimelineFilters { get; } = new[] { "Everything", "Shutdowns", "Graphics errors", "Alerts" };
    public string TimelineFilter
    {
        get => _timelineFilter;
        set { if (Set(ref _timelineFilter, value)) { _signature = ""; Refresh(); } }
    }
    public List<IncidentVm> Timeline { get; private set; } = new();
    public List<CrashVm> Crashes { get; private set; } = new();
    public string CrashSummary { get; private set; } = "";

    // ------------------------------------------------------------ power

    public List<SettingRow> PowerRows { get; private set; } = new();
    public string SleepModeText { get; private set; } = "";
    public string PowerPlanText { get; private set; } = "";

    // ------------------------------------------------------------ keyboard and settings

    public IReadOnlyList<ThresholdOption> Thresholds { get; } = new[]
    {
        new ThresholdOption(30, "Light (30 ms)"),
        new ThresholdOption(50, "Normal (50 ms)"),
        new ThresholdOption(80, "Strong (80 ms)"),
        new ThresholdOption(120, "Very strong (120 ms)"),
    };

    public bool KeyboardFilter
    {
        get => _g.Settings.KeyboardFilter;
        set { _g.Settings.KeyboardFilter = value; SaveAndApply(); Raise(); Raise(nameof(KeyboardStats)); }
    }

    public int KeyboardThreshold
    {
        get => _g.Settings.KeyboardThresholdMs;
        set { _g.Settings.KeyboardThresholdMs = value; SaveAndApply(); Raise(); }
    }

    public string KeyboardStats => _g.Settings.KeyboardFilter
        ? (_g.KeyboardError != null ? "The filter could not start: " + _g.KeyboardError
            : $"{Fmt.Plural(_g.Keyboard.SuppressedCount, "repeated key press", "repeated key presses")} blocked since LaptopGuardian started.")
        : "The filter is off. Every key press goes through.";

    void SaveAndApply()
    {
        _g.SaveSettings();
        _g.ApplyKeyboardSettings();
    }

    public bool StartWithWindows
    {
        get => Startup.IsEnabled();
        set
        {
            try { Startup.Set(value, Environment.ProcessPath!); }
            catch (Exception ex) { ShowMessage("Could not change the startup setting: " + ex.Message, true); }
            Raise();
        }
    }

    public bool NotifyGraphics { get => _g.Settings.NotifyGraphics; set { _g.Settings.NotifyGraphics = value; _g.SaveSettings(); Raise(); } }
    public bool NotifyCrashes { get => _g.Settings.NotifyCrashes; set { _g.Settings.NotifyCrashes = value; _g.SaveSettings(); Raise(); } }
    public bool NotifyHeat { get => _g.Settings.NotifyHeat; set { _g.Settings.NotifyHeat = value; _g.SaveSettings(); Raise(); } }
    public bool NotifyMemory { get => _g.Settings.NotifyMemory; set { _g.Settings.NotifyMemory = value; _g.SaveSettings(); Raise(); } }
    public bool NotifyBattery { get => _g.Settings.NotifyBattery; set { _g.Settings.NotifyBattery = value; _g.SaveSettings(); Raise(); } }
    public bool NotifyHardware { get => _g.Settings.NotifyHardware; set { _g.Settings.NotifyHardware = value; _g.SaveSettings(); Raise(); } }

    // ------------------------------------------------------------ message bar

    public string? Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }
    public Brush MessageBrush => Theme.Brush(_messageIsError ? "Bad" : "Good");
    public string MessageGlyph => _messageIsError ? "" : "";

    public void ShowMessage(string text, bool error = false)
    {
        _messageIsError = error;
        Message = text;
        Raise(nameof(MessageBrush));
        Raise(nameof(MessageGlyph));
        _messageTimer?.Cancel();
        var cts = _messageTimer = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(error ? 20 : 10), cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _ui.BeginInvoke(() => Message = null);
        }, TaskScheduler.Default);
    }

    // ------------------------------------------------------------ commands

    public ICommand CopyReportCommand { get; }
    public ICommand SaveReportCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand TestNotificationCommand { get; }
    public ICommand DismissMessageCommand { get; }
    public ICommand OpenLinkCommand { get; }
    public ICommand CheckDriversCommand { get; }
    public ICommand CheckUpdatesCommand { get; }

    // ------------------------------------------------------------ drivers and app updates

    public List<GuideVm> Guides { get; private set; } = new();
    public List<ProblemDevice> ProblemDevices { get; private set; } = new();
    public List<DriverRowVm> InstalledDrivers { get; private set; } = new();
    public string DriverStatus { get; private set; } = "";
    public string BiosLine { get; private set; } = "";
    public bool CheckingDrivers { get; private set; }

    public bool NotifyDriverUpdates { get => _g.Settings.NotifyDriverUpdates; set { _g.Settings.NotifyDriverUpdates = value; _g.SaveSettings(); Raise(); } }
    public bool AutoUpdate { get => _g.Settings.AutoUpdate; set { _g.Settings.AutoUpdate = value; _g.SaveSettings(); Raise(); } }

    /// <summary>Set by the app; the updater lives there because applying an update restarts the app.</summary>
    public event Action? CheckForUpdatesRequested;
    string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; set => Set(ref _updateStatus, value); }

    void RefreshDrivers(Findings f)
    {
        var d = f.Drivers;
        CheckingDrivers = _g.CheckingDrivers;
        if (d == null)
        {
            DriverStatus = CheckingDrivers ? "Checking NVIDIA, Windows Update and your laptop's maker for newer drivers..." : "Not checked yet.";
            Guides = new(); ProblemDevices = new(); InstalledDrivers = new(); BiosLine = "";
            return;
        }
        DriverStatus = (CheckingDrivers ? "Checking again...  " : "") +
            (d.Updates.Count == 0 ? "Everything LaptopGuardian can check is up to date." : $"{Fmt.Plural(d.Updates.Count, "update", "updates")} available.") +
            $"  Checked {Fmt.WhenInSentence(d.CheckedAt)}." + (d.Notes.Count > 0 ? "  " + string.Join(" ", d.Notes) : "");
        BiosLine = "";
        Guides = UpdateGuides.Build(f.System, d).Select(g => new GuideVm(g.Title, g.Status,
            Theme.Brush(g.UpdateAvailable ? "Warn" : "Good"),
            g.UpdateAvailable ? "" : "",
            g.Steps.Select((s, i) => $"{i + 1}.  {s}").ToList(),
            g.Actions.Select(a => new GuideActionVm(a.Label, a.Primary, a.Kind switch
            {
                GuideAction.App => new Command(() => OpenApp(a.Target, a.Label)),
                GuideAction.Apply => new Command(async () => await ApplyAsync(a.Target)),
                _ => new Command(() => Open(a.Target)),
            })).ToList())).ToList();
        ProblemDevices = d.Problems;
        InstalledDrivers = d.Installed.Select(i => new DriverRowVm(i.Category, i.Device, i.Version, i.Date?.ToString("MMM yyyy") ?? "")).ToList();
    }

    string BuildReport() => ReportBuilder.Markdown(_g.Findings(), _g.Recommendations, Version);

    void CopyReport()
    {
        try
        {
            Clipboard.SetText(BuildReport());
            ShowMessage("Health report copied. Paste it into an email or a support chat.");
        }
        catch (Exception ex) { ShowMessage("Could not copy the report: " + ex.Message, true); }
    }

    void SaveReport()
    {
        // Downloads, not Documents: Windows' ransomware protection often blocks new apps from writing to Documents and Desktop.
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Laptop health report {DateTime.Now:yyyy-MM-dd}.md",
            Filter = "Markdown report (*.md)|*.md|Text file (*.txt)|*.txt",
            InitialDirectory = Directory.Exists(downloads) ? downloads : null,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, BuildReport());
            ShowMessage("Saved " + Path.GetFileName(dlg.FileName) + ".");
        }
        catch (UnauthorizedAccessException)
        {
            ShowMessage("Windows blocked saving there (probably Controlled folder access). Choose another folder, such as Downloads.", true);
        }
        catch (Exception ex) { ShowMessage("Could not save the report: " + ex.Message, true); }
    }

    public async Task ApplyAsync(string actionId)
    {
        var result = await _g.ApplyAsync(actionId);
        ShowMessage(result.Message, !result.Ok);
        if (result.OpenPath != null) Open(result.OpenPath);
    }

    public void Dismiss(string recommendationId) => _g.Dismiss(recommendationId);

    public static void Open(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    void OpenApp(string appId, string label)
    {
        try { InstalledApps.Open(appId); }
        catch (Exception ex) { ShowMessage($"Could not {label.ToLowerInvariant()}: {ex.Message}", true); }
    }

    void SaveNote(string id, string? note) => _g.Journal.SetNote(id, note);

    // ------------------------------------------------------------ refresh

    void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        _ui.BeginInvoke(DispatcherPriority.Background, () => { _refreshQueued = false; Refresh(); });
    }

    void QueueGpu()
    {
        if (_gpuQueued) return;
        _gpuQueued = true;
        _ui.BeginInvoke(DispatcherPriority.Background, () => { _gpuQueued = false; RefreshGpu(); });
    }

    void QueueKeys()
    {
        if (_keysQueued) return;
        _keysQueued = true;
        _ui.BeginInvoke(DispatcherPriority.Background, () => { _keysQueued = false; Raise(nameof(KeyboardStats)); });
    }

    void Refresh()
    {
        var f = _g.Findings();
        var recs = _g.Recommendations;
        var (level, headline, summary) = Advisor.Status(f, recs);
        StatusLevel = level;
        Scanning = _g.Scanning;
        StatusHeadline = f.System.Model.Length == 0 && Scanning ? "Checking..." : headline;
        StatusSummary = f.System.Model.Length == 0 && Scanning ? "Reading what Windows recorded." : summary;
        LastScanText = Scanning ? "Checking now..." : _g.State.LastScan is DateTimeOffset ls ? $"Checked {Fmt.WhenInSentence(ls)}" : "";

        var gpuAdapter = f.System.Discrete;
        MachineLine = string.Join("  ·  ", new[] { $"{f.System.Maker} {f.System.ShortModel}".Trim(), gpuAdapter?.Name, f.System.Windows }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        GpuDriverText = gpuAdapter == null ? "" :
            $"{gpuAdapter.Name}  ·  driver {(gpuAdapter.FriendlyVersion != null ? gpuAdapter.Vendor + " " + gpuAdapter.FriendlyVersion : gpuAdapter.DriverVersion)}" +
            (gpuAdapter.DriverDate is DateTime dd ? $" from {dd:MMMM d, yyyy}" : "");

        // Stability
        int c7 = f.Crashes(TimeSpan.FromDays(7)).Count(), c30 = f.Crashes(TimeSpan.FromDays(30)).Count();
        int g7 = f.GraphicsErrors(TimeSpan.FromDays(7)).Count(), g30 = f.GraphicsErrors(TimeSpan.FromDays(30)).Count();
        Shutdowns7 = c7.ToString();
        Shutdowns7Brush = Theme.Brush(c7 > 0 ? "Bad" : "Good");
        Graphics7 = g7.ToString();
        Graphics7Brush = Theme.Brush(g7 > 2 ? "Bad" : g7 > 0 ? "Warn" : "Good");
        Stability30 = $"Last 30 days: {Fmt.Plural(c30, "shutdown", "shutdowns")}, {Fmt.Plural(g30, "graphics error", "graphics errors")}.";
        var crashes = f.Incidents.Where(i => i.Kind == IncidentKind.UnexpectedShutdown).OrderByDescending(i => i.Time).ToList();
        LastShutdownText = crashes.FirstOrDefault() is Incident last
            ? $"Last: {Fmt.When(last.Time)}, {char.ToLowerInvariant(last.Title[0])}{last.Title[1..]}."
            : "No unexpected shutdowns in the last 60 days.";
        Nav[2].Badge = c7 > 0 ? c7.ToString() : null;

        // Battery
        var (pct, ac) = Guardian.PowerStatus();
        HasBattery = pct != null;
        BatteryPctText = pct is int p ? $"{p}%" : "No battery";
        BatterySourceText = ac switch { true => "plugged in", false => "on battery", _ => "" };
        if (f.Battery?.HealthPct is double h)
        {
            BatteryHealth = h;
            BatteryHealthText = $"{h:0}%";
            BatteryHealthBrush = Theme.Brush(h < 60 ? "Bad" : h < 80 ? "Warn" : "Good");
            BatteryDetailText = $"Holds {f.Battery.FullMWh / 1000.0:0} of its original {f.Battery.DesignMWh / 1000.0:0} Wh" +
                (f.Battery.Cycles is int cyc ? $", {cyc} charge cycles." : ".");
        }
        else
        {
            BatteryHealth = 0;
            BatteryHealthText = "--";
            BatteryHealthBrush = Theme.Brush("Faint");
            BatteryDetailText = HasBattery ? "Reading the battery report..." : "This PC has no battery.";
        }

        // Power settings
        if (f.Power is PowerSettingsInfo pw)
        {
            PowerRows = new List<SettingRow>
            {
                new("Sleep after", Fmt.Seconds(pw.SleepAfterAc), Fmt.Seconds(pw.SleepAfterDc)),
                new("Then hibernate after", Fmt.Seconds(pw.HibernateAfterAc), Fmt.Seconds(pw.HibernateAfterDc)),
                new("Closing the lid", PowerConfig.ActionName(pw.LidAc), PowerConfig.ActionName(pw.LidDc)),
                new("Power button", PowerConfig.ActionName(pw.PowerButtonAc), PowerConfig.ActionName(pw.PowerButtonDc)),
            };
            SleepModeText = (pw.ModernStandby
                    ? "Sleep on this laptop is Modern Standby: Windows stays partly awake (like a phone) so it can wake instantly. "
                    : "Sleep on this PC is classic standby. ")
                + (pw.HibernateAvailable ? "Hibernation is available." : "Hibernation is turned off.");
            PowerPlanText = string.IsNullOrEmpty(pw.SchemeName) ? "" : $"Power plan: {pw.SchemeName}";
        }

        RefreshDrivers(f);

        // Lists, rebuilt only when something in them changed (rebuilding resets open drop-downs).
        var items = f.Incidents;
        string sig = string.Join("|", items.Count, items.FirstOrDefault()?.Id, string.Join(",", recs.Select(r => r.Id + r.ActionLabel)),
            f.KernelReports.Count, f.KernelReports.Sum(k => k.TimesLogged), _timelineFilter, Theme.IsDark, DateTime.Today, f.Drivers?.CheckedAt);
        if (sig != _signature)
        {
            _signature = sig;
            Recommendations = recs.Select(r => new RecommendationVm(r, this)).ToList();
            Recent = items.Where(i => i.Severity >= Severity.Warning).Take(6).Select(i => new IncidentVm(i, false)).ToList();
            Timeline = WithDays(items.Where(i => _timelineFilter switch
            {
                "Shutdowns" => i.Kind == IncidentKind.UnexpectedShutdown,
                "Graphics errors" => i.Kind is IncidentKind.GraphicsError or IncidentKind.HardwareError,
                "Alerts" => i.Kind is IncidentKind.GpuHot or IncidentKind.GpuMemoryFull or IncidentKind.BatteryLow,
                _ => true,
            }).Take(400));
            Crashes = crashes.Take(60).Select(c => new CrashVm(c, SaveNote)).ToList();
            int asleep = crashes.Count(c => c.Crash != null && CrashScanner.IsAsleepCause(c.Crash.Cause));
            CrashSummary = crashes.Count == 0
                ? "None in the last 60 days."
                : $"{Fmt.Plural(crashes.Count, "unexpected shutdown", "unexpected shutdowns")} in the last 60 days, {asleep} of them while asleep." +
                  (crashes.Count >= 2 && crashes.Take(2).All(c => c.Crash != null && CrashScanner.IsAsleepCause(c.Crash.Cause)) ? " The two most recent both happened during sleep." : "");
            GraphicsErrors = items.Where(i => i.Kind is IncidentKind.GraphicsError or IncidentKind.HardwareError).Take(60).Select(i => new IncidentVm(i, false)).ToList();

            var stuck = f.KernelReports.Where(k => k.Stuck).ToList();
            HasStuckReports = stuck.Count > 0;
            KernelReports = f.KernelReports.Select(k => new KernelReportVm(
                k.Code switch { "141" => "Graphics card stopped responding", "117" => "Display driver stopped responding", "193" => "Graphics diagnostic snapshot", _ => k.CodeName },
                $"Happened {Fmt.When(k.HappenedAt)}  ·  logged {k.TimesLogged:N0} {(k.TimesLogged == 1 ? "time" : "times")}" + (k.Stuck ? $", most recently {Fmt.WhenInSentence(k.LastLogged)}" : ""),
                $"{k.EventName} 0x{k.Code} ({k.CodeName}). {k.Meaning}",
                Theme.Brush(k.Stuck ? "Warn" : "Info"), k.Stuck)).ToList();
            int total = f.KernelReports.Sum(k => k.TimesLogged);
            KernelSummary = f.KernelReports.Count == 0
                ? "Windows has no kernel crash reports from the last 60 days."
                : $"Event Viewer and Reliability Monitor show {total:N0} entries, but they are {Fmt.Plural(f.KernelReports.Count, "report", "reports")}. " +
                  (HasStuckReports ? "Windows could not send them, so it retries every 4 hours and logs each retry as a new failure." : "");
        }

        Raise(string.Empty);
    }

    static List<IncidentVm> WithDays(IEnumerable<Incident> items)
    {
        var list = new List<IncidentVm>();
        string? day = null;
        foreach (var i in items)
        {
            var d = Fmt.Day(i.Time);
            list.Add(new IncidentVm(i, d != day));
            day = d;
        }
        return list;
    }

    void RefreshGpu()
    {
        var s = _g.Gpu.Latest;
        var history = _g.Gpu.History.Where(h => !h.Asleep).ToList();
        GpuName = s?.Name ?? _g.System.Discrete?.Name ?? "Graphics card";
        GpuAsleep = s?.Asleep == true;
        double? limit = s?.TemperatureLimitC;
        if (s?.TemperatureC is double t)
        {
            GpuTempText = $"{t:0}°";
            // With no stated limit, stay calm until the card is hot by anyone's measure.
            double hot = limit - 2 ?? 95, warm = limit - 10 ?? 85;
            GpuTempBrush = Theme.Brush(t >= hot ? "Bad" : t >= warm ? "Warn" : "Good");
        }
        else
        {
            GpuTempText = GpuAsleep ? "Asleep" : "--";
            GpuTempBrush = Theme.Brush("Muted");
        }
        GpuLimitText = GpuAsleep ? "Not in use, so it is powered down." : limit is double lim ? $"target {lim:0}°C" : "";
        GpuThrottleText = s?.Throttle != null ? $"Slowing down because of {s.Throttle}." : "";
        // A power cap is routine on laptops; heat is worth a second look.
        GpuThrottleBrush = Theme.Brush(s?.Throttle?.Contains("heat") == true ? "Warn" : "Muted");
        GpuUtil = s?.UtilizationPct ?? 0;
        GpuUtilText = s?.UtilizationPct is double u ? $"{u:0}%" : "--";
        GpuMem = s?.MemoryPct ?? 0;
        GpuMemText = s?.MemoryUsedGB is double m && s.MemoryTotalGB is double tot ? $"{m:0.0} of {tot:0.0} GB" : "--";
        GpuMemBrush = Theme.Brush(GpuMem >= 95 ? "Bad" : GpuMem >= 85 ? "Warn" : "Accent");
        GpuPowerText = s?.PowerW is double pw ? (s.PowerLimitW is double pl ? $"{pw:0} W of {pl:0} W" : $"{pw:0} W") : "--";
        GpuApps = s?.TopApps ?? new List<GpuApp>();
        GpuAppsText = GpuApps.Count == 0 ? (GpuAsleep ? "" : "Nothing is using it much.") : "Busiest: " + string.Join(", ", GpuApps.Take(3).Select(a => $"{a.Name} {a.UtilizationPct:0}%"));
        TempSeries = history.Select(h => h.TemperatureC ?? 0).ToArray();
        UtilSeries = history.Select(h => h.UtilizationPct ?? 0).ToArray();
        MemSeries = history.Select(h => h.MemoryPct ?? 0).ToArray();
        foreach (var name in new[]
        {
            nameof(GpuName), nameof(GpuAsleep), nameof(GpuTempText), nameof(GpuTempBrush), nameof(GpuLimitText), nameof(GpuThrottleText), nameof(GpuThrottleBrush),
            nameof(GpuUtil), nameof(GpuUtilText), nameof(GpuMem), nameof(GpuMemText), nameof(GpuMemBrush), nameof(GpuPowerText),
            nameof(GpuApps), nameof(GpuAppsText), nameof(TempSeries), nameof(UtilSeries), nameof(MemSeries),
        }) Raise(name);
    }

    static ImageSource LoadIcon()
    {
        using var bmp = IconFactory.Render(64, IconFactory.Calm);
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }
}
