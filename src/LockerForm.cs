using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// Borderless host window for the web UI (ui.html, embedded).
// All locker logic lives in Core.cs; this form is only the shell:
// it passes JSON messages between the page and Core, and — in unlock
// mode — becomes the folder's guard after a successful unlock.
public class LockerForm : Form
{
    public const string InstallerId = "__installer__";
    private readonly string _unlockId; // null = admin panel
    private LockerConfig _cfg;
    private WebView2 _web;
    private bool _authed;
    private bool _webReady;
    // v4.1.1: one automatic WebView2 recovery (corrupt data folder, or
    // missing runtime) before giving up with the manual download prompt.
    private bool _webView2Retried;
    private Label _initStatus;

    // v3.3: wrong-try protection + decoy (fake) mode
    private int _fails;
    private DateTime _frozenUntil = DateTime.MinValue;
    private Timer _freezeTimer;
    private bool _decoy;
    private List<Dictionary<string, object>> _fakeFolders;

    // v4.0: vault key in memory, async lock work, recovery session
    private byte[] _dek;
    private readonly System.Collections.Generic.HashSet<string> _busy =
        new System.Collections.Generic.HashSet<string>();
    private bool _recoveryAuthed;
    private bool _updateChecked;

    // v4.0.2: ALL heavy work (lock/unlock/encryption/healing) runs on ONE
    // low-priority worker thread — parallel jobs were thrashing weak disks.
    private readonly System.Collections.Generic.Queue<Action> _jobs =
        new System.Collections.Generic.Queue<Action>();
    private readonly System.Threading.AutoResetEvent _jobSignal =
        new System.Threading.AutoResetEvent(false);

    private void EnqueueJob(Action job)
    {
        lock (_jobs) _jobs.Enqueue(job);
        _jobSignal.Set();
    }

    // v4.2.0: user-initiated unlocks jump ahead of queued maintenance
    // (pending encryption / healing) — the folder the user just asked
    // to open should not wait behind other folders' background work.
    // A job already RUNNING still finishes first; this only reorders
    // the queue.
    private void EnqueueJobFront(Action job)
    {
        lock (_jobs)
        {
            var q = new System.Collections.Generic.Queue<Action>();
            q.Enqueue(job);
            while (_jobs.Count > 0) q.Enqueue(_jobs.Dequeue());
            while (q.Count > 0) _jobs.Enqueue(q.Dequeue());
        }
        _jobSignal.Set();
    }

    private void StartWorker()
    {
        var t = new System.Threading.Thread(() =>
        {
            while (true)
            {
                Action job = null;
                lock (_jobs) { if (_jobs.Count > 0) job = _jobs.Dequeue(); }
                if (job == null) { _jobSignal.WaitOne(1000); continue; }
                try { job(); } catch (Exception ex) { Core.Log("Worker: " + ex.Message); }
            }
        });
        t.IsBackground = true;
        t.Priority = System.Threading.ThreadPriority.BelowNormal;
        t.Start();
    }

    // guard mode (unlock only)
    private NotifyIcon _tray;
    private Timer _watchTimer;
    private WatchBrain _brain;
    private bool _guarding;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public LockerForm(string unlockId)
    {
        _unlockId = unlockId;
        StartWorker();
        // v4.0.5: long file work (encrypt/decrypt) reports progress so the
        // busy overlay shows real numbers instead of a frozen spinner.
        // Throttled to ~2 posts per second so small-file folders don't flood
        // the UI with messages.
        // v4.1.0: coalesced too — if a progress post is already queued for
        // the UI thread, the next one waits. On a weak PC a flood of
        // BeginInvoke calls was itself a source of "Not Responding".
        // The post now carries done/total/folderId so the UI can draw the
        // preloader bar AND the progress bar under that folder's own row.
        DateTime lastProgress = DateTime.MinValue;
        bool progressQueued = false;
        Core.Progress = (op, done, total) =>
        {
            if (total <= 0) return;
            var now = DateTime.Now;
            if (done < total && (now - lastProgress).TotalMilliseconds < 500) return;
            if (progressQueued) return;
            lastProgress = now;
            progressQueued = true;
            string folderId = Core.CurrentProgressFolder;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    progressQueued = false;
                    Post(new Dictionary<string, object>
                    {
                        { "type", "progress" },
                        { "text", op + " — file " + done + " of " + total },
                        { "done", (double)done },
                        { "total", (double)total },
                        { "folderId", folderId ?? "" },
                    });
                }));
            }
            catch { progressQueued = false; }
        };
        _cfg = Core.Load();
        Text = "DarkBox";
        Icon = AppIcon.Get();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(250, 248, 245);
        if (IsUnlockMode)
        {
            ClientSize = new Size(500, 728);
            MinimumSize = MaximumSize = Size;
        }
        else
        {
            ClientSize = new Size(1020, 680);
            MinimumSize = new Size(920, 620);
        }
        // On small screens (or high display scaling) the window must never
        // be taller than the visible work area, or pages start to scroll.
        try
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int maxH = wa.Height - 8, maxW = wa.Width - 8;
            if (ClientSize.Height > maxH || ClientSize.Width > maxW)
            {
                ClientSize = new Size(Math.Min(ClientSize.Width, maxW), Math.Min(ClientSize.Height, maxH));
                if (IsUnlockMode) MinimumSize = MaximumSize = Size;
                else if (MinimumSize.Height > ClientSize.Height || MinimumSize.Width > ClientSize.Width)
                    MinimumSize = new Size(Math.Min(MinimumSize.Width, ClientSize.Width), Math.Min(MinimumSize.Height, ClientSize.Height));
            }
        }
        catch { }
        _web = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_web);
        Load += (s, e) => InitWeb();
        // Pick up changes made in another window (e.g. a new theme color
        // chosen in the admin panel) whenever this window is focused again.
        Activated += (s, e) =>
        {
            try
            {
                if (!_webReady) return;
                _cfg = Core.Load();
                SendState();
            }
            catch { }
        };
    }

    private bool IsUnlockMode { get { return _unlockId != null && _unlockId != InstallerId; } }
    private bool IsInstallerMode { get { return _unlockId == InstallerId; } }

    // ---------------- WebView2 setup ----------------
    private void InitWeb()
    {
        InitWebWithFolder(Path.Combine(Core.DataDir, "WebView2"), false);
    }

    private async void InitWebWithFolder(string udf, bool isFreshFallback)
    {
        try
        {
            Directory.CreateDirectory(udf);
            string fixedFolder = WebView2Setup.GetFixedVersionFolder();
            if (fixedFolder != null) Core.Log("Using fixed-version WebView2 runtime at " + fixedFolder);
            var env = await CoreWebView2Environment.CreateAsync(fixedFolder, udf, null);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.WebMessageReceived += OnWebMessage;
            // Page and fonts are embedded resources, served on a private
            // origin so nothing is written to disk and nothing is fetched
            // from the internet at runtime.
            core.AddWebResourceRequestedFilter("https://locker.app/*",
                CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResource;
            core.NavigationCompleted += (s, e) => { _webReady = true; HideInitStatus(); SendState(); };
            core.Navigate("https://locker.app/index.html");
        }
        catch (Exception ex)
        {
            Core.Log("WebView2 init failed: " + ex);
            // v4.1.1: don't just give up with a download link.
            // One automatic retry, then the old manual prompt:
            //  - runtime IS installed but init still failed: the WebView2
            //    user-data folder is usually corrupt — move it aside and
            //    try once more (a fresh folder is rebuilt).
            //  - v4.1.7: if the folder cannot even be moved aside (access
            //    denied — a stale process still holds it, init error
            //    0x8007139F), use a brand-new user-data folder instead.
            //  - runtime NOT installed: offer to fetch it from Microsoft
            //    and install it now, then open the window automatically.
            if (!_webView2Retried)
            {
                _webView2Retried = true;
                if (WebView2Setup.IsRuntimeInstalled())
                {
                    bool moved = false;
                    try
                    {
                        string defaultUdf = Path.Combine(Core.DataDir, "WebView2");
                        if (Directory.Exists(defaultUdf))
                        {
                            string bak = Path.Combine(Core.DataDir,
                                "WebView2.corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                            Directory.Move(defaultUdf, bak);
                            Core.Log("WebView2 user data looked corrupt — moved to " + bak);
                            moved = true;
                        }
                        else moved = true;
                    }
                    catch (Exception iex) { Core.Log("WebView2 data reset failed: " + iex.Message); }
                    if (moved) { InitWeb(); return; }
                    if (!isFreshFallback)
                    {
                        string fresh = Path.Combine(Core.DataDir,
                            "WebView2-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                        Core.Log("WebView2 data folder is locked by another process — trying a fresh folder: " + fresh);
                        InitWebWithFolder(fresh, true);
                        return;
                    }
                    ShowDownloadPrompt();
                    return;
                }
                var ask = MessageBox.Show(
                    "Microsoft Edge WebView2 Runtime is needed to show the locker window, and it is not installed on this PC yet.\n\n" +
                    "Install it now automatically from Microsoft? It is free and one-time (about 205 MB). DarkBox will open by itself when it is ready.",
                    "DarkBox", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (ask == DialogResult.Yes)
                {
                    ShowInitStatus("Installing Microsoft Edge WebView2 Runtime…\nPlease wait — DarkBox will open automatically.");
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        bool ok = WebView2Setup.TryInstall((pct, detail) =>
                        {
                            try
                            {
                                BeginInvoke((Action)(() =>
                                {
                                    if (_initStatus == null) return;
                                    string line = "Installing Microsoft Edge WebView2 Runtime…";
                                    if (detail != null && detail.Length > 0) line += "\n" + detail;
                                    else if (pct >= 0) line += "\n" + pct + "%";
                                    _initStatus.Text = line;
                                }));
                            }
                            catch { }
                        });
                        try
                        {
                            BeginInvoke((Action)(() =>
                            {
                                if (ok) { HideInitStatus(); InitWeb(); }
                                else { HideInitStatus(); ShowDownloadPrompt(); }
                            }));
                        }
                        catch { }
                    });
                    return; // stay open — the retry comes back on the UI thread
                }
            }
            ShowDownloadPrompt();
        }
    }

    private void ShowInitStatus(string text)
    {
        try
        {
            if (_initStatus == null)
            {
                _initStatus = new Label
                {
                    Dock = DockStyle.Fill,
                    TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                    Font = new System.Drawing.Font("Segoe UI", 13),
                    ForeColor = System.Drawing.Color.FromArgb(60, 50, 90),
                    BackColor = System.Drawing.Color.FromArgb(250, 248, 245),
                };
                Controls.Add(_initStatus);
                _initStatus.BringToFront();
            }
            _initStatus.Text = text;
            _initStatus.Visible = true;
        }
        catch { }
    }

    private void HideInitStatus()
    {
        try { if (_initStatus != null) _initStatus.Visible = false; } catch { }
    }

    // The old manual fallback: offer the Microsoft download page, then close.
    private void ShowDownloadPrompt()
    {
        var r = MessageBox.Show(
            "Microsoft Edge WebView2 Runtime is needed to show the locker window.\n" +
            "It is already installed on most Windows 10/11 PCs. Download it now?",
            "DarkBox", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r == DialogResult.Yes)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://developer.microsoft.com/en-us/microsoft-edge/webview2/",
                    UseShellExecute = true,
                });
            }
            catch { }
        }
        Close();
    }

    private void OpenUrl(string url)
    {
        try
        {
            if (string.IsNullOrEmpty(url)) return;
            if (!url.StartsWith("https://") && !url.StartsWith("http://")
                && !url.StartsWith("mailto:")) return;
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { Core.Log("OpenUrl failed: " + ex.Message); }
    }

    private void OnWebResource(object sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            string path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
            if (path.Length == 0) path = "index.html";
            // Intruder photos live on disk (not embedded) — serve them to
            // the Activity page from the Intruders folder only.
            if (path.StartsWith("intruder/"))
            {
                string fn = Path.GetFileName(path.Substring("intruder/".Length));
                string fp = Path.Combine(Core.IntruderDir, fn);
                if (fn.Length > 0 && File.Exists(fp))
                {
                    e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(
                        File.OpenRead(fp), 200, "OK",
                        "Content-Type: image/jpeg\r\nCache-Control: no-cache");
                }
                else
                {
                    e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(
                        null, 404, "Not Found", "");
                }
                return;
            }
            string resName = path == "index.html" ? "ui.html" : path;
            string ctype = path.EndsWith(".woff2") ? "font/woff2"
                : path.EndsWith(".jpg") || path.EndsWith(".jpeg") ? "image/jpeg"
                : path.EndsWith(".png") ? "image/png"
                : "text/html; charset=utf-8";
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var stream = asm.GetManifestResourceStream(resName);
            if (stream == null)
            {
                e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(
                    null, 404, "Not Found", "");
                return;
            }
            e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(
                stream, 200, "OK",
                "Content-Type: " + ctype + "\r\nCache-Control: max-age=86400");
        }
        catch (Exception ex)
        {
            Core.Log("Web resource failed: " + ex.Message);
        }
    }

    // ---------------- messaging ----------------
    private void Post(Dictionary<string, object> msg)
    {
        if (!_webReady || _web.CoreWebView2 == null) return;
        try { _web.CoreWebView2.PostWebMessageAsString(MiniJson.Write(msg)); }
        catch (Exception ex) { Core.Log("Post failed: " + ex.Message); }
    }

    private void SendToast(string text, bool error)
    {
        Post(new Dictionary<string, object>
        {
            { "type", "toast" }, { "text", text }, { "error", error },
        });
    }

    private void SendAuthError(string text)
    {
        Post(new Dictionary<string, object> { { "type", "authError" }, { "text", text } });
    }

    private Dictionary<string, object> FolderState(FolderEntry f)
    {
        return new Dictionary<string, object>
        {
            { "id", f.Id }, { "name", f.Name }, { "path", f.Path }, { "locked", f.Locked },
            { "color", f.Color ?? "" }, { "note", f.Note ?? "" }, { "hasOwn", f.HasOwnCreds },
            { "encrypted", f.Encrypted }, { "encPending", f.EncPending },
            { "lockMode", f.LockMode ?? "just" },
            { "busy", _busy.Contains(f.Id) },
        };
    }

    private void SendState()
    {
        _cfg = Core.Load();
        var folders = new List<object>();
        if (_decoy)
        {
            foreach (var d in FakeFolders()) folders.Add(d);
        }
        else
        {
            foreach (var f in _cfg.Folders) folders.Add(FolderState(f));
        }
        bool hasPat = _cfg.Pattern != null, hasPass = _cfg.Passcode != null;
        FolderEntry target = null;
        if (IsUnlockMode)
        {
            target = Core.Find(_cfg, _unlockId);
            if (target != null)
            {
                hasPat = hasPat || target.Pattern != null;
                hasPass = hasPass || target.Passcode != null;
            }
        }
        var msg = new Dictionary<string, object>
        {
            { "type", "state" },
            { "mode", IsInstallerMode ? "installer" : (IsUnlockMode ? "unlock" : "admin") },
            { "authed", _authed },
            { "setupRequired", !Core.IsSetUp(_cfg) },
            { "hasPattern", hasPat },
            { "hasPasscode", hasPass },
            { "hasDecoy", _cfg.HasDecoy },
            { "decoyMode", _decoy },
            { "frozenSecs", FrozenSecs() },
            { "folders", folders },
            { "settings", new Dictionary<string, object>
                {
                    { "startup", _cfg.Startup },
                    { "relock", _cfg.RelockOnStartup },
                    { "autolock", _cfg.AutoLockOnClose },
                    { "theme", _cfg.ThemeColor ?? "" },
                    { "theme2", _cfg.ThemeColor2 ?? "" },
                    { "dark", _cfg.DarkUi },
                    { "sounds", _cfg.Sounds },
                    { "pclock", _cfg.LockOnPcLock },
                    { "automins", (double)_cfg.AutoLockMinutes },
                    { "intruder", _cfg.IntruderPhoto },
                    { "encrypt", _cfg.DefaultHighLock },
                    { "contextmenu", _cfg.ContextMenu },
                    { "lite", _cfg.LiteSet ? _cfg.LiteMode : Environment.ProcessorCount <= 4 },
                }
            },
            { "hasRecovery", _cfg.HasRecovery },
            { "recoveryQuestion", _cfg.RecoveryQuestion ?? "" },
            { "version", CurrentVersion() },
        };
        if (!IsUnlockMode && _authed && !_decoy)
        {
            var acts = new List<object>();
            foreach (var a in Core.ReadActivity(100)) acts.Add(a);
            msg["activity"] = acts;
        }
        if (IsUnlockMode)
        {
            msg["target"] = target == null ? null : new Dictionary<string, object>
            {
                { "name", target.Name }, { "path", target.Path },
                { "color", target.Color ?? "" }, { "note", target.Note ?? "" },
                { "hasOwn", target.HasOwnCreds },
            };
        }
        if (IsInstallerMode)
        {
            msg["installer"] = new Dictionary<string, object>
            {
                { "installed", File.Exists(Installer.InstallExe) },
                { "installedVersion", Installer.ShortVersion(Installer.VersionOf(Installer.InstallExe)) },
                { "currentVersion", Installer.ShortVersion(Installer.VersionOf(Core.ExePath)) },
                { "setupDone", Core.IsSetUp(_cfg) },
            };
        }
        Post(msg);
    }

    private void DoInstallRun()
    {
        string staging = Path.GetDirectoryName(Core.ExePath);
        string version = Installer.VersionOf(Core.ExePath);
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Installer.Install(staging, version);
                try { Program.EnsureContextMenu(Core.Load().ContextMenu); } catch { }
                try
                {
                    BeginInvoke((Action)(() =>
                        Post(new Dictionary<string, object> { { "type", "installDone" } })));
                }
                catch { }
            }
            catch (Exception ex)
            {
                Core.Log("Install failed: " + ex);
                try
                {
                    BeginInvoke((Action)(() =>
                        Post(new Dictionary<string, object>
                        {
                            { "type", "installError" }, { "text", ex.Message },
                        })));
                }
                catch { }
            }
        });
    }

    private int FrozenSecs()
    {
        if (_frozenUntil == DateTime.MinValue) return 0;
        double s = (_frozenUntil - DateTime.Now).TotalSeconds;
        return s > 0 ? (int)Math.Ceiling(s) : 0;
    }

    // ---------------- decoy (fake mode) data ----------------
    private List<Dictionary<string, object>> FakeFolders()
    {
        if (_fakeFolders == null)
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            _fakeFolders = new List<Dictionary<string, object>>
            {
                FakeFolder("d1", "Family Photos", Path.Combine(pics, "Family Photos"), false),
                FakeFolder("d2", "Office Documents", Path.Combine(docs, "Office Documents"), false),
                FakeFolder("d3", "Old Projects", Path.Combine(docs, "Old Projects"), true),
            };
        }
        return _fakeFolders;
    }

    private static Dictionary<string, object> FakeFolder(string id, string name, string path, bool locked)
    {
        return new Dictionary<string, object>
        {
            { "id", id }, { "name", name }, { "path", path }, { "locked", locked },
            { "color", "" }, { "note", "" }, { "hasOwn", false },
        };
    }

    private Dictionary<string, object> FindFake(string id)
    {
        foreach (var d in FakeFolders())
        {
            object v;
            if (d.TryGetValue("id", out v) && (string)v == id) return d;
        }
        return null;
    }

    private static string GetStr(Dictionary<string, object> d, string key)
    {
        object v;
        return d.TryGetValue(key, out v) && v is string ? (string)v : "";
    }

    private static bool GetBool(Dictionary<string, object> d, string key)
    {
        object v;
        return d.TryGetValue(key, out v) && v is bool && (bool)v;
    }

    private static List<int> GetDots(Dictionary<string, object> d, string key)
    {
        var dots = new List<int>();
        object v;
        if (d.TryGetValue(key, out v) && v is List<object>)
        {
            foreach (var item in (List<object>)v)
            {
                if (item is double) dots.Add((int)(double)item);
                else if (item is long) dots.Add((int)(long)item);
            }
        }
        return dots;
    }

    private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string json;
            try { json = e.TryGetWebMessageAsString(); }
            catch { return; }
            var parsed = MiniJson.Parse(json) as Dictionary<string, object>;
            if (parsed == null) return;
            string cmd = GetStr(parsed, "cmd");
            switch (cmd)
            {
                case "ready": SendState(); break;
                case "openUrl": OpenUrl(GetStr(parsed, "url")); break;
                case "minimize": WindowState = FormWindowState.Minimized; break;
                case "close": Close(); break;
                case "drag":
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION
                    break;
                case "verifyPattern": DoVerify(GetDots(parsed, "dots"), null); break;
                case "verifyPasscode": DoVerify(null, GetStr(parsed, "passcode")); break;
                case "setup": DoSetup(parsed); break;
                case "authUnlock": DoAuthUnlock(parsed); break;
                case "browse": if (IsAuthed()) DoBrowse(GetStr(parsed, "path")); break;
                case "makeFolder": if (IsAuthed()) DoMakeFolder(parsed); break;
                case "addFolderPath":
                    if (IsAuthed())
                    {
                        if (_decoy)
                        {
                            DoFakeAdd();
                            Post(new Dictionary<string, object> { { "type", "browseDone" } });
                        }
                        else DoAddFolderPath(GetStr(parsed, "path"));
                    }
                    break;
                case "lock": if (IsAuthed()) { if (_decoy) DoFakeFlip(GetStr(parsed, "id"), true); else DoLockUnlock(GetStr(parsed, "id"), true); } break;
                case "unlock": if (IsAuthed()) { if (_decoy) DoFakeFlip(GetStr(parsed, "id"), false); else DoLockUnlock(GetStr(parsed, "id"), false); } break;
                case "remove": if (IsAuthed()) { if (_decoy) DoFakeRemove(GetStr(parsed, "id")); else DoRemove(GetStr(parsed, "id")); } break;
                case "openFolder": if (IsAuthed()) { if (_decoy) DoFakeOpen(GetStr(parsed, "id")); else DoOpenFolder(GetStr(parsed, "id")); } break;
                case "lockAll": if (IsAuthed()) { if (_decoy) { foreach (var d in FakeFolders()) d["locked"] = true; SendState(); SendToast("All open folders are locked.", false); } else { EnqueueJob(() => { Core.RelockUnlocked(Core.Load()); BeginInvoke((Action)(() => { _cfg = Core.Load(); SendState(); SendToast("All open folders are locked.", false); })); }); } } break;
                case "settings": if (IsAuthed()) { if (_decoy) SendToast("Settings saved.", false); else DoSettings(parsed); } break;
                case "changePattern": if (IsAuthed() && !_decoy) DoChangePattern(parsed); break;
                case "changePasscode": if (IsAuthed() && !_decoy) DoChangePasscode(parsed); break;
                case "installRun": if (IsInstallerMode) DoInstallRun(); break;
                case "installOpen":
                    if (IsInstallerMode)
                    {
                        try { Installer.LaunchInstalled(); } catch { }
                        Close();
                    }
                    break;
                case "sizes": if (IsAuthed() && !_decoy) DoSizes(); break;
                case "folderPattern": if (IsAuthed() && !_decoy) DoFolderPattern(parsed); break;
                case "folderPasscode": if (IsAuthed() && !_decoy) DoFolderPasscode(parsed); break;
                case "folderCredsClear": if (IsAuthed() && !_decoy) DoFolderCredsClear(parsed); break;
                case "unlockWithCreds": if (IsAuthed() && !_decoy) DoUnlockWithCreds(parsed); break;
                case "setFolderColor": if (IsAuthed() && !_decoy) DoSetFolderColor(parsed); break;
                case "setFolderNote": if (IsAuthed() && !_decoy) DoSetFolderNote(parsed); break;
                case "setFolderLockMode": if (IsAuthed() && !_decoy) DoSetFolderLockMode(parsed); break;
                case "setDecoyPattern": if (IsAuthed() && !_decoy) DoSetDecoyPattern(parsed); break;
                case "setDecoyPasscode": if (IsAuthed() && !_decoy) DoSetDecoyPasscode(parsed); break;
                case "clearDecoy": if (IsAuthed() && !_decoy) DoClearDecoy(); break;
                case "clearActivity": if (IsAuthed() && !_decoy) { Core.ClearActivity(); SendState(); SendToast("History cleared.", false); } break;
                case "savePhoto": if (IsAuthed() && !_decoy) DoSavePhoto(GetStr(parsed, "file")); break;
                case "recoveryCode": if (IsAuthed() && !_decoy) DoRecoveryCode(); break;
                case "recoveryAnswer": if (IsAuthed() && !_decoy) DoRecoveryAnswer(parsed); break;
                case "recoverVerify": DoRecoverVerify(parsed); break;
                case "recoverReset": DoRecoverReset(parsed); break;
                case "backupExport": if (IsAuthed() && !_decoy) DoBackupExport(parsed); break;
                case "backupRestore": if (IsAuthed() && !_decoy) DoBackupRestore(parsed); break;
                case "checkUpdate": if (IsAuthed() && !_decoy) DoCheckUpdate(false); break;
            }
        }
        catch (Exception ex)
        {
            Core.Log("Web message failed: " + ex);
        }
    }

    private bool IsAuthed()
    {
        if (_authed) return true;
        SendAuthError("Please log in first.");
        return false;
    }

    // ---------------- auth ----------------
    // Wrong-try protection: count consecutive failures. The 3rd (and 5th)
    // failure quietly takes an intruder photo (when enabled); the 5th
    // freezes all input for 30 seconds.
    private void RegisterFail(string context)
    {
        _fails++;
        Core.LogActivity("fail", "Wrong try #" + _fails + " (" + context + ")");
        _cfg = Core.Load();
        if (_cfg.IntruderPhoto && (_fails == 3 || _fails == 5)) CaptureIntruder(context);
        if (_fails >= 5)
        {
            _frozenUntil = DateTime.Now.AddSeconds(30);
            Core.LogActivity("freeze", "Too many wrong tries — input frozen for 30 seconds (" + context + ")");
            if (_freezeTimer != null) { _freezeTimer.Stop(); _freezeTimer.Dispose(); }
            _freezeTimer = new Timer { Interval = 30500 };
            _freezeTimer.Tick += (s, e) => { _freezeTimer.Stop(); SendState(); };
            _freezeTimer.Start();
        }
    }

    private void CaptureIntruder(string context)
    {
        string ctx = context;
        var t = new System.Threading.Thread(() =>
        {
            try
            {
                Directory.CreateDirectory(Core.IntruderDir);
                string name = "intruder-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".jpg";
                string path = Path.Combine(Core.IntruderDir, name);
                if (CameraCapture.CaptureJpeg(path))
                {
                    Core.LogActivity("intruder", "Intruder photo captured (" + ctx + ")", name);
                    try { BeginInvoke((Action)(() => SendState())); } catch { }
                }
            }
            catch (Exception ex) { Core.Log("Intruder capture failed: " + ex.Message); }
            // With or without a camera, always record who/where this was:
            // device name, user, WiFi network, local IP and (when Windows
            // location is on) the approximate location.
            try
            {
                Core.LogActivity("device", DeviceInfo());
                try { BeginInvoke((Action)(() => SendState())); } catch { }
            }
            catch (Exception ex) { Core.Log("Device info failed: " + ex.Message); }
        });
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }

    // v4.1.0: IP-based location, Speedtest-style.
    // The old code only asked Windows Location (GeoCoordinateWatcher) —
    // on almost every PC location is OFF, so the log always said
    // "Unknown (location off)" even though the PC was online. Speedtest
    // does not use Windows Location either: it reads the PUBLIC IP and
    // looks up city/ISP from it. DarkBox now does the same, on the
    // background thread (never the UI thread), with a 4-second timeout
    // and a 10-minute cache so five wrong tries don't make five lookups.
    // Honest limits, stated in the log itself when they apply:
    //  - an IP lookup gives CITY + ISP, never a street address or the
    //    WiFi modem's name — no website can know your router's model
    //    from an IP address. What IS shown: WiFi network name (SSID),
    //    signal %, public IP, city/region/country, ISP.
    //  - the 192.168.x.x address in the old log was only the LOCAL
    //    (home-network) IP — it can never give a location. It is still
    //    recorded, labelled as local, next to the public IP.
    //  - offline = no lookup possible; the log then says so plainly
    //    instead of a bare "Unknown".
    private static string _geoCacheLoc = null;
    private static string _geoCachePublicIp = null;
    private static string _geoCacheIsp = null;
    private static DateTime _geoCacheAt = DateTime.MinValue;

    private static string GeoStr(Dictionary<string, object> d, string key)
    {
        if (d == null) return "";
        object v;
        if (!d.TryGetValue(key, out v) || v == null) return "";
        return v.ToString();
    }

    private static void LookupGeo(out string loc, out string publicIp, out string isp)
    {
        loc = null; publicIp = null; isp = null;
        if (_geoCacheLoc != null && (DateTime.Now - _geoCacheAt).TotalMinutes < 10)
        {
            loc = _geoCacheLoc; publicIp = _geoCachePublicIp; isp = _geoCacheIsp;
            return;
        }
        try
        {
            // ip-api.com free endpoint, no key needed. HTTP (not HTTPS) on
            // the free tier. Only the machine's own public IP is sent —
            // that is the whole point of the lookup — nothing else.
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(
                "http://ip-api.com/json/?fields=status,country,regionName,city,zip,isp,org,query");
            req.Timeout = 4000;
            req.UserAgent = "DarkBox/" + CurrentVersion();
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
            {
                var d = MiniJson.Parse(sr.ReadToEnd()) as Dictionary<string, object>;
                if (d != null && GeoStr(d, "status") == "success")
                {
                    string city = GeoStr(d, "city");
                    string region = GeoStr(d, "regionName");
                    string country = GeoStr(d, "country");
                    string zip = GeoStr(d, "zip");
                    var parts = new List<string>();
                    if (city.Length > 0) parts.Add(city);
                    if (region.Length > 0 && region != city) parts.Add(region);
                    if (country.Length > 0) parts.Add(country);
                    if (zip.Length > 0) parts.Add(zip);
                    loc = parts.Count > 0 ? string.Join(", ", parts.ToArray()) : null;
                    publicIp = GeoStr(d, "query");
                    isp = GeoStr(d, "isp");
                    if (isp.Length == 0) isp = GeoStr(d, "org");
                    _geoCacheLoc = loc; _geoCachePublicIp = publicIp; _geoCacheIsp = isp;
                    _geoCacheAt = DateTime.Now;
                }
            }
        }
        catch { /* offline / blocked — caller falls back below */ }
    }

    // Best-effort snapshot of the machine at the moment of a wrong try.
    private static string DeviceInfo()
    {
        string pc = Environment.MachineName;
        string user = Environment.UserName;
        string wifi = "Wired / none";
        string wifiSignal = "";
        string ip = "—";
        string gateway = "";
        string loc = null;
        string publicIp = null;
        string isp = null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "wlan show interfaces",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using (var proc = System.Diagnostics.Process.Start(psi))
            {
                string output = proc.StandardOutput.ReadToEnd();
                if (!proc.WaitForExit(3000)) { try { proc.Kill(); } catch { } }
                foreach (string raw in output.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("SSID") && !line.StartsWith("BSSID") && line.Contains(":"))
                    {
                        string v = line.Substring(line.IndexOf(':') + 1).Trim();
                        if (v.Length > 0) wifi = v;
                    }
                    else if (line.StartsWith("Signal") && line.Contains(":"))
                    {
                        string v = line.Substring(line.IndexOf(':') + 1).Trim();
                        if (v.Length > 0) wifiSignal = v;
                    }
                }
            }
        }
        catch { }
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        ip = ua.Address.ToString();
                        break;
                    }
                }
                if (gateway.Length == 0)
                {
                    foreach (var gw in ni.GetIPProperties().GatewayAddresses)
                    {
                        if (gw.Address != null && gw.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        { gateway = gw.Address.ToString(); break; }
                    }
                }
                if (ip != "—") break;
            }
        }
        catch { }
        // 1) Public-IP lookup first — works even with Windows Location off.
        LookupGeo(out loc, out publicIp, out isp);
        // 2) Only if the lookup failed, try Windows Location briefly
        // (1.5 s, was 3 s — it usually just times out on PCs with it off).
        if (loc == null)
        {
            try
            {
                var watcher = new System.Device.Location.GeoCoordinateWatcher(
                    System.Device.Location.GeoPositionAccuracy.Default);
                if (watcher.TryStart(false, TimeSpan.FromSeconds(1.5)))
                {
                    var position = watcher.Position;
                    if (position != null && !position.Location.IsUnknown)
                    {
                        loc = position.Location.Latitude.ToString("0.0000") + ", "
                            + position.Location.Longitude.ToString("0.0000");
                    }
                    watcher.Stop();
                }
            }
            catch { }
        }
        if (loc == null)
            loc = "Unknown — no internet lookup and Windows Location is off";
        string wifiText = wifi + (wifiSignal.Length > 0 ? " (" + wifiSignal + " signal)" : "");
        string ipText = "Local " + ip
            + (publicIp != null && publicIp.Length > 0 ? " · Public " + publicIp : " · Public ? (offline)");
        string locText = loc + (isp != null && isp.Length > 0 ? " (ISP: " + isp + ")" : "");
        if (gateway.Length > 0) locText += " · Router: " + gateway;
        return "Intruder info — PC: " + pc + " · User: " + user
            + " · WiFi: " + wifiText + " · IP: " + ipText + " · Location: " + locText;
    }

    private void DoSavePhoto(string file)
    {
        try
        {
            string name = Path.GetFileName(file ?? "");
            if (name.Length == 0) return;
            string src = Path.Combine(Core.IntruderDir, name);
            if (!File.Exists(src)) { SendToast("That photo no longer exists.", true); return; }
            string downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            string dst = Path.Combine(downloads, name);
            if (File.Exists(dst))
            {
                string baseName = Path.GetFileNameWithoutExtension(name);
                dst = Path.Combine(downloads, baseName + "-"
                    + DateTime.Now.ToString("HHmmss") + ".jpg");
            }
            File.Copy(src, dst);
            SendToast("Photo saved to your Downloads folder.", false);
        }
        catch (Exception ex)
        {
            Core.Log("Save photo failed: " + ex.Message);
            SendToast("Could not save the photo: " + ex.Message, true);
        }
    }

    private bool FrozenReject()
    {
        if (FrozenSecs() <= 0) return false;
        SendAuthError("Too many wrong tries — please wait " + FrozenSecs() + " seconds.");
        SendState();
        return true;
    }

    private void EnterDecoy()
    {
        _decoy = true;
        _authed = true;
        _fails = 0;
        Core.LogActivity("decoy", "Fake (decoy) mode was opened");
        SendState();
    }

    private void DoVerify(List<int> dots, string passcode)
    {
        _cfg = Core.Load();
        if (FrozenReject()) return;
        // The decoy (fake-mode) credential opens a harmless decoy panel.
        if (Core.VerifyDecoy(_cfg, dots, passcode)) { EnterDecoy(); return; }
        bool ok = dots != null ? Core.VerifyPattern(_cfg, dots) : Core.VerifyPasscode(_cfg, passcode);
        if (!ok)
        {
            RegisterFail("panel login");
            if (FrozenSecs() > 0)
            {
                SendAuthError("Too many wrong tries — please wait 30 seconds.");
                SendState();
                return;
            }
            SendAuthError(dots != null ? "Wrong pattern — please try again." : "Wrong passcode — please try again.");
            return;
        }
        _fails = 0;
        _authed = true;
        UnlockDek(dots, passcode);
        Program.StartAgentIfNeeded(_cfg);
        SendState();
        MaybeAutoUpdateCheck();
    }

    private void DoSetup(Dictionary<string, object> msg)
    {
        var dots = GetDots(msg, "dots");
        string passcode = GetStr(msg, "passcode");
        bool hasPattern = dots.Count >= 4;
        bool hasPasscode = passcode.Length >= 4;
        if (!hasPattern && !hasPasscode)
        {
            SendAuthError("Draw a pattern (4+ dots) or set a passcode of 4+ characters.");
            return;
        }
        if (passcode.Length > 0 && !hasPasscode)
        {
            SendAuthError("Passcode must be at least 4 characters.");
            return;
        }
        _cfg = Core.Load();
        if (hasPattern) Core.SetPattern(_cfg, dots);
        if (hasPasscode) Core.SetPasscode(_cfg, passcode);
        // v4.0: create the vault key and wrap it with the new credential(s).
        _dek = Core.CreateDek();
        if (hasPattern) _cfg.DekForPattern = Core.WrapDek(_dek, Core.PatternSecret(dots), _cfg.Iterations);
        if (hasPasscode) _cfg.DekForPasscode = Core.WrapDek(_dek, passcode, _cfg.Iterations);
        Core.Save(_cfg);
        Core.SetStartup(_cfg.Startup);
        Core.RemoveLegacyStartup();
        _authed = true;
        Program.StartAgentIfNeeded(_cfg);
        SendState();
        SendToast("Setup complete. Add a folder to protect it.", false);
    }

    private void DoAuthUnlock(Dictionary<string, object> msg)
    {
        if (!IsUnlockMode) return;
        _cfg = Core.Load();
        var entry = Core.Find(_cfg, _unlockId);
        if (entry == null) { SendAuthError("This folder is no longer in the list."); return; }
        if (FrozenReject()) return;
        var dots = GetDots(msg, "dots");
        string passcode = GetStr(msg, "passcode");
        var dotsArg = dots.Count > 0 ? dots : null;
        var passArg = passcode.Length > 0 ? passcode : null;

        // Decoy credential → a harmless empty decoy folder opens instead.
        if (Core.VerifyDecoy(_cfg, dotsArg, passArg))
        {
            Core.LogActivity("decoy", "Fake (decoy) unlock used for \"" + entry.Name + "\"");
            OpenDecoyFolder(entry.Name);
            Close();
            return;
        }
        // The folder's own key when it has one; the panel key is the master.
        if (!Core.VerifyForFolder(_cfg, entry, dotsArg, passArg))
        {
            RegisterFail("folder \"" + entry.Name + "\"");
            if (FrozenSecs() > 0)
            {
                SendAuthError("Too many wrong tries — please wait 30 seconds.");
                SendState();
                return;
            }
            SendAuthError("Wrong pattern / passcode — please try again.");
            return;
        }
        _fails = 0;
        // If the owner used the panel (master) key, the vault key unwraps too
        // — needed when this folder's files are encrypted.
        // v4.2.0: the locker unlock must NOT run the pending-encryption pass
        // first — that job encrypts every pending folder and the unlock
        // waits behind it in the single queue (that is why unlocking from
        // the locker felt much slower than from the admin panel, where the
        // pass already ran at sign-in). Unwrap the key only; the pending
        // work runs on the next admin sign-in.
        if (Core.VerifyAny(_cfg, dotsArg, passArg)) UnlockDek(dotsArg, passArg, false);
        // Encrypted folder + only the folder's own key: by design the files
        // cannot be decrypted here — say so plainly instead of failing.
        if (_dek == null && (entry.Encrypted || Core.HasEncryptedFiles(Core.VaultPath(entry))))
        {
            SendAuthError("This folder is encrypted. Please open it from the Admin Panel with your main pattern or passcode.");
            return;
        }
        // v4.0.5: the unlock runs on the worker, not the UI thread. A big
        // encrypted folder takes minutes to decrypt — on the UI thread the
        // window went "Not Responding" and a force-close mid-decrypt could
        // strand the folder (the exact failure that lost the user's folder).
        // The busy overlay stays live and shows file progress while it works.
        var entryId = entry.Id;
        var autoLock = _cfg.AutoLockOnClose;
        // v4.2.0: the unlock jumps ahead of queued maintenance work.
        EnqueueJobFront(() =>
        {
            try
            {
                var cfg2 = Core.Load();
                var e2 = Core.Find(cfg2, entryId);
                if (e2 == null)
                {
                    BeginInvoke((Action)(() => SendAuthError("This folder is no longer in the list.")));
                    return;
                }
                Core.UnlockFolder(cfg2, e2, true, _dek);
                BeginInvoke((Action)(() =>
                {
                    if (!autoLock) { Close(); return; }
                    var cfg3 = Core.Load();
                    var e3 = Core.Find(cfg3, entryId);
                    if (e3 == null) { Close(); return; }
                    BecomeGuard(e3);
                }));
            }
            catch (Exception ex)
            {
                Core.Log("Unlock failed: " + ex);
                BeginInvoke((Action)(() => SendAuthError("The folder could not be opened: " + ex.Message)));
            }
        });
    }

    private void OpenDecoyFolder(string folderName)
    {
        try
        {
            string safe = string.Concat(folderName.Split(Path.GetInvalidFileNameChars()));
            if (safe.Length == 0) safe = "Folder";
            string dir = Path.Combine(Core.DataDir, "Decoy", safe);
            Directory.CreateDirectory(dir);
            Core.OpenInExplorer(dir);
        }
        catch (Exception ex) { Core.Log("Decoy folder failed: " + ex.Message); }
    }

    // ---------------- folder actions ----------------
    // ---------- in-app folder picker (replaces the Windows dialog) ----------

    private void DoBrowse(string path)
    {
        if (_decoy) { SendFakeBrowse(path); return; }
        string cur = (path ?? "").Trim();
        string parent = "";
        var dirs = new List<object>();
        try
        {
            if (cur.Length == 0)
            {
                foreach (string d in Environment.GetLogicalDrives())
                {
                    string label = "(" + d.TrimEnd('\\') + ")";
                    try
                    {
                        var di = new DriveInfo(d);
                        if (di.IsReady && di.VolumeLabel.Length > 0) label = di.VolumeLabel + " " + label;
                    }
                    catch { }
                    dirs.Add(new Dictionary<string, object> { { "name", label }, { "path", d }, { "drive", true } });
                }
            }
            else
            {
                cur = Path.GetFullPath(cur);
                var info = new DirectoryInfo(cur);
                parent = info.Parent != null ? info.Parent.FullName : "";
                var sub = new List<string>(Directory.GetDirectories(cur));
                sub.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string s in sub)
                {
                    try
                    {
                        var attr = File.GetAttributes(s);
                        if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    }
                    catch { continue; }
                    dirs.Add(new Dictionary<string, object>
                    {
                        { "name", Path.GetFileName(s) }, { "path", s }, { "drive", false },
                    });
                }
            }
        }
        catch (Exception ex)
        {
            SendToast("Cannot open that location: " + ex.Message, true);
            return;
        }
        var places = new List<object>();
        AddPlace(places, "Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        AddPlace(places, "Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddPlace(places, "Downloads", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        AddPlace(places, "Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        AddPlace(places, "Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        AddPlace(places, "Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Post(new Dictionary<string, object>
        {
            { "type", "browse" },
            { "path", cur },
            { "parent", parent },
            { "dirs", dirs },
            { "places", places },
        });
    }

    private static void AddPlace(List<object> list, string name, string path)
    {
        if (path != null && path.Length > 0 && Directory.Exists(path))
            list.Add(new Dictionary<string, object> { { "name", name }, { "path", path } });
    }

    private void SendFakeBrowse(string path)
    {
        string cur = (path ?? "").Trim();
        string parent = "";
        var dirs = new List<object>();
        if (cur.Length == 0)
        {
            dirs.Add(new Dictionary<string, object> { { "name", "Local Disk (C:)" }, { "path", "C:\\" }, { "drive", true } });
            dirs.Add(new Dictionary<string, object> { { "name", "Data (F:)" }, { "path", "F:\\" }, { "drive", true } });
        }
        else
        {
            string basePath = cur.TrimEnd('\\');
            parent = basePath.Length > 2 ? basePath.Substring(0, basePath.LastIndexOf('\\') + 1) : "";
            if (parent.Length == 3) parent = parent; else if (parent.EndsWith(":\\")) parent = parent;
            foreach (string n in new[] { "Family Photos", "Office Documents", "Old Projects" })
                dirs.Add(new Dictionary<string, object>
                {
                    { "name", n }, { "path", basePath + "\\" + n }, { "drive", false },
                });
        }
        var places = new List<object>();
        foreach (string n in new[] { "Desktop", "Documents", "Downloads", "Pictures" })
            places.Add(new Dictionary<string, object>
            {
                { "name", n }, { "path", "C:\\Users\\User\\" + n },
            });
        Post(new Dictionary<string, object>
        {
            { "type", "browse" },
            { "path", cur },
            { "parent", parent },
            { "dirs", dirs },
            { "places", places },
        });
    }

    private void DoMakeFolder(Dictionary<string, object> msg)
    {
        string parent = GetStr(msg, "parent");
        string name = GetStr(msg, "name").Trim();
        if (_decoy) { SendFakeBrowse(parent); return; }
        if (parent.Length == 0 || name.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(parent, name));
            DoBrowse(parent);
        }
        catch (Exception ex) { SendToast("Could not create the folder: " + ex.Message, true); }
    }

    private bool _addingBusy;

    private void DoAddFolderPath(string path)
    {
        // v4.0.4: adding used to run entirely on the UI thread, and since
        // v4.0 it encrypted the whole tree right there — a big folder froze
        // the window (Not Responding) and a force-close left it bugged.
        // Now the picker closes at once and the work runs on the worker.
        if (path.Length == 0 || !Directory.Exists(path))
        {
            SendToast("Please pick a folder first.", true);
            return;
        }
        string root = Path.GetPathRoot(path);
        if (root != null && string.Equals(Path.GetFullPath(path), root, StringComparison.OrdinalIgnoreCase))
        {
            SendToast("A whole drive can't be locked — pick a folder inside it.", true);
            return;
        }
        if (_addingBusy)
        {
            SendToast("Still adding the previous folder — please wait for it to finish.", true);
            return;
        }
        _addingBusy = true;
        Post(new Dictionary<string, object> { { "type", "browseDone" } });
        string name = Path.GetFileName(path.TrimEnd('\\'));
        // v4.1.0: the add MUST show in the preloader, not in a toast the
        // user can miss. The busy overlay opens at once with live file
        // progress, and only closes when the folder is really locked —
        // then a "Folder locked ✓" popup confirms it. Before this the
        // folder just appeared in the list tagged ENCRYPTION PENDING,
        // which looked locked while encryption was still running (or
        // still waiting for the next sign-in).
        Post(new Dictionary<string, object>
        {
            { "type", "showBusy" },
            { "text", "Adding \"" + name + "\" — locking folder…" },
        });
        var dek = _dek;
        EnqueueJob(() =>
        {
            string toast; bool err = false;
            string lockedName = name; int fileCount = 0; bool encrypted = false;
            string lockMode = "just";
            try
            {
                var cfg2 = Core.Load();
                var e = Core.AddFolder(cfg2, path, dek);
                lockedName = e.Name;
                fileCount = Core.CountVaultFiles(e);
                encrypted = e.Encrypted;
                lockMode = e.LockMode ?? "just";
                toast = "Added and locked: " + e.Name;
            }
            catch (Exception ex) { toast = "Could not add that folder: " + ex.Message; err = true; }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    _addingBusy = false;
                    _cfg = Core.Load();
                    SendState();
                    Post(new Dictionary<string, object> { { "type", "hideBusy" } });
                    if (!err)
                    {
                        Post(new Dictionary<string, object>
                        {
                            { "type", "folderLocked" },
                            { "name", lockedName },
                            { "files", (double)fileCount },
                            { "encrypted", encrypted },
                            { "lockMode", lockMode },
                        });
                    }
                    else SendToast(toast, true);
                }));
            }
            catch { _addingBusy = false; }
        });
    }

    private void DoLockUnlock(string id, bool lockIt)
    {
        _cfg = Core.Load();
        var e = Core.Find(_cfg, id);
        if (e == null) { SendState(); return; }
        if (!lockIt && e.HasOwnCreds)
        {
            // This folder has its own key — ask for it in a modal first.
            Post(new Dictionary<string, object>
            {
                { "type", "needFolderCreds" }, { "id", e.Id }, { "name", e.Name },
                { "hasPattern", e.Pattern != null }, { "hasPasscode", e.Passcode != null },
            });
            return;
        }
        if (_busy.Contains(id)) return;
        // v4.0: locking can encrypt a whole folder tree — it runs on the
        // single low-priority worker and the row shows busy meanwhile.
        _busy.Add(id);
        SendState();
        var dek = _dek;
        string name = e.Name;
        // v4.2.0: unlocks jump ahead of queued maintenance work.
        Action job = () =>
        {
            string toast = null; bool err = false;
            string doneName = name; int fileCount = 0; bool encrypted = false; bool didLock = false;
            string lockMode = "just";
            try
            {
                var cfg2 = Core.Load();
                var e2 = Core.Find(cfg2, id);
                if (e2 != null)
                {
                    if (lockIt) Core.LockFolder(cfg2, e2, dek);
                    else Core.UnlockFolder(cfg2, e2, true, dek);
                    if (lockIt)
                    {
                        didLock = true; doneName = e2.Name;
                        fileCount = Core.CountVaultFiles(e2); encrypted = e2.Encrypted;
                        lockMode = e2.LockMode ?? "just";
                    }
                    toast = (lockIt ? "Locked: " : "Unlocked: ") + name;
                }
            }
            catch (Exception ex)
            {
                toast = (lockIt ? "Could not lock: " : "Could not unlock: ") + ex.Message;
                err = true;
            }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    _busy.Remove(id);
                    _cfg = Core.Load();
                    SendState();
                    // v4.1.0: a lock is only "done" when the popup says so —
                    // same confirmation as adding a folder.
                    if (didLock && !err)
                    {
                        Post(new Dictionary<string, object>
                        {
                            { "type", "folderLocked" },
                            { "name", doneName },
                            { "files", (double)fileCount },
                            { "encrypted", encrypted },
                            { "lockMode", lockMode },
                        });
                    }
                    else if (toast != null) SendToast(toast, err);
                }));
            }
            catch { }
        };
        if (lockIt) EnqueueJob(job); else EnqueueJobFront(job);
    }

    private void DoRemove(string id)
    {
        // v4.0.4: removing a locked folder unlocks + decrypts it first —
        // that work now runs on the worker too, not the UI thread.
        if (_busy.Contains(id)) return;
        _cfg = Core.Load();
        var e = Core.Find(_cfg, id);
        if (e == null) { SendState(); return; }
        string name = e.Name;
        _busy.Add(id);
        SendState();
        var dek = _dek;
        EnqueueJob(() =>
        {
            string toast; bool err = false;
            try
            {
                var cfg2 = Core.Load();
                var e2 = Core.Find(cfg2, id);
                if (e2 != null) Core.RemoveFolder(cfg2, e2, dek);
                toast = "Removed from the list: " + name;
            }
            catch (Exception ex) { toast = "Could not remove: " + ex.Message; err = true; }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    _busy.Remove(id);
                    _cfg = Core.Load();
                    SendState();
                    SendToast(toast, err);
                }));
            }
            catch { }
        });
    }

    private void DoOpenFolder(string id)
    {
        _cfg = Core.Load();
        var e = Core.Find(_cfg, id);
        if (e != null && !e.Locked) Core.OpenInExplorer(e.Path);
    }

    private void DoSettings(Dictionary<string, object> msg)
    {
        _cfg = Core.Load();
        _cfg.Startup = GetBool(msg, "startup");
        _cfg.RelockOnStartup = GetBool(msg, "relock");
        _cfg.AutoLockOnClose = GetBool(msg, "autolock");
        _cfg.ThemeColor = GetStr(msg, "theme");
        _cfg.ThemeColor2 = GetStr(msg, "theme2");
        _cfg.DarkUi = GetBool(msg, "dark");
        _cfg.Sounds = GetBool(msg, "sounds");
        _cfg.LockOnPcLock = GetBool(msg, "pclock");
        _cfg.IntruderPhoto = GetBool(msg, "intruder");
        object mv;
        if (msg.TryGetValue("automins", out mv) && mv is double)
            _cfg.AutoLockMinutes = (int)(double)mv;
        if (msg.ContainsKey("encrypt")) _cfg.DefaultHighLock = GetBool(msg, "encrypt");
        if (msg.ContainsKey("lite")) { _cfg.LiteMode = GetBool(msg, "lite"); _cfg.LiteSet = true; }
        if (msg.ContainsKey("contextmenu"))
        {
            _cfg.ContextMenu = GetBool(msg, "contextmenu");
            Program.EnsureContextMenu(_cfg.ContextMenu);
        }
        Core.Save(_cfg);
        Core.SetStartup(_cfg.Startup);
        if (_cfg.Startup) Program.StartAgentIfNeeded(_cfg);
        SendToast("Settings saved.", false);
    }

    // ---------------- v3.3 folder extras ----------------
    private bool _sizesRunning;
    private DateTime _sizesLastRun = DateTime.MinValue;
    private string _sizesSignature = "";

    private void DoSizes()
    {
        // v4.0.3: sizes used to be re-scanned from disk on EVERY state push —
        // on a big folder that meant constant disk thrash on weak PCs. Now:
        // one scan at a time, on the single low-priority worker, and only
        // when the folder set changed or 60 seconds passed.
        var parts = new List<string>();
        foreach (var f in _cfg.Folders) parts.Add(f.Id + (f.Locked ? "L" : "O"));
        string sig = string.Join(",", parts.ToArray());
        if (_sizesRunning) return;
        if (sig == _sizesSignature && (DateTime.Now - _sizesLastRun).TotalSeconds < 60) return;
        _sizesRunning = true;
        _sizesSignature = sig;
        _sizesLastRun = DateTime.Now;
        var snapshot = new List<FolderEntry>(_cfg.Folders);
        EnqueueJob(() =>
        {
            try
            {
                // Send each folder's size as soon as it is measured, so the
                // dashboard fills in progressively instead of waiting for
                // the biggest folder to finish scanning.
                foreach (var f in snapshot)
                {
                    string p = f.Locked ? Core.VaultPath(f) : f.Path;
                    double bytes = (double)Core.FolderSize(p);
                    string id = f.Id;
                    try
                    {
                        BeginInvoke((Action)(() =>
                            Post(new Dictionary<string, object>
                            {
                                { "type", "sizes" },
                                { "sizes", new Dictionary<string, object> { { id, bytes } } },
                            })));
                    }
                    catch { }
                }
            }
            finally { _sizesRunning = false; }
        });
    }

    private void DoFolderPattern(Dictionary<string, object> msg)
    {
        var dots = GetDots(msg, "dots");
        if (dots.Count < 4) { SendToast("Pattern too short — use at least 4 dots.", true); return; }
        _cfg = Core.Load();
        var e = Core.Find(_cfg, GetStr(msg, "id"));
        if (e == null) { SendState(); return; }
        Core.SetFolderPattern(_cfg, e, dots);
        Core.Save(_cfg);
        SendState();
        SendToast("Own pattern set for \"" + e.Name + "\".", false);
    }

    private void DoFolderPasscode(Dictionary<string, object> msg)
    {
        string passcode = GetStr(msg, "passcode");
        if (passcode.Length < 4) { SendToast("Passcode must be at least 4 characters.", true); return; }
        _cfg = Core.Load();
        var e = Core.Find(_cfg, GetStr(msg, "id"));
        if (e == null) { SendState(); return; }
        Core.SetFolderPasscode(_cfg, e, passcode);
        Core.Save(_cfg);
        SendState();
        SendToast("Own passcode set for \"" + e.Name + "\".", false);
    }

    private void DoFolderCredsClear(Dictionary<string, object> msg)
    {
        _cfg = Core.Load();
        var e = Core.Find(_cfg, GetStr(msg, "id"));
        if (e == null) { SendState(); return; }
        e.Pattern = null;
        e.Passcode = null;
        Core.Save(_cfg);
        SendState();
        SendToast("\"" + e.Name + "\" now uses the panel pattern / passcode.", false);
    }

    private void DoUnlockWithCreds(Dictionary<string, object> msg)
    {
        _cfg = Core.Load();
        var e = Core.Find(_cfg, GetStr(msg, "id"));
        if (e == null) { SendState(); return; }
        var dots = GetDots(msg, "dots");
        string passcode = GetStr(msg, "passcode");
        bool ok = Core.VerifyForFolder(_cfg, e,
            dots.Count > 0 ? dots : null, passcode.Length > 0 ? passcode : null);
        Post(new Dictionary<string, object> { { "type", "folderCreds" }, { "ok", ok } });
        if (!ok)
        {
            Core.LogActivity("fail", "Wrong folder key for \"" + e.Name + "\" (from the panel)");
            return;
        }
        // If the entered key is also the panel (master) key, unwrap the
        // vault key — encrypted folders need it to open.
        if (_dek == null && Core.VerifyAny(_cfg,
            dots.Count > 0 ? dots : null, passcode.Length > 0 ? passcode : null))
            UnlockDek(dots.Count > 0 ? dots : null, passcode.Length > 0 ? passcode : null);
        // v4.0.4: the unlock itself (decrypt included) runs on the worker.
        if (_busy.Contains(e.Id)) return;
        string id = e.Id, name = e.Name;
        var dek = _dek;
        _busy.Add(id);
        SendState();
        // v4.2.0: the unlock jumps ahead of queued maintenance work.
        EnqueueJobFront(() =>
        {
            string toast; bool err = false;
            try
            {
                var cfg2 = Core.Load();
                var e2 = Core.Find(cfg2, id);
                if (e2 != null) Core.UnlockFolder(cfg2, e2, true, dek);
                toast = "Unlocked: " + name;
            }
            catch (Exception ex) { toast = "Could not unlock: " + ex.Message; err = true; }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    _busy.Remove(id);
                    _cfg = Core.Load();
                    SendState();
                    SendToast(toast, err);
                }));
            }
            catch { }
        });
    }

    private void DoSetFolderColor(Dictionary<string, object> msg)
    {
        _cfg = Core.Load();
        var e = Core.Find(_cfg, GetStr(msg, "id"));
        if (e == null) { SendState(); return; }
        e.Color = GetStr(msg, "color");
        Core.Save(_cfg);
        SendState();
    }

    private void DoSetFolderNote(Dictionary<string, object> msg)
    {
        _cfg = Core.Load();
        var e = Core.Find(_cfg, GetStr(msg, "id"));
        if (e == null) { SendState(); return; }
        e.Note = GetStr(msg, "note");
        Core.Save(_cfg);
        SendState();
        SendToast("Note saved for \"" + e.Name + "\".", false);
    }

    // v4.2.0: per-folder lock mode — Just Lock (vault move only, fast) or
    // High End Lock (vault + AES-256). Applies on the next lock; switching
    // a locked folder to High encrypts it in place right away when the
    // vault key is in memory, otherwise it finishes on the next sign-in.
    private void DoSetFolderLockMode(Dictionary<string, object> msg)
    {
        _cfg = Core.Load();
        var e = Core.Find(_cfg, GetStr(msg, "id"));
        if (e == null) { SendState(); return; }
        string mode = GetStr(msg, "mode") == "high" ? "high" : "just";
        string old = e.LockMode ?? "just";
        if (old == mode) { SendState(); return; }
        Core.SetLockMode(_cfg, e, mode);
        if (mode == "high" && e.Locked && !e.Encrypted && !Core.HasEncryptedFiles(Core.VaultPath(e)))
        {
            if (_dek != null)
            {
                var dek = _dek; string id = e.Id;
                _busy.Add(id); SendState();
                EnqueueJob(() =>
                {
                    try
                    {
                        var cfg2 = Core.Load();
                        var e2 = Core.Find(cfg2, id);
                        if (e2 != null && e2.Locked && e2.LockMode == "high" && !e2.Encrypted)
                        {
                            bool okAll = Core.EncryptTree(e2, dek);
                            e2.Encrypted = okAll;
                            e2.EncPending = !okAll;
                            Core.Save(cfg2);
                        }
                    }
                    catch (Exception ex) { Core.Log("High End encrypt failed: " + ex.Message); }
                    try { BeginInvoke((Action)(() => { _busy.Remove(id); _cfg = Core.Load(); SendState(); SendToast("High End Lock on — files encrypted.", false); })); } catch { }
                });
                return;
            }
            e.EncPending = true; Core.Save(_cfg);
        }
        SendState();
        SendToast("Lock mode: " + (mode == "high" ? "High End Lock" : "Just Lock") + ".", false);
    }

    private void DoSetDecoyPattern(Dictionary<string, object> msg)
    {
        var dots = GetDots(msg, "dots");
        if (dots.Count < 4) { SendToast("Pattern too short — use at least 4 dots.", true); return; }
        _cfg = Core.Load();
        Core.SetDecoyPattern(_cfg, dots);
        Core.Save(_cfg);
        SendState();
        SendToast("Fake mode is ON — the decoy pattern opens a harmless fake panel.", false);
    }

    private void DoSetDecoyPasscode(Dictionary<string, object> msg)
    {
        string passcode = GetStr(msg, "passcode");
        if (passcode.Length < 4) { SendToast("Passcode must be at least 4 characters.", true); return; }
        _cfg = Core.Load();
        Core.SetDecoyPasscode(_cfg, passcode);
        Core.Save(_cfg);
        SendState();
        SendToast("Fake mode is ON — the decoy passcode opens a harmless fake panel.", false);
    }

    private void DoClearDecoy()
    {
        _cfg = Core.Load();
        _cfg.DecoyPattern = null;
        _cfg.DecoyPasscode = null;
        Core.Save(_cfg);
        SendState();
        SendToast("Fake mode is OFF.", false);
    }

    // ---------------- decoy panel actions (harmless fakes) ----------------
    private void DoFakeAdd()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        FakeFolders().Add(FakeFolder("d" + DateTime.Now.Ticks, "New Folder",
            Path.Combine(docs, "New Folder"), false));
        SendState();
        SendToast("Folder added and locked.", false);
    }

    private void DoFakeFlip(string id, bool locked)
    {
        var d = FindFake(id);
        if (d != null)
        {
            d["locked"] = locked;
            if (!locked) OpenDecoyFolder((string)d["name"]);
        }
        SendState();
    }

    private void DoFakeRemove(string id)
    {
        var d = FindFake(id);
        if (d != null) FakeFolders().Remove(d);
        SendState();
        SendToast("Removed from the list.", false);
    }

    private void DoFakeOpen(string id)
    {
        var d = FindFake(id);
        if (d != null) OpenDecoyFolder((string)d["name"]);
    }

    private void DoChangePattern(Dictionary<string, object> msg)
    {
        var dots = GetDots(msg, "dots");
        if (dots.Count < 4) { SendToast("Pattern too short — use at least 4 dots.", true); return; }
        _cfg = Core.Load();
        Core.SetPattern(_cfg, dots);
        if (_dek != null) _cfg.DekForPattern = Core.WrapDek(_dek, Core.PatternSecret(dots), _cfg.Iterations);
        Core.Save(_cfg);
        SendState();
        SendToast("Pattern updated.", false);
    }

    private void DoChangePasscode(Dictionary<string, object> msg)
    {
        string passcode = GetStr(msg, "passcode");
        if (passcode.Length < 4) { SendToast("Passcode must be at least 4 characters.", true); return; }
        _cfg = Core.Load();
        Core.SetPasscode(_cfg, passcode);
        if (_dek != null) _cfg.DekForPasscode = Core.WrapDek(_dek, passcode, _cfg.Iterations);
        Core.Save(_cfg);
        SendState();
        SendToast("Passcode updated.", false);
    }

    // ---------------- v4.0: vault key, recovery, backup, updates ----------------
    private static string CurrentVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v.Major + "." + v.Minor + "." + v.Build;
    }

    // After a successful sign-in: recover the vault key (DEK) from whichever
    // credential was used, create it on first v4 run, fill in any missing
    // wrap, then finish any encryption that was deferred by tray locks.
    // v4.2.0: runPending=false skips that maintenance pass — the locker
    // unlock window uses it so the user's unlock is not queued behind
    // other folders' encryption work.
    private void UnlockDek(List<int> dots, string passcode, bool runPending = true)
    {
        try
        {
            if (_dek == null && dots != null && _cfg.Pattern != null)
                _dek = Core.UnwrapDek(_cfg.DekForPattern, Core.PatternSecret(dots), _cfg.Iterations);
            if (_dek == null && !string.IsNullOrEmpty(passcode) && _cfg.Passcode != null)
                _dek = Core.UnwrapDek(_cfg.DekForPasscode, passcode, _cfg.Iterations);
            if (_dek == null && !Core.HasAnyDekWrap(_cfg))
                _dek = Core.CreateDek();
            if (_dek != null)
            {
                bool dirty = false;
                if (dots != null && string.IsNullOrEmpty(_cfg.DekForPattern))
                { _cfg.DekForPattern = Core.WrapDek(_dek, Core.PatternSecret(dots), _cfg.Iterations); dirty = true; }
                if (!string.IsNullOrEmpty(passcode) && string.IsNullOrEmpty(_cfg.DekForPasscode))
                { _cfg.DekForPasscode = Core.WrapDek(_dek, passcode, _cfg.Iterations); dirty = true; }
                if (dirty) Core.Save(_cfg);
                if (runPending) ProcessPendingEncryption();
            }
        }
        catch (Exception ex) { Core.Log("UnlockDek: " + ex.Message); }
    }

    private void ProcessPendingEncryption()
    {
        if (_dek == null) return;
        var dek = _dek;
        EnqueueJob(() =>
        {
            try
            {
                var cfg = Core.Load();
                // v4.0.2 self-heal: repair inconsistent states an interrupted
                // v4.0.0 unlock left behind, and decrypt any stray .dbe
                // files sitting in folders that are open again.
                bool changed = Core.FixInconsistentStates(cfg);
                foreach (var e in cfg.Folders)
                {
                    if (!e.Locked && Core.HasEncryptedFiles(e.Path))
                    {
                        int n = Core.DecryptTreeLenient(e.Path, dek, e.Id);
                        if (n > 0)
                        {
                            Core.LogActivity("unlock", "Restored " + n + " encrypted file(s) in \"" + e.Name + "\"");
                            changed = true;
                        }
                    }
                }
                // v4.2.0: only High End folders get their deferred
                // encryption finished — Just Lock folders never encrypt.
                foreach (var e in cfg.Folders)
                {
                    if (e.LockMode == "high" && e.Locked && e.EncPending)
                    {
                        bool okAll = Core.EncryptTree(e, dek);
                        e.Encrypted = okAll;
                        e.EncPending = !okAll;
                        changed = true;
                    }
                }
                if (changed)
                {
                    Core.Save(cfg);
                    try { BeginInvoke((Action)(() => { _cfg = Core.Load(); SendState(); })); } catch { }
                }
            }
            catch (Exception ex) { Core.Log("PendingEnc: " + ex.Message); }
        });
    }

    private void DoRecoveryCode()
    {
        _cfg = Core.Load();
        if (_dek == null) { SendToast("Sign in first, then set recovery.", true); return; }
        string code = Core.GenerateRecoveryCode();
        Core.SetRecoveryCode(_cfg, code, _dek);
        Core.Save(_cfg);
        Post(new Dictionary<string, object> { { "type", "recoveryCode" }, { "code", code } });
        SendState();
    }

    private void DoRecoveryAnswer(Dictionary<string, object> msg)
    {
        _cfg = Core.Load();
        string q = GetStr(msg, "question"), a = GetStr(msg, "answer");
        if (a.Trim().Length < 3) { SendToast("That answer is too short.", true); return; }
        Core.SetRecoveryAnswer(_cfg, q, a, _dek);
        Core.Save(_cfg);
        SendState();
        SendToast("Security question saved.", false);
    }

    private void DoRecoverVerify(Dictionary<string, object> msg)
    {
        _cfg = Core.Load();
        if (FrozenReject()) return;
        string code = GetStr(msg, "code"), answer = GetStr(msg, "answer");
        bool ok = false;
        byte[] dek = null;
        if (code.Length > 0 && Core.VerifyRecoveryCode(_cfg, code))
        {
            ok = true;
            dek = Core.UnwrapDek(_cfg.DekForRecoveryCode, Core.NormalizeCode(code), _cfg.Iterations);
        }
        else if (answer.Length > 0 && Core.VerifyRecoveryAnswer(_cfg, answer))
        {
            ok = true;
            dek = Core.UnwrapDek(_cfg.DekForRecoveryAnswer, Core.NormalizeAnswer(answer), _cfg.Iterations);
        }
        if (!ok)
        {
            RegisterFail("recovery attempt");
            Post(new Dictionary<string, object> { { "type", "recoverResult" }, { "ok", false } });
            return;
        }
        _fails = 0;
        _recoveryAuthed = true;
        if (dek != null) _dek = dek;
        Post(new Dictionary<string, object> { { "type", "recoverResult" }, { "ok", true } });
    }

    private void DoRecoverReset(Dictionary<string, object> msg)
    {
        if (!_recoveryAuthed) return;
        var dots = GetDots(msg, "dots");
        string passcode = GetStr(msg, "passcode");
        bool hasPattern = dots.Count >= 4;
        bool hasPasscode = passcode.Length >= 4;
        if (!hasPattern && !hasPasscode)
        {
            SendAuthError("Set a pattern (4+ dots) or a passcode of 4+ characters.");
            return;
        }
        _cfg = Core.Load();
        if (hasPattern) Core.SetPattern(_cfg, dots); else _cfg.Pattern = null;
        if (hasPasscode) Core.SetPasscode(_cfg, passcode); else _cfg.Passcode = null;
        if (_dek == null) _dek = Core.CreateDek();
        _cfg.DekForPattern = hasPattern ? Core.WrapDek(_dek, Core.PatternSecret(dots), _cfg.Iterations) : "";
        _cfg.DekForPasscode = hasPasscode ? Core.WrapDek(_dek, passcode, _cfg.Iterations) : "";
        Core.Save(_cfg);
        Core.LogActivity("unlock", "Lock reset with recovery.");
        _recoveryAuthed = false;
        _fails = 0;
        if (!IsUnlockMode)
        {
            _authed = true;
            Program.StartAgentIfNeeded(_cfg);
        }
        SendState();
        SendToast("Your lock has been reset.", false);
    }

    private void DoBackupExport(Dictionary<string, object> msg)
    {
        string pw = GetStr(msg, "password");
        if (pw.Length < 4) { SendToast("Backup password must be at least 4 characters.", true); return; }
        using (var dlg = new SaveFileDialog())
        {
            dlg.Filter = "DarkBox Backup (*.dbbackup)|*.dbbackup";
            dlg.FileName = "darkbox-backup-" + DateTime.Now.ToString("yyyyMMdd") + ".dbbackup";
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                Core.ExportBackup(dlg.FileName, pw);
                SendToast("Encrypted backup saved.", false);
            }
            catch (Exception ex) { SendToast("Backup failed: " + ex.Message, true); }
        }
    }

    private void DoBackupRestore(Dictionary<string, object> msg)
    {
        string pw = GetStr(msg, "password");
        if (pw.Length == 0) { SendToast("Enter the backup password first.", true); return; }
        using (var dlg = new OpenFileDialog())
        {
            dlg.Filter = "DarkBox Backup (*.dbbackup)|*.dbbackup|All files (*.*)|*.*";
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                _cfg = Core.ImportBackup(dlg.FileName, pw);
                _dek = null;
                _authed = false;
                Core.LogActivity("unlock", "Settings restored from an encrypted backup.");
                SendState();
                SendToast("Backup restored. Please sign in again.", false);
            }
            catch (Exception ex) { SendToast("Restore failed: " + ex.Message, true); }
        }
    }

    private void MaybeAutoUpdateCheck()
    {
        if (_updateChecked) return;
        _updateChecked = true;
        DoCheckUpdate(true);
    }

    private void DoCheckUpdate(bool auto)
    {
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            string latest = "", url = "", notes = "";
            bool ok = false;
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(
                    "https://hamiiscripts.com/darkbox/version.json");
                req.Timeout = 6000;
                req.UserAgent = "DarkBox/" + CurrentVersion();
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    var d = MiniJson.Parse(sr.ReadToEnd()) as Dictionary<string, object>;
                    if (d != null)
                    {
                        latest = GetStr(d, "version");
                        url = GetStr(d, "url");
                        notes = GetStr(d, "notes");
                        ok = latest.Length > 0;
                    }
                }
            }
            catch { }
            var m = new Dictionary<string, object>
            {
                { "type", "updateInfo" }, { "ok", ok }, { "auto", auto },
                { "latest", latest }, { "url", url }, { "notes", notes },
                { "current", CurrentVersion() },
            };
            try { BeginInvoke((Action)(() => Post(m))); } catch { }
        });
    }

    // ---------------- guard mode (unlock window) ----------------
    private void BecomeGuard(FolderEntry entry)
    {
        _guarding = true;
        Hide();
        ShowInTaskbar = false;
        // v4.1.7: NotifyIcon text must stay under 64 chars (log 11:16:
        // "Text length must be less than 64 characters" crashed the guard
        // for long folder names like "NothingHereXD").
        string trayText = "DarkBox — \"" + entry.Name + "\" is open; it locks when its window closes";
        if (trayText.Length > 63) trayText = trayText.Substring(0, 63);
        _tray = new NotifyIcon
        {
            Icon = AppIcon.Get(),
            Text = trayText,
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Lock Now", null, (s, e) => { if (TryLockNow()) ExitGuard(); });
        menu.Items.Add("Stop Guard (folder stays open)", null, (s, e) => ExitGuard());
        _tray.ContextMenuStrip = menu;
        _tray.ShowBalloonTip(2500, "DarkBox",
            "\"" + entry.Name + "\" is open. It will lock itself when you close its window.",
            ToolTipIcon.Info);
        _brain = new WatchBrain();
        // v4.1.0: 4 s instead of 2 s — the guard enumerates every Explorer
        // window through COM on each tick; on weak PCs that was a steady
        // background cost. Auto-lock still fires ~4 s after the window
        // closes, and the tray agent (same interval) covers the gap.
        _watchTimer = new Timer { Interval = 4000 };
        _watchTimer.Tick += WatchTick;
        _watchTimer.Start();
    }

    private void WatchTick(object sender, EventArgs e)
    {
        try
        {
            var cfg = Core.Load();
            var entry = Core.Find(cfg, _unlockId);
            if (entry == null || entry.Locked || !cfg.AutoLockOnClose) { ExitGuard(); return; }
            var open = Core.GetOpenExplorerPaths();
            if (open == null) return;
            var items = new List<WatchItem>
            {
                new WatchItem(entry.Id, Core.NormalizePath(entry.Path), entry.Locked),
            };
            foreach (string id in _brain.Poll(items, open))
            {
                if (id == entry.Id)
                {
                    if (TryLockNow()) ExitGuard();
                    else _brain.Reset(id); // file in use — retry shortly
                }
            }
        }
        catch (Exception ex)
        {
            Core.Log("Guard tick failed: " + ex.Message);
        }
    }

    private bool TryLockNow()
    {
        try
        {
            var cfg = Core.Load();
            var entry = Core.Find(cfg, _unlockId);
            if (entry == null || entry.Locked) return true;
            Core.LockFolder(cfg, entry);
            return true;
        }
        catch (Exception ex)
        {
            Core.Log("Guard lock failed: " + ex.Message);
            return false;
        }
    }

    private void ExitGuard()
    {
        if (_watchTimer != null) { _watchTimer.Stop(); _watchTimer.Dispose(); _watchTimer = null; }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        _guarding = false;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        base.OnFormClosing(e);
    }

    // ---------------- borderless resize ----------------
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTCLIENT = 1;
        if (m.Msg == WM_NCHITTEST && !IsUnlockMode && WindowState == FormWindowState.Normal)
        {
            base.WndProc(ref m);
            if ((int)m.Result == HTCLIENT)
            {
                int x = (short)(m.LParam.ToInt64() & 0xFFFF);
                int y = (short)((m.LParam.ToInt64() >> 16) & 0xFFFF);
                Point pt = PointToClient(new Point(x, y));
                int b = 6;
                bool left = pt.X <= b, right = pt.X >= ClientSize.Width - b;
                bool top = pt.Y <= b, bottom = pt.Y >= ClientSize.Height - b;
                int code = 0;
                if (left && top) code = 13;        // HTTOPLEFT
                else if (right && top) code = 14;  // HTTOPRIGHT
                else if (left && bottom) code = 16;  // HTBOTTOMLEFT
                else if (right && bottom) code = 17; // HTBOTTOMRIGHT
                else if (left) code = 10;          // HTLEFT
                else if (right) code = 11;         // HTRIGHT
                else if (top) code = 12;           // HTTOP
                else if (bottom) code = 15;        // HTBOTTOM
                if (code != 0) { m.Result = (IntPtr)code; return; }
            }
            return;
        }
        base.WndProc(ref m);
    }
}
