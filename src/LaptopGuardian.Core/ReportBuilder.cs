using System.Text;

namespace LaptopGuardian.Core;

/// <summary>A plain-text (Markdown) health report to keep, or to send to the laptop maker's support.</summary>
public static class ReportBuilder
{
    public static string Markdown(Findings f, IReadOnlyList<Recommendation> recs, string version)
    {
        var sb = new StringBuilder();
        var (_, headline, summary) = Advisor.Status(f, recs);
        sb.AppendLine("# Laptop health report");
        sb.AppendLine();
        sb.AppendLine($"Generated {f.Now.ToLocalTime():f} by LaptopGuardian {version}.");
        sb.AppendLine();
        sb.AppendLine($"**{headline}.** {summary}");
        sb.AppendLine();

        sb.AppendLine("## This laptop");
        sb.AppendLine();
        sb.AppendLine($"- Model: {f.System.Manufacturer} {f.System.Model}");
        sb.AppendLine($"- BIOS: {f.System.BiosVersion}{(f.System.BiosDate is DateTime bd ? $" ({bd:MMMM d, yyyy})" : "")}");
        sb.AppendLine($"- Windows: {f.System.Windows}");
        foreach (var a in f.System.Adapters)
            sb.AppendLine($"- Graphics: {a.Name}, driver {a.DriverVersion}{(a.FriendlyVersion != null ? $" ({a.Vendor} {a.FriendlyVersion})" : "")}{(a.DriverDate is DateTime dd ? $", dated {dd:MMM d, yyyy}" : "")}");
        if (f.Battery?.HealthPct is double h)
            sb.AppendLine($"- Battery: holds {h:0}% of its design capacity ({f.Battery.FullMWh / 1000.0:0.#} of {f.Battery.DesignMWh / 1000.0:0.#} Wh){(f.Battery.Cycles is int c ? $", {c} cycles" : "")}");
        if (f.Power is PowerSettingsInfo p)
        {
            sb.AppendLine($"- Sleep: {(p.ModernStandby ? "Modern Standby (S0 low-power idle)" : "classic standby")}; hibernation {(p.HibernateAvailable ? "available" : "off")}");
            sb.AppendLine($"- Plugged in: sleep after {Fmt.Seconds(p.SleepAfterAc)}, hibernate after {Fmt.Seconds(p.HibernateAfterAc)}, lid: {PowerConfig.ActionName(p.LidAc)}");
            sb.AppendLine($"- On battery: sleep after {Fmt.Seconds(p.SleepAfterDc)}, hibernate after {Fmt.Seconds(p.HibernateAfterDc)}, lid: {PowerConfig.ActionName(p.LidDc)}");
        }
        if (f.Gpu is GpuSnapshot g) sb.AppendLine($"- Graphics card now: {g.Describe()}");
        sb.AppendLine();

        if (recs.Count > 0)
        {
            sb.AppendLine("## What to do");
            sb.AppendLine();
            int i = 1;
            foreach (var r in recs)
            {
                sb.AppendLine($"{i++}. **{r.Title}.** {r.Body}");
            }
            sb.AppendLine();
        }

        if (f.Drivers is DriverReport dr)
        {
            sb.AppendLine("## Drivers");
            sb.AppendLine();
            if (dr.LatestBios != null) sb.AppendLine($"- BIOS: {f.System.BiosVersion}; newest published is {dr.LatestBios}.");
            foreach (var u in dr.Updates) sb.AppendLine($"- Update available from {u.Source}: {u.Title} {u.Available}{(u.Installed != null ? $" (installed {u.Installed})" : "")}.");
            foreach (var bad in dr.Problems) sb.AppendLine($"- Problem: {bad.Name}: {bad.Meaning} (code {bad.ErrorCode}).");
            foreach (var i in dr.Installed) sb.AppendLine($"- {i.Category}: {i.Device}, {i.Version}{(i.Date is DateTime d ? $", {d:MMM yyyy}" : "")}.");
            sb.AppendLine();
        }

        var crashes = f.Crashes(TimeSpan.FromDays(60)).OrderByDescending(c => c.Time).ToList();
        sb.AppendLine($"## Unexpected shutdowns (last 60 days): {crashes.Count}");
        sb.AppendLine();
        if (crashes.Count == 0) sb.AppendLine("None.");
        foreach (var c in crashes)
        {
            sb.AppendLine($"- **{Fmt.When(c.Time)}: {c.Title}.** {c.Summary}");
            if (c.Crash is CrashInfo ci)
            {
                var line = new List<string>();
                if (ci.AsleepSince is DateTimeOffset s) line.Add($"asleep since {Fmt.When(s)}");
                if (ci.LastAlive is DateTimeOffset la) line.Add($"last sign of life {Fmt.When(la)}");
                line.Add($"restarted {Fmt.When(ci.RestartedAt)}");
                if (ci.PowerButtonPressedAt is DateTimeOffset pb) line.Add($"power button held {Fmt.When(pb)}");
                if (ci.BugcheckCode != 0) line.Add($"stop code 0x{ci.BugcheckCode:X} {ci.BugcheckName}");
                sb.AppendLine($"  - {string.Join("; ", line)}");
            }
            if (!string.IsNullOrWhiteSpace(c.UserNote)) sb.AppendLine($"  - What I saw: {c.UserNote}");
        }
        sb.AppendLine();

        var gfx = f.Incidents.Where(i => i.Kind is IncidentKind.GraphicsError or IncidentKind.HardwareError && f.Now - i.Time <= TimeSpan.FromDays(60))
            .OrderByDescending(i => i.Time).ToList();
        sb.AppendLine($"## Graphics and hardware errors (last 60 days): {gfx.Count}");
        sb.AppendLine();
        if (gfx.Count == 0) sb.AppendLine("None.");
        foreach (var e in gfx.Take(60))
        {
            sb.Append($"- {Fmt.When(e.Time)}: {e.Title}");
            if (e.Source != null) sb.Append($" ({e.Source}{(e.Data != null && !e.Source.StartsWith("Windows Error") ? $": \"{e.Data}\"" : "")})");
            sb.AppendLine(".");
            if (e.Gpu != null) sb.AppendLine($"  - At the time: {e.Gpu.Describe()}");
        }
        sb.AppendLine();

        if (f.KernelReports.Count > 0)
        {
            sb.AppendLine("## Windows crash reports");
            sb.AppendLine();
            sb.AppendLine("Distinct reports, with how often Windows logged each one. A report Windows cannot send is retried every 4 hours and logged again each time.");
            sb.AppendLine();
            foreach (var k in f.KernelReports)
                sb.AppendLine($"- {Fmt.When(k.HappenedAt)}: {k.EventName} 0x{k.Code} {k.CodeName}. Logged {k.TimesLogged:N0} times{(k.Stuck ? " (stuck in Windows' retry queue)" : "")}.");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
