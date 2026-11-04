using System.Text;

namespace LaptopGuardian.Core;

/// <summary>Everything the advisor and the report look at, gathered in one place.</summary>
public sealed record Findings
{
    public SystemSnapshot System { get; init; } = new();
    public PowerSettingsInfo? Power { get; init; }
    public BatteryReport? Battery { get; init; }
    public IReadOnlyList<Incident> Incidents { get; init; } = Array.Empty<Incident>();
    public IReadOnlyList<KernelReport> KernelReports { get; init; } = Array.Empty<KernelReport>();
    public GpuSnapshot? Gpu { get; init; }
    public DriverReport? Drivers { get; init; }
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;

    public IEnumerable<Incident> Crashes(TimeSpan within) =>
        Incidents.Where(i => i.Kind == IncidentKind.UnexpectedShutdown && Now - i.Time <= within);

    /// <summary>Graphics errors that matter (diagnostic snapshots Windows takes alongside them are left out).</summary>
    public IEnumerable<Incident> GraphicsErrors(TimeSpan within) =>
        Incidents.Where(i => i.Kind == IncidentKind.GraphicsError && i.Severity >= Severity.Warning && Now - i.Time <= within);
}

/// <summary>Turns findings into a short list of things to do, most important first.</summary>
public static class Advisor
{
    public const string ActionHibernateAfterSleep = "hibernate-after-sleep";
    public const string ActionLidHibernate = "lid-hibernate";
    public const string ActionEnableHibernate = "enable-hibernate";
    public const string ActionClearReports = "clear-reports";
    public const string ActionSleepStudy = "sleep-study";
    public const uint HibernateAfterSeconds = 20 * 60;

    public static List<Recommendation> Advise(Findings f)
    {
        var list = new List<Recommendation>();
        var crashes14 = f.Crashes(TimeSpan.FromDays(14)).ToList();
        var asleep14 = crashes14.Where(c => c.Crash != null && CrashScanner.IsAsleepCause(c.Crash.Cause)).ToList();
        var gfx14 = f.GraphicsErrors(TimeSpan.FromDays(14)).ToList();
        var power = f.Power;

        // 1. Dying in sleep: the one that loses work.
        if (asleep14.Count > 0)
        {
            string title = asleep14.Count == 1 ? "It died while asleep" : $"It has died while asleep {asleep14.Count} times in two weeks";
            var body = new StringBuilder();
            body.Append(asleep14.Count == crashes14.Count
                ? (crashes14.Count == 1 ? "The last unexpected shutdown happened during sleep. " : $"All {crashes14.Count} unexpected shutdowns in the last two weeks happened during sleep. ")
                : $"{asleep14.Count} of {crashes14.Count} unexpected shutdowns in the last two weeks happened during sleep. ");
            body.Append("Sleep on this laptop is Modern Standby, which needs every driver to wake cleanly; when one does not, the laptop hangs and you lose what was open. ");
            body.Append("Hibernating saves everything to disk and powers off, so it cannot fail that way.");

            if (power is { HibernateAvailable: false })
                list.Add(new Recommendation("sleep-deaths", Severity.Critical, title,
                    body + " Hibernation is turned off on this PC; turn it on first.",
                    "Turn on hibernation", ActionEnableHibernate));
            else if (power != null && (power.HibernateAfterAc is 0 or null || power.HibernateAfterAc > HibernateAfterSeconds))
                list.Add(new Recommendation("sleep-deaths", Severity.Critical, title,
                    body + $" Switching to hibernation after {HibernateAfterSeconds / 60} minutes of sleep keeps short naps instant and makes long ones safe.",
                    $"Hibernate after {HibernateAfterSeconds / 60} min of sleep", ActionHibernateAfterSleep));
            else if (power != null && (power.LidAc != PowerConfig.ActionHibernate || power.PowerButtonAc != PowerConfig.ActionHibernate))
                list.Add(new Recommendation("sleep-deaths", Severity.Critical, title,
                    body + " Sleep already turns into hibernation after a while; if it still dies, hibernate straight away when you close the lid or press the power button.",
                    "Hibernate on lid and power button", ActionLidHibernate));
            else
                list.Add(new Recommendation("sleep-deaths", Severity.Critical, title,
                    body + " The safer settings are already on. Windows' sleep study shows what was awake during each sleep.",
                    "Open the sleep study", ActionSleepStudy));
        }

        // 2. BIOS: the usual fix for sleep and stability problems on laptops. Ask the maker when we can.
        var bios = f.System.BiosDate;
        var biosUpdate = f.Drivers?.Updates.FirstOrDefault(u => u.Key.StartsWith("bios:", StringComparison.Ordinal));
        if (biosUpdate != null)
        {
            list.Add(new Recommendation("bios", crashes14.Count > 0 ? Severity.Warning : Severity.Info,
                $"BIOS {biosUpdate.Available} is available",
                $"You have BIOS {biosUpdate.Installed}; {Short(f.System.Manufacturer)} released {biosUpdate.Available}{(biosUpdate.Released is DateTime br ? $" on {br:MMMM d, yyyy}" : "")}. " +
                "BIOS updates fix sleep, wake and stability problems. Plug in the charger before you run it, and do not turn the laptop off while it updates.",
                LinkLabel: "Download the BIOS update", LinkUrl: biosUpdate.Url));
        }
        else if (f.Drivers?.BiosIsLatest == true)
        {
            // The maker's newest BIOS is already installed: nothing to suggest.
        }
        else if (crashes14.Count > 0 && bios is DateTime bd && (f.Now.Date - bd).TotalDays > 270)
        {
            string where = f.System.IsAsus
                ? "Open MyASUS or Armoury Crate and check for updates, or search ASUS support for your model."
                : $"Check {Short(f.System.Manufacturer)}'s support site for your model.";
            list.Add(new Recommendation("bios", Severity.Warning, "Check for a BIOS update",
                $"Your BIOS ({f.System.BiosVersion}) is from {bd:MMMM yyyy}. Laptop makers fix sleep, wake and stability problems in BIOS updates. {where}",
                LinkLabel: "Search for your BIOS",
                LinkUrl: "https://www.bing.com/search?q=" + Uri.EscapeDataString($"{f.System.Manufacturer} {f.System.ShortModel} BIOS download")));
        }

        // 3. Graphics driver: a newer one exists.
        var gpu = f.System.Discrete;
        var nvidia = f.Drivers?.Updates.FirstOrDefault(u => u.Source == "NVIDIA");
        if (nvidia != null)
        {
            bool troubled = gfx14.Count > 0 || crashes14.Count > 0;
            list.Add(new Recommendation("nvidia-driver", troubled ? Severity.Warning : Severity.Info,
                $"NVIDIA driver {nvidia.Available} is available",
                $"You have {nvidia.Installed}; NVIDIA released {nvidia.Available}{(nvidia.Released is DateTime nr ? $" on {nr:MMMM d}" : "")}. " +
                (troubled ? "New drivers often fix graphics resets and failed wakes from sleep. " : "") +
                "Install it from the NVIDIA app or the download page; choose a clean installation if you have had graphics errors.",
                LinkLabel: "NVIDIA download page", LinkUrl: nvidia.Url));
        }

        // 4. Driver updates Windows Update has waiting, and devices with driver problems.
        var wu = f.Drivers?.Updates.Where(u => u.Source == "Windows Update").ToList() ?? new();
        if (wu.Count > 0)
            list.Add(new Recommendation("wu-drivers", Severity.Info,
                $"{Fmt.Plural(wu.Count, "driver update", "driver updates")} waiting in Windows Update",
                string.Join("; ", wu.Take(3).Select(u => u.Title)) + (wu.Count > 3 ? "; and more." : ".") +
                " Windows lists these as optional, so it does not install them by itself.",
                "Install them", DriverChecks.ActionInstallWindowsUpdateDrivers));

        var problems = f.Drivers?.Problems ?? new();
        if (problems.Count > 0)
            list.Add(new Recommendation("problem-devices", problems.Any(p => p.ErrorCode is 10 or 31 or 43 or 39) ? Severity.Warning : Severity.Info,
                problems.Count == 1 ? $"{problems[0].Name} has a driver problem" : $"{problems.Count} devices have driver problems",
                string.Join(" ", problems.Take(3).Select(p => $"{p.Name}: {p.Meaning}")) +
                " If it is something you no longer use (a leftover virtual camera or adapter), remove it in Device Manager.",
                LinkLabel: "Open Device Manager", LinkUrl: "devmgmt.msc"));

        // 5. Graphics resets.
        if (gfx14.Count >= 2)
        {
            bool hot = gfx14.Any(g => g.Gpu?.TemperatureC is double t && g.Gpu.TemperatureLimitC is double lim && t >= lim - 3);
            string driver = gpu?.FriendlyVersion != null ? $" (you have {gpu.Vendor} {gpu.FriendlyVersion})" : "";
            string install = nvidia != null ? "Install the newer driver above, " : $"Keep the graphics driver up to date{driver}, ";
            list.Add(new Recommendation("gpu-resets", Severity.Warning, $"{gfx14.Count} graphics errors in two weeks",
                (hot ? "At least one happened with the graphics card at its temperature limit, so heat is the first suspect. " : "") +
                install + "with a clean install if the errors continue. Remove any overclock or undervolt, and cap the frame rate in games so the card is not at 100% all the time.",
                LinkLabel: gpu?.Vendor switch { "NVIDIA" => "NVIDIA drivers", "AMD" => "AMD drivers", "Intel" => "Intel drivers", _ => null },
                LinkUrl: gpu?.Vendor switch
                {
                    "NVIDIA" => "https://www.nvidia.com/en-us/drivers/",
                    "AMD" => "https://www.amd.com/en/support/download/drivers.html",
                    "Intel" => "https://www.intel.com/content/www/us/en/download-center/home.html",
                    _ => null,
                }));
        }

        // 4. Right now: heat and memory.
        if (f.Gpu is { Asleep: false, TemperatureC: double temp, TemperatureLimitC: double limit } g1 && temp >= limit - 2)
            list.Add(new Recommendation("gpu-hot", Severity.Warning, $"Graphics card at its temperature limit ({temp:0}°C)",
                $"It is at or near {limit:0}°C, where it slows itself down to stay safe. Raise the back of the laptop so air can get in, keep the vents clear of dust, use the laptop's performance fan mode, and cap the frame rate in games." +
                (g1.TopApps.Count > 0 ? $" Busiest right now: {string.Join(", ", g1.TopApps.Take(2).Select(a => a.Name))}." : "")));

        if (f.Gpu is { Asleep: false, MemoryPct: double mp } g2 && mp >= 92)
            list.Add(new Recommendation("gpu-memory", Severity.Warning, $"Graphics memory {mp:0}% full",
                $"{g2.MemoryUsedGB:0.0} of {g2.MemoryTotalGB:0.0} GB is in use. When it runs out, games stutter and the driver is more likely to reset. Close browser windows and apps you are not using, or lower texture quality in the game."));

        // 5. Windows' crash reports stuck in a retry loop.
        var stuck = f.KernelReports.Where(r => r.Stuck).ToList();
        if (stuck.Count > 0)
        {
            int logged = stuck.Sum(r => r.TimesLogged);
            list.Add(new Recommendation("stuck-reports", Severity.Info,
                $"{Fmt.Plural(stuck.Count, "old crash report", "old crash reports")} logged {logged:N0} times",
                $"Windows could not send {(stuck.Count == 1 ? "this report" : "these reports")} and retries every 4 hours, writing each retry to the log as if it were a new problem. " +
                "That is why Reliability Monitor looks so bad. They are not new crashes. Clearing them stops the noise (Windows asks for permission).",
                "Clear old reports", ActionClearReports));
        }

        // 6. Battery wear.
        if (f.Battery?.HealthPct is double h && h < 80)
        {
            string tip = f.System.IsAsus
                ? " If it is plugged in most of the time, turn on Battery Health Charging (charge limit 80%) in Armoury Crate or MyASUS to slow further wear."
                : " If it is plugged in most of the time, turn on your laptop's battery care or charge limit setting to slow further wear.";
            list.Add(new Recommendation("battery-wear", h < 60 ? Severity.Warning : Severity.Info,
                $"Battery holds {h:0}% of its original charge",
                $"It stores {f.Battery.FullMWh / 1000.0:0} Wh of the {f.Battery.DesignMWh / 1000.0:0} Wh it had when new." + tip));
        }

        return list
            .OrderByDescending(r => r.Severity)
            .ToList();
    }

    static string Short(string manufacturer) =>
        manufacturer.Replace("COMPUTER INC.", "", StringComparison.OrdinalIgnoreCase).Replace("Inc.", "", StringComparison.OrdinalIgnoreCase).Trim();

    /// <summary>One line for the status banner and tray tooltip.</summary>
    public static (Severity Level, string Headline, string Summary) Status(Findings f, IReadOnlyList<Recommendation> recs)
    {
        var crashes7 = f.Crashes(TimeSpan.FromDays(7)).ToList();
        var gfx7 = f.GraphicsErrors(TimeSpan.FromDays(7)).ToList();
        if (crashes7.Count > 0)
        {
            int asleep = crashes7.Count(c => c.Crash != null && CrashScanner.IsAsleepCause(c.Crash.Cause));
            string tail = asleep == crashes7.Count ? (crashes7.Count == 1 ? ", while asleep" : ", all while asleep")
                : asleep > 0 ? $", {asleep} while asleep" : "";
            return (Severity.Critical, "Needs attention",
                $"{Fmt.Plural(crashes7.Count, "unexpected shutdown", "unexpected shutdowns")} this week{tail}." +
                (gfx7.Count > 0 ? $" {Fmt.Plural(gfx7.Count, "graphics error", "graphics errors")}." : ""));
        }
        if (gfx7.Count > 0)
            return (Severity.Warning, "Keep an eye on it", $"{Fmt.Plural(gfx7.Count, "graphics error", "graphics errors")} this week, no crashes.");
        if (recs.Any(r => r.Severity >= Severity.Warning))
            return (Severity.Warning, "Mostly fine", recs.First(r => r.Severity >= Severity.Warning).Title + ".");
        return (Severity.Info, "All good", "No crashes or graphics errors this week.");
    }
}
