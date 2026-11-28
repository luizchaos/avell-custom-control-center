# Hardware notes — Avell A65 LIV (Uniwill platform)

Everything here was established on one machine (Avell A65/C65 LIV, BIOS N.1.01) on 2026‑11‑26, from:

- static, read-only analysis of the vendor app's service (`GCUService.exe`, *Avell Custom Control* 1.1.0.45),
- the open-source [tuxedo-drivers](https://github.com/tuxedocomputers/tuxedo-drivers) (Linux) for Uniwill ECs,
- read-only probes and supervised write tests on the hardware (each test log is below).

## TL;DR — what works on this laptop

| Feature | Mechanism | Status |
|---|---|---|
| Read EC RAM | WMI `AcpiTest_MULong.GetSetULong` (admin) | ✅ |
| CPU / GPU temperature | EC `0x43E` / `0x44F` | ✅ |
| Fan RPM | EC `0x464/0x465` and `0x46C/0x46D` | ✅ |
| **Custom fan speed** | **direct control: duty at `0x1804/0x1809` + `0x751` bit 6** | ✅ (software curve) |
| Firmware fan tables (`0xF00`) | user table + `0x7A5` bit 3 | ❌ ignored by this EC |
| Office Advanced curve (`0x743`) | 5 speeds + `0x751 = 0xA0` | ❌ ignored by this EC |
| Fan Boost | `0x751` bit 6 alone = 100 % | ✅ (= our 100 %) |
| Keyboard RGB, 4 zones | ITE 8291 HID feature reports | ✅ |

## Identification

| Item | Value |
|---|---|
| SMBIOS manufacturer / model | `Avell High Performance` / `A65 LIV` |
| BIOS | AMI `N.1.01` |
| Platform | **Uniwill** (not Clevo: `InsydeDCHU.dll` / `ClevoEcInfo.dll` do not apply) |
| EC project ID | `0x740` = `0x0F` (`EC_PROJECT_CML_Gaming` in the vendor's list) |
| EC fan-control status | `0x78E` = `0x0C` → bit 6 = 0 → **no "universal" fan control** (legacy EC) |
| Keyboard controller | ITE 8291, `048D:CE00` rev 0x0002, firmware `11 03` |

## 1. EC access

Through the BIOS's own ACPI/WMI method. No third-party driver. **Requires administrator rights.**

```
Namespace : root\WMI
Object    : AcpiTest_MULong.InstanceName='ACPI\PNP0C14\1_1'
Method    : GetSetULong(Data: UInt64) -> Return: UInt32

Read      : Data = 0x10000000000 + addr          ; value = Return & 0xFF
Write     : Data = ((UInt64)value << 16) + addr  ; Return ignored
```

`addr` is 16-bit (`0x0000–0xFFFF`). Confirmed in the IL of the vendor's `MyControlCenter.WMIEC::WMIReadECRAM/WMIWriteECRAM`
and on the hardware (consistent reads).

> ⚠️ Without the read flag (`1 << 40`), a "read" becomes a **write of 0** to that address.
> Every read goes through one tested function (`WmiEc.Read`); never build the argument by hand.

**Latency:** about **100–150 ms per WMI call**. Telemetry reads only what it needs (~11 reads per 2 s cycle).

The vendor also ships `ECIO.dll` (`OemGetECRAM`/`OemSetECRAM`) on top of the `SparkIO.sys` driver (direct EC port I/O).
**AvellCC does not use it**: it competes with the firmware for the EC and depends on a third-party kernel driver.

## 2. Fans

### Telemetry

| Address | Meaning |
|---|---|
| `0x43E` | **CPU temperature (°C)**: tracks the `\_SB.ECTZ` thermal zone within 1–3 °C (that zone is fed by the EC) |
| `0x44F` | **GPU temperature (°C)**: tracks `nvidia-smi`, a few seconds behind |
| `0x464` (hi) / `0x465` (lo) | Fan 1 RPM |
| `0x46C` (hi) / `0x46D` (lo) | Fan 2 RPM. The vendor uses `0x46B` as the low byte, but on this model `0x46B` is constant (0x78, or 0x64 with `0x751 = 0xA0`), which shows up as 256-RPM steps |
| `0x1804` / `0x1809` | Current fan 1 / fan 2 duty (0..200 = 0..100 %), also while the firmware is in control |
| `0x460` / `0x461` | Firmware curve: current level of fan 1 / that level's threshold (60/70/80 °C seen) |
| `0x468` / `0x469` | Same for fan 2 |

The vendor app does **not** read temperatures from the EC: it uses the Windows `Thermal Zone Information` counter for the
CPU and NVAPI for the GPU. AvellCC uses the EC values, because those are what the EC itself sees.

### Direct control (what AvellCC uses)

Same mechanism as tuxedo-drivers for Uniwill ECs **without** universal fan control (`0x78E` bit 6 = 0):

```
enable : write duty to 0x1804 (fan 1) and 0x1809 (fan 2), 0..200
         set 0x751 bit 6 ("full fan mode", 0x40)
         rewrite the duty a few times right away (before the EC finishes ramping to maximum)
keep   : rewrite the duty on change, plus a 30 s keep-alive
disable: clear only 0x751 bit 6 → the firmware resumes immediately
         (the other bits belong to the EC / operational mode button, see below)
```

Safety rules (from tuxedo-drivers, enforced by AvellCC): **never write 0** (0 = 30 % for 3 minutes, then stop;
1 = stop), minimum 25 %, maximum `0xC8`.

**Duty → RPM, measured** (fan 1; fan 2 is 0–60 RPM higher):

| Duty | 31 % | 36 % | 44 % | 50 % | 60 % | 70 % | 76 % | 85 % | 91 % | 94 % | 97 % | 100 % |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| RPM | 2043 | 2433 | 2894 | 3240 | 3648 | 4138 | 4515 | 4900 | 5133 | 5265 | 5390 | **5543** |

Not linear (~68 RPM/% near 30 %, ~45 RPM/% near 100 %). Encoded in `FanCalibration` for UI estimates only.

### Fan Boost (vendor app)

`MyFanManager_Intel.SetFanBoost` only does `0x751 |= 0x40`, the same bit 6. Measured with `avellcc fan boost-test`:
the EC itself writes duty 200/200 and the fans reach **5543 / 5586 RPM** within ~3 s. That is exactly our 100 %
(5543 / 5590). **Fan Boost = 100 % = the EC's ceiling**; there is no higher speed. AvellCC's `max` preset is equivalent.

### Fan mode register `0x751`

| Value | Vendor meaning | Code |
|---|---|---|
| `0x00` | Gaming: firmware internal curve (factory state) | `SetFanMode(1)` |
| `0x40` | Performance / Turbo / Benchmark / Fan Boost (bit 6) | `SetFanMode(1/4)`, `SetFanBoost` |
| `0xA0` | Office Advanced / Standard (caps the fans at the T5 speed, ~60 %) | `SetFanMode(2/3)` |
| `0x80 + L` | Office Basic, fixed level L (0..5) | `SetBasicFanLevel` |

### Operational mode button (2026-11-27)

What was measured on the A65 LIV (service log with every EC event and every `0x751` change, plus filmed tests):

- **A real press** makes the EC step its operational mode in `0x751` bits 4, 5, 7:
  `0x30 → 0x20 → 0xA0 → 0x30…` (with AvellCC's bit 6: `0x70 → 0x60 → 0xE0`). One press = one step, every time.
- **The WMI event** `root\WMI AcpiTest_EventULong`, `ULong = 0xAB` (vendor `WMIEC.WMIHandleEvent` →
  `OSD_FanModeSwitch` → `MyFanCtrl.ModeSwitchChanged`) is **not a reliable button signal**: the EC also sends it in
  bursts of three (+4 s, +9 s, sometimes ending with `0x00`) with nobody touching the laptop, without changing `0x751`.
  Using it made the curve "change by itself" three times.
- **Do not write the mode bits back**: the EC flips them again ~6 s later (seen every 6 s while AvellCC tried it),
  which looks like more presses.
- The vendor's "app present" flags (`0x7A6` bit 0 for fans, `0x748` bit 0 for the light bar) were set during all of
  this; they did not change the behavior. The EC also sends `0xA7` + `0x43` when `0x751` bit 6 is cleared or set.

AvellCC (service), in curve mode with *Settings → Operational mode button changes the fan curve* on (default): a
change of `0x751` bits 4/5/7 that AvellCC did not make (checked every 2 s) = one press → next fan curve (alphabetical,
wraps around), and the app shows its own on-screen notice (Windows balloons are hidden when notifications / Do not
disturb are off). The EC's new operational mode is kept. Firmware mode or option off: nothing. `0xAB` and every other EC
event is only logged (Windows event log, Application, source `AvellCC.Service`, together with every `0x751` change).
Because the button changes `0x751`, handing control back to the firmware only clears bit 6 instead of restoring the
old byte.

### Keyboard effects on the 4-zone keyboard (filmed 2026-11-27)

Work: Static, Breathing, Wave, Reactive (animates), Rainbow (static rainbow across the zones), Flash, Mix.
Leave the previous lighting unchanged (per-key effects, or need the vendor app, e.g. Music): Ripple, Marquee,
Raindrop, Stack, Impact, Aurora, Neon, Spark, Gaming (fixed purple only), RippleO, Alphabet, StarSpark, StarHitting,
Thinking, Music. The app only offers the working ones; saved settings with another effect fall back to Wave
(`Ite8291.FourZoneEffects`, `KeyboardSettings.EffectiveEffect`). The CLI still accepts all of them for experiments.

### Mechanisms this EC ignores (kept in the code for other models)

**16-level fan table (`0xF00–0xF5F`).** Two tables (CPU at `0xF00/0xF10/0xF20`, GPU at `0xF30/0xF40/0xF50`) of
end temperature, start temperature and duty (% × 2), enabled by `0x7A5` bit 3 ("UserHiModeToFantable") in the vendor app,
or by `0x7C5` bit 7 + `0x7C6` bit 2 in tuxedo-drivers — but **only on ECs with `0x78E` bit 6 set**. Factory state here:
the table is all zeros and `0x7A5 = 0x00` (the EC runs its internal curve). The vendor app's own "fan speed curve"
feature (`UserSet_FanSpeedCurveSetting`) edits this table, so it has no effect on this model either.

**Office Mode Advanced ("MyFan2/MyFan3").** 5 speeds T1..T5 (% × 2) at `0x743–0x747`, defaults at `0x786–0x78A`,
min speed / min temp / extra speed at `0x79E / 0x79F / 0x7A0`, `0x782` bit 3 = MyFan3 supported (`0x782 = 0x5D` here).
Active preset in the vendor registry: T1..T5 = 30/35/40/50/60 %. With `0x751 = 0xA0` the EC follows the *default*
speeds, not `0x743` (test 4), with or without `0x727` bit 6 ("custom profile", tuxedo-drivers) (test 5).

### Test log (2026‑11‑26)

| # | Test | Result |
|---|---|---|
| 0 | Read-only diagnostic under load | CPU 69→84 °C at `0x43E`, GPU 52→75 °C at `0x44F`; fans 3317→5097 / 3192→4984 RPM |
| 1 | `0xF00` table (`safe-trial`) + `0x751 = 0xA0` | fans capped at ~3993 / 3684 RPM while the curve asked for 90 %; CPU held at 80 °C. The cap came from `0xA0` (Office mode, T5 ≈ 60 %), not from the table |
| 2 | `0xF00` table, 100 % from 40 °C, `0x751` unchanged | idle RPM identical to firmware → **table ignored** |
| 3 | Same with `0x751 = 0xA0` | identical to firmware → **table ignored** |
| 4 | Office Advanced, T1..T5 = 70..100 %, `0x751 = 0xA0` | level 1 stayed at ~2043 RPM (30 %) → **`0x743` ignored** |
| 5 | Same + `0x727` bit 6 | ~2043 RPM → **ignored** |
| 6 | **Direct control**, 70 % | **~4230 / ~4250 RPM, stable for 60 s**; firmware resumed instantly when released |
| 7 | **Software curve** `balanced` (3 min, idle → load) | duty followed the curve point by point; RPM followed with 2–3 s lag; hysteresis held small drops (58→55, 64→61 °C); 24 writes; CPU 86 °C at 100 %, no emergency; firmware resumed on exit |
| 8 | Fan Boost (`0x751` bit 6 only) | EC wrote duty 200 itself; 5543 / 5586 RPM = our 100 % |

Writing the 96-byte table plus verification took ~40 s over WMI; restoring ~45 s.

### EC dump `0x700–0x7FF` (factory state, `0x751 = 0x00`)

```
       00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F
0700:  00 00 00 00 00 00 00 00 00 00 D2 00 00 00 00 00
0710:  70 71 71 06 53 D2 70 06 53 D1 70 06 53 81 70 80
0720:  42 00 04 00 00 00 00 00 00 00 00 00 09 00 00 00
0730:  78 78 A5 01 64 64 A5 01 00 30 48 00 01 00 00 00
0740:  0F 00 02 3C 46 50 64 78 81 00 00 00 1F 00 01 B0
0750:  00 00 6C 00 00 00 00 00 40 00 00 42 41 00 10 00
0760:  0B 00 00 00 00 E0 83 00 00 00 00 00 00 00 00 00
0780:  00 00 5D 00 00 00 3C 46 50 64 78 00 04 00 0C 00
0790:  00 00 00 00 00 00 00 00 00 00 79 D3 10 E4 00 00
07A0:  00 00 00 00 80 00 08 00 00 00 00 00 00 00 00 00
07F0:  00 00 00 00 00 00 00 00 00 00 00 13 00 B8 74 4B
```
(Rows `0x770` and `0x7B0–0x7E0` were all zeros and are omitted.)
`0x730–0x732` = 120/120/165 W and `0x734–0x736` = 100/100/165 W are PL1/PL2/PL4 (Gaming / Office), matching the vendor registry.

### Other registers (vendor `Define.ECSpec`, not used by AvellCC)

| Address | Meaning |
|---|---|
| `0x730–0x736`, `0x783–0x785`, `0x7A7–0x7A9` | Power limits PL1/PL2/PL4 (gaming / office / battery saver) |
| `0x738` | CPU Tau |
| `0x765`, `0x766` | Support bytes (`0x766` bit 2 = RGB keyboard) |
| `0x767` | Trigger byte (bit 5 = RGB keyboard, bit 6 = logo, bit 7 = welcome, bit 2 = Fan Boost) |
| `0x768` | Status byte |
| `0x769–0x76B` | R/G/B level of EC-driven single-zone keyboards |
| `0x7B9` | Battery charge limit |
| `0x748–0x74B` | EC light bar (control, R, G, B) — see §3.1 |

## 3. Keyboard RGB — ITE 8291 (HID)

| Device | Interface | Usage page | Role (vendor names) |
|---|---|---|---|
| `048D:CE00` rev `0x0002` | `MI_01` | `0xFF12` | `USAGE_PAGE_4Zone` → **4-zone keyboard** |
| `048D:6005` rev `0x0003` | `MI_01` | `0xFF03` | `USAGE_PAGE_Ligbar` / `ME_2ND` → light bar (see §3.1; the bar stayed dark) |

Both expose a **9-byte feature report**: `byte[0] = 0x00` (report ID) followed by 8 command bytes, sent with
`HidD_SetFeature`; queries are read back with `HidD_GetFeature`. No administrator rights needed. The vendor waits
1 ms between commands and serializes everything with a lock.

| Cmd | Bytes (`[0]` = report ID) | Function |
|---|---|---|
| `0x08` | `00 08 ctrl effect speed bright color dir save` | Set effect / on / off |
| `0x09` | `00 09 02 level 00 00 00 00 00` | Brightness |
| `0x12` | `00 12 00 00 08 00 00 00 00` | Prepare "picture" (per-key, followed by 65-byte output reports) |
| `0x14` | `00 14 00 idx R G B 00 00` | Color of slot `idx` (zones `1..4` on the 4-zone keyboard) |
| `0x16` | `00 16 00 row 00 00 00 00 00` | Row index (per-key) |
| `0x1A` | `00 1A 00 a b 00 00 00 c` | Timeout / sleep |
| `0x80` | `00 80 …` → GetFeature: `fw = [2..5]` | Firmware version (query) |
| `0x88` | `00 88 …` → GetFeature: mirrors `0x08` from byte 2 | Current effect (query) |

- `ctrl` (byte 2 of `0x08`): `1` = LED off, `2` = apply, `3` = welcome, `4` = night.
- **Brightness** (UI 0..4 → firmware): `0, 8, 22, 36, 50` (max `0x32`).
- **Speed** (UI 0..4, slow → fast): `10, 7, 5, 3, 1`.
- **Direction:** `0` left→right, `1` right→left, `2` bottom→top, `3` top→bottom, `4` on key press.

**Effects** (`ITE_SPEC`): Static `0x01`, Breathing `0x02`, Wave `0x03`, Reactive `0x04`, Rainbow `0x05`, Ripple `0x06`,
Marquee `0x09`, Raindrop `0x0A`, Stack `0x0C`, Impact `0x0D`, Aurora `0x0E`, Neon `0x0F`, Spark `0x11`, Flash `0x12`,
Mix `0x13`, Gaming `0x15`, RippleO `0x16`, Alphabet `0x17`, StarSpark `0x18`, StarHitting `0x19`, Thinking `0x21`,
Music `0x22`, UserMode (per-key) `0x33`.

**Sequences** (vendor `LM_ITE_RGB.Set_ITE_Effect_Type_FwMode`, 4-zone types):

| Case | Packets |
|---|---|
| One color (static or effect) | `0x14` slot 1 → `0x08 [2, effect, speed, bright, color=1, dir, save]` |
| Palette (effect with several colors) | `0x14` slots 1..N → `0x08 [..., color=8, ...]` |
| No color (firmware rainbow) | `0x08 [..., color=0, ...]` |
| Live zone color | only `0x14` on slot `zone` (1..4) |

AvellCC does "one color per zone" as `0x14` slot 1 → `0x08 Static color=1` → `0x14` slots 1..4.

**Color correction** (`WKDColor.cheatRGB_4Zone(projectId, r, g, b)`): green and blue LEDs are stronger, so the vendor attenuates them.

```
if projectId in {6, 7}:
    (255,165,0) -> (255, 60, 0)
    (255,255,0) -> (255,120, 0)
    otherwise   -> (R, 120*G/255, 220*B/255)
else:
    (255,100,0) -> (255, 40, 0)
    (255,169,0) -> (255, 70, 0)
    otherwise   -> (R, 100*G/255, 200*B/255)
```

This laptop (project ID `0x0F`) uses the `else` branch.

**Confirmed on the hardware:**

- Firmware query (`0x80`): `11 03 00 00`.
- Effect query (`0x88`) mirrors `0x08` from byte 2. Factory/vendor state found: `02 03 01 24 08 01` = Apply, Wave,
  speed 1, brightness 36, cycling palette, right→left. The `save` byte reads back as 0 even when saved.
- Zones **1→4 are left→right**, colors correct, and white looks white with the correction (visually confirmed).
- Palette effects work: `kb effect wave -s 1 -b 4 -d ltr -c FF0000,00FF00,0000FF` moves right; slot 1 leads,
  so reading left to right the palette appears reversed and cyclic.

To return to the vendor's effect (exact bytes read above): `avellcc kb raw 00 08 02 03 01 24 08 01 01`.
The palette used by the original Wave (slots 1..7) cannot be read back.

### 3.1 Front light bar — investigated, stays dark (2026-11-27)

The vendor service (`GCUService.exe`) has three light bar paths. All three were reproduced; every command was accepted,
but **the bar never lit — not even with Avell Custom Control itself running**, so the cause is most likely hardware or
BIOS on this unit, not the protocol. Since 0.8.0 the app has an **experimental** light bar card (Keyboard tab) built on the HID path below; it is marked untested.

| Path (vendor class) | What it does | Result here |
|---|---|---|
| HID light bar (`HIDRGBLightbar`, light bar type `"3"`) | ITE `048D:6005` `MI_01`, usage page `0xFF03`, firmware `15 02`. Same 9-byte feature commands as the keyboard. For colors the vendor uses **user mode**: `0x08 [2, 0x33, 0, brightness, 0, 0, save]`, then 8 rows of `0x16 [0, row]` + a 65-byte output report `[0, 0, B×21, G×21, R×21]` (`LM_ITE_RGB.Set_ITE_Effect_Type_UserMode_Lightbar`, LED map in `ConvertToLightBarMap`, color correction `WKDColor.cheatRGB_HIDLightbar`) | state reads back (`0x88`), LEDs dark |
| EC RGB light bar (`MyRgbLightbarManager`, type `"2"`) | `0x749/0x74A/0x74B` = R/G/B level (vendor level 9 → 200/170/180, `RGBLightBarTable`); `0x748` control: bit 0 app present, bit 2 off, bit 3 no breathing, bit 4/5 apply, bit 7 colorful | EC clears the apply bit, LEDs dark |
| EC on/off (`MySettingManager.LightBar_Trigger`) | `0x767 = (0x767 & 0xFD) + 2` toggles; `0x768` bit 1 = on | trigger consumed, `0x768` bit 1 stays 0 |

Support flags: `HKLM\SOFTWARE\OEM\GamingCenter2\Support` `LightBar=1`, `RGBLB=1`; EC `0x78E` bit 4 (EC RGB light bar)
= 0. Original EC values (before any write): `0x748=0x80`, `0x749–0x74B=0`, saved in
`C:\ProgramData\AvellCC\backup\ec-lightbar.original.json`. Original HID state: `ctrl=Welcome effect=0x20 speed=5
brightness=36` (restore: `avellcc lb raw 00 08 03 20 05 24 08 00 00`).

AvellCC implementation (experimental): `Core/Keyboard/Lightbar.cs` (HID path: user mode for a single color, firmware effects Breathing/Wave/Rainbow/Flash/Mix, off; same color on every LED, no per-model map or color correction), `LightbarSettings` in `settings.json`, pipe `setLightbar`, `info.lightbarPresent`. The EC paths (`0x748`–`0x74B`, `0x767` trigger) are not used by the app.

CLI used for the experiments: `avellcc lb info|color|fill|raw` (HID, no admin) and
`avellcc ec lightbar [toggle --yes | set <ctrl> <r> <g> <b> --yes]` (EC, admin, refused while the service runs).
