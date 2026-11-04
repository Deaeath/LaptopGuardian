using System.Globalization;

namespace LaptopGuardian.Core;

/// <summary>Event 6008, "The previous system shutdown at ... was unexpected".</summary>
public static class ShutdownRecord
{
    /// <summary>
    /// The binary data of event 6008 holds two SYSTEMTIMEs: local time, then UTC, of the last moment
    /// Windows recorded itself as running. Reading them avoids the localized date text, which on
    /// some machines is in a different culture from the user's and carries invisible direction marks.
    /// </summary>
    public static DateTimeOffset? LastAliveFromBinary(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        hex = new string(hex.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length < 64) return null;
        byte[] b;
        try { b = Convert.FromHexString(hex); } catch { return null; }

        if (ReadSystemTime(b, 16) is DateTime utc) return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        if (ReadSystemTime(b, 0) is DateTime local) return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local));
        return null;
    }

    static DateTime? ReadSystemTime(byte[] b, int offset)
    {
        if (b.Length < offset + 16) return null;
        int U(int i) => BitConverter.ToUInt16(b, offset + i * 2);
        try { return new DateTime(U(0), U(1), U(3), U(4), U(5), U(6), U(7)); }
        catch { return null; }
    }
}

/// <summary>Kernel-Power event 41: "The system has rebooted without cleanly shutting down first."</summary>
public sealed record KernelPower41(
    long RecordId,
    DateTimeOffset Time,
    uint BugcheckCode,
    int SleepInProgress,
    ulong PowerButtonTimestamp,
    bool LongPowerButtonPress,
    bool ConnectedStandby)
{
    public static KernelPower41 FromData(long recordId, DateTimeOffset time, IReadOnlyDictionary<string, string> d)
    {
        string Get(string k) => d.TryGetValue(k, out var v) ? v.Trim() : "";
        uint bug = 0;
        var b = Get("BugcheckCode");
        if (b.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) StopCodes.TryParseHex(b, out bug);
        else uint.TryParse(b, out bug);
        int.TryParse(Get("SleepInProgress"), out var sleep);
        ulong.TryParse(Get("PowerButtonTimestamp"), out var pb);
        return new KernelPower41(recordId, time, bug, sleep, pb,
            bool.TryParse(Get("LongPowerButtonPressDetected"), out var lp) && lp,
            bool.TryParse(Get("ConnectedStandbyInProgress"), out var cs) && cs);
    }

    public DateTimeOffset? PowerButtonPressedAt
    {
        get
        {
            if (PowerButtonTimestamp == 0) return null;
            try { return DateTimeOffset.FromFileTime((long)PowerButtonTimestamp); } catch { return null; }
        }
    }
}

public sealed record CrashFacts
{
    public DateTimeOffset RestartedAt { get; init; }
    public DateTimeOffset? LastAlive { get; init; }
    public uint BugcheckCode { get; init; }
    public DateTimeOffset? PowerButtonPressedAt { get; init; }
    public bool LongPowerButtonPress { get; init; }
    public bool ConnectedStandby { get; init; }
    public int SleepInProgress { get; init; }
    /// <summary>The battery report's power state when it died (null when the report does not go back that far).</summary>
    public UsageEntry? StateBefore { get; init; }
    /// <summary>The battery report covers this moment, so a missing state really means "in use".</summary>
    public bool PowerHistoryCovers { get; init; }
    public int GraphicsErrorsBefore { get; init; }
}

public sealed record CrashVerdict(CrashCause Cause, string Title, string Summary, string Detail, Severity Severity);

/// <summary>Decides, from what Windows recorded, what most likely happened. Pure, so it is tested directly.</summary>
public static class CrashClassifier
{
    /// <summary>Stop codes raised by a failed sleep or wake: Modern Standby watchdogs and driver power transitions.</summary>
    public static readonly uint[] SleepStopCodes = { 0x14F, 0x15F, 0x9F, 0xA0 };

    public static CrashVerdict Classify(CrashFacts f)
    {
        bool asleep = f.ConnectedStandby || f.StateBefore?.IsAsleep == true;
        // The timestamp records a press of the power button (to wake it, or to force it off); a long press is explicit.
        bool pressed = f.PowerButtonPressedAt != null || f.LongPowerButtonPress || f.BugcheckCode == 0x1C8;
        string when = f.LastAlive is DateTimeOffset la ? $" at {Fmt.Clock(la)}" : "";
        string press = f.PowerButtonPressedAt is DateTimeOffset pb
            ? $" The power button was {(f.LongPowerButtonPress ? "held" : "pressed")} at {Fmt.Clock(pb)}."
            : pressed ? " The power button was held." : "";
        string power = PowerPhrase(f.StateBefore);
        string gfx = f.GraphicsErrorsBefore switch
        {
            0 => "",
            1 => " One graphics error was logged in the hour before.",
            var n => $" {n} graphics errors were logged in the hour before.",
        };

        if (f.BugcheckCode != 0 && f.BugcheckCode != 0x1C8)
        {
            var (name, meaning) = StopCodes.Describe(f.BugcheckCode);
            if (SleepStopCodes.Contains(f.BugcheckCode))
                return new CrashVerdict(CrashCause.SleepTransition, "Crashed waking from sleep",
                    $"Windows stopped with error 0x{f.BugcheckCode:X} ({name}){when}. {meaning}{press}{gfx}",
                    AsleepAdvice, Severity.Critical);
            return new CrashVerdict(CrashCause.BlueScreen, $"Blue screen: {name}",
                $"Windows stopped with error 0x{f.BugcheckCode:X} ({name}){when} and restarted.{gfx}",
                meaning + " Note the time and stop code; if the same code repeats, it points at the driver or part named in it.",
                Severity.Critical);
        }

        if (pressed && asleep)
            return new CrashVerdict(CrashCause.FrozeAsleepForcedOff, "Would not wake from sleep",
                $"It was asleep{SleptPhrase(f)} and stopped responding{when}.{press}{gfx}",
                AsleepAdvice, Severity.Critical);

        if (pressed)
            return new CrashVerdict(CrashCause.FrozeForcedOff, "Stopped responding",
                $"It stopped responding{when}.{press}{power}{gfx}",
                "A freeze with no stop code is usually a driver deadlock, the graphics card hanging, a failed wake from sleep, or overheating. " +
                "If it happens during games, watch the temperature on the Graphics page.",
                Severity.Critical);

        if (f.SleepInProgress != 0)
            return new CrashVerdict(CrashCause.SleepTransition, "Crashed going to sleep or waking",
                $"It died while going to sleep or waking up{when}.{gfx}",
                AsleepAdvice, Severity.Critical);

        if (asleep)
            return new CrashVerdict(CrashCause.DiedAsleep, "Shut down while asleep",
                $"It went to sleep{SleptPhrase(f)} and stopped{AfterPhrase(f)}.{power} No error code was recorded.{gfx}",
                AsleepAdvice, Severity.Critical);

        if (f.StateBefore is { Ac: false, Percent: int pct } && pct <= 5)
            return new CrashVerdict(CrashCause.BatteryRanOut, "Battery ran out",
                $"The battery was at {pct}% and ran out{when}.",
                "Plug in sooner, or turn on hibernation so Windows saves your work before the battery empties.",
                Severity.Warning);

        if (f.StateBefore == null && !f.PowerHistoryCovers)
            return new CrashVerdict(CrashCause.Unknown, "Froze or lost power",
                $"It stopped{when} without an error code.{gfx} This is older than Windows' battery report, so whether it was asleep or in use is not recorded.",
                "No stop code usually means a hard freeze, a failed wake from sleep, a power cut, or a thermal shutdown. If you remember what you saw, note it below; it narrows this down.",
                Severity.Critical);

        return new CrashVerdict(CrashCause.Unknown, "Froze or lost power",
            $"It froze or lost power while in use{when}.{power} Windows recorded no error code.{gfx}",
            "No stop code usually means a hard freeze, a power cut, or a thermal shutdown. If you remember what you saw, note it below; it narrows this down.",
            Severity.Critical);
    }

    const string AsleepAdvice =
        "The laptop was in Modern Standby (Windows' sleep) and never came back: it hung while asleep or while waking, " +
        "and the next thing it did was a cold start. On gaming laptops this is most often the BIOS or the graphics driver failing a sleep or wake transition. " +
        "Hibernate does not depend on waking from standby, so it is the safer choice until a BIOS or driver update fixes it.";

    static string SleptPhrase(CrashFacts f) =>
        f.StateBefore is { IsAsleep: true } s ? $" at {Fmt.Clock(s.Time)}" : "";

    static string AfterPhrase(CrashFacts f) =>
        f.StateBefore is { IsAsleep: true } s && f.LastAlive is DateTimeOffset la && la > s.Time
            ? $" {Fmt.Span(la - s.Time)} later, at {Fmt.Clock(la)}"
            : f.LastAlive is DateTimeOffset la2 ? $" at {Fmt.Clock(la2)}" : "";

    static string PowerPhrase(UsageEntry? s)
    {
        if (s == null) return "";
        string pct = s.Percent is int p ? $" with the battery at {p}%" : "";
        return s.Ac ? $" It was plugged in{pct}." : $" It was on battery{pct}.";
    }
}

/// <summary>Finds unexpected shutdowns in the System log and explains each one.</summary>
public static class CrashScanner
{
    public sealed record Found(KernelPower41 Event, CrashFacts Facts, CrashVerdict Verdict);

    public static List<Found> Scan(TimeSpan window, BatteryReport? battery)
    {
        var kp = EventLogX.Query("System",
            $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and {EventLogX.Since(window)}]]",
            r => KernelPower41.FromData(r.RecordId ?? 0, EventLogX.Time(r), EventLogX.NamedData(r)));
        if (kp.Count == 0) return new();

        var lastAlive = EventLogX.Query("System",
            $"*[System[Provider[@Name='EventLog'] and EventID=6008 and {EventLogX.Since(window + TimeSpan.FromDays(1))}]]",
            r => new Tuple<DateTimeOffset, DateTimeOffset?>(EventLogX.Time(r), ShutdownRecord.LastAliveFromBinary(EventLogX.Binary(r))));

        var graphics = EventLogX.Query("System",
            $"*[System[Provider[{string.Join(" or ", GraphicsEvents.GraphicsProviders.Select(p => $"@Name='{p}'"))}] and (Level=1 or Level=2 or Level=3) and {EventLogX.Since(window + TimeSpan.FromDays(1))}]]",
            r => new Tuple<DateTimeOffset>(EventLogX.Time(r)));

        var found = new List<Found>();
        foreach (var e in kp)
        {
            // 6008 is written by the event log service a few seconds after the 41 of the same boot.
            var match = lastAlive
                .Where(x => x.Item1 >= e.Time.AddMinutes(-2) && x.Item1 <= e.Time.AddMinutes(10))
                .OrderBy(x => Math.Abs((x.Item1 - e.Time).TotalSeconds))
                .FirstOrDefault();
            DateTimeOffset? alive = match?.Item2;
            var probe = alive ?? e.Time.AddMinutes(-1);
            int gfx = graphics.Count(g => g.Item1 <= probe && g.Item1 >= probe.AddHours(-1));

            var facts = new CrashFacts
            {
                RestartedAt = e.Time,
                LastAlive = alive,
                BugcheckCode = e.BugcheckCode,
                PowerButtonPressedAt = e.PowerButtonPressedAt,
                LongPowerButtonPress = e.LongPowerButtonPress,
                ConnectedStandby = e.ConnectedStandby,
                SleepInProgress = e.SleepInProgress,
                StateBefore = battery?.StateAt(probe),
                PowerHistoryCovers = battery is { Usage.Count: > 0 } && battery.Usage[0].Time <= probe,
                GraphicsErrorsBefore = gfx,
            };
            found.Add(new Found(e, facts, CrashClassifier.Classify(facts)));
        }
        return found;
    }

    public static Incident ToIncident(Found f) => new()
    {
        Id = "crash:" + f.Event.Time.UtcTicks.ToString(CultureInfo.InvariantCulture),
        Kind = IncidentKind.UnexpectedShutdown,
        Time = f.Facts.LastAlive ?? f.Event.Time,
        Severity = f.Verdict.Severity,
        Title = f.Verdict.Title,
        Summary = f.Verdict.Summary,
        Detail = f.Verdict.Detail,
        Source = "Kernel-Power event 41" + (f.Facts.LastAlive != null ? " + EventLog 6008" : ""),
        Crash = new CrashInfo
        {
            Cause = f.Verdict.Cause,
            LastAlive = f.Facts.LastAlive,
            RestartedAt = f.Event.Time,
            AsleepSince = f.Facts.StateBefore is { IsAsleep: true } s ? s.Time : null,
            OnAc = f.Facts.StateBefore?.Ac,
            BatteryPercent = f.Facts.StateBefore?.Percent,
            BugcheckCode = f.Event.BugcheckCode,
            BugcheckName = f.Event.BugcheckCode != 0 ? StopCodes.Describe(f.Event.BugcheckCode).Name : null,
            PowerButtonPressedAt = f.Event.PowerButtonPressedAt,
            ConnectedStandby = f.Event.ConnectedStandby,
            SleepInProgress = f.Event.SleepInProgress,
            GraphicsErrorsBefore = f.Facts.GraphicsErrorsBefore,
        },
    };

    public static bool IsAsleepCause(CrashCause c) =>
        c is CrashCause.DiedAsleep or CrashCause.FrozeAsleepForcedOff or CrashCause.SleepTransition;
}
