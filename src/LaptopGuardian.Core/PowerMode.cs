using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LaptopGuardian.Core;

/// <summary>Windows' power mode (Settings > System > Power): the overlay on top of the power plan.</summary>
public enum WindowsPowerMode { Unknown, BestPowerEfficiency, Balanced, BetterPerformance, BestPerformance }

public sealed record PowerModeSettings(WindowsPowerMode PluggedIn, WindowsPowerMode OnBattery);

/// <summary>
/// Reads and sets Windows' power mode. On 15 November 2026, "Best performance" kept an idle Ryzen 9 7945HX at 96%
/// of its top speed and its thermal sensor at 86-96°C. Switching to "Best power efficiency" took it to 62°C
/// within minutes, with the fans unchanged. Laptop utilities (Armoury Crate's "Power mode synchronization")
/// can switch it back without saying so.
/// </summary>
public static class PowerMode
{
    public static readonly Guid BestPowerEfficiencyId = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid BalancedId = Guid.Empty;
    public static readonly Guid BetterPerformanceId = new("3af9b8d9-7c97-431d-ad78-34a8bfea439f");
    public static readonly Guid BestPerformanceId = new("ded574b5-45a0-4f42-8737-46345c09c238");

    const string Key = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

    public static WindowsPowerMode FromGuid(Guid g) =>
        g == BestPowerEfficiencyId ? WindowsPowerMode.BestPowerEfficiency
        : g == BalancedId ? WindowsPowerMode.Balanced
        : g == BetterPerformanceId ? WindowsPowerMode.BetterPerformance
        : g == BestPerformanceId ? WindowsPowerMode.BestPerformance
        : WindowsPowerMode.Unknown;

    public static Guid ToGuid(WindowsPowerMode m) => m switch
    {
        WindowsPowerMode.BestPowerEfficiency => BestPowerEfficiencyId,
        WindowsPowerMode.BetterPerformance => BetterPerformanceId,
        WindowsPowerMode.BestPerformance => BestPerformanceId,
        _ => BalancedId,
    };

    public static string Name(WindowsPowerMode m) => m switch
    {
        WindowsPowerMode.BestPowerEfficiency => "Best power efficiency",
        WindowsPowerMode.Balanced => "Balanced",
        WindowsPowerMode.BetterPerformance => "Better performance",
        WindowsPowerMode.BestPerformance => "Best performance",
        _ => "Unknown",
    };

    public static WindowsPowerMode FromName(string? name) =>
        Enum.GetValues<WindowsPowerMode>().FirstOrDefault(m => m != WindowsPowerMode.Unknown && Name(m) == name);

    /// <summary>The performance modes, which keep the processor near full speed even when idle.</summary>
    public static bool IsPerformance(WindowsPowerMode m) => m is WindowsPowerMode.BestPerformance or WindowsPowerMode.BetterPerformance;

    /// <summary>The mode in force now, for whichever power source is in use.</summary>
    public static WindowsPowerMode Current()
    {
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var g) == 0) return FromGuid(g);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        var s = Settings();
        var (_, ac) = Guardian.PowerStatus();
        return s == null ? WindowsPowerMode.Unknown : ac == false ? s.OnBattery : s.PluggedIn;
    }

    /// <summary>The mode chosen for plugged in and for on battery.</summary>
    public static PowerModeSettings? Settings()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(Key);
            if (k == null) return null;
            static WindowsPowerMode Read(RegistryKey k, string name) =>
                k.GetValue(name) is string s && Guid.TryParse(s, out var g) ? FromGuid(g) : WindowsPowerMode.Balanced;
            return new PowerModeSettings(Read(k, "ActiveOverlayAcPowerScheme"), Read(k, "ActiveOverlayDcPowerScheme"));
        }
        catch { return null; }
    }

    /// <summary>Sets the mode for the power source in use now, as the Settings app does. Returns a Win32 error code; 0 is success.</summary>
    public static uint Set(WindowsPowerMode m)
    {
        try { return PowerSetActiveOverlayScheme(ToGuid(m)); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return 120; } // ERROR_CALL_NOT_IMPLEMENTED
    }

    [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid overlay);
}
