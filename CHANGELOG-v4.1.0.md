# DarkBox by Hamii — v4.1.0 changelog (12 Nov 2026)

All 5 fixes from the user's list. Source: `~/workspace/folder-locker-web/`.

## 1. Windows Defender / SmartScreen on a new PC
- Full publisher identity in both EXEs (`HamiiFolderLocker.csproj` +
  `setup-boot/DarkBoxSetup.csproj`): Company Hamii Scripts, Product DarkBox
  by Hamii, Copyright © 2026, Description.
- New `src/app.manifest`: `asInvoker` (no admin prompt — DarkBox never
  needs admin), Windows 10/11 compatibility, PerMonitorV2 DPI, long paths.
- New `SIGNING.md` + `sign.bat`: the permanent fix is a code-signing
  certificate (Standard ~$70–100/yr, EV ~$200–300/yr for instant
  reputation) + Microsoft file submission. **Honest limit: without a
  certificate no code change can remove the blue "Run anyway" screen —
  that is Windows by design, not a DarkBox bug.** Until then the user
  guide in SIGNING.md is the official More info → Run anyway path.

## 2. Folder add no longer lies about "ENCRYPTION PENDING"
- Adding a folder now opens the **preloader at once** with a live progress
  bar (file X of Y, %) — `showBusy`/`hideBusy` messages from
  `LockerForm.DoAddFolderPath`, progress via `Core.Progress` (now carries
  done/total/folderId).
- The folder's own row also shows a **progress bar under it** while it is
  busy (`Core.CurrentProgressFolder` tracks which folder the numbers
  belong to).
- When the lock really finishes, a **"Folder locked ✓" popup** appears
  (folder name, file count via `Core.CountVaultFiles`, AES-256 badge).
  Same popup for Lock from the list.
- The `ENCRYPTION PENDING` tag now appears ONLY after the job finished
  but encryption is genuinely deferred (tray lock without the vault key),
  reading "ENCRYPTION PENDING — finishes on next sign-in" with a tooltip.
  While a job runs, the tag is hidden and the progress bar shows instead.

## 3. Intruder location — Speedtest-style (IP lookup)
- `LockerForm.DeviceInfo()` rewritten: public-IP geolocation via
  ip-api.com (free, no key) gives **city, region, country, ISP and public
  IP**, cached 10 minutes, 4-second timeout, on the background thread.
- Also now recorded: WiFi name (SSID) + **signal %**, local IP (labelled
  "Local" — the old 192.168.x.x could never give a location), **public IP**,
  router/gateway IP. Windows Location is now only a fallback (1.5 s, was 3 s).
- Offline = the log says "Unknown — no internet lookup and Windows
  Location is off" instead of a bare "Unknown (location off)".
- Honest limit written in the code: an IP lookup gives city + ISP, never
  a street address or the WiFi modem/router model — no site (not even
  Speedtest) can know that from an IP.

## 4. Activity / Logs UI redesigned
- Filter chips: All / Locks / Wrong tries / Intruder / System.
- Search box (folder, PC, WiFi, city).
- Date groups: Today / Yesterday / date, event count ("X of Y events").
- Intruder entries are cards: bigger photo, severity pill, and the device
  line parsed into a neat labelled block (PC / User / WiFi / IP / Location)
  instead of one long sentence.
- Activity feed now loads 100 events (was 40) so filters have something
  to filter.

## 5. Smooth on low-end PCs
- Guard + tray watch ticks: 2 s → **4 s** (COM Explorer enumeration is
  the expensive part; auto-lock still fires ~4 s after close).
- Tray tick **early exit**: when every folder is already locked and no
  auto-lock timer is set, the COM enumeration is skipped entirely.
- Progress posts **coalesced** (one queued BeginInvoke at a time) so the
  progress feed itself can't flood the UI thread.
- Folder-size polling: 20 s → **60 s** in the UI (server already
  throttled at 60 s).
- Lite Mode still auto-on for ≤4-core PCs.

## Build (Windows PC, .NET SDK 8)
```bat
dotnet build -c Release
copy bin\Release\net48\HamiiFolderLocker.exe pkg\HamiiFolderLocker\
mkdir build-payload && copy pkg\HamiiFolderLocker\* build-payload\
(cd build-payload && zip ../setup-boot/payload.zip *)
dotnet build setup-boot/DarkBoxSetup.csproj -c Release
REM then, with a certificate: sign.bat
```
This Linux VM **does** build it: .NET SDK 8.0.428 lives at `~/.dotnet/dotnet`
(just not on PATH) — `dotnet build -c Release` + payload.zip + setup-boot
build worked 12 Nov, 0 errors. Setup EXE delivered as
`~/workspace/your_files/DarkBox-Setup-v4.1.0.exe` (836 KB, PE32, payload
verified to contain the v4.1.0 app EXE with the new UI strings).
