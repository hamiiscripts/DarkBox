# DarkBox by Hamii — Developer README (v4.2.0)

For the developer who will work on this code next. Read this before you
change anything. It covers the build, the architecture, the rules that
must not be broken, and the release procedure.

## What it is

DarkBox is a Windows folder locker. C# (.NET Framework 4.8, WinForms
host) + WebView2 web UI. One EXE (`HamiiFolderLocker.exe`) runs in four
modes, and a bootstrapper setup (`DarkBoxSetup.exe`) installs it.

- **Admin Panel** — the main window: add folders, lock/unlock, settings,
  activity, intruder log, backup/restore, updates.
- **Locker (`--unlock <folder-id>`)** — the small unlock window that opens
  when you double-click a locked folder's shortcut. Pattern/passcode
  only, then the folder opens.
- **Tray agent (`--background`)** — sits in the system tray, watches
  folders, auto re-locks, handles hotkeys.
- **Installer (`--installer` / `--uninstall`)** — run by the setup; the
  same setup EXE copied as `Uninstall.exe` handles uninstall.

Contact / owner: Hamid Ali — hamidali@hamiiscripts.com

## Prerequisites

- Windows 10/11 to run and test.
- **.NET SDK 8** to build (the app targets .NET Framework 4.8, built
  with the modern SDK). On this project's Linux build VM the SDK is
  **8.0.428 at `~/.dotnet/dotnet`** and is NOT on PATH — always run:
  `export PATH="$HOME/.dotnet:$PATH"` first.
- NuGet packages restore automatically: `Microsoft.Web.WebView2`
  (SDK **1.0.2903.40**) and `DirectShowLib` (intruder camera).

## Project layout

```
src/
  Program.cs        entry point, mode dispatch, tray agent startup,
                    context-menu registration, single-instance guard
  LockerForm.cs     WebView2 host + JS bridge (all "cmd:" messages),
                    the single worker queue, sign-in / DEK handling
  Core.cs           the lock engine: vault move, AES-256 encrypt/decrypt,
                    config load/save, PBKDF2 hashes, DEK wrap/unwrap,
                    shortcuts, activity log
  WatchBrain.cs     auto re-lock watcher (tray agent)
  Installer.cs      in-app install/uninstall logic
  WebView2Setup.cs  WebView2 runtime detection + auto-install
                    (registry check, full standalone installer ~205 MB,
                    fixed-version .cab / extracted folder support)
  CameraCapture.cs  intruder photo (DirectShow)
  TrayContext.cs    tray icon + menu
  AppIcon.cs        icon helpers
  MiniJson.cs       tiny JSON reader/writer (no Newtonsoft dependency)
  app.manifest      asInvoker, publisher identity (SmartScreen)
ui.html             THE WHOLE UI — embedded as a resource, served on
                    https://locker.app/ via WebResourceRequestedFilter
setup-boot/         DarkBoxSetup.csproj — the bootstrapper that becomes
                    DarkBox-Setup.exe (embeds payload.zip, extracts it,
                    runs the app with --installer; also shows the
                    progress window: MB + %, Cancel)
pkg/HamiiFolderLocker/  package contents (exe + DLLs + README.md)
build-payload/      staging dir rebuilt from pkg/ on every release
CHANGELOG-vX.Y.Z.md one file per version — write a new one per release
README-SOURCE.md    short layout/build note (this file is the full one)
SIGNING.md, sign.bat  code-signing notes (see "SmartScreen" below)
```

## How locking works (the core idea)

Locking is TWO layers. Keep them separate in your head:

1. **Vault move (always).** The folder is MOVED from its original path
   to a hidden vault directory next to it:
   - New vaults: a random name, `.dbx-<16 hex chars>` — reveals nothing
     about the folder (the old name-derived vault could be renamed back
     by hand).
   - Legacy vaults: `.<Name>.hvault-<first 8 chars of id>`.
   - The vault gets Hidden + System attributes. A shortcut
     `<Name>.lnk` is left at the original location; it launches
     `HamiiFolderLocker.exe --unlock <folder-id>`.
2. **Encryption (High End Lock only).** Every file inside the vault is
   AES-256 encrypted in place and renamed with a `.dbe` extension.
   The key is a per-folder key derived from the vault key (DEK) and the
   folder ID — `FolderKey(dek, folderId)`. Files are encrypted with a
   temp file + atomic move, so a crash mid-file never destroys data
   (`.dbtmp` recovery on the next run).

Unlocking always decrypts INSIDE the vault first, and only moves the
folder back after every file decrypted cleanly. Never move first and
decrypt after — a failed file would strand a half-moved folder.

### Lock modes (v4.2.0)

Each folder has `LockMode` in config.json (`"lock_mode"`):

- `"just"` — **Just Lock (default).** Vault move only, no encryption.
  Lock/unlock is instant even for huge folders on low-end PCs. The
  folder still cannot be opened without DarkBox (it is not at its
  original location, it is hidden, and only the shortcut points at it).
- `"high"` — **High End Lock.** Vault move + AES-256 encryption of
  every file. Even if someone finds the vault on disk, the files are
  unreadable without the key. Slow on big folders — that is expected.

Rules:
- New folders default to `"just"`. Settings toggle "New folders use
  High End Lock by default" (config `default_high_lock`, default
  false) changes the default for new folders only.
- Old configs migrate on load: a folder with `encrypted` or
  `enc_pending` becomes `"high"`, everything else `"just"`.
- `Core.LockFolder` encrypts only when `e.LockMode == "high"`.
- `Core.SetLockMode(cfg, e, mode)`: switching to Just cancels pending
  encryption (`EncPending = false`); already-encrypted files stay
  encrypted until the next unlock, which decrypts them as usual.
  Switching a locked folder to High encrypts it in place right away
  when the DEK is in memory (LockerForm does that as a worker job),
  otherwise marks it pending for the next sign-in.
- The old global `encrypt_files` / `EncryptFiles` is RETIRED. Do not
  bring it back.

### Keys and credentials

- The vault key (DEK) is a random 32-byte key. It is stored WRAPPED
  (encrypted) under each credential: pattern, passcode, recovery code,
  recovery answer (`dek_pattern`, `dek_passcode`, ... in config).
- Pattern/passcode themselves are stored only as PBKDF2-SHA256 hashes
  with salt (`iterations` in config). Verifying a credential unwraps
  the DEK into memory (`_dek` in LockerForm).
- A folder can also have its OWN pattern/passcode. The panel credential
  is always a master key for every folder. A folder's own key alone
  cannot decrypt an encrypted folder — by design, the panel key is
  needed for the DEK.
- Decoy pattern/passcode opens a harmless fake panel with fake folders.

### The worker queue (read this before touching jobs)

ALL heavy work (lock / unlock / encrypt / decrypt / healing) runs on
ONE low-priority background thread fed by a FIFO queue
(`EnqueueJob` in LockerForm.cs). Never run heavy work on the UI thread
— a big decrypt froze the window and a force-close mid-decrypt once
lost a folder.

- `EnqueueJob` — normal queue (maintenance, locks, pending work).
- `EnqueueJobFront` (v4.2.0) — user-initiated UNLOCKS jump ahead of
  queued maintenance. A job already running still finishes first;
  this only reorders the queue.
- `ProcessPendingEncryption` runs on sign-in: finishes deferred
  encryption for High End folders and heals stray `.dbe` files in
  open folders. It does NOT run from the locker unlock window
  (`UnlockDek(..., runPending: false)`) — that was the v4.2.0 fix for
  "locker unlock is much slower than admin unlock": the unlock was
  queued behind every pending folder's encryption.

### WebView2

- Runtime detection is REGISTRY-based:
  `SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-9251-3EDEE3F2E132}`
  in HKLM + HKCU, 64-bit and 32-bit views. File-based fallback:
  `msedgewebview2.exe` on disk counts.
- **The bundled WebView2 SDK 1.0.2903.40 does NOT have
  `GetAvailableCoreWebView2BrowserVersionString`** (build error CS0117).
  Do NOT re-add that API call.
- The setup can also use a fixed-version runtime: it finds a downloaded
  `.cab` (`Microsoft.WebView2.FixedVersionRuntime.*.x64`) or an already
  extracted folder in Downloads/Desktop/setup folder and installs it to
  `%LOCALAPPDATA%\Programs\HamiiFolderLocker\WebView2Runtime`; the app
  uses it via `CreateAsync(browserExecutableFolder)`.
- If the WebView2 user-data folder
  (`%APPDATA%\HamiiFolderLockerPro\WebView2`) is locked by a stale
  process, init fails with `COMException (0x8007139F)`. v4.1.7+: the
  app retries with a fresh `WebView2-<timestamp>` data folder, and the
  setup kills any stale `HamiiFolderLocker.exe` before launching the
  wizard. Diagnose from `%APPDATA%\HamiiFolderLockerPro\locker.log`.
- The UI is `ui.html`, embedded as a resource. The C# side talks to it
  with `PostWebMessageAsString` / `WebMessageReceived` (`"cmd:"`
  messages). Folder rows are rendered by JS from the state pushed by
  `SendState()`; per-folder fields sent include `lockMode`,
  `encrypted`, `encPending`, `hasOwn`, `color`, `note`, `busy`.

### Config file

`%APPDATA%\HamiiFolderLockerPro\config.json`. NEVER delete it in code
or in testing — it holds the DEK wraps; deleting it destroys the only
way back into encrypted folders. Same for the Desktop backup
`darkbox-config-backup.json` if present.

Per-folder fields: `id`, `name`, `path`, `locked`, `pattern`,
`passcode`, `color`, `note`, `vault`, `enc_pending`, `encrypted`,
`lock_mode`.

## Safety rules (do not break these)

1. Never delete config.json, never re-lock an already locked folder,
   never encrypt a folder that is already encrypted (double
   encryption). `LockFolder` returns early if `e.Locked`.
2. Decrypt inside the vault BEFORE moving the folder back.
3. Save the folder entry (ID + vault name) to config BEFORE anything
   moves. A kill mid-lock must never leave a folder with no record.
4. Never ship a simulated/fake login or a fake success state. A lock
   is "done" only when the job finished (the popup confirms it).
5. Never add a backdoor, master key, or no-pattern rescue. There is
   none, by design.
6. Keep the UI SVG icons only — no emoji in the app UI.
7. Do not change nearby UI when fixing one thing. Edit only what was
   asked for.

## SmartScreen

The EXEs carry publisher identity + an `asInvoker` manifest, but the
blue "Windows protected your PC" screen only disappears permanently
with a paid code-signing certificate (Standard ~$70–100/year,
EV ~$200–300/year). See `SIGNING.md` / `sign.bat`. Without it, users
click "More info → Run anyway".

## Build & release (do this exactly, in order)

```bat
export PATH="$HOME/.dotnet:$PATH"   (Linux build VM only; SDK 8.0.428)
dotnet build -c Release                                   -> app, 0 errors
cp bin/Release/net48/HamiiFolderLocker.exe pkg/HamiiFolderLocker/
rm -rf build-payload && mkdir build-payload
cp -r pkg/HamiiFolderLocker/* build-payload/
(cd build-payload && zip ../setup-boot/payload.zip *)
dotnet build setup-boot/DarkBoxSetup.csproj -c Release    -> DarkBox-Setup.exe
```

Verify before delivering:
- `strings -e l` (UTF-16) on the app EXE: version string + new
  user-visible strings present.
- Plain `strings` on the app EXE: new `ui.html` bits present
  (embedded resource is UTF-8, so `-e l` misses it).
- `unzip -p setup-boot/payload.zip HamiiFolderLocker.exe` → the
  payload really contains the NEW app EXE (check the same strings).

Version bump (every release):
1. `HamiiFolderLocker.csproj` + `setup-boot/DarkBoxSetup.csproj`:
   `<Version>` / `<FileVersion>` / `<AssemblyVersion>`.
2. `src/app.manifest`: `assemblyIdentity version`.
3. `README-SOURCE.md` header.
4. `darkbox-version.json` (in `~/workspace/your_files/`): `version` +
   `notes` — the user uploads this to
   `https://hamiiscripts.com/darkbox/`.
5. New `CHANGELOG-vX.Y.Z.md` in this folder.
6. Deliverables: `DarkBox-Setup-vX.Y.Z.exe` (the setup EXE) +
   `DarkBox-Source-Code-vX.Y.Z.zip` (this folder minus
   `bin/ obj/ pkg/ build-payload/ setup-boot/bin/ setup-boot/obj/`).
   EXE only to the user unless they explicitly say the work is done —
   then the source ZIP too.

## Changelog index

- v4.2.0 — per-folder lock mode (Just / High End, default Just);
  locker unlock no longer waits behind pending encryption.
- v4.1.7 — fresh WebView2 data folder fallback; setup kills stale
  DarkBox; tray-icon crash fix (>63 chars).
- v4.1.6 — app/wizard also check the install-dir runtime folder.
- v4.1.5 — setup copies an already-extracted fixed-version folder.
- v4.1.4 — fixed-version runtime from a downloaded .cab.
- v4.1.3 — file-based runtime detection + full diagnostics.
- v4.1.2 — real setup progress window (MB + %), full standalone
  installer (~205 MB).
- v4.1.1 — WebView2 auto-install.
- v4.1.0 — SmartScreen identity, live lock progress, intruder
  location, redesigned activity logs, low-end smoothness.
- v4.0.5 — AES-256 file encryption, save-first add, async unlock.
- Older changelogs: CHANGELOG-vX.Y.Z.md in this folder.
