using LaptopGuardian.Core;

namespace LaptopGuardian.Tests;

public class ShutdownRecordTests
{
    // The binary data of a real event 6008 ("The previous system shutdown at 21:24:57 on 03/11/2026 was unexpected").
    const string Real6008 = "EA070B00020003001500180039001501EA070B00030004000200180039001501DC0500003C00000001000000DC050000010000001E0000000000000000000000";

    [Fact]
    public void Reads_the_utc_time_of_death_from_the_binary_data()
    {
        var t = ShutdownRecord.LastAliveFromBinary(Real6008);
        Assert.NotNull(t);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 2, 24, 57, 277, TimeSpan.Zero), t!.Value.ToUniversalTime());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EA07")]
    [InlineData("not hex at all, not hex at all, not hex at all, not hex at all, not hex")]
    public void Returns_null_for_missing_or_short_data(string? hex) => Assert.Null(ShutdownRecord.LastAliveFromBinary(hex));
}

public class KernelPower41Tests
{
    [Fact]
    public void Parses_the_named_fields_of_event_41()
    {
        // Sanitized from a real event: the laptop was in Modern Standby and the power button was pressed.
        var xml = """
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Kernel-Power'/><EventID>41</EventID></System>
            <EventData><Data Name='BugcheckCode'>0</Data><Data Name='BugcheckParameter1'>0x0</Data><Data Name='SleepInProgress'>0</Data>
            <Data Name='PowerButtonTimestamp'>134373099721160667</Data><Data Name='ConnectedStandbyInProgress'>true</Data>
            <Data Name='LongPowerButtonPressDetected'>false</Data></EventData></Event>
            """;
        var e = KernelPower41.FromData(1, DateTimeOffset.Now, EventLogX.NamedData(xml));
        Assert.Equal(0u, e.BugcheckCode);
        Assert.True(e.ConnectedStandby);
        Assert.False(e.LongPowerButtonPress);
        Assert.NotNull(e.PowerButtonPressedAt);
        Assert.Equal(2026, e.PowerButtonPressedAt!.Value.Year);
    }

    [Fact]
    public void Reads_a_hex_bugcheck_code()
    {
        var e = KernelPower41.FromData(1, DateTimeOffset.Now, new Dictionary<string, string> { ["BugcheckCode"] = "0x14f" });
        Assert.Equal(0x14Fu, e.BugcheckCode);
    }

    [Fact]
    public void Reads_a_decimal_bugcheck_code()
    {
        var e = KernelPower41.FromData(1, DateTimeOffset.Now, new Dictionary<string, string> { ["BugcheckCode"] = "335" });
        Assert.Equal(0x14Fu, e.BugcheckCode);
    }
}

public class CrashClassifierTests
{
    static readonly DateTimeOffset Slept = new(2026, 11, 3, 20, 39, 0, TimeSpan.FromHours(-5));
    static readonly DateTimeOffset Died = new(2026, 11, 3, 21, 24, 57, TimeSpan.FromHours(-5));
    static readonly DateTimeOffset Back = new(2026, 11, 3, 21, 30, 39, TimeSpan.FromHours(-5));

    static CrashFacts Facts(DateTimeOffset? asleep = null, uint bug = 0, DateTimeOffset? button = null, bool standby = false, UsageEntry? power = null, DateTimeOffset? activity = null, FlightSample? reading = null) => new()
    {
        RestartedAt = Back,
        LastAlive = Died,
        LastActivity = activity,
        LastReading = reading,
        AsleepSince = asleep,
        BugcheckCode = bug,
        PowerButtonPressedAt = button,
        ConnectedStandby = standby,
        PowerBefore = power,
    };

    static UsageEntry Row(string type = "Active", bool ac = true, int charge = 53813) => new(Slept, type, ac, charge, 61854);

    [Fact]
    public void Windows_saying_it_went_to_sleep_is_died_asleep()
    {
        var v = CrashClassifier.Classify(Facts(asleep: Slept, power: Row()));
        Assert.Equal(CrashCause.DiedAsleep, v.Cause);
        Assert.Contains("went to sleep", v.Summary);
    }

    [Fact]
    public void Connected_standby_flag_alone_means_asleep() =>
        Assert.Equal(CrashCause.DiedAsleep, CrashClassifier.Classify(Facts(standby: true)).Cause);

    [Fact]
    public void Asleep_and_power_button_pressed_is_would_not_wake()
    {
        var v = CrashClassifier.Classify(Facts(asleep: Slept, button: Died.AddMinutes(8)));
        Assert.Equal(CrashCause.FrozeAsleepForcedOff, v.Cause);
        Assert.Contains("power button was pressed", v.Summary);
        Assert.DoesNotContain("turned off", v.Summary);
    }

    [Fact]
    public void In_use_and_power_button_pressed_is_stopped_responding() =>
        Assert.Equal(CrashCause.FrozeForcedOff, CrashClassifier.Classify(Facts(button: Died.AddMinutes(7))).Cause);

    [Theory]
    [InlineData(0x14Fu)] // PDC_WATCHDOG_TIMEOUT: failed to leave Modern Standby
    [InlineData(0x9Fu)]  // DRIVER_POWER_STATE_FAILURE
    public void Sleep_stop_codes_are_crashes_around_sleep(uint code)
    {
        var v = CrashClassifier.Classify(Facts(bug: code));
        Assert.Equal(CrashCause.SleepTransition, v.Cause);
        Assert.True(CrashScanner.IsAsleepCause(v.Cause));
        Assert.Contains($"0x{code:X}", v.Summary);
    }

    [Fact]
    public void Other_stop_codes_are_blue_screens_with_their_name()
    {
        var v = CrashClassifier.Classify(Facts(bug: 0x116));
        Assert.Equal(CrashCause.BlueScreen, v.Cause);
        Assert.Contains("VIDEO_TDR_FAILURE", v.Title);
    }

    [Fact]
    public void Power_button_hold_bugcheck_is_not_reported_as_a_blue_screen() =>
        Assert.Equal(CrashCause.FrozeForcedOff, CrashClassifier.Classify(Facts(bug: 0x1C8)).Cause);

    [Fact]
    public void Empty_battery_is_battery_ran_out() =>
        Assert.Equal(CrashCause.BatteryRanOut, CrashClassifier.Classify(Facts(power: Row(ac: false, charge: 1500))).Cause);

    [Fact]
    public void Running_then_restarting_within_seconds_is_a_sudden_reset()
    {
        var v = CrashClassifier.Classify(Facts(activity: Back.AddSeconds(-24), reading: new FlightSample { At = Back.AddSeconds(-24), ZoneC = 96, GpuC = 87, GpuBusyPct = 100 }));
        Assert.Equal(CrashCause.SuddenReset, v.Cause);
        Assert.Contains("24 s later", v.Summary);
        Assert.Contains("96°C", v.Summary);
        Assert.Contains("no press of the power button", v.Summary);
    }

    [Fact]
    public void A_long_gap_before_the_restart_is_not_called_sudden()
    {
        var v = CrashClassifier.Classify(Facts(activity: Back.AddMinutes(-9)));
        Assert.Equal(CrashCause.Unknown, v.Cause);
        Assert.DoesNotContain("while in use", v.Summary);
    }

    [Fact]
    public void Only_a_stale_stamp_is_described_as_some_time_after_it() =>
        Assert.Contains("some time after", CrashClassifier.Classify(Facts()).Summary);
}

public class FlightRecorderTests
{
    [Fact]
    public void The_last_line_before_a_crash_survives_a_half_written_one()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lg-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "flight.jsonl");
        try
        {
            var r = new FlightRecorder(path);
            var t = new DateTimeOffset(2026, 11, 4, 14, 48, 50, TimeSpan.FromHours(-5));
            r.Write(new FlightSample { At = t.AddSeconds(-10), GpuC = 80 });
            r.Write(new FlightSample { At = t, GpuC = 87, ZoneC = 96 });
            File.AppendAllText(path, "{\"At\":\"2026-11-04T14:48"); // the reset came mid-write
            var last = r.LastBefore(t.AddSeconds(26), TimeSpan.FromHours(1));
            Assert.Equal(87, last!.GpuC);
            Assert.Null(r.LastBefore(t.AddSeconds(-30), TimeSpan.FromHours(1)));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_reading_says_what_the_processor_was_doing_too()
    {
        var s = new FlightSample { ZoneC = 98, CpuBusyPct = 85, CpuSpeedPct = 61, TopCpuApps = "Game.exe 40%", GpuC = 69, GpuFanRpm = 0 };
        string d = s.Describe();
        Assert.Contains("processor 85% busy at 61% of its top speed", d);
        Assert.Contains("Busiest on the processor: Game.exe 40%", d);
        Assert.Contains("fan at 0 rpm", d);
    }

    [Fact]
    public void The_processor_sampler_reports_after_its_first_call()
    {
        using var cpu = new CpuSampler();
        var first = cpu.Sample();
        Assert.Null(first.Busy);
        Thread.Sleep(300);
        var second = cpu.Sample();
        Assert.NotNull(second.Busy);
        Assert.InRange(second.Busy!.Value, 0, 100);
    }
    [Fact]
    public void It_stays_small()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lg-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "flight.jsonl");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, new byte[FlightRecorder.MaxFileBytes + 10]);
            new FlightRecorder(path).Write(new FlightSample { At = DateTimeOffset.Now });
            Assert.True(new FileInfo(path).Length < 1000);
            Assert.True(File.Exists(path + ".1"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}