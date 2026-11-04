# Changelog

## 2.2.1

### New
- **The flight recorder now watches the processor too:** how busy it is, whether it has slowed itself down (its speed against its maximum), and which apps are using it most. A reset with the graphics card nearly idle showed that the processor side was a blind spot.
- **The graphics card's fan speed** is recorded where the driver reports it.

## 2.2.0

**Update from 2.1.0: it could wrongly say a crash happened while the laptop was asleep.**

### Fixed
- **Crashes in use were reported as "Shut down while asleep".** 2.1.0 read the battery report's "Suspend" rows as sleep, but those rows are gaps in the record around every restart. It now says "asleep" only when Windows' own sleep events or crash record say so, and existing entries are worked out again. If you changed your sleep settings because of this advice, you may want to change them back: on the Power page, or in Windows' power settings.
- **The time of a crash could be far too early.** The stamp Windows keeps can be many minutes old (19 minutes in one crash we checked). LaptopGuardian now uses the last thing known to have happened.
- **False heat warnings on AMD and other cards.** It assumed an NVIDIA laptop card's 87°C limit for any card that does not state one. It now warns only about a limit the driver states.
- **"Clear old reports" left the suggestion on screen,** because the log keeps the old retries after the reports are gone.
- **Hibernate settings applied to one power plan only,** so utilities that switch plans (such as Armoury Crate's performance modes) undid them. They now apply to every plan.
- BIOS advice no longer suggests an update when you already have the newest the maker lists.

### New
- **Flight recorder.** Windows often records nothing about a sudden reset. LaptopGuardian now saves temperatures, load, power and the busiest apps every 10 seconds, straight to disk, so the next crash shows exactly when it stopped and what the laptop was doing. It stays on your PC and uses at most 2 MB.
- **"Reset itself without warning".** A new verdict for a laptop that was running one moment and restarting seconds later, with steps to narrow down the cause.
- **Guided updates.** The Drivers page gives numbered steps and exact links for your graphics drivers and BIOS, and opens the right app for you: NVIDIA App, AMD Software, Intel's checker, MyASUS, Armoury Crate, HP, Dell, Lenovo and others.
- **AMD driver checks.** Compares your AMD Software version with the newest AMD lists.

## 2.1.0

- Drivers page: NVIDIA, Windows Update and ASUS BIOS checks, and devices with driver problems.
- Updates itself from GitHub releases, with a SHA-256 check.
- Installs itself from a downloaded .exe, and uninstalls from Windows' Apps list.

## 2.0.0

- Rebuilt from the small v1 tray app: crash explanations, graphics error decoding, live graphics card readings, battery and power checks, stuck Windows Error Reporting reports, and the health report.
- Fixed v1 announcing old GPU errors as new ones.
