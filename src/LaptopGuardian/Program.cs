using LaptopGuardian.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace LaptopGuardian;

public static class Program
{
    public const string ShowEventName = @"Local\LaptopGuardian.Show";
    // Same name as version 1, so the two can never run (and hook the keyboard) at the same time.
    const string MutexName = @"Local\LaptopGuardian.SingleInstance";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--diagnose")) return Cli.Diagnose(args);
        if (args.Contains("--write-icon")) return Cli.WriteIcon(args);
        if (args.Contains("--screenshot"))
        {
            // Renders every page to PNG off-screen, from a live scan, without the tray or the keyboard hook.
            int i = Array.IndexOf(args, "--screenshot");
            var shots = new App
            {
                ScreenshotDir = i + 1 < args.Length ? args[i + 1] : ".",
                ForceDark = args.Contains("--dark") ? true : args.Contains("--light") ? false : null,
            };
            shots.InitializeComponent();
            return shots.Run();
        }
        if (args.Length > 0 && args[0] == Elevation.Flag) return Elevation.Main(args.Skip(1).ToArray());

        if (args.Contains("--uninstall"))
        {
            try { ToastNotificationManagerCompat.Uninstall(); } catch { }
            Installer.Uninstall();
            return 0;
        }

        // Downloaded and double-clicked: install to the per-user programs folder and run from there.
        if (Installer.ShouldInstall(Environment.ProcessPath) && Installer.InstallAndRun(Environment.ProcessPath!)) return 0;

        bool toastActivated = false;
        try { toastActivated = ToastNotificationManagerCompat.WasCurrentProcessToastActivated(); } catch { }

        using var mutex = new Mutex(true, MutexName, out bool first);
        // After a self-update the old copy is still exiting: wait for it rather than quitting as a second instance.
        if (!first && args.Contains("--updated-from"))
        {
            try { first = mutex.WaitOne(TimeSpan.FromSeconds(20)); }
            catch (AbandonedMutexException) { first = true; }
        }
        if (!first)
        {
            // Already running: bring that window forward instead (unless Windows started us at sign-in).
            if (!args.Contains("--background"))
                try { using var show = EventWaitHandle.OpenExisting(ShowEventName); show.Set(); } catch { }
            return 0;
        }

        int from = Array.IndexOf(args, "--updated-from");
        var app = new App
        {
            StartHidden = args.Contains("--background") && !toastActivated,
            UpdatedFrom = from >= 0 && from + 1 < args.Length ? args[from + 1] : null,
        };
        app.InitializeComponent();
        return app.Run();
    }
}
