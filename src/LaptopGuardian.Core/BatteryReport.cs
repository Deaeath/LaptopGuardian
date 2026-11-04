using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;

namespace LaptopGuardian.Core;

/// <summary>One row of the battery report's "Recent usage": a power state and when it began.</summary>
public sealed record UsageEntry(DateTimeOffset Time, string Type, bool Ac, int? ChargeMWh, int? FullMWh)
{
    public int? Percent => ChargeMWh is int c && FullMWh is int f && f > 0 ? (int)Math.Round(100.0 * c / f) : null;

    /// <summary>Asleep (standby, suspend, hibernate) rather than in use. Report-generated rows count as in use.</summary>
    public bool IsAsleep =>
        !string.Equals(Type, "Active", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(Type, "ReportGenerated", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Windows' own battery report (<c>powercfg /batteryreport /xml</c>). It needs no administrator rights, holds the
/// battery's wear, and records every sleep and wake of the last few days, which is what tells a crash
/// in use apart from a laptop that died asleep.
/// </summary>
public sealed class BatteryReport
{
    public string? Manufacturer { get; init; }
    public string? Chemistry { get; init; }
    public int? DesignMWh { get; init; }
    public int? FullMWh { get; init; }
    public int? Cycles { get; init; }
    public bool ModernStandby { get; init; }
    public List<UsageEntry> Usage { get; init; } = new();

    public double? HealthPct => DesignMWh is int d && FullMWh is int f && d > 0 ? Math.Round(100.0 * f / d, 1) : null;

    /// <summary>The power state the laptop was in at <paramref name="t"/>, if the report goes back that far.</summary>
    public UsageEntry? StateAt(DateTimeOffset t)
    {
        if (Usage.Count == 0 || t < Usage[0].Time) return null;
        return Usage.LastOrDefault(u => u.Time <= t);
    }

    public static BatteryReport Parse(XDocument doc)
    {
        XElement? First(XContainer c, string name) => c.Descendants().FirstOrDefault(e => e.Name.LocalName == name);
        int? Int(XElement? e) => e != null && int.TryParse(e.Value, out var v) ? v : null;

        var battery = First(doc, "Battery");
        var usage = new List<UsageEntry>();
        foreach (var e in doc.Descendants().Where(e => e.Name.LocalName == "UsageEntry"))
        {
            var when = ParseTime((string?)e.Attribute("Timestamp"), (string?)e.Attribute("LocalTimestamp"));
            if (when == null) continue;
            usage.Add(new UsageEntry(
                when.Value,
                (string?)e.Attribute("EntryType") ?? "",
                (string?)e.Attribute("Ac") == "1",
                int.TryParse((string?)e.Attribute("ChargeCapacity"), out var c) ? c : null,
                int.TryParse((string?)e.Attribute("FullChargeCapacity"), out var f) ? f : null));
        }
        usage.Sort((a, b) => a.Time.CompareTo(b.Time));

        int? cycles = battery == null ? null : Int(First(battery, "CycleCount"));
        return new BatteryReport
        {
            Manufacturer = battery == null ? null : First(battery, "Manufacturer")?.Value,
            Chemistry = battery == null ? null : First(battery, "Chemistry")?.Value,
            DesignMWh = battery == null ? null : Int(First(battery, "DesignCapacity")),
            FullMWh = battery == null ? null : Int(First(battery, "FullChargeCapacity")),
            Cycles = cycles is > 0 ? cycles : null, // 0 means the battery does not report it
            ModernStandby = First(doc, "ConnectedStandby")?.Value == "1",
            Usage = usage,
        };
    }

    static DateTimeOffset? ParseTime(string? utc, string? local)
    {
        if (!string.IsNullOrEmpty(utc) &&
            DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var u))
            return u;
        if (!string.IsNullOrEmpty(local) &&
            DateTime.TryParse(local, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var l))
            return new DateTimeOffset(l);
        return null;
    }

    /// <summary>Runs powercfg and parses the result. Null on a desktop or if powercfg fails.</summary>
    public static BatteryReport? Generate(TimeSpan? timeout = null)
    {
        string path = Path.Combine(Path.GetTempPath(), $"lg-battery-{Environment.ProcessId}-{Guid.NewGuid():N}.xml");
        try
        {
            var psi = new ProcessStartInfo("powercfg.exe", $"/batteryreport /xml /output \"{path}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds)) { try { p.Kill(); } catch { } return null; }
            if (!File.Exists(path)) return null;
            return Parse(XDocument.Load(path));
        }
        catch { return null; }
        finally { try { File.Delete(path); } catch { } }
    }
}
