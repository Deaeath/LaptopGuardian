using System.Text.Json.Serialization;

namespace LaptopGuardian.Core;

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity { Info, Warning, Critical }

[JsonConverter(typeof(JsonStringEnumConverter<IncidentKind>))]
public enum IncidentKind { GraphicsError, UnexpectedShutdown, HardwareError, GpuHot, GpuMemoryFull, BatteryLow }

[JsonConverter(typeof(JsonStringEnumConverter<CrashCause>))]
public enum CrashCause
{
    /// <summary>Windows stopped with a stop code (a blue screen).</summary>
    BlueScreen,
    /// <summary>Asleep, would not wake, and the power button was held.</summary>
    FrozeAsleepForcedOff,
    /// <summary>Froze while in use and the power button was held.</summary>
    FrozeForcedOff,
    /// <summary>Died while asleep (Modern Standby) with no error recorded.</summary>
    DiedAsleep,
    /// <summary>Died while going to sleep or waking up.</summary>
    SleepTransition,
    /// <summary>Battery reached empty.</summary>
    BatteryRanOut,
    /// <summary>Froze or lost power while in use; nothing recorded.</summary>
    Unknown,
}

/// <summary>One thing that happened to the laptop, kept in the journal.</summary>
public sealed class Incident
{
    /// <summary>Stable key, so the same event is never recorded (or announced) twice.</summary>
    public string Id { get; set; } = "";
    public IncidentKind Kind { get; set; }
    public DateTimeOffset Time { get; set; }
    public Severity Severity { get; set; }
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? Detail { get; set; }
    /// <summary>Where it came from, e.g. "nvlddmkm event 153".</summary>
    public string? Source { get; set; }
    /// <summary>The raw text the driver logged, e.g. "Error occurred on GPUID: 100".</summary>
    public string? Data { get; set; }
    /// <summary>What the graphics card was doing when it happened, if LaptopGuardian was watching.</summary>
    public GpuSnapshot? Gpu { get; set; }
    public CrashInfo? Crash { get; set; }
    /// <summary>What the person saw, for unexpected shutdowns.</summary>
    public string? UserNote { get; set; }
}

public sealed class GpuSnapshot
{
    public DateTimeOffset At { get; set; }
    public string? Name { get; set; }
    public double? TemperatureC { get; set; }
    public double? TemperatureLimitC { get; set; }
    public double? UtilizationPct { get; set; }
    public double? MemoryUsedGB { get; set; }
    public double? MemoryTotalGB { get; set; }
    public double? PowerW { get; set; }
    public double? PowerLimitW { get; set; }
    /// <summary>Why the card is holding its clocks back, in words ("heat", "its power limit"), if it is.</summary>
    public string? Throttle { get; set; }
    public bool Asleep { get; set; }
    public List<GpuApp> TopApps { get; set; } = new();

    [JsonIgnore]
    public double? MemoryPct => MemoryUsedGB is double u && MemoryTotalGB is double t && t > 0 ? 100.0 * u / t : null;

    public string Describe()
    {
        if (Asleep) return "The graphics card was asleep.";
        var parts = new List<string>();
        if (TemperatureC is double tc) parts.Add($"{tc:0}°C");
        if (UtilizationPct is double u) parts.Add($"{u:0}% busy");
        if (MemoryUsedGB is double m && MemoryTotalGB is double t) parts.Add($"{m:0.0} of {t:0.0} GB memory in use");
        if (PowerW is double p) parts.Add($"drawing {p:0} W");
        var s = string.Join(", ", parts);
        if (Throttle != null) s += $" (held back by {Throttle})";
        if (TopApps.Count > 0) s += ". Busiest: " + string.Join(", ", TopApps.Take(3).Select(a => $"{a.Name} {a.UtilizationPct:0}%"));
        return s.Length == 0 ? "No readings." : s + ".";
    }
}

public sealed record GpuApp(string Name, int Pid, double UtilizationPct);

public sealed class CrashInfo
{
    public CrashCause Cause { get; set; }
    /// <summary>The last moment Windows recorded itself as running (event 6008).</summary>
    public DateTimeOffset? LastAlive { get; set; }
    /// <summary>When Windows started again (event 41).</summary>
    public DateTimeOffset RestartedAt { get; set; }
    /// <summary>When it went to sleep, if it was asleep when it died.</summary>
    public DateTimeOffset? AsleepSince { get; set; }
    public bool? OnAc { get; set; }
    public int? BatteryPercent { get; set; }
    public uint BugcheckCode { get; set; }
    public string? BugcheckName { get; set; }
    public DateTimeOffset? PowerButtonPressedAt { get; set; }
    public bool ConnectedStandby { get; set; }
    public int SleepInProgress { get; set; }
    public int GraphicsErrorsBefore { get; set; }
}

public sealed record Alert(
    IncidentKind Kind,
    Severity Severity,
    string Title,
    string Body,
    string Page,
    string? IncidentId = null);

public sealed record Recommendation(
    string Id,
    Severity Severity,
    string Title,
    string Body,
    string? ActionLabel = null,
    string? ActionId = null,
    string? LinkLabel = null,
    string? LinkUrl = null);
