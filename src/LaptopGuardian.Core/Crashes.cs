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
    /// <summary>Windows' stamp in event 6008. It is written only now and then: on 4 November 2026 it was 19 minutes early.</summary>
    public DateTimeOffset? LastAlive { get; init; }
    /// <summary>The last thing known to have happened before the restart.</summary>
    public DateTimeOffset? LastActivity { get; init; }
    /// <summary>What the flight recorder last saw, if it was running.</summary>
    public FlightSample? LastReading { get; init; }
    /// <summary>When Windows' own events say it went to sleep and did not come back. Null means it was not asleep.</summary>
    public DateTimeOffset? AsleepSince { get; init; }
    public uint BugcheckCode { get; init; }
    public DateTimeOffset? PowerButtonPressedAt { get; init; }
    public bool LongPowerButtonPress { get; init; }
    public bool ConnectedStandby { get; init; }
    public int SleepInProgress { get; init; }
    /// <summary>
    /// The battery report's row at that moment. Only its plug state and charge mean anything here. Its "Suspend"
    /// rows are not sleep: they are gaps in the record around every restart, clean ones included.
    /// </summary>
    public UsageEntry? PowerBefore { get; init; }
    public int GraphicsErrorsBefore { get; init; }
}

public sealed record CrashVerdict(CrashCause Cause, string Title, string Summary, string Detail, Severity Severity);

/// <summary>Decides, from what Windows recorded, what most likely happened. Pure, so it is tested directly.</summary>
public static class CrashClassifier
{
    /// <summary>Raise this when the reasoning changes, so existing entries are worked out again.</summary>
    public const int Version = 2;

    /// <summary>Stop codes raised by a failed sleep or wake: Modern Standby watchdogs and driver power transitions.</summary>
    public static readonly uint[] SleepStopCodes = { 0x14F, 0x15F, 0x9F, 0xA0 };

    /// <summary>Firmware start-up alone takes most of this, so no one had time to hold a power button.</summary>
    public static readonly TimeSpan SuddenResetWithin = TimeSpan.FromSeconds(90);

    public static CrashVerdict Classify(CrashFacts f)
    {
        bool asleep = f.ConnectedStandby || f.AsleepSince != null;
        // The timestamp records a press of the power button (to wake it, or to force it off); a long press is explicit.
        bool pressed = f.PowerButtonPressedAt != null || f.LongPowerButtonPress || f.BugcheckCode == 0x1C8;
        string when = f.LastActivity is DateTimeOffset a ? $" at about {Fmt.Clock(a)}"
            : f.LastAlive is DateTimeOffset la ? $" some time after {Fmt.Clock(la)}" : "";
        string press = f.PowerButtonPressedAt is DateTimeOffset pb
            ? $" The power button was {(f.LongPowerButtonPress ? "held" : "pressed")} at {Fmt.Clock(pb)}."
            : pressed ? " The power button was held." : "";
        string power = f.PowerBefore == null ? "" : f.PowerBefore.Ac ? " It was plugged in." : " It was on battery.";
        string reading = f.LastReading != null ? $" Last reading, {Fmt.Clock(f.LastReading.At)}: {f.LastReading.Describe()}" : "";
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
                return new CrashVerdict(CrashCause.SleepTransition, "Crashed going to sleep or waking",
                    $"Windows stopped with error 0x{f.BugcheckCode:X} ({name}){when}. {meaning}{press}{gfx}",
                    AsleepAdvice, Severity.Critical);
            return new CrashVerdict(CrashCause.BlueScreen, $"Blue screen: {name}",
                $"Windows stopped with error 0x{f.BugcheckCode:X} ({name}){when} and restarted.{gfx}",
                meaning + " Note the time and stop code; if the same code repeats, it points at the driver or part named in it.",
                Severity.Critical);
        }

        if (pressed && asleep)
            return new CrashVerdict(CrashCause.FrozeAsleepForcedOff, "Would not wake from sleep",
                $"It was asleep{Since(f)} and stopped responding{when}.{press}{gfx}",
                AsleepAdvice, Severity.Critical);

        if (pressed)
            return new CrashVerdict(CrashCause.FrozeForcedOff, "Stopped responding",
                $"It stopped responding{when}.{press}{power}{reading}{gfx}",
                "A freeze with no stop code is usually a driver deadlock, the graphics card hanging, or overheating. " +
                "If it happens during games, watch the temperature on the Graphics page.",
                Severity.Critical);

        if (f.SleepInProgress != 0)
            return new CrashVerdict(CrashCause.SleepTransition, "Crashed going to sleep or waking",
                $"It died while going to sleep or waking up{when}.{gfx}",
                AsleepAdvice, Severity.Critical);

        if (asleep)
            return new CrashVerdict(CrashCause.DiedAsleep, "Shut down while asleep",
                $"It went to sleep{Since(f)} and stopped{when}.{power} No error code was recorded.{gfx}",
                AsleepAdvice, Severity.Critical);

        if (f.PowerBefore is { Ac: false, Percent: int pct } && pct <= 5)
            return new CrashVerdict(CrashCause.BatteryRanOut, "Battery ran out",
                $"The battery was at about {pct}% and ran out{when}.",
                "Plug in sooner, or turn on hibernation so Windows saves your work before the battery empties.",
                Severity.Warning);

        if (f.LastActivity is DateTimeOffset act && f.RestartedAt - act <= SuddenResetWithin)
            return new CrashVerdict(CrashCause.SuddenReset, "Reset itself without warning",
                $"It was running at about {Fmt.Clock(act)} and starting up again {Fmt.Span(f.RestartedAt - act)} later. Windows recorded no error and no press of the power button.{power}{reading}{gfx}",
                ResetAdvice, Severity.Critical);

        return new CrashVerdict(CrashCause.Unknown, "Stopped without warning",
            $"It stopped{when} and Windows recorded no error.{power}{reading}{gfx}",
            "No error usually means a hard freeze, a power cut, or a reset from below Windows such as heat protection. If you remember what you saw, note it below; it narrows this down.",
            Severity.Critical);
    }

    const string AsleepAdvice =
        "The laptop was asleep and never came back: it hung while asleep or while waking, and the next thing it did was a cold start. " +
        "The usual suspects are the BIOS and drivers, especially the graphics driver, failing a sleep or wake transition. " +
        "Hibernating does not depend on waking from sleep, so it is the safer choice until a BIOS or driver update fixes it.";

    const string ResetAdvice =
        "A restart this fast, with no error, means Windows never got the chance to report anything. A driver crash normally leaves a stop code, " +
        "so this points to something below Windows: heat protection, the power system, memory, or a fault in the processor or graphics card. " +
        "It does not prove which. See whether it happens only under heavy load, such as a game.";

    static string Since(CrashFacts f) => f.AsleepSince is DateTimeOffset s ? $" at {Fmt.Clock(s)}" : "";
}

/// <summary>Finds unexpected shutdowns in the System log and explains each one.</summary>
public static class CrashScanner
{
    public sealed record Found(KernelPower41 Event, CrashFacts Facts, CrashVerdict Verdict);

    /// <summary>Logs that Windows writes to often, used to find the last moment anything happened.</summary>
    static readonly string[] BusyLogs =
    {
        "System", "Application",
        "Microsoft-Windows-Diagnosis-PCW/Operational",
        "Microsoft-Windows-WMI-Activity/Operational",
        "Microsoft-Windows-Security-Mitigations/KernelMode",
        "Microsoft-Windows-PowerShell/Operational",
        "Microsoft-Windows-PushNotification-Platform/Operational",
        "Microsoft-Windows-Kernel-PnP/Configuration",
        "Microsoft-Windows-StateRepository/Operational",
    };

    /// <param name="needsWork">Whether this restart time still needs working out. Finished entries are left as they are.</param>
    public static List<Found> Scan(TimeSpan window, BatteryReport? battery, FlightRecorder? recorder = null, Func<DateTimeOffset, bool>? needsWork = null)
    {
        var kp = EventLogX.Query("System",
            $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and {EventLogX.Since(window)}]]",
            r => KernelPower41.FromData(r.RecordId ?? 0, EventLogX.Time(r), EventLogX.NamedData(r)));
        if (needsWork != null) kp = kp.Where(e => needsWork(e.Time)).ToList();
        if (kp.Count == 0) return new();

        var span = window + TimeSpan.FromDays(1);
        var lastAlive = EventLogX.Query("System",
            $"*[System[Provider[@Name='EventLog'] and EventID=6008 and {EventLogX.Since(span)}]]",
            r => new Tuple<DateTimeOffset, DateTimeOffset?>(EventLogX.Time(r), ShutdownRecord.LastAliveFromBinary(EventLogX.Binary(r))));

        var graphics = EventLogX.Query("System",
            $"*[System[Provider[{string.Join(" or ", GraphicsEvents.GraphicsProviders.Select(p => $"@Name='{p}'"))}] and (Level=1 or Level=2 or Level=3) and {EventLogX.Since(span)}]]",
            r => new Tuple<DateTimeOffset>(EventLogX.Time(r)));

        // Every start of Windows, so each crash is looked at only within its own session.
        var boots = EventLogX.Query("System",
            $"*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12 and {EventLogX.Since(span)}]]",
            r => new Tuple<DateTimeOffset>(EventLogX.Time(r))).Select(b => b.Item1).OrderBy(b => b).ToList();

        // Windows' real sleep and wake events.
        var sleep = EventLogX.Query("System",
            $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power' or @Name='Microsoft-Windows-Power-Troubleshooter'] and (EventID=42 or EventID=506 or EventID=107 or EventID=507 or EventID=1) and {EventLogX.Since(span)}]]",
            r => new Tuple<DateTimeOffset, bool>(EventLogX.Time(r), r.Id is 42 or 506));

        var found = new List<Found>();
        foreach (var e in kp)
        {
            // 6008 is written by the event log service a few seconds after the 41 of the same boot.
            var match = lastAlive
                .Where(x => x.Item1 >= e.Time.AddMinutes(-2) && x.Item1 <= e.Time.AddMinutes(10))
                .OrderBy(x => Math.Abs((x.Item1 - e.Time).TotalSeconds))
                .FirstOrDefault();
            DateTimeOffset? alive = match?.Item2;

            var bootsBefore = boots.Where(b => b <= e.Time).ToList();
            var thisBoot = bootsBefore.Count > 0 ? bootsBefore[^1] : e.Time.AddSeconds(-5);
            DateTimeOffset? previousBoot = bootsBefore.Count > 1 ? bootsBefore[^2] : null;

            var reading = recorder?.LastBefore(thisBoot, TimeSpan.FromHours(6));
            if (reading != null && previousBoot is DateTimeOffset pb && reading.At < pb) reading = null;

            DateTimeOffset? activity = NewestOf(reading?.At, NewestEntryBefore(thisBoot, previousBoot), alive);
            var probe = activity ?? e.Time.AddMinutes(-1);
            int gfx = graphics.Count(g => g.Item1 <= probe && g.Item1 >= probe.AddHours(-1));

            var facts = new CrashFacts
            {
                RestartedAt = e.Time,
                LastAlive = alive,
                LastActivity = activity != null && activity == alive ? null : activity,
                LastReading = reading,
                AsleepSince = reading?.State == "suspending" ? reading.At : AsleepSince(sleep, thisBoot, previousBoot),
                BugcheckCode = e.BugcheckCode,
                PowerButtonPressedAt = e.PowerButtonPressedAt,
                LongPowerButtonPress = e.LongPowerButtonPress,
                ConnectedStandby = e.ConnectedStandby,
                SleepInProgress = e.SleepInProgress,
                PowerBefore = battery?.StateAt(probe),
                GraphicsErrorsBefore = gfx,
            };
            found.Add(new Found(e, facts, CrashClassifier.Classify(facts)));
        }
        return found;
    }

    /// <summary>Asleep only if the last sleep event of that session was going in, with no coming back after it.</summary>
    internal static DateTimeOffset? AsleepSince(IEnumerable<Tuple<DateTimeOffset, bool>> sleepEvents, DateTimeOffset boot, DateTimeOffset? previousBoot)
    {
        var last = sleepEvents.Where(s => s.Item1 < boot && (previousBoot == null || s.Item1 > previousBoot)).OrderBy(s => s.Item1).LastOrDefault();
        return last is { Item2: true } ? last.Item1 : null;
    }

    static DateTimeOffset? NewestOf(params DateTimeOffset?[] times) => times.Where(t => t != null).OrderBy(t => t).LastOrDefault();

    static DateTimeOffset? NewestEntryBefore(DateTimeOffset boot, DateTimeOffset? previousBoot)
    {
        string utc = boot.UtcDateTime.AddSeconds(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        DateTimeOffset? newest = null;
        foreach (var log in BusyLogs)
        {
            var hit = EventLogX.Query(log, $"*[System[TimeCreated[@SystemTime<='{utc}']]]", r => new Tuple<DateTimeOffset>(EventLogX.Time(r)), max: 1).FirstOrDefault();
            if (hit == null) continue;
            if (previousBoot is DateTimeOffset p && hit.Item1 <= p) continue;
            if (newest == null || hit.Item1 > newest) newest = hit.Item1;
        }
        return newest;
    }

    public static Incident ToIncident(Found f) => new()
    {
        Id = "crash:" + f.Event.Time.UtcTicks.ToString(CultureInfo.InvariantCulture),
        Kind = IncidentKind.UnexpectedShutdown,
        Time = f.Facts.LastActivity ?? f.Facts.LastAlive ?? f.Event.Time,
        Severity = f.Verdict.Severity,
        Title = f.Verdict.Title,
        Summary = f.Verdict.Summary,
        Detail = f.Verdict.Detail,
        Source = "Kernel-Power event 41" + (f.Facts.LastAlive != null ? " + EventLog 6008" : ""),
        Crash = new CrashInfo
        {
            Cause = f.Verdict.Cause,
            ClassifierVersion = CrashClassifier.Version,
            LastAlive = f.Facts.LastAlive,
            LastActivity = f.Facts.LastActivity,
            LastReading = f.Facts.LastReading,
            RestartedAt = f.Event.Time,
            AsleepSince = f.Facts.AsleepSince,
            OnAc = f.Facts.PowerBefore?.Ac,
            BatteryPercent = f.Facts.PowerBefore?.Percent,
            BugcheckCode = f.Event.BugcheckCode,
            BugcheckName = f.Event.BugcheckCode != 0 ? StopCodes.Describe(f.Event.BugcheckCode).Name : null,
            PowerButtonPressedAt = f.Event.PowerButtonPressedAt,
            ConnectedStandby = f.Event.ConnectedStandby,
            SleepInProgress = f.Event.SleepInProgress,
            GraphicsErrorsBefore = f.Facts.GraphicsErrorsBefore,
        },
    };

    public static string IdFor(DateTimeOffset restart) => "crash:" + restart.UtcTicks.ToString(CultureInfo.InvariantCulture);

    public static bool IsAsleepCause(CrashCause c) =>
        c is CrashCause.DiedAsleep or CrashCause.FrozeAsleepForcedOff or CrashCause.SleepTransition;
}
