using LaptopGuardian.Core;

namespace LaptopGuardian.Tests;

/// <summary>Bugs seen in the wild, named as they were seen.</summary>
public class RegressionTests
{
    [Fact]
    public void An_nvidia_error_from_two_days_ago_was_announced_as_a_new_gpu_driver_error()
    {
        // 4 November 2026: version 1 used EventLog.EntryWritten, which re-raises old entries when the full,
        // circular System log wraps. It popped "GPU driver error - nvlddmkm (event 153)" for an event
        // logged on 2 November at 5:51 PM. Version 2 subscribes with EvtSubscribe and, whatever delivers
        // an event, only announces it if it was written in the last few minutes.
        var logged = new DateTimeOffset(2026, 11, 2, 17, 51, 35, TimeSpan.FromHours(-5));
        var seen = new DateTimeOffset(2026, 11, 4, 4, 58, 0, TimeSpan.FromHours(-5));
        Assert.False(Guardian.IsFresh(logged, seen));
        Assert.True(Guardian.IsFresh(seen.AddSeconds(-5), seen));
        Assert.False(Guardian.IsFresh(seen.AddHours(1), seen)); // a clock jump forward is not news either
    }

    [Fact]
    public void Crashes_in_use_were_reported_as_dying_asleep_because_the_battery_report_has_gaps_around_every_restart()
    {
        // 4 November 2026: version 2.1 said "It went to sleep at 1:29 PM" while the laptop was overheating under a
        // game at 1:37 PM and publishing a release at 2:15 PM. The battery report's "Suspend" rows are gaps
        // around restarts (all five that week contained a reboot, two of them clean), not sleep.
        var row = new UsageEntry(new DateTimeOffset(2026, 11, 4, 13, 29, 0, TimeSpan.FromHours(-5)), "Suspend", true, 53813, 61854);
        var back = new DateTimeOffset(2026, 11, 4, 14, 49, 20, TimeSpan.FromHours(-5));
        var v = CrashClassifier.Classify(new CrashFacts { RestartedAt = back, LastActivity = back.AddSeconds(-24), PowerBefore = row });
        Assert.Equal(CrashCause.SuddenReset, v.Cause);
        Assert.False(CrashScanner.IsAsleepCause(v.Cause));
        Assert.DoesNotContain("sleep", v.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Asleep_comes_only_from_windows_own_sleep_events()
    {
        var boot = new DateTimeOffset(2026, 11, 4, 14, 30, 28, TimeSpan.FromHours(-5));
        var enter = new Tuple<DateTimeOffset, bool>(boot.AddMinutes(-40), true);
        var exit = new Tuple<DateTimeOffset, bool>(boot.AddMinutes(-20), false);
        Assert.Equal(enter.Item1, CrashScanner.AsleepSince(new[] { enter }, boot, boot.AddHours(-3)));
        Assert.Null(CrashScanner.AsleepSince(new[] { enter, exit }, boot, boot.AddHours(-3)));
        Assert.Null(CrashScanner.AsleepSince(Array.Empty<Tuple<DateTimeOffset, bool>>(), boot, null));
        // A sleep before the previous start belongs to an earlier session.
        Assert.Null(CrashScanner.AsleepSince(new[] { enter }, boot, boot.AddMinutes(-10)));
    }

    [Fact]
    public void Clearing_the_stuck_reports_left_the_suggestion_up_because_the_log_keeps_the_old_retries()
    {
        var t0 = new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);
        var entries = Enumerable.Range(0, 100).Select(i => new WerEntry(t0.AddHours(i * 4), "LiveKernelEvent", "141", "r", "2051", "", "")).ToList();
        var reports = WerReports.Group(entries);
        Assert.True(reports.Single().Stuck);
        reports.Single().MarkClearedAt(t0.AddDays(60));
        Assert.DoesNotContain(Advisor.Advise(new Findings { KernelReports = reports }), r => r.Id == "stuck-reports");

        // If Windows logs it again afterwards, it did not stay cleared.
        var again = WerReports.Group(entries);
        again.Single().MarkClearedAt(t0.AddDays(1));
        Assert.True(again.Single().Stuck);
    }

    [Fact]
    public void Clearing_reports_deletes_them_inside_windows_folders_and_nothing_else()
    {
        string root = Path.Combine(Path.GetTempPath(), "lg-test-" + Guid.NewGuid().ToString("N"));
        string queue = Path.Combine(root, "WER"), dumps = Path.Combine(root, "LiveKernelReports"), outside = Path.Combine(root, "keep");
        string folder = Path.Combine(queue, "ReportQueue", "Kernel_141_x"), dump = Path.Combine(dumps, "WATCHDOG", "WATCHDOG-1.dmp"), kept = Path.Combine(outside, "keep.dmp");
        Directory.CreateDirectory(folder); Directory.CreateDirectory(Path.GetDirectoryName(dump)!); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(folder, "Report.wer"), "x"); File.WriteAllText(dump, "xx"); File.WriteAllText(kept, "x");
        try
        {
            var report = new KernelReport { ReportId = "r", QueueFolder = folder, DumpFiles = new() { dump, kept } };
            var (n, files, _, errors) = WerReports.Clear(new[] { report }, queue, dumps);
            Assert.Equal(1, n);
            Assert.Equal(1, files);
            Assert.Empty(errors);
            Assert.False(Directory.Exists(folder));
            Assert.False(File.Exists(dump));
            Assert.True(File.Exists(kept));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void An_amd_card_was_warned_about_heat_using_an_nvidia_laptops_limit()
    {
        var hotAmd = new GpuSnapshot { TemperatureC = 90, TemperatureLimitC = null, Name = "AMD Radeon RX 7900 XTX" };
        Assert.DoesNotContain(Advisor.Advise(new Findings { Gpu = hotAmd }), r => r.Id == "gpu-hot");
        var hotNvidia = new GpuSnapshot { TemperatureC = 87, TemperatureLimitC = 87 };
        Assert.Contains(Advisor.Advise(new Findings { Gpu = hotNvidia }), r => r.Id == "gpu-hot");
    }

    [Fact]
    public void The_same_event_is_recorded_once_however_often_it_is_delivered()
    {
        var j = new Journal(null);
        var i = new Incident { Id = "evt:393707:638000000000000000", Title = "x", Time = DateTimeOffset.Now };
        Assert.True(j.Add(i));
        Assert.False(j.Add(new Incident { Id = i.Id, Title = "x again", Time = DateTimeOffset.Now }));
        Assert.Single(j.Items);
    }

    [Fact]
    public void Reliability_monitor_counted_four_crash_reports_as_two_thousand_eight_hundred_failures()
    {
        // Windows retried four unsendable kernel reports every 4 hours for six weeks: 2,856 log entries.
        var t0 = new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);
        var entries = Enumerable.Range(0, 2856).Select(i => new WerEntry(t0.AddMinutes(i * 60), "LiveKernelEvent", i % 4 == 0 ? "141" : "193",
            $"report-{i % 4}", "2051", "", "")).ToList();
        var f = new Findings { KernelReports = WerReports.Group(entries) };
        Assert.Equal(4, f.KernelReports.Count);
        var rec = Advisor.Advise(f).Single(r => r.Id == "stuck-reports");
        Assert.Contains("4 old crash reports", rec.Title);
        Assert.Contains("2,856", rec.Title);
        Assert.Equal(Advisor.ActionClearReports, rec.ActionId);
    }
}

public class AdvisorTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 4, 5, 0, 0, TimeSpan.FromHours(-5));

    static Incident Crash(int daysAgo, CrashCause cause) => new()
    {
        Id = "crash:" + daysAgo,
        Kind = IncidentKind.UnexpectedShutdown,
        Time = Now.AddDays(-daysAgo),
        Severity = Severity.Critical,
        Title = cause.ToString(),
        Crash = new CrashInfo { Cause = cause, RestartedAt = Now.AddDays(-daysAgo) },
    };

    static PowerSettingsInfo Power(bool hibernate = true, uint? hibAc = 0) => new()
    {
        HibernateSupported = hibernate, HiberFilePresent = hibernate, HibernateAfterAc = hibAc, ModernStandby = true,
        LidAc = PowerConfig.ActionSleep, PowerButtonAc = PowerConfig.ActionSleep,
    };

    [Fact]
    public void Dying_asleep_offers_hibernation_after_sleep()
    {
        var f = new Findings { Now = Now, Power = Power(), Incidents = new[] { Crash(1, CrashCause.DiedAsleep), Crash(2, CrashCause.DiedAsleep) } };
        Assert.DoesNotContain(Advisor.Advise(f), r => r.Id == "sudden-resets");
        var rec = Advisor.Advise(f).First();
        Assert.Equal("sleep-deaths", rec.Id);
        Assert.Equal(Advisor.ActionHibernateAfterSleep, rec.ActionId);
        Assert.Contains("2 times", rec.Title);
    }

    [Fact]
    public void With_hibernation_off_it_offers_to_turn_it_on_first()
    {
        var f = new Findings { Now = Now, Power = Power(hibernate: false), Incidents = new[] { Crash(1, CrashCause.DiedAsleep) } };
        Assert.Equal(Advisor.ActionEnableHibernate, Advisor.Advise(f).First().ActionId);
    }

    [Fact]
    public void Once_sleep_already_hibernates_it_suggests_the_lid_next()
    {
        var f = new Findings { Now = Now, Power = Power(hibAc: 1200), Incidents = new[] { Crash(1, CrashCause.SleepTransition) } };
        Assert.Equal(Advisor.ActionLidHibernate, Advisor.Advise(f).First().ActionId);
    }

    [Fact]
    public void An_old_bios_is_flagged_only_when_there_are_crashes()
    {
        var sys = new SystemSnapshot { Manufacturer = "ASUSTeK COMPUTER INC.", Model = "ROG Strix G713PI_G713PI", BiosVersion = "G713PI.336", BiosDate = new DateTime(2025, 10, 1) };
        Assert.DoesNotContain(Advisor.Advise(new Findings { Now = Now, System = sys }), r => r.Id == "bios");
        var withCrash = new Findings { Now = Now, System = sys, Incidents = new[] { Crash(1, CrashCause.Unknown) } };
        var bios = Advisor.Advise(withCrash).Single(r => r.Id == "bios");
        Assert.Contains("MyASUS", bios.Body);
        Assert.Contains("G713PI", bios.LinkUrl);
    }

    [Fact]
    public void Sudden_resets_get_a_way_to_narrow_them_down_and_no_hibernate_advice()
    {
        var f = new Findings { Now = Now, Power = Power(), Incidents = new[] { Crash(0, CrashCause.SuddenReset), Crash(1, CrashCause.SuddenReset) } };
        var recs = Advisor.Advise(f);
        var rec = recs.Single(r => r.Id == "sudden-resets");
        Assert.Contains("2 times", rec.Title);
        Assert.Equal("mdsched.exe", rec.LinkUrl);
        Assert.DoesNotContain(recs, r => r.Id == "sleep-deaths");
    }

    [Fact]
    public void A_quiet_week_is_all_good()
    {
        var (level, headline, _) = Advisor.Status(new Findings { Now = Now }, Array.Empty<Recommendation>());
        Assert.Equal(Severity.Info, level);
        Assert.Equal("All good", headline);
    }

    [Fact]
    public void Two_crashes_asleep_this_week_needs_attention()
    {
        var f = new Findings { Now = Now, Incidents = new[] { Crash(1, CrashCause.DiedAsleep), Crash(2, CrashCause.DiedAsleep) } };
        var (level, headline, summary) = Advisor.Status(f, Array.Empty<Recommendation>());
        Assert.Equal(Severity.Critical, level);
        Assert.Equal("Needs attention", headline);
        Assert.Contains("all while asleep", summary);
    }
}

public class JournalTests
{
    [Fact]
    public void Notes_survive_a_restart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lg-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "journal.json");
        try
        {
            var j = new Journal(path);
            j.Add(new Incident { Id = "crash:1", Kind = IncidentKind.UnexpectedShutdown, Time = DateTimeOffset.Now, Title = "Shut down while asleep" });
            j.SetNote("crash:1", "Black screen, it would not wake");

            var again = new Journal(path);
            Assert.Equal("Black screen, it would not wake", again.Items.Single().UserNote);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_broken_file_is_set_aside_not_fatal()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lg-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "journal.json");
        File.WriteAllText(path, "{ this is not json");
        try
        {
            var j = new Journal(path);
            Assert.Empty(j.Items);
            Assert.Contains(Directory.GetFiles(dir), f => f.Contains(".broken-"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Version_one_settings_carry_over()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lg-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, """{ "Enabled": false, "ThresholdMs": 80 }""");
        try
        {
            var s = JsonFile.Load<AppSettings>(path);
            Assert.False(s.KeyboardFilter);
            Assert.Equal(80, s.KeyboardThresholdMs);
            Assert.True(s.NotifyCrashes);
        }
        finally { Directory.Delete(dir, true); }
    }
}
