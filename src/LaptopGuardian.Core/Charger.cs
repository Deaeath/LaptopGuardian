using System.Diagnostics.Eventing.Reader;
using System.Globalization;

namespace LaptopGuardian.Core;

/// <summary>Kernel-Power event 105: the laptop switched between the charger and the battery.</summary>
public sealed record PowerSourceChange(DateTimeOffset Time, bool OnCharger);

/// <summary>Charger disconnects that came close together.</summary>
public sealed record ChargerEpisode(DateTimeOffset Start, DateTimeOffset End, int Disconnects, int QuickDrops)
{
    /// <summary>More drops than a person unplugging and plugging it back in would cause.</summary>
    public bool Unstable => QuickDrops >= 2 || Disconnects >= 3;

    /// <summary>Worth recording: unstable, or a single drop too short for a person to have unplugged it.</summary>
    public bool Notable => Unstable || QuickDrops >= 1;
}

/// <summary>
/// Finds a charger that keeps cutting out. On 15 November 2026 the plug dropped out 5 times in 9 minutes and the
/// laptop switched itself off 20 minutes later; on 4 November it dropped 6 times in 3 minutes, 3 minutes before
/// another sudden reset. Windows logs each switch (event 105) and nothing else says a word about it.
/// </summary>
public static class Charger
{
    /// <summary>Off and back on faster than this was not someone unplugging it.</summary>
    public static readonly TimeSpan QuickDrop = TimeSpan.FromSeconds(10);
    /// <summary>Disconnects closer together than this are one episode.</summary>
    public static readonly TimeSpan EpisodeGap = TimeSpan.FromMinutes(10);
    /// <summary>A crash within this long after the charger cut out is counted against it.</summary>
    public static readonly TimeSpan CrashWindow = TimeSpan.FromHours(1);

    public static string XPath(TimeSpan? since = null) =>
        "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=105" +
        (since is TimeSpan s ? " and " + EventLogX.Since(s) : "") + "]]";

    public static PowerSourceChange? FromRecord(EventRecord r)
    {
        var d = EventLogX.NamedData(r);
        if (!d.TryGetValue("AcOnline", out var v) || !bool.TryParse(v.Trim(), out bool on)) return null;
        return new PowerSourceChange(EventLogX.Time(r), on);
    }

    /// <summary>Every switch Windows logged in the window, oldest first.</summary>
    public static List<PowerSourceChange> Read(TimeSpan window) =>
        EventLogX.Query("System", XPath(window), FromRecord, newestFirst: false).OrderBy(c => c.Time).ToList();

    /// <summary>
    /// Real switches only. Windows sometimes logs the same state twice in a row (around restarts, for one),
    /// and a repeat is not a disconnect.
    /// </summary>
    public static List<PowerSourceChange> Transitions(IEnumerable<PowerSourceChange> changes)
    {
        var list = new List<PowerSourceChange>();
        foreach (var c in changes.OrderBy(c => c.Time))
            if (list.Count == 0 || list[^1].OnCharger != c.OnCharger) list.Add(c);
        return list;
    }

    /// <summary>Groups disconnects that came within <see cref="EpisodeGap"/> of each other.</summary>
    public static List<ChargerEpisode> Episodes(IEnumerable<PowerSourceChange> changes)
    {
        var t = Transitions(changes);
        var episodes = new List<ChargerEpisode>();
        DateTimeOffset start = default, end = default;
        int disconnects = 0, quick = 0;

        void Close()
        {
            if (disconnects > 0) episodes.Add(new ChargerEpisode(start, end, disconnects, quick));
            disconnects = quick = 0;
        }

        for (int i = 0; i < t.Count; i++)
        {
            if (t[i].OnCharger) continue;
            var off = t[i].Time;
            DateTimeOffset? back = i + 1 < t.Count ? t[i + 1].Time : null;
            if (disconnects > 0 && off - end > EpisodeGap) Close();
            if (disconnects == 0) start = off;
            disconnects++;
            if (back is DateTimeOffset b && b - off <= QuickDrop) quick++;
            end = back ?? off;
        }
        Close();
        return episodes;
    }

    /// <summary>Disconnects in the <paramref name="within"/> before <paramref name="probe"/>.</summary>
    public static int DisconnectsBefore(IEnumerable<PowerSourceChange> changes, DateTimeOffset probe, TimeSpan within) =>
        Transitions(changes).Count(c => !c.OnCharger && c.Time <= probe && c.Time >= probe - within);

    public static string IdFor(DateTimeOffset start) => "charger:" + start.UtcTicks.ToString(CultureInfo.InvariantCulture);

    public static Incident ToIncident(ChargerEpisode e)
    {
        string title, summary;
        if (e.Unstable)
        {
            title = "Charger kept cutting out";
            string how = e.QuickDrops == e.Disconnects ? ", each time for only a few seconds"
                : e.QuickDrops > 0 ? $", {e.QuickDrops} of them for only a few seconds" : "";
            summary = $"The laptop lost power from the charger {e.Disconnects} times between {Fmt.Clock(e.Start)} and {Fmt.Clock(e.End)}{how}. " +
                      "That many drops so close together points to a loose plug, a damaged cable or a failing power brick.";
        }
        else
        {
            title = "Charger cut out for a moment";
            summary = $"The charger disconnected at {Fmt.Clock(e.Start)} and came back {Fmt.Span(e.End - e.Start)} later. " +
                      "A drop that short is usually the plug or the cable, not someone unplugging it.";
        }
        return new Incident
        {
            Id = IdFor(e.Start),
            Kind = IncidentKind.ChargerUnstable,
            Time = e.Start,
            Severity = e.Unstable ? Severity.Warning : Severity.Info,
            Title = title,
            Summary = summary,
            Detail = Advice,
            Source = "Kernel-Power event 105",
            Charger = new ChargerInfo { Start = e.Start, End = e.End, Disconnects = e.Disconnects, QuickDrops = e.QuickDrops },
        };
    }

    public const string Advice =
        "Push the plug fully into the laptop, then wiggle it gently while watching the battery icon: if the icon flickers, the cable or the laptop's power socket is worn. " +
        "Plug straight into a wall socket rather than a power strip, and use the charger that came with the laptop. " +
        "A charger that drops out while the laptop is working hard can switch it off, especially with a worn battery.";
}
