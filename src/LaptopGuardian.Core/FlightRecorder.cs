using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LaptopGuardian.Core;

/// <summary>One line of the flight recorder: what the laptop was doing at a moment.</summary>
public sealed record FlightSample
{
    public DateTimeOffset At { get; init; }
    /// <summary>"awake", or "suspending" when Windows told us it was going to sleep.</summary>
    public string State { get; init; } = "awake";
    /// <summary>The firmware's thermal zone, usually near the processor.</summary>
    public double? ZoneC { get; init; }
    public double? GpuC { get; init; }
    public double? GpuLimitC { get; init; }
    public double? GpuBusyPct { get; init; }
    public double? GpuMemoryPct { get; init; }
    public double? GpuW { get; init; }
    public string? Throttle { get; init; }
    public double? GpuFanRpm { get; init; }
    /// <summary>The whole processor, all cores.</summary>
    public double? CpuBusyPct { get; init; }
    /// <summary>Its clock speed as a share of its rated maximum: it drops when the processor slows itself for heat or power.</summary>
    public double? CpuSpeedPct { get; init; }
    public string? TopCpuApps { get; init; }
    public bool? OnAc { get; init; }
    public int? BatteryPct { get; init; }
    public string? TopApps { get; init; }

    public string Describe()
    {
        var p = new List<string>();
        if (ZoneC is double z) p.Add($"laptop's thermal sensor {z:0}°C");
        if (CpuBusyPct is double c) p.Add($"processor {c:0}% busy{(CpuSpeedPct is double sp ? $" at {sp:0}% of its top speed" : "")}");
        if (GpuC is double g) p.Add($"graphics card {g:0}°C");
        if (GpuBusyPct is double b) p.Add($"{b:0}% busy");
        if (GpuMemoryPct is double m) p.Add($"graphics memory {m:0}% full");
        if (GpuW is double w) p.Add($"drawing {w:0} W");
        if (GpuFanRpm is double f) p.Add($"its fan at {f:0} rpm");
        if (OnAc is bool ac) p.Add(ac ? "plugged in" : $"on battery{(BatteryPct is int pct ? $" at {pct}%" : "")}");
        string s = string.Join(", ", p);
        if (Throttle != null) s += $" (held back by {Throttle})";
        if (!string.IsNullOrEmpty(TopCpuApps)) s += $". Busiest on the processor: {TopCpuApps}";
        if (!string.IsNullOrEmpty(TopApps)) s += $". Busiest on the graphics card: {TopApps}";
        return s.Length == 0 ? "No readings." : char.ToUpperInvariant(s[0]) + s[1..] + ".";
    }
}

/// <summary>Processor load, clock speed and the busiest processes, measured between one call and the next.</summary>
public sealed class CpuSampler : IDisposable
{
    PerformanceCounter? _busy, _speed;
    readonly Dictionary<int, (string Name, TimeSpan Cpu)> _last = new();
    DateTime _lastAt;
    bool _primed;

    public (double? Busy, double? Speed, string? Top) Sample()
    {
        double? busy = null, speed = null;
        try
        {
            _busy ??= new PerformanceCounter("Processor Information", "% Processor Time", "_Total", readOnly: true);
            _speed ??= new PerformanceCounter("Processor Information", "% of Maximum Frequency", "_Total", readOnly: true);
            float b = _busy.NextValue(), s = _speed.NextValue();
            // Rate counters read 0 the first time; they mean something from the second call on.
            if (_primed) { busy = Math.Round(Math.Clamp(b, 0, 100)); speed = Math.Round(Math.Max(0, s)); }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }

        var now = DateTime.UtcNow;
        double elapsed = (now - _lastAt).TotalMilliseconds;
        _lastAt = now;
        var use = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<int, (string, TimeSpan)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id is 0 or 4) continue; // Idle and System
                    var t = p.TotalProcessorTime;
                    seen[p.Id] = (p.ProcessName, t);
                    if (_primed && _last.TryGetValue(p.Id, out var prev) && prev.Name == p.ProcessName && elapsed is > 0 and < 120_000)
                        use[p.ProcessName] = use.GetValueOrDefault(p.ProcessName) + (t - prev.Cpu).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100;
                }
                catch { } // protected processes cannot be read
            }
        }
        _last.Clear();
        foreach (var kv in seen) _last[kv.Key] = kv.Value;
        _primed = true;

        var top = use.Where(u => u.Value >= 1).OrderByDescending(u => u.Value).Take(3).Select(u => $"{u.Key}.exe {u.Value:0}%").ToList();
        return (busy, speed, top.Count > 0 ? string.Join(", ", top) : null);
    }

    public void Dispose()
    {
        _busy?.Dispose();
        _speed?.Dispose();
    }
}

/// <summary>
/// A flight recorder. Windows often records nothing about a sudden reset, and what it does record can be
/// many minutes old, so this writes a short line every few seconds straight through to the disk. After a
/// crash, the last line says when the laptop stopped and what it was doing at the time.
/// It stays small: two files of at most 1 MB each, about 40 hours of history.
/// </summary>
public sealed class FlightRecorder
{
    public const long MaxFileBytes = 1024 * 1024;
    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    readonly string? _path;
    readonly object _gate = new();

    /// <param name="path">Null records nothing (used by --diagnose, which must not write beside a running copy).</param>
    public FlightRecorder(string? path) => _path = path;

    public void Write(FlightSample sample)
    {
        if (_path == null) return;
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > MaxFileBytes) File.Move(_path, _path + ".1", overwrite: true);
                byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(sample, Json) + "\n");
                // WriteThrough: a sudden reset loses whatever is still in the disk cache, and this is the line that matters.
                using var f = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
                f.Write(line);
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    /// <summary>The last sample written before <paramref name="moment"/>, within the given window.</summary>
    public FlightSample? LastBefore(DateTimeOffset moment, TimeSpan within)
    {
        if (_path == null) return null;
        FlightSample? best = null;
        foreach (var file in new[] { _path, _path + ".1" })
        {
            try
            {
                if (!File.Exists(file)) continue;
                using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(f, Encoding.UTF8);
                for (string? line = r.ReadLine(); line != null; line = r.ReadLine())
                {
                    var s = Parse(line);
                    if (s == null || s.At >= moment || moment - s.At > within) continue;
                    if (best == null || s.At > best.At) best = s;
                }
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }
        return best;
    }

    /// <summary>A sudden reset can leave half a line behind; that line is simply skipped.</summary>
    internal static FlightSample? Parse(string line)
    {
        try { return JsonSerializer.Deserialize<FlightSample>(line, Json); }
        catch { return null; }
    }

    /// <summary>The firmware's thermal zone in °C, if Windows exposes one. No administrator rights needed.</summary>
    public static double? ReadThermalZone()
    {
        try
        {
            var data = new PerformanceCounterCategory("Thermal Zone Information").ReadCategory();
            var temps = data["Temperature"];
            if (temps == null) return null;
            double max = 0;
            foreach (InstanceData i in temps.Values) max = Math.Max(max, i.RawValue);
            // Reported in kelvin. Anything outside a believable range is a firmware placeholder.
            double c = max - 273.15;
            return c is > 5 and < 130 ? Math.Round(c, 0) : null;
        }
        catch { return null; }
    }
}
