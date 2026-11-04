using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;

namespace LaptopGuardian.Core;

/// <summary>Small helpers over the modern event log API (EvtQuery), which reads by record and never replays.</summary>
public static class EventLogX
{
    /// <summary>XPath clause selecting events newer than <paramref name="span"/>.</summary>
    public static string Since(TimeSpan span) => $"TimeCreated[timediff(@SystemTime) <= {(long)span.TotalMilliseconds}]";

    public static List<T> Query<T>(string log, string xpath, Func<EventRecord, T?> map, int max = 20000, bool newestFirst = true)
        where T : class
    {
        var list = new List<T>();
        try
        {
            var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = newestFirst };
            using var reader = new EventLogReader(query);
            while (list.Count < max)
            {
                using var record = reader.ReadEvent();
                if (record == null) break;
                try
                {
                    var value = map(record);
                    if (value != null) list.Add(value);
                }
                catch (EventLogException) { }
            }
        }
        catch (EventLogNotFoundException) { }
        catch (UnauthorizedAccessException) { }
        catch (EventLogException) { }
        return list;
    }

    public static DateTimeOffset Time(EventRecord r) =>
        r.TimeCreated is DateTime t
            ? new DateTimeOffset(t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Local) : t)
            : DateTimeOffset.MinValue;

    public static IReadOnlyList<string> Props(EventRecord r)
    {
        try { return r.Properties.Select(p => p.Value?.ToString() ?? "").ToList(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Named &lt;Data&gt; values from the event's XML (manifest-based providers such as Kernel-Power).</summary>
    public static Dictionary<string, string> NamedData(EventRecord r) => NamedData(SafeXml(r));

    public static Dictionary<string, string> NamedData(string? xml)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(xml)) return d;
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var e in doc.Descendants().Where(e => e.Name.LocalName == "Data"))
            {
                var name = (string?)e.Attribute("Name");
                if (!string.IsNullOrEmpty(name) && !d.ContainsKey(name)) d[name] = e.Value;
            }
        }
        catch { }
        return d;
    }

    /// <summary>The hex &lt;Binary&gt; payload of a classic event, if any.</summary>
    public static string? Binary(EventRecord r) => Binary(SafeXml(r));

    public static string? Binary(string? xml)
    {
        if (string.IsNullOrEmpty(xml)) return null;
        try
        {
            return XDocument.Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "Binary")?.Value;
        }
        catch { return null; }
    }

    static string? SafeXml(EventRecord r)
    {
        try { return r.ToXml(); } catch { return null; }
    }
}
