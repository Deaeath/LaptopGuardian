using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LaptopGuardian.Core;

public sealed record ActionResult(bool Ok, string Message, string? OpenPath = null);

/// <summary>
/// The engine behind the window and the tray: watches the event log as it is written, samples the
/// graphics card, checks the battery, scans history, and raises one alert per real event.
/// </summary>
public sealed class Guardian : IDisposable
{
    public static string DefaultDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaptopGuardian");

    readonly string? _dataDir;
    readonly object _scanGate = new();
    readonly Dictionary<string, DateTimeOffset> _lastAlert = new();
    readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    EventLogWatcher? _watcher;
    Timer? _batteryTimer, _rescanTimer;
    int _batteryAlertLevel = 101;
    DateTimeOffset? _hotSince, _memSince;

    public Journal Journal { get; }
    public AppSettings Settings { get; }
    public AppState State { get; }
    public GpuMonitor Gpu { get; } = new();
    public KeyboardDebouncer Keyboard { get; } = new();

    public SystemSnapshot System { get; private set; } = new();
    public PowerSettingsInfo? Power { get; private set; }
    public BatteryReport? Battery { get; private set; }
    public List<KernelReport> KernelReports { get; private set; } = new();
    public List<Recommendation> Recommendations { get; private set; } = new();
    public DriverReport? Drivers { get; private set; }
    public bool CheckingDrivers { get; private set; }
    Timer? _driverTimer, _recorderTimer;
    FlightRecorder Recorder => _recorder ??= new FlightRecorder(_dataDir == null ? null : Path.Combine(_dataDir, "flight-recorder.jsonl"));
    FlightRecorder? _recorder;
    volatile string _powerState = "awake";
    /// <summary>The firmware's thermal sensor, as last recorded.</summary>
    public double? ThermalZoneC { get; private set; }

    void RecordFlightSample()
    {
        var (pct, ac) = PowerStatus();
        ThermalZoneC = FlightRecorder.ReadThermalZone();
        var g = Gpu.Latest;
        if (g != null && DateTimeOffset.Now - g.At > TimeSpan.FromMinutes(1)) g = null;
        Recorder.Write(new FlightSample
        {
            At = DateTimeOffset.Now,
            State = _powerState,
            ZoneC = ThermalZoneC,
            GpuC = g?.TemperatureC,
            GpuLimitC = g?.TemperatureLimitC,
            GpuBusyPct = g?.UtilizationPct is double u ? Math.Round(u) : null,
            GpuMemoryPct = g?.MemoryPct is double m ? Math.Round(m) : null,
            GpuW = g?.PowerW is double w ? Math.Round(w) : null,
            Throttle = g?.Throttle,
            OnAc = ac,
            BatteryPct = pct,
            TopApps = g == null || g.TopApps.Count == 0 ? null : string.Join(", ", g.TopApps.Take(3).Select(a => $"{a.Name} {a.UtilizationPct:0}%")),
        });
    }

    void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend) { _powerState = "suspending"; RecordFlightSample(); }
        else if (e.Mode == Microsoft.Win32.PowerModes.Resume) { _powerState = "awake"; RecordFlightSample(); }
    }
    public bool Scanning { get; private set; }
    public string? KeyboardError { get; private set; }
    /// <summary>Milliseconds per step of the last scan (shown by --diagnose).</summary>
    public Dictionary<string, long> ScanTimings { get; private set; } = new();

    public event Action<Alert>? AlertRaised;
    /// <summary>Raised (on a background thread) whenever anything the window shows has changed.</summary>
    public event Action? Changed;

    /// <param name="dataDir">Null keeps everything in memory (used by --diagnose).</param>
    public Guardian(string? dataDir)
    {
        _dataDir = dataDir;
        Journal = new Journal(dataDir == null ? null : Path.Combine(dataDir, "journal.json"));
        Settings = JsonFile.Load<AppSettings>(dataDir == null ? null : Path.Combine(dataDir, "settings.json"));
        State = JsonFile.Load<AppState>(dataDir == null ? null : Path.Combine(dataDir, "state.json"));
        Journal.Changed += () => Changed?.Invoke();
        Gpu.Sampled += OnGpuSample;
    }

    public string? DataDir => _dataDir;

    public void SaveSettings() => JsonFile.Save(_dataDir == null ? null : Path.Combine(_dataDir, "settings.json"), Settings);
    void SaveState() => JsonFile.Save(_dataDir == null ? null : Path.Combine(_dataDir, "state.json"), State);

    public void Start()
    {
        ApplyKeyboardSettings();
        Gpu.Start();
        StartWatcher();
        _batteryTimer = new Timer(_ => CheckBattery(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
        // Full scan now, then every six hours (Windows Error Reporting retries on its own four-hour clock).
        _rescanTimer = new Timer(_ => Scan(), null, TimeSpan.FromSeconds(1), TimeSpan.FromHours(6));
        // Driver and BIOS updates: shortly after start (the history scan goes first), then daily.
        _driverTimer = new Timer(_ => _ = CheckDriversAsync(), null, TimeSpan.FromSeconds(45), TimeSpan.FromHours(24));
        // The flight recorder: a line every 10 seconds, so the next sudden reset leaves evidence.
        _recorderTimer = new Timer(_ => { try { RecordFlightSample(); } catch (Exception ex) { Debug.WriteLine(ex); } }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10));
        try { Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged; } catch (Exception ex) { Debug.WriteLine(ex); }
    }

    int _checkingDrivers;

    /// <summary>Checks NVIDIA, Windows Update and the laptop maker for newer drivers and BIOS, and lists problem devices.</summary>
    public async Task CheckDriversAsync()
    {
        if (Interlocked.Exchange(ref _checkingDrivers, 1) == 1) return;
        try
        {
            CheckingDrivers = true;
            Changed?.Invoke();
            if (string.IsNullOrEmpty(System.Model)) System = SystemInfo.Read();
            var report = await DriverChecks.RunAsync(System);
            Drivers = report;
            Recompute(); // everything but Windows Update, which can take minutes

            var wuUpdates = await Task.Run(() => DriverChecks.WindowsUpdateDrivers(report.Notes));
            report.Updates.AddRange(wuUpdates);
            report.WindowsUpdateChecked = true;
            State.LastDriverCheck = report.CheckedAt;

            var fresh = report.Updates.Where(u => !State.AnnouncedDriverUpdates.Contains(u.Key)).ToList();
            State.AnnouncedDriverUpdates.AddRange(fresh.Select(u => u.Key));
            if (State.AnnouncedDriverUpdates.Count > 200) State.AnnouncedDriverUpdates.RemoveRange(0, State.AnnouncedDriverUpdates.Count - 200);
            SaveState();

            if (fresh.Count > 0 && Settings.NotifyDriverUpdates)
            {
                var top = fresh.OrderByDescending(u => u.Severity).First();
                string title = fresh.Count == 1 ? $"{top.Title} {top.Available} is available" : $"{fresh.Count} driver updates are available";
                string body = top.Source switch
                {
                    "NVIDIA" => $"You have {top.Installed}. Newer graphics drivers often fix crashes and resets.",
                    "ASUS" => $"Your BIOS is {top.Installed}. BIOS updates fix sleep, wake and stability problems.",
                    _ => "Open LaptopGuardian to install them.",
                };
                Raise("drivers", TimeSpan.FromHours(12), new Alert(IncidentKind.GraphicsError, Severity.Info, title, body, "drivers"));
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally
        {
            CheckingDrivers = false;
            Interlocked.Exchange(ref _checkingDrivers, 0);
            Recompute();
        }
    }

    public void ApplyKeyboardSettings()
    {
        Keyboard.Enabled = Settings.KeyboardFilter;
        Keyboard.ThresholdMs = Settings.KeyboardThresholdMs;
        if (Settings.KeyboardFilter && !Keyboard.Running)
        {
            try { Keyboard.Start(); KeyboardError = null; }
            catch (Exception ex) { KeyboardError = ex.Message; }
        }
    }

    // ---------------------------------------------------------------- live events

    void StartWatcher()
    {
        try
        {
            // EvtSubscribe delivers only events written from now on. The old EventLog.EntryWritten
            // component re-raised the whole log when the full, circular System log wrapped, which
            // announced months-old GPU errors as if they had just happened.
            var query = new EventLogQuery("System", PathType.LogName, GraphicsEvents.XPath());
            _watcher = new EventLogWatcher(query);
            _watcher.EventRecordWritten += OnEventWritten;
            _watcher.Enabled = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            _watcher = null;
        }
    }

    void OnEventWritten(object? sender, EventRecordWrittenEventArgs e)
    {
        if (e.EventException != null)
        {
            // The event log service restarted or the subscription broke: resubscribe shortly.
            ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(5000); try { _watcher?.Dispose(); } catch { } StartWatcher(); });
            return;
        }
        using var r = e.EventRecord;
        if (r == null) return;
        var incident = FromEventRecord(r);
        if (incident == null) return;

        // Never announce something old, whatever delivered it.
        bool fresh = IsFresh(incident.Time, DateTimeOffset.Now);
        if (fresh && Gpu.Latest is GpuSnapshot g && DateTimeOffset.Now - g.At < TimeSpan.FromMinutes(1)) incident.Gpu = g;
        if (!Journal.Add(incident) || !fresh) return;

        if (incident.Kind == IncidentKind.GraphicsError && Settings.NotifyGraphics)
            Raise("graphics", TimeSpan.FromMinutes(5), new Alert(incident.Kind, incident.Severity, incident.Title,
                incident.Summary + (incident.Gpu != null ? " At the time: " + incident.Gpu.Describe() : ""), "timeline", incident.Id));
        else if (incident.Kind == IncidentKind.HardwareError && Settings.NotifyHardware && incident.Severity >= Severity.Warning)
            Raise("hardware", TimeSpan.FromMinutes(30), new Alert(incident.Kind, incident.Severity, incident.Title, incident.Summary, "timeline", incident.Id));
        Recompute();
    }

    /// <summary>Only events written in the last few minutes are news. Anything older is history, recorded quietly.</summary>
    public static bool IsFresh(DateTimeOffset eventTime, DateTimeOffset now) =>
        eventTime <= now.AddMinutes(1) && now - eventTime < TimeSpan.FromMinutes(10);

    static Incident? FromEventRecord(EventRecord r)
    {
        string provider = r.ProviderName ?? "";
        int level = r.Level ?? 2;
        var d = GraphicsEvents.Decode(provider, r.Id, level, EventLogX.Props(r));
        var time = EventLogX.Time(r);
        return new Incident
        {
            Id = $"evt:{r.RecordId}:{time.UtcTicks}",
            Kind = d.Kind,
            Time = time,
            Severity = d.Severity,
            Title = d.Title,
            Summary = d.Summary,
            Detail = d.Detail,
            Source = $"{provider} event {r.Id}",
            Data = d.Data,
        };
    }

    // ---------------------------------------------------------------- scans

    /// <summary>Reads everything again: machine, power, battery, crash history, graphics history, crash reports.</summary>
    public void Scan()
    {
        if (!Monitor.TryEnter(_scanGate)) return;
        try
        {
            Scanning = true;
            Changed?.Invoke();
            bool firstImport = !State.HistoryImported;
            var timings = new Dictionary<string, long>();
            var sw = Stopwatch.StartNew();
            void Mark(string step) { timings[step] = sw.ElapsedMilliseconds; sw.Restart(); }

            System = SystemInfo.Read();
            try { Power = PowerConfig.Read(); } catch { Power = null; }
            Mark("system+power");
            Battery = BatteryReport.Generate() ?? Battery;
            Mark("battery report");

            // Graphics and hardware errors that happened while LaptopGuardian was not running.
            var missed = new List<Incident>();
            foreach (var inc in EventLogX.Query("System", GraphicsEvents.XPath(TimeSpan.FromDays(60)), FromEventRecord))
                if (Journal.Add(inc, save: false) && inc.Time > _startedAt.AddHours(-1) && inc.Kind == IncidentKind.GraphicsError)
                    missed.Add(inc);
            Mark("graphics history");

            // Unexpected shutdowns.
            var newCrashes = new List<Incident>();
            // Work out new crashes, and any recorded by older reasoning (keeping what the person noted about them).
            foreach (var found in CrashScanner.Scan(TimeSpan.FromDays(60), Battery, Recorder,
                t => (Journal.Find(CrashScanner.IdFor(t))?.Crash?.ClassifierVersion ?? 0) < CrashClassifier.Version))
            {
                var inc = CrashScanner.ToIncident(found);
                bool isNew = !Journal.Contains(inc.Id);
                Journal.Upsert(inc);
                if (isNew && DateTimeOffset.Now - found.Event.Time < TimeSpan.FromHours(24)) newCrashes.Add(inc);
            }
            Mark("crashes");

            // Windows Error Reporting: distinct reports, not every retry.
            KernelReports = ReadKernelReports();
            foreach (var k in KernelReports.Where(k => k.EventName.Equals("LiveKernelEvent", StringComparison.OrdinalIgnoreCase)))
                Journal.Add(FromKernelReport(k), save: false);
            Mark("crash reports");

            Journal.Save();
            Mark("save");
            ScanTimings = timings;
            State.LastScan = DateTimeOffset.Now;
            State.HistoryImported = true;
            SaveState();
            Recompute();

            if (firstImport) AnnounceHistory();
            else
            {
                if (Settings.NotifyCrashes)
                    foreach (var c in newCrashes.OrderBy(c => c.Time))
                        Raise("crash:" + c.Id, TimeSpan.FromDays(1), new Alert(c.Kind, Severity.Critical,
                            $"{c.Title}, {Fmt.WhenInSentence(c.Time)}", c.Summary, "shutdowns", c.Id));
                if (missed.Count > 0 && Settings.NotifyGraphics)
                    Raise("graphics", TimeSpan.FromMinutes(5), new Alert(IncidentKind.GraphicsError, Severity.Warning,
                        Fmt.Plural(missed.Count, "graphics error", "graphics errors") + " while LaptopGuardian was closed",
                        missed[0].Summary, "timeline", missed[0].Id));
            }
        }
        finally
        {
            Scanning = false;
            Monitor.Exit(_scanGate);
            Changed?.Invoke();
        }
    }

    List<KernelReport> ReadKernelReports()
    {
        var reports = WerReports.Group(WerReports.Read(TimeSpan.FromDays(60)));
        // The log keeps the old retries after the reports themselves are gone.
        if (State.ReportsClearedAt is DateTimeOffset cleared)
            foreach (var r in reports) r.MarkClearedAt(cleared);
        return reports;
    }

    void AnnounceHistory()
    {
        var f = Findings();
        int crashes = f.Crashes(TimeSpan.FromDays(30)).Count();
        int gfx = f.GraphicsErrors(TimeSpan.FromDays(30)).Count();
        if (crashes == 0 && gfx == 0) return;
        var parts = new List<string>();
        if (crashes > 0) parts.Add(Fmt.Plural(crashes, "unexpected shutdown", "unexpected shutdowns"));
        if (gfx > 0) parts.Add(Fmt.Plural(gfx, "graphics error", "graphics errors"));
        Raise("history", TimeSpan.FromDays(1), new Alert(IncidentKind.UnexpectedShutdown, crashes > 0 ? Severity.Critical : Severity.Warning,
            "LaptopGuardian checked the last 30 days",
            $"It found {string.Join(" and ", parts)}. Open it to see what happened and what to do.", "overview"));
    }

    static Incident FromKernelReport(KernelReport k)
    {
        bool reset = k.Code is "141" or "117";
        string title = k.Code switch
        {
            "141" => "Graphics card stopped responding",
            "117" => "Display driver stopped responding",
            "193" => "Graphics diagnostic snapshot",
            _ => k.CodeName,
        };
        return new Incident
        {
            Id = "wer:" + k.ReportId,
            Kind = IncidentKind.GraphicsError,
            Time = k.HappenedAt,
            Severity = reset ? Severity.Warning : Severity.Info,
            Title = title,
            Summary = k.Meaning,
            Detail = k.Stuck
                ? $"Windows saved this report and has tried to send it {k.TimesLogged:N0} times since {Fmt.When(k.FirstLogged)}. Each try is logged again, so it looks like many failures; it was one."
                : $"Windows saved this report {Fmt.When(k.FirstLogged)}.",
            Source = $"Windows Error Reporting, {k.EventName} 0x{k.Code}",
            Data = k.ReportId,
        };
    }

    public Findings Findings() => new()
    {
        System = System,
        Power = Power,
        Battery = Battery,
        Incidents = Journal.Items,
        KernelReports = KernelReports,
        Gpu = Gpu.Latest,
        Drivers = Drivers,
        Now = DateTimeOffset.Now,
    };

    /// <summary>Re-derives the advice from what is known now (live heat and memory change it) and tells the window.</summary>
    public void Recompute()
    {
        try
        {
            var f = Findings();
            Recommendations = Advisor.Advise(f).Where(r => !State.DismissedRecommendations.Contains(r.Id)).ToList();
        }
        catch { }
        Changed?.Invoke();
    }

    public void RestoreDismissed()
    {
        State.DismissedRecommendations.Clear();
        SaveState();
        Recompute();
    }

    public void Dismiss(string recommendationId)
    {
        if (!State.DismissedRecommendations.Contains(recommendationId)) State.DismissedRecommendations.Add(recommendationId);
        SaveState();
        Recompute();
    }

    // ---------------------------------------------------------------- sample data

    /// <summary>Made-up data for the public screenshots, so they show no real machine, apps or drivers.</summary>
    public void LoadSampleData()
    {
        var now = DateTimeOffset.Now;
        System = new SystemSnapshot
        {
            Manufacturer = "Example", Model = "Laptop 15", BiosVersion = "1.24", BiosDate = now.AddMonths(-14).Date,
            Windows = "Windows 11 Home 25H2", Cpu = "AMD Ryzen 7 7840HS",
            Adapters = new()
            {
                new GraphicsAdapter("NVIDIA GeForce RTX 4060 Laptop GPU", "32.0.16.1656", "616.56", now.AddMonths(-2).Date, 8, "NVIDIA"),
                new GraphicsAdapter("AMD Radeon(TM) 780M", "32.0.21045.16010", "26.10.1", now.AddDays(-20).Date, 0.5, "AMD"),
            },
        };
        Power = new PowerSettingsInfo
        {
            SchemeName = "Balanced", SleepAfterAc = 1800, SleepAfterDc = 300, HibernateAfterAc = 0, HibernateAfterDc = 3600,
            LidAc = PowerConfig.ActionSleep, LidDc = PowerConfig.ActionSleep, PowerButtonAc = PowerConfig.ActionSleep, PowerButtonDc = PowerConfig.ActionSleep,
            ModernStandby = true, HibernateSupported = true, HiberFilePresent = true, LidPresent = true, HasBattery = true,
        };
        Battery = new BatteryReport { DesignMWh = 60000, FullMWh = 51600, Cycles = 212, ModernStandby = true };

        Incident Crash(int minutesAgo, int gapSeconds, FlightSample? reading, uint bug = 0, bool standby = false, DateTimeOffset? asleep = null)
        {
            var back = now.AddMinutes(-minutesAgo);
            var active = back.AddSeconds(-gapSeconds);
            var facts = new CrashFacts { RestartedAt = back, LastActivity = active, LastReading = reading, BugcheckCode = bug, ConnectedStandby = standby, AsleepSince = asleep };
            var e = new KernelPower41(minutesAgo, back, bug, 0, 0, false, standby);
            return CrashScanner.ToIncident(new CrashScanner.Found(e, facts, CrashClassifier.Classify(facts)));
        }
        var hot = new FlightSample { At = now.AddMinutes(-95).AddSeconds(-24), ZoneC = 96, GpuC = 87, GpuLimitC = 87, GpuBusyPct = 99, GpuMemoryPct = 91, GpuW = 112, OnAc = true, TopApps = "Game.exe 92%, Browser.exe 4%" };
        Journal.Add(Crash(95, 24, hot), save: false);
        Journal.Add(Crash(60 * 27, 31, hot with { At = now.AddMinutes(-60 * 27).AddSeconds(-31), GpuC = 86 }), save: false);
        Journal.Add(Crash(60 * 24 * 9, 300, null, bug: 0x116), save: false);
        var gpu = GraphicsEvents.Decode("nvlddmkm", 153, 2, new[] { @"\Device\Video3", "Error occurred on GPUID: 100" });
        Journal.Add(new Incident
        {
            Id = "sample:gpu", Kind = gpu.Kind, Time = now.AddHours(-30), Severity = gpu.Severity, Title = gpu.Title, Summary = gpu.Summary, Detail = gpu.Detail,
            Source = "nvlddmkm event 153", Data = gpu.Data,
            Gpu = new GpuSnapshot { TemperatureC = 86, TemperatureLimitC = 87, UtilizationPct = 98, MemoryUsedGB = 7.4, MemoryTotalGB = 8, PowerW = 110, TopApps = new() { new GpuApp("Game.exe", 1, 91) } },
        }, save: false);

        var start = now.AddDays(-40);
        var retries = Enumerable.Range(0, 300).Select(i => new WerEntry(start.AddHours(i * 4), "LiveKernelEvent", i % 2 == 0 ? "141" : "193",
            i % 2 == 0 ? "sample-1" : "sample-2", "2051", "", "")).ToList();
        KernelReports = WerReports.Group(retries);

        var nvidia = new DriverUpdate("nvidia:621.14", "NVIDIA", "NVIDIA Game Ready driver", "616.56", "621.14", now.AddDays(-3).Date, DriverPages.Nvidia, null, Severity.Warning, DriverPages.Nvidia);
        var amd = new DriverUpdate("amd:26.10.2", "AMD", "AMD Software driver", "26.10.1", "26.10.2", null, DriverPages.Amd, null, Severity.Warning);
        Drivers = new DriverReport
        {
            Updates = new() { nvidia, amd },
            NvidiaLatest = "621.14", AmdLatest = "26.10.2", AmdPageUrl = DriverPages.Amd, WindowsUpdateChecked = true,
            Apps = new() { new InstalledApp("NVIDIA App", "sample.nvidia"), new InstalledApp("AMD Software", "sample.amd") },
            Installed = new()
            {
                new DriverInfo("Graphics", "NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA", "32.0.16.1656", now.AddMonths(-2).Date),
                new DriverInfo("Graphics", "AMD Radeon(TM) 780M", "Advanced Micro Devices, Inc.", "32.0.21045.16010", now.AddDays(-20).Date),
                new DriverInfo("Network", "Wi-Fi 6E Wireless LAN Card", "Example", "3.4.2.1046", now.AddMonths(-20).Date),
            },
        };
        ThermalZoneC = 71;
        Gpu.InjectSamples(Enumerable.Range(0, 120).Select(i => new GpuSnapshot
        {
            At = now.AddSeconds((i - 120) * 7.5), Name = "NVIDIA GeForce RTX 4060 Laptop GPU",
            TemperatureC = 66 + 8 * Math.Sin(i / 14.0), TemperatureLimitC = 87, UtilizationPct = 52 + 22 * Math.Sin(i / 9.0), MemoryUsedGB = 3.4 + 0.6 * Math.Sin(i / 30.0), MemoryTotalGB = 8,
            PowerW = 58, PowerLimitW = 115, TopApps = new() { new GpuApp("Game.exe", 1, 48), new GpuApp("Browser.exe", 2, 5) },
        }));
        State.LastScan = now;
        Recompute();
    }

    // ---------------------------------------------------------------- live checks

    void OnGpuSample(GpuSnapshot s)
    {
        var now = s.At;
        if (!s.Asleep && s.TemperatureC is double t && s.TemperatureLimitC is double lim && t >= lim - 1)
        {
            _hotSince ??= now;
            if (now - _hotSince >= TimeSpan.FromSeconds(90) && Settings.NotifyHeat)
            {
                var inc = new Incident
                {
                    Id = $"hot:{now.UtcTicks}", Kind = IncidentKind.GpuHot, Time = now, Severity = Severity.Warning,
                    Title = $"Graphics card at its temperature limit ({t:0}°C)",
                    Summary = $"It has been at {t:0}°C (limit {lim:0}°C) for over a minute and is slowing itself down.",
                    Detail = "Raise the back of the laptop, clear the vents, use the performance fan mode, or cap the frame rate.",
                    Source = "LaptopGuardian", Gpu = s,
                };
                if (Raise("heat", TimeSpan.FromMinutes(45), new Alert(inc.Kind, inc.Severity, inc.Title, inc.Summary + " " + s.Describe(), "graphics", inc.Id)))
                    Journal.Add(inc);
            }
        }
        else _hotSince = null;

        if (!s.Asleep && s.MemoryPct is double mp && mp >= 95)
        {
            _memSince ??= now;
            if (now - _memSince >= TimeSpan.FromSeconds(60) && Settings.NotifyMemory)
            {
                var inc = new Incident
                {
                    Id = $"mem:{now.UtcTicks}", Kind = IncidentKind.GpuMemoryFull, Time = now, Severity = Severity.Warning,
                    Title = $"Graphics memory {mp:0}% full",
                    Summary = $"{s.MemoryUsedGB:0.0} of {s.MemoryTotalGB:0.0} GB in use. Games stutter and the driver is more likely to reset when it runs out.",
                    Detail = "Close browser windows and apps you are not using, or lower texture quality in the game.",
                    Source = "LaptopGuardian", Gpu = s,
                };
                if (Raise("memory", TimeSpan.FromMinutes(45), new Alert(inc.Kind, inc.Severity, inc.Title, inc.Summary, "graphics", inc.Id)))
                    Journal.Add(inc);
            }
        }
        else _memSince = null;
        // No Changed here: the window follows Gpu.Sampled for live readings, so a sample every
        // two seconds does not rebuild every list on the page.
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public static (int? Percent, bool? OnAc) PowerStatus()
    {
        if (!GetSystemPowerStatus(out var s)) return (null, null);
        int? pct = s.BatteryFlag == 128 || s.BatteryLifePercent == 255 ? null : s.BatteryLifePercent;
        bool? ac = s.ACLineStatus switch { 0 => false, 1 => true, _ => null };
        return (pct, ac);
    }

    void CheckBattery()
    {
        var (pct, ac) = PowerStatus();
        if (pct == null) return;
        if (ac != false) { _batteryAlertLevel = 101; return; }
        foreach (int level in new[] { 20, 10, 5 })
        {
            if (pct <= level && _batteryAlertLevel > level)
            {
                _batteryAlertLevel = level;
                if (Settings.NotifyBattery)
                    Raise("battery", TimeSpan.Zero, new Alert(IncidentKind.BatteryLow, level <= 5 ? Severity.Critical : Severity.Warning,
                        "Battery low", $"Battery at {pct}%. Plug in the charger." + (Power is { HibernateAvailable: false } && level <= 10 ? " Hibernation is off, so unsaved work is lost if it runs out." : ""), "power"));
                break;
            }
        }
    }

    /// <summary>Raises an alert unless one with the same key went out within <paramref name="cooldown"/>.</summary>
    bool Raise(string key, TimeSpan cooldown, Alert alert)
    {
        lock (_lastAlert)
        {
            if (_lastAlert.TryGetValue(key, out var last) && DateTimeOffset.Now - last < cooldown) return false;
            _lastAlert[key] = DateTimeOffset.Now;
        }
        AlertRaised?.Invoke(alert);
        return true;
    }

    /// <summary>For the "send a test notification" button.</summary>
    public void RaiseTest() => AlertRaised?.Invoke(new Alert(IncidentKind.GraphicsError, Severity.Info,
        "LaptopGuardian is watching", "This is how alerts will look. Nothing is wrong.", "overview"));

    // ---------------------------------------------------------------- actions

    public async Task<ActionResult> ApplyAsync(string actionId)
    {
        ActionResult result;
        switch (actionId)
        {
            case Advisor.ActionHibernateAfterSleep:
                result = await PowerChange(() => PowerConfig.SetHibernateAfter(Advisor.HibernateAfterSeconds), $"hibernate-after {Advisor.HibernateAfterSeconds}",
                    $"Done. Sleep now turns into hibernation after {Advisor.HibernateAfterSeconds / 60} minutes.");
                break;
            case Advisor.ActionLidHibernate:
                result = await PowerChange(() => PowerConfig.SetLidAndButtonToHibernate(), "lid-hibernate",
                    "Done. Closing the lid or pressing the power button now hibernates.");
                break;
            case Advisor.ActionEnableHibernate:
                result = await Elevation.RunAsync(_dataDir, "enable-hibernate");
                break;
            case Advisor.ActionClearReports:
                result = await Elevation.RunAsync(_dataDir, "clear-reports");
                if (result.Ok)
                {
                    State.ReportsClearedAt = DateTimeOffset.Now;
                    SaveState();
                    await Task.Run(() => { KernelReports = ReadKernelReports(); });
                }
                break;
            case Advisor.ActionSleepStudy:
                result = await Elevation.RunAsync(_dataDir, "sleep-study");
                break;
            case DriverChecks.ActionInstallWindowsUpdateDrivers:
                result = await Elevation.RunAsync(_dataDir, DriverChecks.ActionInstallWindowsUpdateDrivers);
                _ = CheckDriversAsync();
                break;
            default:
                return new ActionResult(false, "Unknown action.");
        }
        try { Power = PowerConfig.Read(); } catch { }
        Recompute();
        return result;
    }

    async Task<ActionResult> PowerChange(Func<uint> apply, string elevatedTask, string success)
    {
        uint r = await Task.Run(apply);
        if (r == 0) return new ActionResult(true, success);
        if (r == PowerConfig.ErrorAccessDenied)
        {
            var e = await Elevation.RunAsync(_dataDir, elevatedTask);
            return e.Ok ? new ActionResult(true, success) : e;
        }
        return new ActionResult(false, $"Windows refused the change (error {r}).");
    }

    public void Dispose()
    {
        try { if (_watcher != null) { _watcher.Enabled = false; _watcher.Dispose(); } } catch { }
        _batteryTimer?.Dispose();
        _rescanTimer?.Dispose();
        _driverTimer?.Dispose();
        _recorderTimer?.Dispose();
        try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
        Gpu.Dispose();
        Keyboard.Dispose();
    }
}

/// <summary>Runs one task as administrator in a second copy of the program, which Windows asks permission for.</summary>
public static class Elevation
{
    public const string Flag = "--elevated";

    public static async Task<ActionResult> RunAsync(string? dataDir, string task)
    {
        dataDir ??= Guardian.DefaultDataDir;
        string resultPath = Path.Combine(dataDir, "elevated-result.json");
        try { File.Delete(resultPath); } catch { }
        try
        {
            // The data folder goes along: on a standard account, the elevated copy runs as a different user with a different %APPDATA%.
            var psi = new ProcessStartInfo(Environment.ProcessPath!, $"{Flag} {task} --data \"{dataDir}\"") { UseShellExecute = true, Verb = "runas" };
            using var p = Process.Start(psi);
            if (p == null) return new ActionResult(false, "Could not start the helper.");
            await p.WaitForExitAsync();
            var r = File.Exists(resultPath) ? JsonSerializer.Deserialize<ActionResult>(File.ReadAllText(resultPath)) : null;
            return r ?? new ActionResult(p.ExitCode == 0, p.ExitCode == 0 ? "Done." : $"The helper failed (code {p.ExitCode}).");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ActionResult(false, "Cancelled. Windows needs your permission to make this change.");
        }
        catch (Exception ex)
        {
            return new ActionResult(false, ex.Message);
        }
    }

    /// <summary>Entry point of the elevated copy. Writes its result where the normal copy will read it.</summary>
    public static int Main(string[] args)
    {
        int dataArg = Array.IndexOf(args, "--data");
        string dataDir = dataArg >= 0 && dataArg + 1 < args.Length ? args[dataArg + 1] : Guardian.DefaultDataDir;
        Directory.CreateDirectory(dataDir);
        ActionResult result;
        try
        {
            string task = args.Length > 0 ? args[0] : "";
            result = task switch
            {
                "clear-reports" => ClearReports(),
                "enable-hibernate" => RunTool("powercfg.exe", "/hibernate on", "Hibernation is on."),
                "lid-hibernate" => Check(PowerConfig.SetLidAndButtonToHibernate(bestEffort: true), "Closing the lid or pressing the power button now hibernates."),
                "hibernate-after" => Check(PowerConfig.SetHibernateAfter(uint.Parse(args[1], CultureInfo.InvariantCulture), bestEffort: true), "Sleep now turns into hibernation after a while."),
                "sleep-study" => SleepStudy(dataDir),
                DriverChecks.ActionInstallWindowsUpdateDrivers => DriverChecks.InstallWindowsUpdateDrivers(),
                _ => new ActionResult(false, $"Unknown task '{task}'."),
            };
        }
        catch (Exception ex) { result = new ActionResult(false, ex.Message); }
        JsonFile.Save(Path.Combine(dataDir, "elevated-result.json"), result);
        return result.Ok ? 0 : 1;
    }

    static ActionResult Check(uint r, string ok) => r == 0 ? new ActionResult(true, ok) : new ActionResult(false, $"Windows refused the change (error {r}).");

    static ActionResult ClearReports()
    {
        var stuck = WerReports.Group(WerReports.Read(TimeSpan.FromDays(120))).Where(r => r.Stuck).ToList();
        if (stuck.Count == 0) return new ActionResult(true, "There were no stuck reports.");
        var (n, files, bytes, errors) = WerReports.Clear(stuck);
        if (n == 0 && errors.Count == 0) return new ActionResult(true, "Those reports were already gone. Windows will not retry them.");
        string msg = $"Cleared {Fmt.Plural(n, "report", "reports")} and {Fmt.Plural(files, "dump file", "dump files")} ({bytes / 1048576.0:0} MB). Windows will stop retrying them.";
        if (errors.Count > 0) msg += " Some could not be removed: " + string.Join("; ", errors.Take(3));
        return new ActionResult(n > 0, msg);
    }

    static ActionResult SleepStudy(string dataDir)
    {
        string path = Path.Combine(dataDir, "sleep-study.html");
        var r = RunTool("powercfg.exe", $"/sleepstudy /duration 7 /output \"{path}\"", "The sleep study is ready.");
        return r.Ok && File.Exists(path) ? r with { OpenPath = path } : r;
    }

    static ActionResult RunTool(string exe, string args, string ok)
    {
        var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(60000);
        return p.ExitCode == 0 ? new ActionResult(true, ok) : new ActionResult(false, output.Trim());
    }
}
