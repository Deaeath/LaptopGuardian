using System.Globalization;
using System.Text.RegularExpressions;

namespace LaptopGuardian.Core;

/// <summary>One entry Windows Error Reporting wrote to the Application log (event 1001).</summary>
public sealed record WerEntry(DateTimeOffset Time, string EventName, string Code, string ReportId, string Status, string AttachedFiles, string QueueFolder);

/// <summary>A distinct crash report, however many times Windows logged it.</summary>
public sealed class KernelReport
{
    public string ReportId { get; init; } = "";
    public string EventName { get; init; } = "";
    public string Code { get; init; } = "";
    public string CodeName { get; init; } = "";
    public string Meaning { get; init; } = "";
    public DateTimeOffset FirstLogged { get; init; }
    public DateTimeOffset LastLogged { get; init; }
    public int TimesLogged { get; init; }
    public string QueueFolder { get; init; } = "";
    public List<string> DumpFiles { get; init; } = new();
    /// <summary>When the snapshot was actually taken, read from the dump file's name.</summary>
    public DateTimeOffset? CapturedAt { get; init; }
    /// <summary>Windows keeps retrying it (every 4 hours) and logs each retry as if it were new.</summary>
    public bool Stuck { get; init; }

    public DateTimeOffset HappenedAt => CapturedAt ?? FirstLogged;
}

/// <summary>
/// Reads Windows Error Reporting's log entries for kernel reports. A report Windows cannot send stays in
/// its queue and is retried by the "QueueReporting" task every four hours, and every retry writes the
/// same report to the log again; Reliability Monitor then shows each retry as a new hardware failure.
/// Grouping by Report Id shows how many real events there were.
/// </summary>
public static class WerReports
{
    static readonly string[] Interesting = { "LiveKernelEvent", "BlueScreen" };

    public static List<WerEntry> Read(TimeSpan window) =>
        EventLogX.Query("Application",
            $"*[System[Provider[@Name='Windows Error Reporting'] and EventID=1001 and {EventLogX.Since(window)}]]",
            r =>
            {
                var p = EventLogX.Props(r);
                if (p.Count < 21) return null;
                if (!Interesting.Contains(p[2], StringComparer.OrdinalIgnoreCase)) return null;
                return new WerEntry(EventLogX.Time(r), p[2], p[5], p[19], p[20], p[15], p[16]);
            });

    public static List<KernelReport> Group(IEnumerable<WerEntry> entries)
    {
        var list = new List<KernelReport>();
        foreach (var g in entries.Where(e => !string.IsNullOrWhiteSpace(e.ReportId)).GroupBy(e => e.ReportId, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = g.OrderBy(e => e.Time).ToList();
            var first = ordered[0];
            var last = ordered[^1];
            var dumps = SplitFiles(first.AttachedFiles).Where(f => f.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)).ToList();
            var (name, meaning) = Explain(first.EventName, first.Code);
            list.Add(new KernelReport
            {
                ReportId = g.Key,
                EventName = first.EventName,
                Code = first.Code,
                CodeName = name,
                Meaning = meaning,
                FirstLogged = first.Time,
                LastLogged = last.Time,
                TimesLogged = ordered.Count,
                QueueFolder = StripLongPrefix(first.QueueFolder),
                DumpFiles = dumps,
                CapturedAt = dumps.Select(CapturedFromDumpName).FirstOrDefault(t => t != null),
                Stuck = ordered.Count >= 3 && (last.Time - first.Time) >= TimeSpan.FromDays(1),
            });
        }
        return list.OrderByDescending(r => r.HappenedAt).ToList();
    }

    public static (string Name, string Meaning) Explain(string eventName, string code)
    {
        if (string.Equals(eventName, "BlueScreen", StringComparison.OrdinalIgnoreCase))
            return ("Blue screen report", "Windows saved a report of a blue-screen crash.");
        if (StopCodes.TryParseHex(code, out var c))
        {
            var (name, meaning) = StopCodes.Describe(c);
            return (name, meaning);
        }
        return ("Kernel report", "Windows saved a diagnostic snapshot of the system.");
    }

    public static IEnumerable<string> SplitFiles(string? s) =>
        (s ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(StripLongPrefix)
                 .Where(x => x.Length > 0);

    public static string StripLongPrefix(string? p)
    {
        p = (p ?? "").Trim();
        return p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;
    }

    static readonly Regex DumpStamp = new(@"-(\d{8})-(\d{4})\.dmp$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"WATCHDOG-20261001-0955.dmp" was written at 9:55 on 1 October 2026, local time.</summary>
    public static DateTimeOffset? CapturedFromDumpName(string path)
    {
        var m = DumpStamp.Match(path);
        if (!m.Success) return null;
        if (!DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t))
            return null;
        return new DateTimeOffset(t);
    }

    /// <summary>
    /// Deletes the queued copies of the given reports, and the dumps they hold, so Windows stops retrying them.
    /// Only paths inside Windows' own report folders are touched. Needs administrator rights.
    /// </summary>
    public static (int Reports, int Files, long Bytes, List<string> Errors) Clear(IEnumerable<KernelReport> reports)
    {
        string queueRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER") + Path.DirectorySeparatorChar;
        string dumpRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "LiveKernelReports") + Path.DirectorySeparatorChar;
        int n = 0, files = 0; long bytes = 0; var errors = new List<string>();

        foreach (var r in reports)
        {
            bool any = false;
            foreach (var dump in r.DumpFiles)
            {
                var full = SafeFull(dump);
                if (full == null || !full.StartsWith(dumpRoot, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var fi = new FileInfo(full);
                    if (fi.Exists) { bytes += fi.Length; fi.Delete(); files++; any = true; }
                }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(full)}: {ex.Message}"); }
            }
            var q = SafeFull(r.QueueFolder);
            if (q != null && q.StartsWith(queueRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(q))
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(q, "*", SearchOption.AllDirectories))
                        try { bytes += new FileInfo(f).Length; } catch { }
                    Directory.Delete(q, recursive: true);
                    any = true;
                }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(q)}: {ex.Message}"); }
            }
            if (any) n++;
        }
        return (n, files, bytes, errors);
    }

    static string? SafeFull(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        try { return Path.GetFullPath(StripLongPrefix(p)); } catch { return null; }
    }
}
