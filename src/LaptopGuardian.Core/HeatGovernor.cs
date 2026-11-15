using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LaptopGuardian.Core;

/// <summary>
/// When to cap and when to let go. Pure, so it is tested directly.
/// On 15 November 2026 a laptop with failing thermal paste held 86-87°C at full load for 35 seconds, then jumped
/// to 98°C within two seconds; a minute earlier the same jump had switched it off. So the governor steps in
/// on temperature or on sustained full load, whichever comes first, and keeps the cap long enough to matter.
/// </summary>
public sealed class GovernorPolicy
{
    public double CapAtC { get; set; } = 88;
    public double ReleaseBelowC { get; set; } = 80;
    public double BusyTriggerPct { get; set; } = 90;
    public double ReleaseBusyBelowPct { get; set; } = 70;
    public TimeSpan BusyFor { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan CoolFor { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MinCap { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>While capped and still hot, cap one more app at most this often.</summary>
    public TimeSpan MoreEvery { get; set; } = TimeSpan.FromSeconds(5);

    DateTimeOffset? _busySince, _coolSince, _cappedAt, _lastCap;

    public enum Step { None, Cap, Release }
    public bool Capped => _cappedAt != null;

    public Step Next(DateTimeOffset now, double? tempC, double? busyPct)
    {
        if (busyPct >= BusyTriggerPct) _busySince ??= now; else _busySince = null;
        bool hot = tempC >= CapAtC;

        if (_cappedAt == null)
        {
            if (hot || (_busySince is DateTimeOffset b && now - b >= BusyFor))
            {
                _cappedAt = _lastCap = now;
                _coolSince = null;
                return Step.Cap;
            }
            return Step.None;
        }

        // With no temperature reading (a sensor that comes and goes), load alone decides.
        bool cool = (tempC is not double t || t < ReleaseBelowC) && (busyPct ?? 0) < ReleaseBusyBelowPct;
        if (cool) _coolSince ??= now; else _coolSince = null;
        if (_coolSince is DateTimeOffset c && now - c >= CoolFor && now - _cappedAt >= MinCap)
        {
            _cappedAt = _coolSince = _busySince = _lastCap = null;
            return Step.Release;
        }
        if (hot && now - _lastCap >= MoreEvery)
        {
            _lastCap = now;
            return Step.Cap;
        }
        return Step.None;
    }

    public void Reset() => _busySince = _coolSince = _cappedAt = _lastCap = null;
}

public sealed record GovernorAction(bool Engaged, DateTimeOffset At, double? TempC, IReadOnlyList<string> Apps, DateTimeOffset? Since);

/// <summary>An app being held back now: by the person's own "always" cap, or by the governor because it got hot.</summary>
public sealed record HeldApp(string App, int Percent, bool Always);

/// <summary>A process's share of the whole processor over a short sample.</summary>
public sealed record ProcessUse(int Pid, string Name, double Share);

/// <summary>
/// Keeps an overheating laptop from switching itself off. When it gets too hot, the busiest apps are held to a share
/// of the processor (a Windows job object's hard CPU cap, or, where an app cannot be put in a job, fewer cores) and
/// released once it has cooled. The person can also give apps their own caps: always on, only when hot, or never.
/// Windows' own clock limits are ignored by some processors; these are not.
/// </summary>
public sealed class HeatGovernor : IDisposable
{
    public GovernorPolicy Policy { get; } = new();
    /// <summary>The most an app gets when the governor caps it and it has no cap of its own.</summary>
    public int CapPercent { get; set; } = 40;
    /// <summary>
    /// 15 November 2026: a flat 40% cap on 3DXChat (using 25%), Chrome (12%) and Discord (4%) held nothing back, and the
    /// laptop sat at 97-110°C and cut out. So a cap starts at half of what the app is using, and tightens while it stays hot.
    /// </summary>
    public int MaxApps { get; set; } = 5;
    public const int MinPercent = 5;
    public const double TightenBy = 0.75;
    public bool Enabled { get; private set; }

    /// <summary>Raised (on a background thread) when the governor caps apps or lets them go.</summary>
    public event Action<GovernorAction>? Acted;

    /// <summary>Never capped: Windows itself, the screen, sound, and this program.</summary>
    public static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Secure System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "LsaIso",
        "dwm", "audiodg", "fontdrvhost", "MemCompression", "explorer", "LaptopGuardian",
    };

    readonly object _gate = new();
    readonly Dictionary<int, CappedApp> _capped = new();   // by the governor, while hot
    readonly Dictionary<int, CappedApp> _always = new();   // by the person's "always" caps
    IReadOnlyList<AppCapRule> _rules = Array.Empty<AppCapRule>();
    DateTimeOffset? _engagedAt;
    Timer? _timer;
    PerformanceCounter? _busy;
    int _ticking, _tick;

    sealed record CappedApp(int Pid, string Name, int Percent, IntPtr Job, IntPtr? OldAffinity);

    public IReadOnlyList<string> CappedApps { get { lock (_gate) return _capped.Values.Select(c => c.Name).Distinct().ToList(); } }
    public DateTimeOffset? EngagedAt => _engagedAt;
    public double? LastTempC { get; private set; }

    /// <summary>Every app held back right now, one line per app.</summary>
    public IReadOnlyList<HeldApp> Holding
    {
        get
        {
            lock (_gate)
                return _always.Values.Select(c => new HeldApp(c.Name, c.Percent, true))
                    .Concat(_capped.Values.Select(c => new HeldApp(c.Name, c.Percent, false)))
                    .GroupBy(h => (h.App.ToLowerInvariant(), h.Always)).Select(g => g.First()).ToList();
        }
    }

    public void SetEnabled(bool on)
    {
        lock (_gate)
        {
            Enabled = on;
            if (!on)
            {
                ReleaseAll(null);
                Policy.Reset();
            }
            UpdateTimer();
        }
    }

    /// <summary>The person's per-app caps. "Always" caps go on (or come off) within a few seconds.</summary>
    public void SetRules(IEnumerable<AppCapRule> rules)
    {
        lock (_gate)
        {
            _rules = rules.Where(r => !string.IsNullOrWhiteSpace(r.App)).Select(r => new AppCapRule
            {
                App = AppCapRule.Normalize(r.App), CapPercent = r.CapPercent is int p ? Math.Clamp(p, 1, 95) : null, Always = r.Always, Never = r.Never,
            }).ToList();
            UpdateTimer();
        }
        ThreadPool.QueueUserWorkItem(_ => { try { ApplyAlways(); } catch (Exception ex) { Debug.WriteLine(ex); } });
    }

    void UpdateTimer()
    {
        bool needed = Enabled || _rules.Any(r => r.Always && !r.Never) || _always.Count > 0;
        if (needed && _timer == null) _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        if (!needed && _timer != null) { _timer.Dispose(); _timer = null; }
    }

    void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            // New windows of an app with an "always" cap get it within five seconds.
            if (++_tick % 5 == 0) ApplyAlways();
            if (!Enabled) return;
            double? busy = null;
            try
            {
                _busy ??= new PerformanceCounter("Processor Information", "% Processor Time", "_Total", readOnly: true);
                busy = _busy.NextValue();
            }
            catch { }
            var t = FlightRecorder.ReadThermalZone();
            LastTempC = t;
            var now = DateTimeOffset.Now;
            switch (Policy.Next(now, t, busy))
            {
                case GovernorPolicy.Step.Cap: CapBusiest(now, t); break;
                case GovernorPolicy.Step.Release: ReleaseAll(t); break;
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    /// <summary>
    /// Which apps to cap now, in order: apps the person gave a cap of their own (if they are doing anything),
    /// then the busiest of the rest. Never Windows itself, never an app marked "never", never one already capped.
    /// Each gets half of what it is using now, never more than its own cap (or the default), never under 5%.
    /// </summary>
    public static List<(int Pid, string Name, int Percent)> PickTargets(IEnumerable<ProcessUse> busiest, IReadOnlyList<AppCapRule> rules,
        Func<int, bool> alreadyCapped, int limit, int selfPid, int defaultPercent, double minShare = 3)
    {
        var byName = rules.GroupBy(r => r.App, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var open = busiest
            .Where(b => b.Pid != selfPid && b.Pid > 4 && !Protected.Contains(b.Name) && !alreadyCapped(b.Pid))
            .Where(b => !(byName.TryGetValue(b.Name, out var r) && r.Never))
            .OrderByDescending(b => b.Share)
            .ToList();
        var mine = open.Where(b => b.Share >= 1 && byName.TryGetValue(b.Name, out var r) && r.CapPercent != null);
        var others = open.Where(b => b.Share >= minShare);
        return mine.Concat(others)
            .DistinctBy(b => b.Pid)
            .Take(Math.Max(0, limit))
            .Select(b => (b.Pid, b.Name, Math.Max(MinPercent, Math.Min(
                byName.TryGetValue(b.Name, out var r) && r.CapPercent is int p ? p : defaultPercent,
                (int)Math.Round(b.Share / 2)))))
            .ToList();
    }

    /// <summary>Each process's share of the whole processor over half a second, busiest first.</summary>
    public static List<ProcessUse> Sample()
    {
        Dictionary<int, (string, TimeSpan)> Snap()
        {
            var d = new Dictionary<int, (string, TimeSpan)>();
            foreach (var p in Process.GetProcesses())
                using (p)
                    try { d[p.Id] = (p.ProcessName, p.TotalProcessorTime); } catch { }
            return d;
        }
        var a = Snap();
        var sw = Stopwatch.StartNew();
        Thread.Sleep(500);
        var b = Snap();
        double ms = sw.Elapsed.TotalMilliseconds * Environment.ProcessorCount;
        return b.Where(kv => a.TryGetValue(kv.Key, out var old) && old.Item1 == kv.Value.Item1)
            .Select(kv => new ProcessUse(kv.Key, kv.Value.Item1, 100 * (kv.Value.Item2 - a[kv.Key].Item2).TotalMilliseconds / ms))
            .OrderByDescending(x => x.Share)
            .ToList();
    }

    void CapBusiest(DateTimeOffset now, double? t)
    {
        // Still hot: tighten what is already held back, by a quarter each time, down to 5%.
        lock (_gate)
            foreach (var pid in _capped.Keys.ToList())
            {
                var c = _capped[pid];
                int next = Math.Max(MinPercent, (int)Math.Floor(c.Percent * TightenBy));
                if (next < c.Percent && Retarget(c, next)) _capped[pid] = c with { Percent = next };
            }

        int limit;
        IReadOnlyList<AppCapRule> rules;
        lock (_gate)
        {
            limit = Math.Min(MaxApps - _capped.Count, _capped.Count == 0 ? 2 : 1);
            rules = _rules;
        }
        if (limit <= 0) return;
        var fresh = new List<string>();
        bool Capped(int pid) { lock (_gate) return _capped.ContainsKey(pid) || _always.ContainsKey(pid); }
        foreach (var (pid, name, percent) in PickTargets(Sample(), rules, Capped, limit, Environment.ProcessId, CapPercent))
        {
            var c = TryCap(pid, name, percent);
            if (c == null) continue;
            lock (_gate) _capped[pid] = c;
            fresh.Add(c.Name);
        }
        if (fresh.Count == 0) return;
        _engagedAt ??= now;
        Acted?.Invoke(new GovernorAction(true, now, t, fresh, _engagedAt));
    }

    /// <summary>
    /// The cores an app with an "always" cap may use: enough for its share of the whole processor (at least one),
    /// taken from the top so they stay clear of where Windows starts most work. All of the app's processes share
    /// them, so the cap holds for the app as a whole: Chrome runs as twenty processes, each using a little.
    /// </summary>
    public static long AlwaysMask(int processors, int percent)
    {
        processors = Math.Clamp(processors, 1, 64);
        int cores = Math.Clamp((int)Math.Round(processors * percent / 100.0), 1, processors);
        long ones = cores >= 64 ? -1L : (1L << cores) - 1;
        return processors >= 64 ? ones << (64 - cores) : ones << (processors - cores);
    }

    /// <summary>Puts every running process of an "always" app under its cap, and takes caps off apps no longer listed.</summary>
    void ApplyAlways()
    {
        List<AppCapRule> always;
        lock (_gate) always = _rules.Where(r => r.Always && !r.Never).ToList();

        // Caps that no longer apply (rule removed, changed, or the app has exited).
        List<CappedApp> stale;
        lock (_gate)
        {
            stale = _always.Values.Where(c =>
            {
                var r = always.FirstOrDefault(x => string.Equals(x.App + ".exe", c.Name, StringComparison.OrdinalIgnoreCase));
                return r == null || (r.CapPercent ?? CapPercent) != c.Percent || !Alive(c.Pid, c.Name);
            }).ToList();
            foreach (var c in stale) _always.Remove(c.Pid);
        }
        foreach (var c in stale) Release(c);

        foreach (var r in always)
        {
            int percent = r.CapPercent ?? CapPercent;
            long mask = AlwaysMask(Environment.ProcessorCount, percent);
            Process[] procs;
            try { procs = Process.GetProcessesByName(r.App); } catch { continue; }
            foreach (var p in procs)
            {
                using (p)
                {
                    if (p.Id == Environment.ProcessId || Protected.Contains(r.App)) continue;
                    lock (_gate) if (_always.ContainsKey(p.Id)) continue;
                    try
                    {
                        var old = p.ProcessorAffinity;
                        // A child started by a capped process already has the mask: remember the full set to put back.
                        long full = Environment.ProcessorCount >= 64 ? -1L : (1L << Environment.ProcessorCount) - 1;
                        var restore = (long)old == mask ? (IntPtr)full : old;
                        p.ProcessorAffinity = (IntPtr)mask;
                        lock (_gate) _always[p.Id] = new CappedApp(p.Id, p.ProcessName + ".exe", percent, IntPtr.Zero, restore);
                    }
                    catch { } // a protected or elevated process: left alone
                }
            }
        }
        lock (_gate) UpdateTimer();
    }

    static bool Alive(int pid, string name)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && string.Equals(p.ProcessName + ".exe", name, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    CappedApp? TryCap(int pid, string name, int percent)
    {
        string label = name + ".exe";
        IntPtr h = OpenProcess(ProcessSetQuota | ProcessTerminate | ProcessQueryLimited, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job != IntPtr.Zero)
                {
                    var info = new CpuRate { ControlFlags = RateEnable | RateHardCap, Rate = (uint)Math.Clamp(percent * 100, 100, 10000) };
                    if (SetInformationJobObject(job, JobObjectCpuRateControlInformation, ref info, (uint)Marshal.SizeOf<CpuRate>()) && AssignProcessToJobObject(job, h))
                        return new CappedApp(pid, label, percent, job, null);
                    CloseHandle(job);
                }
            }
            finally { CloseHandle(h); }
        }
        // Some apps are already in a job that cannot nest (or deny quota rights): give them fewer cores instead.
        try
        {
            using var p = Process.GetProcessById(pid);
            var old = p.ProcessorAffinity;
            int cores = Math.Max(1, Environment.ProcessorCount * percent / 100);
            long mask = cores >= 63 ? long.MaxValue : (1L << cores) - 1;
            p.ProcessorAffinity = (IntPtr)(mask & (long)old);
            return new CappedApp(pid, label, percent, IntPtr.Zero, old);
        }
        catch { return null; }
    }

    /// <summary>Changes an app's cap in place.</summary>
    static bool Retarget(CappedApp c, int percent)
    {
        try
        {
            if (c.Job != IntPtr.Zero)
            {
                var info = new CpuRate { ControlFlags = RateEnable | RateHardCap, Rate = (uint)Math.Clamp(percent * 100, 100, 10000) };
                return SetInformationJobObject(c.Job, JobObjectCpuRateControlInformation, ref info, (uint)Marshal.SizeOf<CpuRate>());
            }
            if (c.OldAffinity is IntPtr old)
            {
                using var p = Process.GetProcessById(c.Pid);
                int cores = Math.Max(1, Environment.ProcessorCount * percent / 100);
                long mask = cores >= 63 ? long.MaxValue : (1L << cores) - 1;
                p.ProcessorAffinity = (IntPtr)(mask & (long)old);
                return true;
            }
        }
        catch { }
        return false;
    }

    static void Release(CappedApp c)
    {
        try
        {
            if (c.Job != IntPtr.Zero)
            {
                // A process cannot leave a job, so the cap is switched off, then the handle closed.
                var off = new CpuRate();
                SetInformationJobObject(c.Job, JobObjectCpuRateControlInformation, ref off, (uint)Marshal.SizeOf<CpuRate>());
                CloseHandle(c.Job);
            }
            else if (c.OldAffinity is IntPtr old)
            {
                using var p = Process.GetProcessById(c.Pid);
                p.ProcessorAffinity = old;
            }
        }
        catch { } // the app has exited
    }

    /// <summary>Lets go of everything the governor capped for heat (the person's "always" caps stay).</summary>
    void ReleaseAll(double? t)
    {
        List<CappedApp> all;
        lock (_gate)
        {
            all = _capped.Values.ToList();
            _capped.Clear();
        }
        foreach (var c in all) Release(c);
        var since = _engagedAt;
        _engagedAt = null;
        if (all.Count > 0) Acted?.Invoke(new GovernorAction(false, DateTimeOffset.Now, t, all.Select(c => c.Name).Distinct().ToList(), since));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            Enabled = false;
        }
        ReleaseAll(null);
        List<CappedApp> always;
        lock (_gate) { always = _always.Values.ToList(); _always.Clear(); }
        foreach (var c in always) Release(c);
        _busy?.Dispose();
    }

    const uint ProcessTerminate = 0x0001, ProcessSetQuota = 0x0100, ProcessQueryLimited = 0x1000;
    const int JobObjectCpuRateControlInformation = 15;
    const uint RateEnable = 0x1, RateHardCap = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    struct CpuRate { public uint ControlFlags; public uint Rate; }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref CpuRate info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
