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

    static CrashFacts Facts(UsageEntry? state = null, uint bug = 0, DateTimeOffset? button = null, bool standby = false, bool covered = true) => new()
    {
        RestartedAt = Back,
        LastAlive = Died,
        BugcheckCode = bug,
        PowerButtonPressedAt = button,
        ConnectedStandby = standby,
        StateBefore = state,
        PowerHistoryCovers = covered,
    };

    static UsageEntry Asleep(bool ac = true) => new(Slept, "Suspend", ac, 53813, 61854);
    static UsageEntry Active(bool ac = true, int charge = 53813) => new(Slept, "Active", ac, charge, 61854);

    [Fact]
    public void Plugged_in_and_asleep_with_no_code_is_died_asleep()
    {
        // The crash of 3 November 2026: asleep at 8:39 PM, last alive at 9:24 PM, plugged in at 87%.
        var v = CrashClassifier.Classify(Facts(Asleep()));
        Assert.Equal(CrashCause.DiedAsleep, v.Cause);
        Assert.Contains("46 min later", v.Summary);
        Assert.Contains("plugged in with the battery at 87%", v.Summary);
        Assert.Contains("No error code", v.Summary);
    }

    [Fact]
    public void Connected_standby_flag_alone_means_asleep() =>
        Assert.Equal(CrashCause.DiedAsleep, CrashClassifier.Classify(Facts(standby: true)).Cause);

    [Fact]
    public void Asleep_and_power_button_pressed_is_would_not_wake()
    {
        var v = CrashClassifier.Classify(Facts(Asleep(), button: Died.AddMinutes(8)));
        Assert.Equal(CrashCause.FrozeAsleepForcedOff, v.Cause);
        Assert.Contains("power button was pressed", v.Summary);
        Assert.DoesNotContain("turned off", v.Summary);
    }

    [Fact]
    public void In_use_and_power_button_pressed_is_stopped_responding() =>
        Assert.Equal(CrashCause.FrozeForcedOff, CrashClassifier.Classify(Facts(Active(), button: Died.AddMinutes(7))).Cause);

    [Theory]
    [InlineData(0x14Fu)] // PDC_WATCHDOG_TIMEOUT: failed to leave Modern Standby
    [InlineData(0x9Fu)]  // DRIVER_POWER_STATE_FAILURE
    public void Sleep_stop_codes_are_crashes_waking_from_sleep(uint code)
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
        Assert.Equal(CrashCause.FrozeForcedOff, CrashClassifier.Classify(Facts(Active(), bug: 0x1C8)).Cause);

    [Fact]
    public void Empty_battery_is_battery_ran_out() =>
        Assert.Equal(CrashCause.BatteryRanOut, CrashClassifier.Classify(Facts(Active(ac: false, charge: 1500))).Cause);

    [Fact]
    public void Older_than_the_battery_report_does_not_claim_it_was_in_use()
    {
        var v = CrashClassifier.Classify(Facts(state: null, covered: false));
        Assert.Equal(CrashCause.Unknown, v.Cause);
        Assert.DoesNotContain("in use at", v.Summary);
        Assert.Contains("not recorded", v.Summary);
    }

    [Fact]
    public void Covered_and_active_is_froze_while_in_use() =>
        Assert.Contains("while in use", CrashClassifier.Classify(Facts(Active())).Summary);
}
