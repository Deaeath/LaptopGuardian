using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace LaptopGuardian.Core;

/// <summary>
/// Samples the main graphics card: temperature from the kernel graphics interface (the source Task Manager
/// uses, which does not wake a sleeping card), load and memory from Windows' GPU performance counters, and,
/// for NVIDIA cards that are already awake, power draw and throttle reasons from NVML.
/// </summary>
public sealed class GpuMonitor : IDisposable
{
    readonly object _gate = new();
    readonly List<GpuSnapshot> _history = new();
    readonly Dictionary<string, CounterSample> _prevEngine = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, string> _names = new();
    Timer? _timer;
    bool _disposed;

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan Keep { get; set; } = TimeSpan.FromMinutes(15);
    public GpuSnapshot? Latest { get; private set; }
    public event Action<GpuSnapshot>? Sampled;

    public IReadOnlyList<GpuSnapshot> History { get { lock (_gate) return _history.ToList(); } }

    public void Start()
    {
        _timer ??= new Timer(_ => Tick(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Sample sooner, e.g. when the window opens.</summary>
    public void Poke() => _timer?.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);

    /// <summary>Takes one sample now, on the calling thread, and records it.</summary>
    public void SampleOnce() => Tick();

    /// <summary>Fills the history with made-up samples, for the sample-data screenshots.</summary>
    internal void InjectSamples(IEnumerable<GpuSnapshot> samples)
    {
        lock (_gate) { _history.Clear(); _history.AddRange(samples); Latest = _history.LastOrDefault(); }
    }

    void Tick()
    {
        try
        {
            var s = SampleNow();
            if (s != null)
            {
                lock (_gate)
                {
                    _history.Add(s);
                    _history.RemoveAll(h => s.At - h.At > Keep);
                }
                Latest = s;
                Sampled?.Invoke(s);
            }
        }
        catch { }
        finally
        {
            if (!_disposed) try { _timer?.Change(Interval, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
        }
    }

    bool _countersWarm;

    public GpuSnapshot? SampleNow()
    {
        var adapter = Kmt.MainAdapter();
        if (adapter == null) return null;
        var a = adapter.Value;

        var snap = new GpuSnapshot { At = DateTimeOffset.Now, Name = a.Name, MemoryTotalGB = a.DedicatedBytes > 0 ? a.DedicatedBytes / 1073741824.0 : null };
        if (a.TemperatureDeciC > 0) snap.TemperatureC = a.TemperatureDeciC / 10.0;

        if (!_countersWarm)
        {
            // The first read of Windows' GPU counters takes several seconds (it loads the counter
            // provider). Publish temperature and power right away instead of a blank card.
            var quick = new GpuSnapshot { At = snap.At, Name = snap.Name, MemoryTotalGB = snap.MemoryTotalGB, TemperatureC = snap.TemperatureC };
            if (quick.TemperatureC != null && a.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) Nvml.Enrich(quick);
            Latest = quick;
            Sampled?.Invoke(quick);
            _countersWarm = true;
        }

        ReadCounters(a.LuidKey, snap);

        // Asleep: no temperature and nothing running on it. Leave it asleep (NVML would wake it).
        bool awake = snap.TemperatureC != null || (snap.UtilizationPct ?? 0) > 0.5;
        snap.Asleep = !awake && a.TemperatureDeciC == 0;
        if (awake && a.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) Nvml.Enrich(snap);
        // No limit is assumed when the driver does not state one: many AMD cards normally run hotter than
        // an NVIDIA laptop card's target, and warning them about it would be a false alarm.
        return snap;
    }

    void ReadCounters(string luidKey, GpuSnapshot snap)
    {
        try
        {
            var engines = new PerformanceCounterCategory("GPU Engine").ReadCategory();
            var util = engines["Utilization Percentage"];
            if (util != null)
            {
                var perEngine = new Dictionary<string, double>();
                var perPid = new Dictionary<int, double>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool havePrev = _prevEngine.Count > 0;
                foreach (InstanceData inst in util.Values)
                {
                    string name = inst.InstanceName;
                    seen.Add(name);
                    if (name.IndexOf(luidKey, StringComparison.OrdinalIgnoreCase) < 0) { _prevEngine[name] = inst.Sample; continue; }
                    double v = 0;
                    if (_prevEngine.TryGetValue(name, out var prev))
                        v = CounterSample.Calculate(prev, inst.Sample);
                    _prevEngine[name] = inst.Sample;
                    if (v <= 0 || double.IsNaN(v)) continue;

                    string eng = Between(name, "_eng_", "_engtype") ?? "0";
                    perEngine[eng] = perEngine.GetValueOrDefault(eng) + v;
                    if (int.TryParse(Between(name, "pid_", "_luid"), out int pid))
                        perPid[pid] = Math.Max(perPid.GetValueOrDefault(pid), v);
                }
                foreach (var gone in _prevEngine.Keys.Where(k => !seen.Contains(k)).ToList()) _prevEngine.Remove(gone);

                if (havePrev)
                {
                    snap.UtilizationPct = Math.Min(100, perEngine.Count == 0 ? 0 : perEngine.Values.Max());
                    snap.TopApps = perPid.Where(p => p.Value >= 1).OrderByDescending(p => p.Value).Take(4)
                        .Select(p => new GpuApp(ProcessName(p.Key), p.Key, Math.Min(100, p.Value))).ToList();
                }
            }
        }
        catch { }

        try
        {
            var mem = new PerformanceCounterCategory("GPU Adapter Memory").ReadCategory();
            var dedicated = mem["Dedicated Usage"];
            if (dedicated != null)
            {
                double bytes = 0; bool found = false;
                foreach (InstanceData inst in dedicated.Values)
                    if (inst.InstanceName.IndexOf(luidKey, StringComparison.OrdinalIgnoreCase) >= 0) { bytes += inst.RawValue; found = true; }
                if (found) snap.MemoryUsedGB = bytes / 1073741824.0;
            }
        }
        catch { }
    }

    string ProcessName(int pid)
    {
        if (_names.TryGetValue(pid, out var n)) return n;
        try { using var p = Process.GetProcessById(pid); n = p.ProcessName + ".exe"; }
        catch { n = $"process {pid}"; }
        if (_names.Count > 500) _names.Clear();
        return _names[pid] = n;
    }

    static string? Between(string s, string a, string b)
    {
        int i = s.IndexOf(a, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        i += a.Length;
        int j = s.IndexOf(b, i, StringComparison.OrdinalIgnoreCase);
        return j < 0 ? null : s[i..j];
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        Nvml.Shutdown();
    }
}

/// <summary>The kernel-mode graphics thunks (gdi32 D3DKMT*): adapter list, memory size and Task Manager's perf data.</summary>
internal static class Kmt
{
    public readonly record struct Adapter(string Name, string LuidKey, ulong DedicatedBytes, uint TemperatureDeciC, uint FanRpm, uint PowerTenthsPct);

    const int KMTQAITYPE_GETSEGMENTSIZE = 3;
    const int KMTQAITYPE_ADAPTERREGISTRYINFO = 8;
    const int KMTQAITYPE_ADAPTERPERFDATA = 62;

    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct ADAPTERINFO { public uint hAdapter; public LUID Luid; public uint NumOfSources; public int bPrecisePresentRegionsPreferred; }
    [StructLayout(LayoutKind.Sequential)] struct ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }
    [StructLayout(LayoutKind.Sequential)] struct QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pData; public uint DataSize; }
    [StructLayout(LayoutKind.Sequential)] struct CLOSEADAPTER { public uint hAdapter; }
    [StructLayout(LayoutKind.Sequential)] struct SEGMENTSIZEINFO { public ulong DedicatedVideoMemorySize; public ulong DedicatedSystemMemorySize; public ulong SharedSystemMemorySize; }
    [StructLayout(LayoutKind.Sequential)]
    struct ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOC, MemoryBandwidth, PCIEBandwidth;
        public uint FanRPM, Power, Temperature;
        public byte PowerStateOverride;
    }

    [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref ENUMADAPTERS2 args);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref QUERYADAPTERINFO args);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CLOSEADAPTER args);

    public static List<Adapter> All()
    {
        var result = new List<Adapter>();
        var e = new ENUMADAPTERS2();
        if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return result;
        int size = Marshal.SizeOf<ADAPTERINFO>();
        e.pAdapters = Marshal.AllocHGlobal(size * (int)e.NumAdapters);
        try
        {
            if (D3DKMTEnumAdapters2(ref e) != 0) return result;
            for (int i = 0; i < e.NumAdapters; i++)
            {
                var info = Marshal.PtrToStructure<ADAPTERINFO>(e.pAdapters + i * size);
                try
                {
                    string name = Query<RegistryInfo>(info.hAdapter, KMTQAITYPE_ADAPTERREGISTRYINFO, out var reg) ? reg.AdapterString : "";
                    ulong dedicated = Query<SEGMENTSIZEINFO>(info.hAdapter, KMTQAITYPE_GETSEGMENTSIZE, out var seg) ? seg.DedicatedVideoMemorySize : 0;
                    uint temp = 0, fan = 0, power = 0;
                    var perf = new ADAPTER_PERFDATA();
                    if (QueryInOut(info.hAdapter, KMTQAITYPE_ADAPTERPERFDATA, ref perf)) { temp = perf.Temperature; fan = perf.FanRPM; power = perf.Power; }
                    // Instance names in the GPU performance counters: luid_0x{High:X8}_0x{Low:X8}
                    string key = $"luid_0x{(uint)info.Luid.High:X8}_0x{info.Luid.Low:X8}";
                    result.Add(new Adapter(name.Trim(), key, dedicated, temp, fan, power));
                }
                finally
                {
                    var c = new CLOSEADAPTER { hAdapter = info.hAdapter };
                    D3DKMTCloseAdapter(ref c);
                }
            }
        }
        finally { Marshal.FreeHGlobal(e.pAdapters); }
        return result;
    }

    /// <summary>The card with the most dedicated memory: the discrete GPU on a gaming laptop.</summary>
    public static Adapter? MainAdapter()
    {
        try
        {
            var all = All().Where(a => !a.Name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)).ToList();
            if (all.Count == 0) return null;
            return all.OrderByDescending(a => a.DedicatedBytes).First();
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RegistryInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string AdapterString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string BiosString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DacType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ChipType;
    }

    static bool Query<T>(uint adapter, int type, out T value) where T : struct
    {
        value = default;
        int size = Marshal.SizeOf<T>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            unsafe { new Span<byte>((void*)buf, size).Clear(); }
            var q = new QUERYADAPTERINFO { hAdapter = adapter, Type = type, pData = buf, DataSize = (uint)size };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return false;
            value = Marshal.PtrToStructure<T>(buf);
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static bool QueryInOut<T>(uint adapter, int type, ref T inOut) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(inOut, buf, false);
            var q = new QUERYADAPTERINFO { hAdapter = adapter, Type = type, pData = buf, DataSize = (uint)size };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return false;
            inOut = Marshal.PtrToStructure<T>(buf);
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}

/// <summary>NVIDIA Management Library, loaded at run time from the driver. Optional: everything works without it.</summary>
internal static unsafe class Nvml
{
    static readonly object Gate = new();
    static IntPtr _lib;
    static IntPtr _device;
    static bool _tried, _ok;
    static uint? _limit;

    static delegate* unmanaged<int> _init;
    static delegate* unmanaged<int> _shutdown;
    static delegate* unmanaged<uint, IntPtr*, int> _handle;
    static delegate* unmanaged<IntPtr, int, uint*, int> _temp;
    static delegate* unmanaged<IntPtr, int, uint*, int> _threshold;
    static delegate* unmanaged<IntPtr, uint*, int> _power;
    static delegate* unmanaged<IntPtr, uint*, int> _powerLimit;
    static delegate* unmanaged<IntPtr, ulong*, int> _reasons;

    static bool Ready()
    {
        if (_tried) return _ok;
        _tried = true;
        string[] candidates =
        {
            "nvml.dll",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvml.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll"),
        };
        foreach (var c in candidates)
            if (NativeLibrary.TryLoad(c, out _lib)) break;
        if (_lib == IntPtr.Zero) return false;

        IntPtr F(string name) => NativeLibrary.TryGetExport(_lib, name, out var p) ? p : IntPtr.Zero;
        _init = (delegate* unmanaged<int>)F("nvmlInit_v2");
        _shutdown = (delegate* unmanaged<int>)F("nvmlShutdown");
        _handle = (delegate* unmanaged<uint, IntPtr*, int>)F("nvmlDeviceGetHandleByIndex_v2");
        _temp = (delegate* unmanaged<IntPtr, int, uint*, int>)F("nvmlDeviceGetTemperature");
        _threshold = (delegate* unmanaged<IntPtr, int, uint*, int>)F("nvmlDeviceGetTemperatureThreshold");
        _power = (delegate* unmanaged<IntPtr, uint*, int>)F("nvmlDeviceGetPowerUsage");
        _powerLimit = (delegate* unmanaged<IntPtr, uint*, int>)F("nvmlDeviceGetEnforcedPowerLimit");
        var reasons = F("nvmlDeviceGetCurrentClocksEventReasons");
        if (reasons == IntPtr.Zero) reasons = F("nvmlDeviceGetCurrentClocksThrottleReasons");
        _reasons = (delegate* unmanaged<IntPtr, ulong*, int>)reasons;

        if (_init == null || _handle == null || _init() != 0) return false;
        IntPtr dev;
        if (_handle(0, &dev) != 0) return false;
        _device = dev;
        _ok = true;
        return true;
    }

    public static void Enrich(GpuSnapshot s)
    {
        lock (Gate)
        {
            try
            {
                if (!Ready()) return;
                uint v;
                if (_temp != null && _temp(_device, 0, &v) == 0) s.TemperatureC = v;
                if (_limit == null && _threshold != null)
                {
                    // ACOUSTIC_CURR (5) is the "GPU Target Temperature" nvidia-smi shows: on laptops the card
                    // holds itself at this temperature by cutting clocks. GPU_MAX and SLOWDOWN are hardware
                    // ceilings (around 100-105°C) and say nothing useful about everyday heat.
                    _limit = _threshold(_device, 5, &v) == 0 && v is >= 60 and <= 100 ? v : 0;
                }
                if (_limit is > 0) s.TemperatureLimitC = _limit;
                if (_power != null && _power(_device, &v) == 0) s.PowerW = v / 1000.0;
                if (_powerLimit != null && _powerLimit(_device, &v) == 0) s.PowerLimitW = v / 1000.0;
                ulong r;
                if (_reasons != null && _reasons(_device, &r) == 0) s.Throttle = Describe(r);
            }
            catch { }
        }
    }

    static string? Describe(ulong r)
    {
        var why = new List<string>();
        if ((r & (0x20 | 0x40)) != 0) why.Add("heat");
        if ((r & 0x04) != 0) why.Add("its power limit");
        if ((r & (0x08 | 0x80)) != 0) why.Add("a hardware slowdown");
        return why.Count == 0 ? null : string.Join(" and ", why);
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            try { if (_ok && _shutdown != null) _shutdown(); } catch { }
            _ok = false;
        }
    }
}
