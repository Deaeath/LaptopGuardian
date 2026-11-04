using System.Globalization;
using Microsoft.Win32;

namespace LaptopGuardian.Core;

public sealed record GraphicsAdapter(string Name, string DriverVersion, string? FriendlyVersion, DateTime? DriverDate, double? MemoryGB, string Vendor);

public sealed record SystemSnapshot
{
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string BiosVersion { get; init; } = "";
    public DateTime? BiosDate { get; init; }
    public string Windows { get; init; } = "";
    public string Cpu { get; init; } = "";
    public List<GraphicsAdapter> Adapters { get; init; } = new();

    public string ShortModel => Model.Contains('_') ? Model[..Model.IndexOf('_')] : Model;
    public bool IsAsus => Manufacturer.Contains("ASUS", StringComparison.OrdinalIgnoreCase);

    /// <summary>The name people use: "ASUSTeK COMPUTER INC." is ASUS, "HP Inc." is HP.</summary>
    public string Maker
    {
        get
        {
            if (IsAsus) return "ASUS";
            string m = Manufacturer;
            foreach (var tail in new[] { " COMPUTER INC.", " Inc.", " Inc", " Corporation", " Corp.", " Co., Ltd.", " LLC", " Ltd." })
                if (m.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) m = m[..^tail.Length];
            return m.Trim();
        }
    }
    public GraphicsAdapter? Discrete =>
        Adapters.Where(a => a.Vendor is "NVIDIA" or "AMD" && (a.MemoryGB ?? 0) >= 2).OrderByDescending(a => a.MemoryGB).FirstOrDefault()
        ?? Adapters.OrderByDescending(a => a.MemoryGB).FirstOrDefault();
}

/// <summary>Reads the machine's identity from the registry: fast, and no WMI.</summary>
public static class SystemInfo
{
    const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static SystemSnapshot Read()
    {
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return new SystemSnapshot
        {
            Manufacturer = Clean(bios?.GetValue("SystemManufacturer") as string),
            Model = Clean(bios?.GetValue("SystemProductName") as string),
            BiosVersion = Clean(bios?.GetValue("BIOSVersion") as string),
            BiosDate = ParseDate(bios?.GetValue("BIOSReleaseDate") as string),
            Windows = WindowsName(nt),
            Cpu = Cpu(),
            Adapters = Adapters(),
        };
    }

    static string Cpu()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return Clean(k?.GetValue("ProcessorNameString") as string);
    }

    static string WindowsName(RegistryKey? nt)
    {
        if (nt == null) return Environment.OSVersion.VersionString;
        string product = nt.GetValue("ProductName") as string ?? "Windows";
        string display = nt.GetValue("DisplayVersion") as string ?? "";
        int.TryParse(nt.GetValue("CurrentBuild") as string, out int build);
        var ubr = nt.GetValue("UBR");
        // ProductName still says "Windows 10" on Windows 11.
        if (build >= 22000) product = product.Replace("Windows 10", "Windows 11");
        return $"{product} {display} (build {build}{(ubr != null ? "." + ubr : "")})".Replace("  ", " ");
    }

    static List<GraphicsAdapter> Adapters()
    {
        var list = new List<GraphicsAdapter>();
        using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
        if (cls == null) return list;
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            try
            {
                using var k = cls.OpenSubKey(sub);
                var name = k?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(name) || name.Contains("Basic", StringComparison.OrdinalIgnoreCase)) continue;
                string ver = k!.GetValue("DriverVersion") as string ?? "";
                double? mem = k.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l / 1073741824.0,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0) / 1073741824.0,
                    byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0) / 1073741824.0,
                    int i => (uint)i / 1073741824.0,
                    _ => null,
                };
                string match = (k.GetValue("MatchingDeviceId") as string ?? "").ToUpperInvariant();
                string vendor = match.Contains("VEN_10DE") || name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
                    : match.Contains("VEN_1002") || name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "AMD"
                    : match.Contains("VEN_8086") || name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : "Other";
                list.Add(new GraphicsAdapter(name.Trim(), ver, vendor == "NVIDIA" ? NvidiaVersion(ver) : vendor == "AMD" ? k.GetValue("RadeonSoftwareVersion") as string : null,
                    ParseDate(k.GetValue("DriverDate") as string), mem is double m ? Math.Round(m, 1) : null, vendor));
            }
            catch { }
        }
        return list;
    }

    /// <summary>Windows driver version 32.0.16.1656 is NVIDIA's 616.56: the last five digits of the last two parts.</summary>
    public static string? NvidiaVersion(string? windowsVersion)
    {
        var parts = (windowsVersion ?? "").Split('.');
        if (parts.Length < 4) return null;
        string digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5 || !digits.All(char.IsDigit)) return null;
        digits = digits[^5..];
        return $"{int.Parse(digits[..3], CultureInfo.InvariantCulture)}.{digits[3..]}";
    }

    static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string[] formats = { "MM/dd/yyyy", "M-d-yyyy", "M/d/yyyy", "MM-dd-yyyy", "yyyy-MM-dd" };
        return DateTime.TryParseExact(s.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    static string Clean(string? s) => (s ?? "").Trim();
}
