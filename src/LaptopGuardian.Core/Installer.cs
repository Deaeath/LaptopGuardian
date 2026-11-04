using System.Diagnostics;
using Microsoft.Win32;

namespace LaptopGuardian.Core;

/// <summary>
/// A downloaded LaptopGuardian.exe is its own installer. Run from anywhere, it copies itself to the
/// per-user programs folder (no administrator rights), adds a Start menu shortcut and an entry in
/// Windows' Apps list, starts at sign-in, and runs from there. That stable location is what lets it update itself.
/// </summary>
public static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LaptopGuardian";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LaptopGuardian");
    public static string InstalledExe => Path.Combine(InstallDir, AppUpdater.ExeName);
    static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\LaptopGuardian.lnk");

    public static bool IsInstalledCopy(string? exe) =>
        exe != null && string.Equals(Path.GetFullPath(exe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>A copy run from Downloads or a USB stick installs itself; a development build never does.</summary>
    public static bool ShouldInstall(string? exe) =>
        exe != null && !IsInstalledCopy(exe) && !exe.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies this program into place and starts that copy. False if it could not, so it can run where it is.</summary>
    public static bool InstallAndRun(string exe)
    {
        bool firstInstall = !File.Exists(InstalledExe);
        try
        {
            StopOtherCopies();
            Directory.CreateDirectory(InstallDir);
            using (var src = File.OpenRead(exe))
            using (var dst = File.Create(InstalledExe))
                src.CopyTo(dst);
            if (firstInstall) Startup.Set(true, InstalledExe);
            Register(InstalledExe);
            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    /// <summary>Start menu shortcut and Apps list entry. Run at every start, so the listed version stays true after an update.</summary>
    public static void Register(string exe)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic s = shell.CreateShortcut(Shortcut);
            s.TargetPath = exe;
            s.WorkingDirectory = Path.GetDirectoryName(exe);
            s.IconLocation = exe + ",0";
            s.Description = "Explains crashes, graphics errors, sleep failures, heat and battery wear.";
            s.Save();
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
            k.SetValue("DisplayName", "LaptopGuardian");
            k.SetValue("DisplayVersion", AppUpdater.Current.ToString());
            k.SetValue("Publisher", "LaptopGuardian");
            k.SetValue("DisplayIcon", exe);
            k.SetValue("InstallLocation", Path.GetDirectoryName(exe) ?? "");
            k.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            k.SetValue("URLInfoAbout", $"https://github.com/{AppUpdater.Repo}");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    /// <summary>Removes the program, shortcut, startup entry and Apps list entry. Your journal and settings stay in the data folder.</summary>
    public static void Uninstall()
    {
        StopOtherCopies();
        try { Startup.Set(false, InstalledExe); } catch { }
        try { File.Delete(Shortcut); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        // A running program cannot delete itself; a short-lived command does it once this one has exited.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    static void StopOtherCopies()
    {
        foreach (var p in Process.GetProcessesByName("LaptopGuardian"))
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId) continue;
                try { p.Kill(); p.WaitForExit(5000); } catch { }
            }
        }
    }
}
