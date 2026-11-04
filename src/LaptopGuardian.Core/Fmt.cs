using System.Globalization;

namespace LaptopGuardian.Core;

/// <summary>Human-readable times and sizes, in the user's local time.</summary>
public static class Fmt
{
    static CultureInfo Ui => CultureInfo.CurrentCulture;

    /// <summary>Overridable for tests.</summary>
    public static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.Now;

    public static string Clock(DateTimeOffset t) => t.ToLocalTime().ToString("t", Ui);

    /// <summary>"Today 9:24 PM", "Yesterday 4:40 PM", "Mon 4:40 PM", "Oct 9, 4:40 PM".</summary>
    public static string When(DateTimeOffset t)
    {
        var local = t.ToLocalTime();
        var today = Now().ToLocalTime().Date;
        var day = local.Date;
        string clock = local.ToString("t", Ui);
        if (day == today) return $"Today {clock}";
        if (day == today.AddDays(-1)) return $"Yesterday {clock}";
        if (day > today.AddDays(-7) && day < today) return $"{local.ToString("ddd", Ui)} {clock}";
        if (day.Year == today.Year) return $"{local.ToString("MMM d", Ui)}, {clock}";
        return $"{local.ToString("MMM d, yyyy", Ui)}, {clock}";
    }

    public static string Day(DateTimeOffset t)
    {
        var local = t.ToLocalTime().Date;
        var today = Now().ToLocalTime().Date;
        if (local == today) return "Today";
        if (local == today.AddDays(-1)) return "Yesterday";
        return local.ToString(local.Year == today.Year ? "dddd, MMMM d" : "dddd, MMMM d, yyyy", Ui);
    }

    public static string Span(TimeSpan s)
    {
        if (s.TotalMinutes < 1) return $"{Math.Max(0, (int)s.TotalSeconds)} s";
        if (s.TotalHours < 1) return $"{(int)Math.Round(s.TotalMinutes)} min";
        if (s.TotalDays < 1) return s.Minutes == 0 ? $"{(int)s.TotalHours} h" : $"{(int)s.TotalHours} h {s.Minutes} min";
        return $"{(int)s.TotalDays} d {s.Hours} h";
    }

    public static string Ago(DateTimeOffset t)
    {
        var s = Now() - t;
        if (s.TotalMinutes < 1) return "just now";
        return Span(s) + " ago";
    }

    public static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    public static string Seconds(uint? s)
    {
        if (s == null) return "Unknown";
        if (s == 0) return "Never";
        return Span(TimeSpan.FromSeconds(s.Value));
    }
}
