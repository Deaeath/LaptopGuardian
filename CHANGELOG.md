# Changelog

## 2.4.0

**For laptops that switch themselves off from heat.**

### New
- **Heat page**, second in the menu. The thermal sensor live with the last 15 minutes, the heat governor, your app caps, the settings that make heat, overheating shutdowns, and the history of all of it. Everything on it works on any Windows laptop, whatever its maker.
- **Heat governor** (off by default). When the laptop passes a temperature you choose (88°C by default), or after 15 seconds of near-full load, it holds back the busiest apps (up to 5): each to half of what it is using (never more than a share you choose, 40% by default), a quarter less every 5 seconds while the laptop stays hot, down to 5%. It lets go once the laptop has cooled. Windows' own processes are never touched. Each time it steps in, it says so. Windows' own clock limits (maximum processor state, maximum frequency, core parking) are ignored by some processors; the governor's per-app caps are not.
- **App caps.** A table of the apps using the processor, where you can give any app its own cap, keep it capped all the time, or tell the governor never to touch it.
- **Processor boost switch.** Turning boost off keeps the processor at its base speed in every power plan, which removes the bursts that trip an overheating laptop's protection. Turning it back on restores what it was.
- **"Overheated and switched off".** A new verdict for a shutdown whose last flight recorder reading was 95°C or more, with a suggestion that turns the heat governor on in one click. Existing entries are worked out again.
- **Windows' power mode.** LaptopGuardian reads it (and records it every 10 seconds), shows it on the Power page with a picker, and notices the laptop running hot while doing nothing: on "Best performance" the processor can sit near full speed at idle. A suggestion switches to Best power efficiency in one click, and warns that Armoury Crate's "Power mode synchronization" switches it back.
- The health report lists the power mode and any stretches of running hot while idle.

## 2.3.0

### New
- **Dismiss alerts.** Every alert has a dismiss button: in Recent problems, the timeline, the Shutdowns page and the Graphics page, and "Dismiss all" on the Overview. A dismissed alert leaves the Overview, the Shutdowns badge and the red status, but stays in the timeline (faded, with a button to bring it back). The Dismiss button on a notification now dismisses the alert in the app too. The timeline has a "Dismissed" filter.
- **Charger watch.** LaptopGuardian now notices a charger that keeps cutting out: Windows logs every switch between charger and battery, and nothing else mentions them. A drop of a few seconds is recorded quietly; several drops close together raise an alert and a suggestion with steps to find the fault. Each unexpected shutdown says whether the charger cut out in the hour before, and the health report lists the drops. On the laptop this was built for, the plug had been dropping out for seven weeks, including five times in the 30 minutes before a sudden reset.

### Fixed
- **Hiding a suggestion hid it for good.** The advice about sudden resets stayed hidden through four more resets in one night. A hidden suggestion now comes back when the problem happens again, and a hidden driver or BIOS update comes back when a newer version is out. Suggestions hidden in earlier versions count as hidden on the day you update.
- Working out an older crash again no longer loses the flight recorder reading saved with it.

## 2.2.2

### Fixed
- **The driver check ran again after every restart and every update.** Its Windows Update part keeps the processor busy for minutes, which is the last thing a struggling laptop needs. It now runs once a day, counted across restarts.

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
