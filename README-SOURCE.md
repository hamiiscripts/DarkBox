# DarkBox by Hamii — Source Code (v4.2.0)

Windows folder locker: C# (.NET Framework 4.8, WinForms host) + WebView2 web UI.

## Layout
- `src/` — the app: Program.cs (modes: panel / --background tray agent / --unlock <id> / --installer),
  LockerForm.cs (WebView2 host + bridge), Core.cs (lock engine, PBKDF2 hashes, config),
  WatchBrain.cs (auto re-lock), Installer.cs (in-app installer), CameraCapture.cs (intruder photo),
  TrayContext.cs, AppIcon.cs, MiniJson.cs
- `ui.html` — the entire UI (embedded as a resource, served on https://locker.app/)
- `logo.png`, `avatar.jpg` — embedded branding images
- `HamiiFolderLocker.csproj` — app project (Microsoft.Web.WebView2 + DirectShowLib via NuGet)
- `setup-boot/` — DarkBoxSetup.csproj: the bootstrapper that becomes DarkBox-Setup.exe
  (embeds payload.zip, extracts it, runs the app with --installer;
  the same exe copied as Uninstall.exe handles --uninstall)
- `pkg/HamiiFolderLocker/` — user-facing package contents (exe + DLLs + README.md)
- `installer.nsi` — RETIRED NSIS script (kept for history; the modern installer replaced it)

## Data (NOT in the install folder)
`%APPDATA%\HamiiFolderLockerPro\config.json` — pattern/passcode hashes, folder list,
theme, settings. Locked folders live in a hidden sibling vault: `.<Name>.hvault-<id8>`.

## Build (needs .NET SDK 8)
```
dotnet build -c Release                                   -> bin/Release/net48/HamiiFolderLocker.exe
cp bin/Release/net48/HamiiFolderLocker.exe pkg/HamiiFolderLocker/
mkdir build-payload && cp pkg/HamiiFolderLocker/* build-payload/
(cd build-payload && zip ../setup-boot/payload.zip *)
dotnet build setup-boot/DarkBoxSetup.csproj -c Release    -> DarkBox-Setup.exe
```
