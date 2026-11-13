using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Core.ExePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        bool installerMode = args.Length >= 1 && args[0] == "--installer";
        if (!installerMode)
        {
            EnsureShortcuts();
            try { EnsureContextMenu(Core.Load().ContextMenu); } catch { }
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (s, e) => Core.Log("UI exception: " + e.Exception);

        try
        {
            if (args.Length >= 1 && args[0] == "--background")
            {
                bool created;
                using (var mutex = new Mutex(true, "HamiiFolderLockerPro.Background", out created))
                {
                    if (!created) return; // agent already running
                    Application.Run(new TrayContext());
                }
                return;
            }

            if (installerMode)
            {
                string setupPath = "";
                for (int i = 1; i + 1 < args.Length; i++)
                    if (args[i] == "--setuppath") setupPath = args[i + 1];
                Installer.SetupExePath = setupPath;
                Application.Run(new LockerForm(LockerForm.InstallerId));
                return;
            }

            if (args.Length >= 2 && args[0] == "--lockpath")
            {
                // Explorer right-click: "Lock with DarkBox"
                var cfg = Core.Load();
                if (!Core.IsSetUp(cfg))
                {
                    MessageBox.Show("Please open DarkBox and set a pattern or passcode first.",
                        "DarkBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                string full = Path.GetFullPath(args[1].TrimEnd('\\'));
                foreach (var e in cfg.Folders)
                {
                    if (string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!e.Locked) Core.LockFolder(cfg, e);
                        Balloon("DarkBox", "\"" + e.Name + "\" is locked.");
                        return;
                    }
                }
                var added = Core.AddFolder(cfg, full, null);
                Balloon("DarkBox", "\"" + added.Name + "\" locked with DarkBox.");
                return;
            }

            if (args.Length >= 2 && args[0] == "--unlock")
            {
                var cfg = Core.Load();
                var entry = Core.Find(cfg, args[1]);
                if (entry == null)
                {
                    // The config lost this folder's entry (e.g. after the data
                    // folder was moved during testing). Dead-end errors help
                    // nobody: explain and open the panel instead.
                    MessageBox.Show("This folder is not in DarkBox's list anymore, so its shortcut cannot open it.\n\n" +
                        "Your files are NOT lost — the folder is still on the disk. The Admin Panel will open now; " +
                        "if the folder is still hidden on the disk, it can be restored from there.",
                        "DarkBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    Application.Run(new LockerForm(null));
                    return;
                }
                if (!Core.IsSetUp(cfg))
                {
                    MessageBox.Show("Please open the Admin Panel and set a pattern or passcode first.",
                        "DarkBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (!entry.Locked)
                {
                    Core.OpenInExplorer(entry.Path);
                    return;
                }
                Application.Run(new LockerForm(args[1]));
                return;
            }

            // default / --admin — the web UI handles setup + login itself.
            var config = Core.Load();
            Core.RemoveLegacyStartup();
            Core.SetStartup(config.Startup); // this EXE takes over the startup entry
            Application.Run(new LockerForm(null));
        }
        catch (Exception ex)
        {
            Core.Log("Fatal: " + ex);
            MessageBox.Show("Something went wrong — details are in locker.log.\n\n" + ex.Message,
                "DarkBox", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Make sure the app can always be found again: Start Menu (root and
    // folder) + Desktop shortcuts pointing at this EXE, recreated whenever
    // they go missing — from whichever copy the user launches.
    private static void EnsureShortcuts()
    {
        try
        {
            string exe = Core.ExePath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
            string dir = Path.GetDirectoryName(exe);
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            TryShortcut(Path.Combine(programs, "DarkBox.lnk"), exe, dir);
            string folder = Path.Combine(programs, "DarkBox");
            Directory.CreateDirectory(folder);
            TryShortcut(Path.Combine(folder, "DarkBox.lnk"), exe, dir);
            TryShortcut(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "DarkBox.lnk"), exe, dir);
        }
        catch (Exception ex) { Core.Log("Shortcut heal failed: " + ex.Message); }
    }

    private static void TryShortcut(string linkPath, string target, string workDir)
    {
        if (File.Exists(linkPath)) return;
        CreateShortcut(linkPath, target, workDir);
    }

    internal static void CreateShortcut(string linkPath, string target, string workDir)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic sc = shell.CreateShortcut(linkPath);
            sc.TargetPath = target;
            sc.WorkingDirectory = workDir;
            sc.IconLocation = target;
            sc.Description = "DarkBox by Hamii";
            sc.Save();
        }
        catch (Exception ex) { Core.Log("Shortcut create failed: " + ex.Message); }
    }

    // Explorer right-click entry (HKCU, per-user). v4.0.
    internal static void EnsureContextMenu(bool enable)
    {
        const string keyPath = @"Software\Classes\Directory\shell\DarkBoxLock";
        try
        {
            if (!enable)
            {
                Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
                return;
            }
            // Skip the registry writes when everything is already correct
            // (this runs on every launch — cheap PCs feel every write).
            string wantCmd = "\"" + Core.ExePath + "\" --lockpath \"%1\"";
            using (var c = Registry.CurrentUser.OpenSubKey(keyPath + @"\command"))
            {
                if (c != null && string.Equals(Convert.ToString(c.GetValue("")), wantCmd,
                        StringComparison.OrdinalIgnoreCase))
                    return;
            }
            using (var k = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                k.SetValue("", "Lock with DarkBox");
                k.SetValue("Icon", Core.ExePath + ",0");
            }
            using (var c = Registry.CurrentUser.CreateSubKey(keyPath + @"\command"))
                c.SetValue("", wantCmd);
        }
        catch (Exception ex) { Core.Log("Context menu failed: " + ex.Message); }
    }

    private static void Balloon(string title, string text)
    {
        try
        {
            using (var ni = new NotifyIcon())
            {
                ni.Icon = AppIcon.Get();
                ni.Visible = true;
                ni.ShowBalloonTip(2500, title, text, ToolTipIcon.Info);
                Thread.Sleep(2800);
            }
        }
        catch { }
    }

    // Start the background agent alongside the admin panel (if it is not
    // already running) so auto-lock-on-close always works.
    internal static void StartAgentIfNeeded(LockerConfig cfg)
    {
        if (cfg == null || !cfg.Startup) return;
        try
        {
            try
            {
                using (Mutex.OpenExisting("HamiiFolderLockerPro.Background")) { return; }
            }
            catch (WaitHandleCannotBeOpenedException) { }
            Process.Start(new ProcessStartInfo
            {
                FileName = Core.ExePath,
                Arguments = "--background",
                UseShellExecute = false,
            });
        }
        catch (Exception ex) { Core.Log("Agent start failed: " + ex.Message); }
    }
}
