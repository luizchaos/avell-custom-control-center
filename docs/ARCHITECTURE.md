# Architecture

## Overview

```
┌──────────────────────────────┐   named pipe (JSON)   ┌──────────────────────────────────────┐
│ AvellCC.App (WPF UI + tray)  │ ────────────────────▶ │ AvellCC.Service (Windows service)    │
│ regular user, no admin       │ ◀──────────────────── │ LocalSystem, single hardware owner   │
└──────────────────────────────┘                       │  ├─ EC        (WMI GetSetULong)      │
                                                       │  ├─ Fans      (software curve →      │
                                                       │  │             direct duty)          │
                                                       │  ├─ Keyboard  (ITE 8291 HID)         │
                                                       │  └─ %ProgramData%\AvellCC            │
                                                       │       settings.json, curves\, backup\│
                                                       └──────────────────────────────────────┘
AvellCC.Core  — library with no UI dependency (protocols, models, safety rules, control loop)
AvellCC.Ipc   — pipe protocol, server and client
AvellCC.Cli   — avellcc.exe: diagnostics and hardware experiments on top of Core
```

- Only the **service** touches the hardware. The UI never runs elevated.
- Core is testable without hardware: `IEcAccess` has a real implementation (`WmiEc`), a test one (`FakeEc`)
  and a simulator (`SimulatedEc`, used by `AvellCC.Service.exe --simulate`).
- Every EC access within a process goes through one lock.

## Rule 1 — One controller at a time

> **Avell Custom Control and AvellCC must not run at the same time.**

The vendor app (`GCUBridge` + `GCUService.exe` + `ControlCenterU.exe`) writes to the **same EC registers**
(`0x751`, fan tables, `0x7A5`) and to the **same keyboard HID device**, and re-applies its own settings on
AC/battery changes, resume from sleep and Fn keys. With both running, each undoes the other.

The MSI installer and `tools/install.ps1` stop and disable `GCUBridge` (uninstalling the MSI sets it back to automatic).
To do it by hand (elevated PowerShell):

```powershell
Get-Process ControlCenterU, OSDTpDetect, EnableTray -ErrorAction SilentlyContinue | Stop-Process -Force
Stop-Service GCUBridge -Force
Set-Service GCUBridge -StartupType Disabled
```

To go back to Avell Custom Control: `tools\uninstall.ps1 -RestoreVendor`, or
`Set-Service GCUBridge -StartupType Automatic; Start-Service GCUBridge`.

How AvellCC enforces it:

- The service checks for `GCUService`, `GCUBridge`, `ControlCenterU` and `OSDTpDetect` every cycle. If any is running,
  it hands the fans back to the firmware and reports **Blocked**.
- Every write path in Core (`DirectFanService`, `FanTableService`, `OfficeFanService`) refuses to write while they run.
- The CLI also refuses to write while `AvellCC.Service` itself is running: the rule applies to us too.

## Rule 2 — Back up the original state before any change

> **The first state read from the EC is saved as `original` and never overwritten.**

Location: `%ProgramData%\AvellCC\backup\`. Each mechanism has its own group, so new registers get their own original
read before the first write:

| File | Contents |
|---|---|
| `fan-table.original.json` | raw `0xF00–0xF5F` (96 bytes) + fan mode registers |
| `ec-office.original.json` | `0x751`, `0x727`, `0x7A5`, `0x743–0x747`, `0x79E–0x7A0` |
| `ec-direct.original.json` | `0x751`, `0x1804`, `0x1809` |
| `<group>.<timestamp>.before-<reason>.json` | snapshot before every write |

- Originals are created with `FileMode.CreateNew` (never overwritten, even in a race), marked read-only and carry a SHA‑256.
  A corrupt or tampered backup is rejected.
- Every write verifies by reading back; on mismatch it restores the snapshot taken just before.
- "Original" means **what was in the EC the first time AvellCC read it**. On the tested laptop that is the factory state:
  zeroed fan table, `0x751 = 0x00`, `0x7A5 = 0x00` → the EC runs its internal curve. The vendor app keeps no copy
  of its default tables on disk or in the registry, so **keep a copy of the backup folder off the laptop**.

## Rule 3 — Safe write order

- Enabling a mechanism: write and verify the data first, flip the mode bit **last**
  (direct control: duty → `0x751` bit 6 → duty again).
- Restoring: flip the mode bit back **first**, then restore the data. The EC never runs a half-written configuration.

## Decision: the fan curve is applied in software

The original plan was to store the curve in the EC firmware, so it would keep working even if the app crashed.
Hardware tests showed **this EC accepts no custom curve in firmware** (docs/HARDWARE.md, tests 1–5):

- the `0xF00` table is ignored, because this is a "legacy" EC (`0x78E` bit 6 = 0);
- Office Advanced (`0x743`) is ignored too, with or without `0x727` bit 6.

The only mechanism that works is **direct control** (`0x1804/0x1809` + `0x751` bit 6), the same one tuxedo-drivers uses
on Linux for this EC generation. So the service runs a loop: read temperature → compute duty → write.

| Risk | Mitigation |
|---|---|
| Service dies and the duty stays at a low value | Windows service recovery restarts it in 2/5/10 s and it takes control again; 25 % minimum duty |
| Temperature spikes | Emergency in the loop: CPU ≥ 92 °C or GPU ≥ 87 °C → 100 % until 10 °C below the limit |
| Invalid sensor reading (0/255) | 100 % until readings are valid again |
| Sleep / hibernate | Direct control is released on suspend and re-acquired on resume (as tuxedo-drivers does) |
| Something clears `0x751` bit 6 (Fn key, BIOS) | The loop checks `0x751` every cycle and takes control again |
| Normal stop / Ctrl+C / exception | `0x751` is restored to the original: the firmware resumes |
| Reboot | The BIOS resets `0x751`: the firmware runs until the service starts |
| Vendor app starts | The loop releases control and reports Blocked |

Last line of defence: the CPU throttles itself at Tjmax, and the EC has its own thermal shutdown.

### Curve controller (`FanCurveController`)

- Points (°C, %) per CPU and GPU, linear interpolation; by default both fans use the hotter of the two curves (shared heat pipes).
- Hysteresis on the temperature: rising follows immediately, falling only after dropping more than `hysteresis` °C.
- Ramp: speeding up is immediate; slowing down is limited to 2 %/s (no audible pumping).
- Writes only on change, plus a 30 s keep-alive.
- Validation: increasing temperatures, non-decreasing duty, last point at or below 90 °C with at least 80 %,
  hysteresis 2–10 °C, 25 % effective minimum.

## Service and UI

| Piece | Detail |
|---|---|
| `AvellCC` service | LocalSystem, automatic start; recovery restarts after 2 s / 5 s / 10 s (`sc failure`) |
| Loop | `FanControlLoop.Tick()` every 2 s (~11 WMI reads, ~1.3 s) |
| Power events | `PowerAwareServiceLifetime`: suspend → firmware; resume → take control again and re-apply the keyboard |
| Crash and restart | if `0x751` bit 6 was left set: in curve mode the service takes over (`takeOver`); in firmware mode it releases it |
| State | `settings.json` (atomic writes), `curves\*.json` (built-in curves only created when missing) |
| IPC | pipe `AvellCC.Control`, one JSON line per message. ACL: SYSTEM and Administrators (full), INTERACTIVE (read/write) |
| Validation | the service validates every curve and keyboard setting; the UI validates too, to show errors earlier |
| UI | `AvellCC.exe`: Dashboard, Fans (mode + editor with chart), Keyboard, Settings; closing goes to the tray |
| System readings | `Core/Monitoring/SystemMonitor` runs **in the UI process** (normal user): CPU (`GetSystemTimes`), GPU per adapter and VRAM (PDH `GPU Engine` / `GPU Adapter Memory`, names via DXGI), disk active time (PDH `PhysicalDisk`), memory, drives, network, power plan. Sampled every 1 s only while the Dashboard is visible. Temperatures and fans still come from the service. `avellcc sys` prints the same data |
| Cleanup buttons | *Free up memory* → pipe `freeMemory` → service trims all working sets (admin; standby cache untouched; once per 20 s). *Clean up disk* → opens Windows Storage settings / `cleanmgr.exe` as the user. Rationale: [CLEANUP.md](CLEANUP.md) |
| Icon | drawn in code (`App/AppIcon.cs`): exe icon `Assets\AvellCC.ico` (generated with `AvellCC.exe --export-icon`), tray icon = same drawing + status dot |
| Localization | `Languages\<code>.json` next to the exe; see `src/AvellCC.App/Languages/README.md` |
| Keyboard profiles | named presets stored in `settings.json` (`keyboardProfiles`, active one in `keyboardProfile`); pipe commands `saveKeyboardProfile`, `deleteKeyboardProfile`, `applyKeyboardProfile`; Keyboard tab and tray submenu |
| Mode button | service: `EcEventWatcher` (WMI `AcpiTest_EventULong`, code `0xAB`) **or** an EC-made change of `0x751` bits 4/5/7 (polled every 2 s; the EC sometimes skips the event) → in curve mode next curve and `status.modeSwitchCount++`; the app shows its own on-screen notice (`OsdPopup`) when the count changes. Firmware mode: ignored. `pressModeButton` simulates it in `--simulate`. Details: HARDWARE.md |
| Service log | Windows event log, Application, source `AvellCC.Service`: AvellCC messages from Information up (fan mode, mode button, EC events, `0x751` changes), others from Warning |

**Pipe security:** any signed-in user can change the curve and the RGB, but cannot go past the limits the service
validates (25 % minimum, last point ≤ 90 °C with ≥ 80 %, emergency override). That is acceptable for a personal laptop;
to restrict it, replace INTERACTIVE with a specific user's SID in `PipeServer`.

## Milestones

| Milestone | Deliverable | Status |
|---|---|---|
| M0 | Read-only diagnostic + original backups | ✅ |
| M1 | Keyboard RGB: per-zone color, brightness, firmware effects | ✅ verified on hardware |
| M2 | Telemetry: RPM, EC temperatures, duty | ✅ |
| M3 | Custom fan curve (software, direct control) with backups and safety | ✅ verified under load |
| M4 | Windows service + WPF UI + tray + start with Windows + localization | ✅ implemented, verified in simulation |
| M5 | Profiles (AC/battery, per app), hotkeys | planned |
