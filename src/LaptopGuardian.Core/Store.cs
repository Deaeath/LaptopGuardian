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
    public bool NotifyDriverUpdates { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;
}

public sealed class AppState
{
    public bool HistoryImported { get; set; }
    public DateTimeOffset? LastScan { get; set; }
    public DateTimeOffset? LastDriverCheck { get; set; }
    public List<string> DismissedRecommendations { get; set; } = new();
    /// <summary>Driver updates already announced, so each one is announced once.</summary>
    public List<string> AnnouncedDriverUpdates { get; set; } = new();
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
