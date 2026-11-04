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
    public bool? OnAc { get; init; }
    public int? BatteryPct { get; init; }
    public string? TopApps { get; init; }

    public string Describe()
    {
        var p = new List<string>();
        if (ZoneC is double z) p.Add($"laptop's thermal sensor {z:0}°C");
        if (GpuC is double g) p.Add($"graphics card {g:0}°C");
        if (GpuBusyPct is double b) p.Add($"{b:0}% busy");
        if (GpuMemoryPct is double m) p.Add($"graphics memory {m:0}% full");
        if (GpuW is double w) p.Add($"drawing {w:0} W");
        if (OnAc is bool ac) p.Add(ac ? "plugged in" : $"on battery{(BatteryPct is int pct ? $" at {pct}%" : "")}");
        string s = string.Join(", ", p);
        if (Throttle != null) s += $" (held back by {Throttle})";
        if (!string.IsNullOrEmpty(TopApps)) s += $". Busiest: {TopApps}";
        return s.Length == 0 ? "No readings." : char.ToUpperInvariant(s[0]) + s[1..] + ".";
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
