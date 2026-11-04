# LaptopGuardian

A free Windows app that tells you, in plain English, why your laptop crashed, froze or died in its sleep, and what to do about it. It also checks your drivers and BIOS for updates.

Windows records almost everything that goes wrong, but scatters it across the event log, Windows Error Reporting, the battery report and the power settings. Much of it is unreadable ("The description for Event ID 153 from source nvlddmkm cannot be found") or misleading: one crash report Windows cannot send is logged again every four hours, so Reliability Monitor can show thousands of "hardware errors" that are really four. LaptopGuardian reads all of it, joins it up, and explains each event once.

## Install

1. Download **LaptopGuardian.exe** from the [latest release](https://github.com/Deaeath/LaptopGuardian/releases/latest).
2. Run it. It needs no administrator rights and no .NET install. It installs itself for your user account, adds itself to the Start menu and the notification area, and starts when you sign in. You can turn that off in Settings.

**Windows may warn you the first time.** The program is not code-signed yet, so SmartScreen can show "Windows protected your PC". Choose **More info**, then **Run anyway**. Each release lists the file's SHA-256 hash if you want to check it, and the source is all here.

Requires 64-bit Windows 10 (1809 or newer) or Windows 11.

**Updates are automatic.** LaptopGuardian checks this page's releases twice a day, verifies the download against its SHA-256 hash, and restarts itself in the background. You can turn this off in Settings.

**To remove it,** use Windows Settings > Apps > Installed apps > LaptopGuardian > Uninstall. Your history and settings stay in `%APPDATA%\LaptopGuardian`; delete that folder if you want them gone too.

## What it does

- **Explains unexpected shutdowns.** It reads Windows' crash records and the battery report's sleep history to tell apart a laptop that died asleep, one that would not wake, one that froze in use, a blue screen (with the stop code in words), and an empty battery.
- **Decodes graphics driver and hardware errors** as they happen (NVIDIA, AMD, Intel, display-driver resets, WHEA), with what the graphics card was doing at that moment.
- **Checks drivers and BIOS.** It looks up the newest NVIDIA driver for your card, Windows Update's driver catalog (with one-click install), and the BIOS list from your laptop's maker (ASUS today; other makers get a search link). It also lists devices with driver problems.
- **Collapses Windows Error Reporting noise.** It shows how many real events there were, and can clear the stuck queue so Windows stops re-logging them.
- **Watches the graphics card live**: temperature against the driver's own target, load, memory and power. It warns when the card sits at its limit or runs out of memory, and it does not wake a sleeping graphics card to do so.
- **Checks power and battery**: battery wear, sleep type, and sleep, hibernate and lid settings, with one-click fixes such as hibernating after 20 minutes of sleep.
- **Filters keyboard chatter**: swallows a second press of the same key that arrives too soon, for keyboards that type letters twice.
- **Exports a health report** to send to your laptop maker's support.

## Privacy

Your data stays on your PC, in `%APPDATA%\LaptopGuardian`. There are no accounts and no telemetry. The app only contacts the internet to check for updates:

| Service | Why | What it sends |
|---|---|---|
| GitHub | New versions of LaptopGuardian | Nothing about you or your PC |
| NVIDIA | Newest driver (NVIDIA cards only) | Your graphics card's model |
| Microsoft Windows Update | Driver updates | What Windows Update normally sends |
| ASUS | Newest BIOS (ASUS laptops only) | Your laptop's model |

Links you click open in your browser.

## Build from source

Needs the .NET 10 SDK.

```powershell
dotnet test src\LaptopGuardian.Tests     # the logic, built from real events
.\publish.ps1 -Install                   # test, build a self-contained exe, install it
.\publish.ps1 -Release                   # also publish it as a GitHub release (needs gh)
```

`LaptopGuardian.exe --diagnose --out report.md` runs a full scan without the window. `--screenshot <folder> [--dark|--light]` renders every page to PNG off-screen.

- `src/LaptopGuardian.Core`: everything that reads Windows and decides what it means. No UI.
- `src/LaptopGuardian`: the WPF app: window, tray icon, notifications.
- `src/LaptopGuardian.Tests`: xUnit tests, including regression tests named after the bugs they pin down.

## Design notes

- **Never announce something old.** Version 1 used `EventLog.EntryWritten`, which can re-raise old entries once the System log is full and overwriting itself. Version 2 subscribes with `EventLogWatcher`, records each event once, and only announces events written in the last few minutes.
- **Say only what the record supports.** A power-button timestamp means the button was pressed, not held. A crash older than the battery report is "not recorded", not "in use". The BIOS advice asks the maker instead of guessing from its age.
- **Do not cost what you watch.** Idle in the tray it uses about 50 MB of memory and under 1% of one CPU core.

## License

[MIT](LICENSE)
