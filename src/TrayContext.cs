using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

// Background agent: lives in the system tray after PC startup.
// - Re-locks folders left open (when that setting is on)
// - Watches unlocked folders and locks each one the moment its
//   Explorer window closes (when auto-lock is on)
// - v3.3: locks everything when the PC locks/sleeps, locks open
//   folders after the auto-lock timer, and listens for the global
//   Ctrl+Shift+L "lock everything now" hotkey.
public class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly Timer _timer;
    private readonly WatchBrain _brain = new WatchBrain();
    private readonly Dictionary<string, DateTime> _openSince = new Dictionary<string, DateTime>();
    private HotkeyWindow _hotkey;

    public TrayContext()
    {
        try
        {
            var cfg = Core.Load();
            if (cfg.Startup) Core.SetStartup(true); // point startup at this EXE
            if (cfg.RelockOnStartup) Core.RelockUnlocked(cfg);
        }
        catch (Exception ex) { Core.Log("Tray startup failed: " + ex.Message); }

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Get(),
            Text = "DarkBox — running",
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Admin Panel", null, (s, e) => OpenAdmin());
        menu.Items.Add("Lock All Folders Now", null, (s, e) => LockAll("All folders locked."));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) => Exit());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (s, e) => OpenAdmin();

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        try
        {
            _hotkey = new HotkeyWindow();
            _hotkey.Pressed += (s, e) => LockAll("All folders locked (Ctrl+Shift+L).");
        }
        catch (Exception ex) { Core.Log("Hotkey failed: " + ex.Message); }

        // v4.1.0: 4 s (was 2 s) — the tick re-reads the config and, when
        // auto-lock is on, enumerates every Explorer window through COM.
        // On weak PCs the 2 s cadence was a steady background cost for a
        // check that only needs to fire seconds after a window closes.
        // The tick also exits early now when there is nothing to watch
        // (no open folders, auto-lock off, timer off) instead of paying
        // for the COM enumeration every time.
        _timer = new Timer { Interval = 4000 };
        _timer.Tick += Tick;
        _timer.Start();
    }

    private void Balloon(string text)
    {
        try { _tray.ShowBalloonTip(2200, "DarkBox", text, ToolTipIcon.Info); }
        catch { }
    }

    private void LockAll(string balloon)
    {
        try
        {
            var cfg = Core.Load();
            Core.RelockUnlocked(cfg);
            if (balloon != null) Balloon(balloon);
        }
        catch (Exception ex) { Core.Log("LockAll failed: " + ex.Message); }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionLock) return;
        try
        {
            var cfg = Core.Load();
            if (!cfg.LockOnPcLock) return;
            Core.RelockUnlocked(cfg);
            Core.LogActivity("pclock", "PC locked — all open folders were locked");
        }
        catch (Exception ex) { Core.Log("Session lock handler failed: " + ex.Message); }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Suspend) return;
        try
        {
            var cfg = Core.Load();
            if (!cfg.LockOnPcLock) return;
            Core.RelockUnlocked(cfg);
            Core.LogActivity("pclock", "PC went to sleep — all open folders were locked");
        }
        catch (Exception ex) { Core.Log("Suspend handler failed: " + ex.Message); }
    }

    private void OpenAdmin()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Core.ExePath,
                Arguments = "--admin",
                UseShellExecute = false,
            });
        }
        catch (Exception ex) { Core.Log("Admin open failed: " + ex.Message); }
    }

    private void Tick(object sender, EventArgs e)
    {
        try
        {
            var cfg = Core.Load();

            // v4.1.0 early exit: if every folder is already locked and no
            // auto-lock timer is set, there is nothing this tick could do
            // — skip the Explorer COM enumeration entirely.
            bool anyOpen = false;
            foreach (var f in cfg.Folders) if (!f.Locked) { anyOpen = true; break; }
            if (!anyOpen && cfg.AutoLockMinutes <= 0) return;

            // Auto-lock timer: a folder left open longer than the chosen
            // number of minutes locks itself, window open or not.
            if (cfg.AutoLockMinutes > 0)
            {
                foreach (var f in cfg.Folders)
                {
                    if (f.Locked) { _openSince.Remove(f.Id); continue; }
                    DateTime since;
                    if (!_openSince.TryGetValue(f.Id, out since))
                    {
                        _openSince[f.Id] = DateTime.Now;
                        continue;
                    }
                    if ((DateTime.Now - since).TotalMinutes >= cfg.AutoLockMinutes)
                    {
                        try
                        {
                            Core.LockFolder(cfg, f);
                            _openSince.Remove(f.Id);
                            Core.LogActivity("autolock",
                                "Auto-locked after " + cfg.AutoLockMinutes + " minutes: \"" + f.Name + "\"");
                            Balloon("Auto-locked: \"" + f.Name + "\"");
                        }
                        catch { /* a file is in use — retry on the next tick */ }
                    }
                }
            }
            else if (_openSince.Count > 0) _openSince.Clear();

            if (!cfg.AutoLockOnClose) return;
            var open = Core.GetOpenExplorerPaths();
            if (open == null) return;
            var items = new List<WatchItem>();
            foreach (var f in cfg.Folders)
                items.Add(new WatchItem(f.Id, Core.NormalizePath(f.Path), f.Locked));
            foreach (string id in _brain.Poll(items, open))
            {
                var entry = Core.Find(cfg, id);
                if (entry == null || entry.Locked) continue;
                try { Core.LockFolder(cfg, entry); }
                catch { _brain.Reset(id); } // file in use — retry later
            }
        }
        catch { }
    }

    private void Exit()
    {
        _timer.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_hotkey != null) { _hotkey.Unregister(); _hotkey.DestroyHandle(); _hotkey = null; }
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }

    // Hidden window that owns the global Ctrl+Shift+L registration.
    private class HotkeyWindow : NativeWindow
    {
        public event EventHandler Pressed;
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 0xDB01;
        private const uint MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004;
        private const uint VK_L = 0x4C;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
            RegisterHotKey(Handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, VK_L);
        }

        public void Unregister()
        {
            try { UnregisterHotKey(Handle, HOTKEY_ID); } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && (int)m.WParam == HOTKEY_ID)
            {
                var handler = Pressed;
                if (handler != null) handler(this, EventArgs.Empty);
            }
            base.WndProc(ref m);
        }
    }
}
