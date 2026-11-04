using Microsoft.Win32;

namespace LaptopGuardian.Core;

/// <summary>
/// "Start with Windows". On means Windows will actually start it: the Run value exists and Task Manager's
/// Startup tab has not disabled it (that switch lives in StartupApproved, separately from the Run key).
/// </summary>
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "LaptopGuardian";

    public static string Command(string exePath) => $"\"{exePath}\" --background";

    public static bool IsEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(ValueName) == null) return false;
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return approved?.GetValue(ValueName) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
    }

    /// <summary>The program the Run value starts, if any.</summary>
    public static string? RegisteredPath()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        var cmd = run?.GetValue(ValueName) as string;
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        cmd = cmd.Trim();
        if (cmd.StartsWith('"')) { int end = cmd.IndexOf('"', 1); return end > 0 ? cmd[1..end] : cmd.Trim('"'); }
        int space = cmd.IndexOf(' ');
        return space > 0 ? cmd[..space] : cmd;
    }

    public static void Set(bool on, string exePath)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) run.SetValue(ValueName, Command(exePath));
            else run.DeleteValue(ValueName, false);
        }
        if (!on) return;
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        if (approved?.GetValue(ValueName) is byte[] b && b.Length > 0 && (b[0] & 1) != 0)
            approved.SetValue(ValueName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
    }

    /// <summary>
    /// Points an existing registration at this copy of the program (after an update or a move).
    /// Leaves Task Manager's enabled/disabled switch exactly as the person set it.
    /// </summary>
    public static void Repair(string exePath)
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (run?.GetValue(ValueName) is not string cmd) return;
        if (cmd != Command(exePath)) run.SetValue(ValueName, Command(exePath));
    }
}
