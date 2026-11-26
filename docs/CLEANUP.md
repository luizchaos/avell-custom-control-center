# Investigation: "Free up memory" and "Clean up the disk"

Requested as dashboard buttons, like the ones in *MSI Center*. **Nothing here is implemented yet.**
This page records what each button can really do on Windows, the risks, and a recommendation.

## Free up memory (RAM)

### What Windows does with RAM

Windows keeps "unused" RAM busy as a cache. Task Manager's *In use* is memory that processes need right now; the rest is:

| List | Meaning | Example on the test laptop (64 GB, idle) |
|---|---|---|
| Free + zero | really empty | 43.5 GB |
| Standby | cached file and program pages; handed out instantly when someone needs memory | 6.1 GB |
| Modified | changed pages waiting to be written to disk / page file | 0.7 GB |

*Available* (49.6 GB here) = free + standby. Standby memory is not "used" — it makes programs open faster and is
discarded automatically, without cost, the moment a process needs RAM.

### What "RAM cleaner" buttons do

All of them use the same three documented-or-semi-documented mechanisms (RAMMap, ISLC, MSI/ASUS/Razer tools):

| Action | API | Effect | Needs |
|---|---|---|---|
| Trim working sets | `EmptyWorkingSet(process)` per process, or `NtSetSystemInformation(SystemMemoryListInformation, MemoryEmptyWorkingSets)` | Moves every process's pages to the standby/modified lists. *In use* drops a lot. | admin (`PROCESS_SET_QUOTA` on other processes) |
| Purge standby list | `NtSetSystemInformation(..., MemoryPurgeStandbyList)` (or `MemoryPurgeLowPriorityStandbyList`) | Drops the cache: *Free* goes up. | admin + `SeProfileSingleProcessPrivilege` |
| Flush modified list | `NtSetSystemInformation(..., MemoryFlushModifiedList)` | Writes dirty pages to disk now. | admin + same privilege |

### Is it dangerous?

It does **not** lose data and does not kill processes — the concern that "other processes may be using the RAM" is
handled by Windows: trimmed pages stay in RAM (standby/modified) or on the page file and are paged back on next use.
But it is **counter-productive**:

- right after trimming, every app faults its pages back in → stutters, disk activity, sometimes a slower game for minutes;
- purging standby throws away the file cache → the next app launch / level load reads from disk again;
- the "freed" number is cosmetic: Windows would have reclaimed that memory on demand anyway.

The one legitimate case is a known Windows 10 bug era (2018–2019) where the standby list was not released fast enough
during games, causing stutter; tools like ISLC purge it only when free memory drops below a threshold.

### Feasibility in AvellCC

Easy: the **service** already runs as LocalSystem (has the privileges), so it would be one new pipe command
(`trimMemory { workingSets, standby }`) calling `NtSetSystemInformation`, plus a button with a confirmation. ~100 lines.

**Recommendation:** if added, offer only *"Purge standby cache"* with an honest tooltip
("frees cached memory; apps may load slower right after"), never run it automatically, and do not trim working sets.
A low-value feature; it mostly makes a number look better.

## Clean up the disk

### Options

| Option | What it removes | Risk | Effort |
|---|---|---|---|
| **Open Windows' own tools** — `ms-settings:storagesense` (Storage Sense / Cleanup recommendations) or `cleanmgr.exe` | temp files, Windows Update leftovers, Delivery Optimization cache, thumbnails, Recycle Bin, old upgrade files — user picks | none (Microsoft's tool, user confirms) | trivial (one button) |
| **Own safe subset** | `%TEMP%` and `C:\Windows\Temp` files older than 24 h, Recycle Bin (`SHEmptyRecycleBin`), thumbnail cache, GPU shader caches (`%LOCALAPPDATA%\NVIDIA\DXCache`, `D3DSCache`) | low: files in use are skipped; shader caches are rebuilt (first game launch slower) | medium: size preview, per-category checkboxes, confirmation, report |
| **Component store** — `DISM /Online /Cleanup-Image /StartComponentCleanup` | superseded Windows Update components | low, but slow (minutes) and irreversible (cannot uninstall those updates) | low, admin (service) |
| Anything in Downloads, Documents, browser profiles, app data | — | **data loss** — never | — |

On the test laptop `%TEMP%` held ~3.9 GB, which is the typical win.

### Recommendation

1. **First step:** a *"Clean up disk"* button that opens `ms-settings:storagesense` (falls back to `cleanmgr.exe`), next to
   the drive bars on the dashboard. Zero risk, no admin, Windows shows sizes and asks for confirmation.
2. **Optional later:** an own "safe subset" dialog (temp > 24 h, Recycle Bin, shader caches) with a size preview and
   confirmation. Temp and shader-cache cleanup runs as the user (no service needed); `C:\Windows\Temp` and DISM need the service.
3. Never delete user folders, and never run cleanup automatically.
