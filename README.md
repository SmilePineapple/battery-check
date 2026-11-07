# BatteryCheck

Decides whether a laptop battery is really worn out, or whether short battery life comes from power consumption (software or configuration). It uses only built-in Windows tools (`powercfg`), so there is nothing to install. It works on Windows 10 and 11.

## BatteryCheck.exe (single file)

`BatteryCheck.exe` is a normal Windows program with its own window, so there's no browser and no console. Copy it to any Windows 10/11 laptop or a USB stick and double-click it. It asks for administrator rights once. Test results go into a `Reports` folder next to the exe. If that folder is read-only, they go to `Documents\BatteryCheck Reports` instead.

- **Basic / Expert (top right):** Basic, the default, shows one "Test this laptop" button, a short history and, per test, the verdict, key figures and findings. Expert adds the ticket fields, import, CSV export and all detail tabs. The choice is saved in `Reports\settings.json`.
- **One test:** every test now includes the sleep study (the former "Advanced" test), so standby drain and wake-ups are always checked.
- **Main window:**
  - Summary figures at the top.
  - *New test*, either on this PC (Basic or Advanced) or by importing XML reports made on another laptop. Each test can carry a ticket number, technician, customer and complaint.
  - *Test history*, which you can search, filter and sort. Click a test to open it.
  - While a test runs, the window shows the current step and has a *Cancel* button. The sleep study (advanced test) covers the last 7 days and can take a few minutes on laptops with a lot of logged history.
- **Test detail:**
  - The verdict and key figures.
  - Tabs for Findings, Runtime & capacity (wear vs. consumption split, capacity chart), Battery sessions, Sleep study (advanced tests), Battery & system, and Device history.
  - Editable ticket details (status, notes).
  - *Open / print full report* opens the HTML report in your browser.
- `BatteryCheck.exe --cli [-Advanced] [-Days 14] ...` runs one test in a console window.
- `BatteryCheck.exe --selftest <file>` (for troubleshooting) runs a basic test the same way the window does and writes the result, or the error, to `<file>`.
- **Rebuilding after a change:** run `build\Build.ps1`. The analysis is in `BatteryCheck.ps1` and the window is in `build\App.cs`. The build uses the C# compiler built into Windows, so nothing needs to be installed. `Build.ps1 -Out <path> -AsInvoker` makes a test copy that doesn't ask for admin rights.
- **Updates:** the *Check for updates* button (top right) looks for a newer GitHub Release of this repo. It checks quietly at startup and turns blue when one is available. One click downloads the new exe, checks its SHA-256 against the release notes, closes the app, swaps the exe and reopens it. Saved tests are kept. To publish a version, run `build\Publish.ps1 1.3.1 "what changed"`, which needs the GitHub CLI (`gh`) logged in.
- **SmartScreen and antivirus:** the exe is unsigned. Windows SmartScreen may show "Windows protected your PC" the first time (click *More info → Run anyway*). Some antivirus products are suspicious of unsigned programs that start PowerShell. To avoid this in a company setting, sign the exe with your code-signing certificate or whitelist it.

## Scripts (without the exe)

| Double-click | What it does | Admin? |
|---|---|---|
| `BatteryCheck.cmd` | Basic check using `powercfg /batteryreport` | No |
| `BatteryCheck-Advanced.cmd` | Basic check plus `powercfg /sleepstudy` analysis | Yes (asks for it) |

Each run opens an HTML report and saves three things in `Reports\`:
- `BatteryCheck-<PC>-<time>.html`: report to print or attach to the ticket
- `BatteryCheck-<PC>-<time>.json`: verdict and figures, for logging or statistics
- `raw-<time>\`: the original powercfg reports

**Tip:** run it at intake, before the laptop is charged. The tool measures real consumption from the last 14 days of use on battery. If the customer barely used the laptop on battery, there is not enough data to judge consumption.

### Command-line options

```powershell
.\BatteryCheck.ps1 [-Advanced] [-Days 14] [-OutputDir path] [-NoOpen]
# Analyse reports collected on another machine:
.\BatteryCheck.ps1 -BatteryReportXml battery.xml [-SleepStudyXml sleep.xml]
```

To collect reports on a customer machine without the tool, run this (the second command needs an elevated prompt):

```
powercfg /batteryreport /xml /output battery.xml
powercfg /sleepstudy /xml /output sleep.xml
```

## What it measures

**Battery wear** (basic)
- Health = full charge capacity ÷ design capacity, and wear %
- Cycle count, and whether the wear fits that cycle count (too much wear for few cycles points to calibration, heat or a defective cell, so check the warranty)
- Capacity trend over time, and sudden drops

**Real consumption** (basic). The battery report logs every session on battery, so the tool calculates:
- Average power draw in use (W)
- Standby drain (% per hour)
- Runtime now, with a new battery, and with a new battery at normal consumption
- **How the runtime shortfall splits between battery wear and extra power consumption.** This is the objective basis for "replace or not". Runtime = capacity ÷ power, so the loss splits cleanly into the capacity lost and the extra power used.

**Sleep study** (advanced)
- Standby sessions on battery: drain and hardware low-power residency (DRIPS)
- Top blockers: the drivers or devices that keep the laptop awake in standby
- Software that woke the system during standby, and why each standby ended
- **Self-wakes (battery report, all 14 days):** many short (≤ 6 min) wake-ups on battery between standby sessions mean the laptop wakes itself up. On a live test the sleep study looks back 7 days instead of 3 when the battery report shows these, so it can show the reason.
- Energy per app while in use on battery (Windows' SRUM estimates)

## Verdicts

| Battery | Consumption | Verdict |
|---|---|---|
| < 65% | normal | **REPLACE BATTERY** |
| < 65% | elevated | **REPLACE + FIX SOFTWARE** |
| 65–75% | normal | **REPLACEMENT JUSTIFIED** |
| 65–75% | elevated, wear is the main cause | **REPLACE + FIX SOFTWARE** |
| 65–75% | elevated, consumption is the main cause | **FIX SOFTWARE FIRST** |
| ≥ 75% | elevated | **DO NOT REPLACE – SOFTWARE/CONFIG** |
| ≥ 75% | normal | **NO FAULT FOUND** |
| ≥ 75% | not enough data | **BATTERY OK** |

The thresholds are in the `$Config` block at the top of `BatteryCheck.ps1`. Tune them to your repair policy. The main ones:
- `HealthOkPct` 80, `HealthFairPct` 75, `HealthReplacePct` 65
- `ActiveReferenceW` 10 W: what "normal" light-use consumption is. Raise this for gaming or workstation laptops, or the tool will flag their normal consumption as too high.
- `StandbyElevatedPctH` 1.0, `StandbyHighPctH` 2.5 (% per hour)

## Limitations

- Capacity values come from the battery's own gauge. A badly calibrated gauge can show too much or too little wear. The tool flags values that look implausible.
- Consumption is judged against a generic reference. A laptop that is heavily used (games, video) will show "elevated" even when nothing is wrong. Use the per-app table to confirm.
- Some batteries (often on older or cheaper models) do not report a cycle count.
