using System.Text.Json;
using System.Text.Json.Serialization;

namespace LaptopGuardian.Core;

public sealed class AppSettings
{
    // Field names "Enabled" and "ThresholdMs" are the ones version 1 wrote, so its settings carry over.
    [JsonPropertyName("Enabled")] public bool KeyboardFilter { get; set; } = true;
    [JsonPropertyName("ThresholdMs")] public int KeyboardThresholdMs { get; set; } = 50;
    public bool NotifyGraphics { get; set; } = true;
    public bool NotifyCrashes { get; set; } = true;
    public bool NotifyHeat { get; set; } = true;
    public bool NotifyMemory { get; set; } = true;
    public bool NotifyBattery { get; set; } = true;
    public bool NotifyHardware { get; set; } = true;
    public bool NotifyCharger { get; set; } = true;
    /// <summary>Hold the busiest apps to a share of the processor when the laptop gets too hot.</summary>
    public bool HeatGovernor { get; set; }
    public int GovernorCapAtC { get; set; } = 88;
    public int GovernorCapPercent { get; set; } = 40;
    /// <summary>Per-app processor caps the person set (like 3dxGC's RAM caps): an app's own cap, always or only when hot, or never.</summary>
    public List<AppCapRule> AppCaps { get; set; } = new();
    public bool NotifyDriverUpdates { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;
}

/// <summary>One app's processor cap.</summary>
public sealed class AppCapRule
{
    /// <summary>Process name without ".exe", e.g. "chrome".</summary>
    public string App { get; set; } = "";
    /// <summary>Share of the whole processor it may use when capped. Null: the governor's own cap.</summary>
    public int? CapPercent { get; set; }
    /// <summary>Capped all the time, not only when the laptop is hot.</summary>
    public bool Always { get; set; }
    /// <summary>Never capped, however hot it gets.</summary>
    public bool Never { get; set; }

    public static string Normalize(string name) =>
        (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name).Trim();
}

public sealed class AppState
{
    public bool HistoryImported { get; set; }
    public DateTimeOffset? LastScan { get; set; }
    public DateTimeOffset? LastDriverCheck { get; set; }
    /// <summary>Version 2.2 and earlier: suggestions hidden for good. Moved into <see cref="HiddenSuggestions"/> on load.</summary>
    public List<string> DismissedRecommendations { get; set; } = new();
    /// <summary>Suggestions the person hid, by id, with when and what they said, so they can come back when things change.</summary>
    public Dictionary<string, HiddenSuggestion> HiddenSuggestions { get; set; } = new();
    /// <summary>Driver updates already announced, so each one is announced once.</summary>
    public List<string> AnnouncedDriverUpdates { get; set; } = new();
    /// <summary>When the stuck crash reports were last cleared; older log entries of them no longer count.</summary>
    public DateTimeOffset? ReportsClearedAt { get; set; }
    /// <summary>Processor boost before LaptopGuardian turned it off, to put back when it is turned on again.</summary>
    public uint? BoostBeforeAc { get; set; }
    public uint? BoostBeforeDc { get; set; }

    /// <summary>Moves the old hidden-for-good list over, as hidden now. Returns true if anything moved.</summary>
    public bool MigrateHidden(DateTimeOffset now)
    {
        if (DismissedRecommendations.Count == 0) return false;
        foreach (var id in DismissedRecommendations)
            HiddenSuggestions.TryAdd(id, new HiddenSuggestion { At = now });
        DismissedRecommendations.Clear();
        return true;
    }
}

/// <summary>Reads and writes small JSON files atomically (write, then rename), so a crash never leaves half a file.</summary>
public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Load<T>(string? path) where T : new()
    {
        if (path == null) return new T();
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch { TryQuarantine(path); }
        return new T();
    }

    public static void Save<T>(string? path, T value)
    {
        if (path == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }

    static void TryQuarantine(string path)
    {
        try { File.Move(path, path + $".broken-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true); } catch { }
    }
}

/// <summary>Everything that has happened, newest first, de-duplicated by <see cref="Incident.Id"/>.</summary>
public sealed class Journal
{
    readonly string? _path;
    readonly object _gate = new();
    readonly List<Incident> _items;
    public const int MaxItems = 3000;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(365);

    public event Action? Changed;

    /// <param name="path">Null keeps the journal in memory only (used by --diagnose).</param>
    public Journal(string? path)
    {
        _path = path;
        _items = JsonFile.Load<List<Incident>>(path);
        Prune();
    }

    public IReadOnlyList<Incident> Items { get { lock (_gate) return _items.OrderByDescending(i => i.Time).ToList(); } }

    public bool Contains(string id) { lock (_gate) return _items.Any(i => i.Id == id); }

    public Incident? Find(string id) { lock (_gate) return _items.FirstOrDefault(i => i.Id == id); }

    /// <summary>Adds the incident unless one with the same id exists. Returns true if it was new.</summary>
    public bool Add(Incident incident, bool save = true)
    {
        lock (_gate)
        {
            if (_items.Any(i => i.Id == incident.Id)) return false;
            _items.Add(incident);
            Prune();
            if (save) SaveLocked();
        }
        if (save) Changed?.Invoke();
        return true;
    }

    /// <summary>Replaces an incident's computed fields but keeps what the person wrote about it.</summary>
    public void Upsert(Incident incident)
    {
        lock (_gate)
        {
            int i = _items.FindIndex(x => x.Id == incident.Id);
            if (i >= 0)
            {
                incident.UserNote ??= _items[i].UserNote;
                incident.Gpu ??= _items[i].Gpu;
                incident.DismissedAt ??= _items[i].DismissedAt;
                _items[i] = incident;
            }
            else _items.Add(incident);
            Prune();
        }
    }

    public void SetNote(string id, string? note)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item == null) return;
            item.UserNote = string.IsNullOrWhiteSpace(note) ? null : note;
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Dismisses (or brings back) the given alerts. Returns how many changed.</summary>
    public int SetDismissed(IEnumerable<string> ids, bool dismissed, DateTimeOffset? at = null)
    {
        var set = ids.ToHashSet();
        int changed = 0;
        lock (_gate)
        {
            foreach (var item in _items.Where(i => set.Contains(i.Id)))
            {
                if (item.Dismissed == dismissed) continue;
                item.DismissedAt = dismissed ? at ?? DateTimeOffset.Now : null;
                changed++;
            }
            if (changed > 0) SaveLocked();
        }
        if (changed > 0) Changed?.Invoke();
        return changed;
    }

    public void Save()
    {
        lock (_gate) SaveLocked();
        Changed?.Invoke();
    }

    void SaveLocked()
    {
        try { JsonFile.Save(_path, _items); } catch { }
    }

    void Prune()
    {
        var cutoff = DateTimeOffset.Now - MaxAge;
        _items.RemoveAll(i => i.Time < cutoff);
        if (_items.Count > MaxItems)
        {
            _items.Sort((a, b) => b.Time.CompareTo(a.Time));
            _items.RemoveRange(MaxItems, _items.Count - MaxItems);
        }
    }
}
