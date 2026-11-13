DarkBox by Hamii — User Guide
Version 4.2.0 (12 November 2026)
==================================

WHAT IS DARKBOX?
----------------
DarkBox locks your folders so nobody can open them without you.
A locked folder disappears from its place — only DarkBox can open
it again. You can also choose High End Lock, which encrypts the
files as well, so even if someone finds the hidden folder, the
files cannot be read.

INSTALLATION
------------
1. Run DarkBox-Setup.exe.
2. If Windows shows a blue "Windows protected your PC" screen,
   click "More info", then "Run anyway". (This is normal for now —
   the app is not code-signed yet.)
3. Follow the installer. It installs DarkBox and, if needed,
   downloads and installs the WebView2 runtime by itself (about
   205 MB, with a progress bar — please let it finish).
4. When the setup finishes, DarkBox opens and asks you to set your
   lock: draw a pattern, or set a passcode, or both. This is your
   master key — remember it. Without it, locked folders cannot be
   opened.
5. Optional but recommended: set a Recovery Code or a Security
   Question in Settings, so you can get back in if you forget
   your pattern/passcode.

ADD AND LOCK A FOLDER
---------------------
1. Open DarkBox (Admin Panel).
2. Click "Add Folder" and pick the folder you want to protect.
3. The folder locks immediately and disappears from its place.
   A shortcut with the same name stays there — double-clicking it
   opens the DarkBox unlock window.

UNLOCK A FOLDER
---------------
- Double-click the folder's shortcut, enter your pattern or
  passcode, and the folder opens.
- Or open the Admin Panel and click "Unlock" on the folder.
- To lock it again, click "Lock" on the folder in the panel,
  or right-click the folder in Explorer (DarkBox menu), or let
  DarkBox auto-lock it when you close it.

LOCK MODES — JUST LOCK vs HIGH END LOCK
---------------------------------------
Every folder has its own lock mode. Click the lock icon on a
folder row to change it.

JUST LOCK (default)
  Fast. The folder is moved to a hidden vault and cannot be
  opened without DarkBox. No encryption, so locking and unlocking
  are instant — even for very large folders on a slow PC.
  This is a real lock, not just hiding: the folder is not at
  its original location anymore.

HIGH END LOCK
  The folder is moved to the vault AND every file inside is
  encrypted with AES-256. Even if someone finds the hidden vault,
  the files cannot be read without your key.
  Use this for your most private folders. Locking and unlocking
  a large folder takes longer — that is normal, encryption takes
  time. On a slow PC, prefer Just Lock for big folders.

New folders use Just Lock by default. You can change the default
for new folders in Settings ("New folders use High End Lock by
default"). Folders that were already encrypted stay on High End
Lock unless you change them.

OTHER FEATURES
--------------
- Own key per folder: give a folder its own pattern/passcode.
  Your panel pattern/passcode still works as a master key.
- Auto-lock: folders lock again automatically when you close
  them, when the PC locks, or after a time you set.
- Intruder alert: if someone enters a wrong pattern/passcode
  3 times, DarkBox takes their photo (if a camera is available)
  and records their location.
- Activity log: see everything that happened — locks, unlocks,
  wrong attempts.
- Backup: export an encrypted backup of your settings and folder
  list (Settings > Backup). Keep it somewhere safe.

TROUBLESHOOTING
---------------
- "WebView2 is missing" / the panel is blank:
  Run the setup again — it installs WebView2 automatically.
  Or install "Microsoft Edge WebView2 Runtime" from Microsoft.
- A folder does not open after unlock:
  Close DarkBox completely (including the tray icon) and open
  it again, then try once more. If it still fails, check the log
  file at %APPDATA%\HamiiFolderLockerPro\locker.log and send it
  to support.
- Forgot pattern/passcode:
  Use your Recovery Code or Security Question from the login
  screen. If you never set one, there is no way back in — that
  is by design, so nobody else can get in either.
- Locked folder still visible in Explorer:
  Press F5 to refresh Explorer. The folder is in the hidden vault;
  the shortcut is the only thing at the old place.

UNINSTALL
---------
Run Uninstall.exe from the DarkBox install folder, or uninstall
from Windows Settings > Apps. Uninstalling does NOT delete your
locked folders — unlock all folders first, then uninstall.

SUPPORT
-------
Developer: Hamid Ali (Hamii Scripts)
Email: hamidali@hamiiscripts.com
Website: https://hamiiscripts.com

Version 4.2.0 — per-folder lock mode (Just Lock / High End Lock)
and faster unlock from the locker window.
