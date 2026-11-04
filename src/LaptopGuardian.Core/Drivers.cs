using System.Globalization;
using System.Management;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;

namespace LaptopGuardian.Core;

public sealed record DriverInfo(string Category, string Device, string Provider, string Version, DateTime? Date);
public sealed record ProblemDevice(string Name, string DeviceId, int ErrorCode, string Meaning);

/// <summary>A driver or firmware newer than the one installed.</summary>
public sealed record DriverUpdate(
    string Key,          // stable id, e.g. "nvidia:621.14", so each update is announced once
    string Source,       // "NVIDIA", "Windows Update", "ASUS"
    string Title,
    string? Installed,
    string Available,
    DateTime? Released,
    string? Url,         // where to download it, if it is not installed from here
    string? ActionId,    // set when LaptopGuardian can install it itself
    Severity Severity);

public sealed class DriverReport
{
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;
    public List<DriverInfo> Installed { get; init; } = new();
    public List<ProblemDevice> Problems { get; init; } = new();
    public List<DriverUpdate> Updates { get; init; } = new();
    /// <summary>The newest BIOS the laptop maker publishes, if we could ask.</summary>
    public string? LatestBios { get; init; }
    public bool BiosIsLatest { get; init; }
    public List<string> Notes { get; init; } = new();
}

/// <summary>
/// Driver and firmware checks. Installed drivers come from the registry (fast), problem devices from WMI,
/// and updates from three sources: NVIDIA's driver lookup (any NVIDIA graphics card), Windows Update's
/// driver catalog (any device), and the laptop maker's BIOS feed (ASUS today; others get a search link).
/// </summary>
public static class DriverChecks
{
    public const string ActionInstallWindowsUpdateDrivers = "install-driver-updates";

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        // NVIDIA's lookup answers an error page to clients that do not look like a browser.
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 LaptopGuardian/2");
        return h;
    }

    public static async Task<DriverReport> RunAsync(SystemSnapshot sys, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var installed = Task.Run(Installed, ct);
        var problems = Task.Run(Problems, ct);
        var wu = Task.Run(() => WindowsUpdateDrivers(notes), ct);
        var gpu = sys.Adapters.FirstOrDefault(a => a.Vendor == "NVIDIA");
        var nvidia = gpu != null ? Nvidia(gpu, notes, ct) : Task.FromResult<DriverUpdate?>(null);
        var bios = sys.IsAsus ? AsusBios(sys, notes, ct) : Task.FromResult<(DriverUpdate?, string?, bool)>((null, null, false));

        await Task.WhenAll(installed, problems, wu, nvidia, bios);
        var updates = new List<DriverUpdate>();
        if (nvidia.Result is DriverUpdate n) updates.Add(n);
        if (bios.Result.Item1 is DriverUpdate b) updates.Add(b);
        updates.AddRange(wu.Result);
        return new DriverReport
        {
            Installed = installed.Result,
            Problems = problems.Result,
            Updates = updates,
            LatestBios = bios.Result.Item2,
            BiosIsLatest = bios.Result.Item3,
            Notes = notes,
        };
    }

    // ------------------------------------------------------------ installed drivers

    static readonly (string Guid, string Category)[] Classes =
    {
        ("{4d36e968-e325-11ce-bfc1-08002be10318}", "Graphics"),
        ("{4d36e97d-e325-11ce-bfc1-08002be10318}", "Chipset"),
        ("{4d36e972-e325-11ce-bfc1-08002be10318}", "Network"),
        ("{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}", "Bluetooth"),
        ("{4d36e96c-e325-11ce-bfc1-08002be10318}", "Audio"),
        ("{4d36e97b-e325-11ce-bfc1-08002be10318}", "Storage"),
        ("{f2e7dd72-6468-4e36-b6f1-6488f42c1b52}", "Firmware"),
    };

    public static List<DriverInfo> Installed()
    {
        var list = new List<DriverInfo>();
        foreach (var (guid, category) in Classes)
        {
            try
            {
                using var cls = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{guid}");
                if (cls == null) continue;
                foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
                {
                    using var k = cls.OpenSubKey(sub);
                    string? desc = k?.GetValue("DriverDesc") as string;
                    string provider = k?.GetValue("ProviderName") as string ?? "";
                    string version = k?.GetValue("DriverVersion") as string ?? "";
                    if (string.IsNullOrWhiteSpace(desc) || version.Length == 0) continue;
                    // Windows' own in-box drivers update with Windows itself.
                    if (provider.Length == 0 || provider.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                    if (desc.Contains("Basic", StringComparison.OrdinalIgnoreCase) || desc.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase)) continue;
                    var date = ParseDriverDate(k!.GetValue("DriverDate") as string);
                    list.Add(new DriverInfo(category, desc.Trim(), provider.Trim(), version, date));
                }
            }
            catch { }
        }
        return list
            .GroupBy(d => (d.Device, d.Version)).Select(g => g.First())
            .OrderBy(d => Array.FindIndex(Classes, c => c.Category == d.Category)).ThenBy(d => d.Device)
            .ToList();
    }

    static DateTime? ParseDriverDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string[] f = { "M-d-yyyy", "MM-dd-yyyy", "M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-dd" };
        return DateTime.TryParseExact(s.Trim(), f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    // ------------------------------------------------------------ devices with problems

    public static string ProblemMeaning(int code) => code switch
    {
        1 => "It is not set up correctly.",
        3 => "Its driver may be damaged, or Windows is low on memory.",
        10 => "It cannot start.",
        12 => "It cannot find enough free resources.",
        18 => "Its driver needs reinstalling.",
        19 => "Its settings in the registry are damaged.",
        21 => "Windows is removing it.",
        22 => "It has been turned off in Device Manager.",
        28 => "No driver is installed for it.",
        29 => "It is turned off in the firmware (BIOS).",
        31 => "It is not working properly; Windows could not load its driver.",
        32 => "Its driver has been turned off.",
        37 or 39 or 41 => "Its driver failed to load or is damaged.",
        43 => "Windows stopped it because it reported problems.",
        48 => "Its driver was blocked because it has known problems.",
        52 => "Windows cannot verify its driver's signature.",
        _ => $"Device Manager reports problem code {code}.",
    };

    public static List<ProblemDevice> Problems()
    {
        var list = new List<ProblemDevice>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, HardwareID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
            foreach (ManagementObject m in s.Get())
            {
                int code = Convert.ToInt32(m["ConfigManagerErrorCode"] ?? 0);
                if (code is 24 or 45) continue; // not plugged in right now: normal
                string id = m["PNPDeviceID"] as string ?? "";
                string? name = m["Name"] as string;
                if (string.IsNullOrWhiteSpace(name) && m["HardwareID"] is string[] hw && hw.Length > 0)
                {
                    // A hardware id such as "Maker_Maker_Virtual_Camera" reads better without the repeat.
                    var words = hw[0].Split('_', ' ');
                    name = string.Join(' ', words.Where((w, i) => i == 0 || !w.Equals(words[i - 1], StringComparison.OrdinalIgnoreCase)));
                }
                list.Add(new ProblemDevice(string.IsNullOrWhiteSpace(name) ? id : name!, id, code, ProblemMeaning(code)));
            }
        }
        catch { }
        return list;
    }

    // ------------------------------------------------------------ NVIDIA

    /// <summary>"NVIDIA GeForce RTX 4070 Laptop GPU" and "GeForce RTX 4070 Laptop GPU" are the same card.</summary>
    public static string NormalizeGpuName(string s)
    {
        s = s.Trim();
        if (s.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase)) s = s[7..];
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }

    public static bool IsNewer(string? available, string? installed)
    {
        if (!Version.TryParse(Pad(available), out var a)) return false;
        if (!Version.TryParse(Pad(installed), out var i)) return true;
        return a > i;
        static string? Pad(string? v) => v != null && !v.Contains('.') ? v + ".0" : v;
    }

    /// <summary>The (product, series) ids NVIDIA's driver lookup uses for this card, from its product list.</summary>
    public static (string Pfid, string Psid)? FindNvidiaProduct(XDocument list, string adapterName)
    {
        string want = NormalizeGpuName(adapterName);
        foreach (var v in list.Descendants().Where(e => e.Name.LocalName == "LookupValue"))
        {
            var name = v.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
            var value = v.Elements().FirstOrDefault(e => e.Name.LocalName == "Value")?.Value;
            var parent = (string?)v.Attribute("ParentID");
            if (name != null && value != null && parent != null && NormalizeGpuName(name) == want) return (value, parent);
        }
        return null;
    }

    static async Task<DriverUpdate?> Nvidia(GraphicsAdapter gpu, List<string> notes, CancellationToken ct)
    {
        try
        {
            var products = XDocument.Parse(await Http.GetStringAsync("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3", ct));
            var found = FindNvidiaProduct(products, gpu.Name);
            if (found == null)
            {
                lock (notes) notes.Add($"NVIDIA's driver list does not include \"{gpu.Name}\".");
                return null;
            }
            var (pfid, psid) = found.Value;
            string url = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
                $"?func=DriverManualLookup&psid={psid}&pfid={pfid}&osID=57&languageCode=1033&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0&sort1=0&numberOfResults=1";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
            var info = doc.RootElement.GetProperty("IDS")[0].GetProperty("downloadInfo");
            string version = info.GetProperty("Version").GetString() ?? "";
            if (version.Length == 0) return null;
            DateTime? released = DateTime.TryParseExact(info.GetProperty("ReleaseDateTime").GetString(), "ddd MMM d, yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var r) ? r : null;
            if (!IsNewer(version, gpu.FriendlyVersion)) return null;
            return new DriverUpdate($"nvidia:{version}", "NVIDIA", "NVIDIA Game Ready driver", gpu.FriendlyVersion, version, released,
                info.TryGetProperty("DetailsURL", out var d) ? d.GetString() : "https://www.nvidia.com/en-us/drivers/", null, Severity.Warning);
        }
        catch (Exception ex)
        {
            lock (notes) notes.Add("Could not reach NVIDIA's driver lookup: " + ex.Message);
            return null;
        }
    }

    // ------------------------------------------------------------ BIOS (ASUS)

    public static string AsusModel(string productName)
    {
        // "ROG Strix G713PI_G713PI" -> "G713PI"; "ASUS TUF Gaming A15 FA507NV" -> "FA507NV"
        int u = productName.LastIndexOf('_');
        if (u >= 0 && u < productName.Length - 1) return productName[(u + 1)..].Trim();
        return productName.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? productName;
    }

    /// <summary>"G713PI.336" -> "336".</summary>
    public static string AsusBiosNumber(string biosVersion)
    {
        int dot = biosVersion.LastIndexOf('.');
        return (dot >= 0 ? biosVersion[(dot + 1)..] : biosVersion).Trim();
    }

    static async Task<(DriverUpdate?, string?, bool)> AsusBios(SystemSnapshot sys, List<string> notes, CancellationToken ct)
    {
        string model = AsusModel(sys.Model);
        foreach (var host in new[] { "rog.asus.com", "www.asus.com" })
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(
                    $"https://{host}/support/webapi/product/GetPDBIOS?website=global&model={Uri.EscapeDataString(model)}&cpu=", ct));
                var obj = doc.RootElement.GetProperty("Result").GetProperty("Obj");
                (int Number, string Version, DateTime? Date, string? Url)? best = null;
                foreach (var cat in obj.EnumerateArray())
                {
                    bool windowsUpdater = (cat.GetProperty("Name").GetString() ?? "").Contains("Windows", StringComparison.OrdinalIgnoreCase);
                    foreach (var f in cat.GetProperty("Files").EnumerateArray())
                    {
                        string v = f.GetProperty("Version").GetString() ?? "";
                        if (!int.TryParse(v, out int n)) continue;
                        DateTime? date = DateTime.TryParseExact(f.GetProperty("ReleaseDate").GetString(), "yyyy/MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                        string? url = f.TryGetProperty("DownloadUrl", out var du) && du.TryGetProperty("Global", out var g) ? g.GetString() : null;
                        // Prefer the Windows updater (an .exe anyone can run) over the raw image for the same version.
                        if (best == null || n > best.Value.Number || (n == best.Value.Number && windowsUpdater))
                            best = (n, v, date, url);
                    }
                }
                if (best == null) continue;
                string current = AsusBiosNumber(sys.BiosVersion);
                bool latest = int.TryParse(current, out int cur) && cur >= best.Value.Number;
                var update = latest ? null : new DriverUpdate($"bios:{best.Value.Version}", "ASUS", "BIOS update", current, best.Value.Version,
                    best.Value.Date, best.Value.Url, null, Severity.Warning);
                return (update, best.Value.Version, latest);
            }
            catch { }
        }
        lock (notes) notes.Add($"Could not reach ASUS's BIOS list for {model}.");
        return (null, null, false);
    }

    // ------------------------------------------------------------ Windows Update

    static List<DriverUpdate> WindowsUpdateDrivers(List<string> notes)
    {
        var list = new List<DriverUpdate>();
        try
        {
            dynamic searcher = Session().CreateUpdateSearcher();
            searcher.Online = true;
            dynamic result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
            int count = result.Updates.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic u = result.Updates[i];
                string title = u.Title;
                DateTime? date = null;
                try { date = (DateTime)u.DriverVerDate; } catch { }
                list.Add(new DriverUpdate("wu:" + (string)u.Identity.UpdateID, "Windows Update", title, null,
                    date?.ToString("yyyy-MM-dd") ?? "new", date, null, ActionInstallWindowsUpdateDrivers, Severity.Info));
            }
        }
        catch (Exception ex)
        {
            lock (notes) notes.Add("Windows Update could not be searched: " + ex.Message);
        }
        return list;
    }

    static dynamic Session() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new InvalidOperationException("Windows Update is not available."))!;

    /// <summary>Downloads and installs every available driver update from Windows Update. Needs administrator rights.</summary>
    public static ActionResult InstallWindowsUpdateDrivers()
    {
        dynamic session = Session();
        dynamic searcher = session.CreateUpdateSearcher();
        searcher.Online = true;
        dynamic result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
        int count = result.Updates.Count;
        if (count == 0) return new ActionResult(true, "There were no driver updates to install.");
        dynamic coll = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
        for (int i = 0; i < count; i++)
        {
            dynamic u = result.Updates[i];
            try { if (!(bool)u.EulaAccepted) u.AcceptEula(); } catch { }
            coll.Add(u);
        }
        dynamic downloader = session.CreateUpdateDownloader();
        downloader.Updates = coll;
        downloader.Download();
        dynamic installer = session.CreateUpdateInstaller();
        installer.Updates = coll;
        dynamic r = installer.Install();
        int code = r.ResultCode; // 2 succeeded, 3 succeeded with errors
        bool reboot = r.RebootRequired;
        string tail = reboot ? " Restart the laptop to finish." : "";
        return code is 2 or 3
            ? new ActionResult(true, $"Installed {Fmt.Plural(count, "driver update", "driver updates")}{(code == 3 ? " (some with errors)" : "")}.{tail}")
            : new ActionResult(false, $"Windows Update could not install the driver updates (result {code}).");
    }
}
