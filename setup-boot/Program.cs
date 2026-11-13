using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

// DarkBox Setup bootstrapper. It carries the whole app as an embedded
// payload.zip, unpacks it to a staging folder and launches the app's
// own modern installer wizard (--installer). The same file, copied into
// the install folder as Uninstall.exe, removes the program when run
// with --uninstall (locker data in %APPDATA% is kept on purpose).
internal static class BootProgram
{
    private static string InstallDir
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "HamiiFolderLocker");
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool uninstall = Array.IndexOf(args, "--uninstall") >= 0;
        try
        {
            if (uninstall) { Uninstall(); return; }

            // v4.1.1: the installer wizard itself is a WebView2 window — if
            // the runtime is missing, the wizard cannot even open (the user
            // just gets the "WebView2 Runtime is needed" box). So check
            // FIRST, here in the plain-WinForms bootstrapper, and install
            // the free Microsoft runtime automatically when it is missing.
            // Most PCs already have it, so most installs download nothing.
            // v4.1.7: a stale DarkBox (tray) instance can hold the WebView2
            // user-data folder locked, so the wizard's init fails with
            // 0x8007139F even with a good runtime. Close it first — the
            // wizard reinstalls the app anyway.
            foreach (var p in Process.GetProcessesByName("HamiiFolderLocker"))
            {
                try { p.Kill(); } catch { }
            }
            System.Threading.Thread.Sleep(700);
            EnsureWebView2();

            string staging = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DarkBoxSetup", "staging");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);

            using (var stream = Assembly.GetExecutingAssembly()
                       .GetManifestResourceStream("payload.zip"))
            {
                if (stream == null) throw new Exception("The setup payload is missing.");
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                    zip.ExtractToDirectory(staging);
            }

            string self = Assembly.GetExecutingAssembly().Location;
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(staging, "HamiiFolderLocker.exe"),
                Arguments = "--installer --setuppath \"" + self + "\"",
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("DarkBox Setup could not start:\n\n" + ex.Message,
                "DarkBox Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // v4.1.1: WebView2 auto-install.
    // The runtime's EdgeUpdate client id — its "pv" value holds the
    // installed version. Checked in both hives and both registry views,
    // because the runtime can be installed per-machine or per-user.
    private const string WebView2ClientId = "{F3017226-FE2A-4295-9251-3EDEE3F2E132}";
    // Microsoft's official Evergreen STANDALONE installer (full runtime,
    // about 205 MB — checked 12 Nov 2026: Content-Length 214,990,032).
    // v4.1.1 used the small bootstrapper instead, which then downloads the
    // same payload invisibly inside its silent install — no progress bar
    // was possible and its failure was invisible. Downloading the full
    // installer here gives a real progress bar and a real error.
    private const string WebView2StandaloneUrl =
        "https://go.microsoft.com/fwlink/p/?LinkId=2124701";

    // Registry (the documented detection) OR the runtime files
    // themselves. v4.1.2 proved the silent installer can exit 0
    // without the registry key appearing — so files on disk count too,
    // and a key counts even before its version value is written.
    // v4.1.4: a fixed-version runtime folder in the install dir counts
    // too — the app uses it directly, no system install needed.
    private static bool IsWebView2Installed()
    {
        if (FindFixedRuntimeExe(Path.Combine(InstallDir, "WebView2Runtime")) != null) return true;
        string sub = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WebView2ClientId;
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                    using (var key = baseKey.OpenSubKey(sub))
                    {
                        if (key != null) return true;
                    }
                }
                catch { }
            }
        }
        return IsRuntimeOnDisk();
    }

    // msedgewebview2.exe under EdgeWebView\Application\<version>\ in
    // either Program Files location.
    private static bool IsRuntimeOnDisk()
    {
        foreach (string pf in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        })
        {
            try
            {
                string appDir = Path.Combine(pf, "Microsoft", "EdgeWebView", "Application");
                if (!Directory.Exists(appDir)) continue;
                foreach (string ver in Directory.GetDirectories(appDir))
                    if (File.Exists(Path.Combine(ver, "msedgewebview2.exe"))) return true;
            }
            catch { }
        }
        return false;
    }

    // msedgewebview2.exe directly in the folder, or one level down
    // (in case extraction created a nested folder). Returns the exe
    // path, or null.
    private static string FindFixedRuntimeExe(string folder)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
            string exe = Path.Combine(folder, "msedgewebview2.exe");
            if (File.Exists(exe)) return exe;
            foreach (string sub in Directory.GetDirectories(folder))
            {
                exe = Path.Combine(sub, "msedgewebview2.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { }
        return null;
    }

    // Finds a fixed-version runtime package the user already downloaded
    // (Microsoft.WebView2.FixedVersionRuntime.<version>.x64.cab) in
    // Downloads, on the Desktop, or next to this setup. Newest wins.
    private static string FindFixedVersionPackage()
    {
        var dirs = new System.Collections.Generic.List<string>();
        try { dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")); } catch { }
        try { dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)); } catch { }
        try { dirs.Add(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)); } catch { }
        string best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (string dir in dirs)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                foreach (string f in Directory.GetFiles(dir, "Microsoft.WebView2.FixedVersionRuntime.*"))
                {
                    DateTime t = File.GetLastWriteTime(f);
                    if (t > bestTime) { bestTime = t; best = f; }
                }
            }
            catch { }
        }
        return best;
    }

    // v4.1.4: if the user already downloaded the fixed-version package,
    // extract it next to the app with Windows' own expand.exe. The
    // runtime then lives in a folder — no system install, no registry,
    // nothing to register. Returns the runtime folder, or null.
    private static string TryUseDownloadedFixedVersion()
    {
        string cab = FindFixedVersionPackage();
        if (cab == null) { LogSetup("No downloaded fixed-version WebView2 package found."); return null; }
        LogSetup("Found fixed-version WebView2 package: " + cab);
        string dest = Path.Combine(InstallDir, "WebView2Runtime");
        try
        {
            Directory.CreateDirectory(dest);
            var psi = new ProcessStartInfo
            {
                FileName = "expand.exe",
                Arguments = "\"" + cab + "\" -F:* \"" + dest + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var proc = Process.Start(psi))
            {
                if (proc == null) return null;
                string outText = proc.StandardOutput.ReadToEnd();
                string errText = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                LogSetup("expand.exe exit code " + proc.ExitCode + ".");
                if (!string.IsNullOrWhiteSpace(outText)) LogSetup("expand output: " + outText.Trim());
                if (!string.IsNullOrWhiteSpace(errText)) LogSetup("expand error: " + errText.Trim());
                if (proc.ExitCode != 0) return null;
            }
            string exe = FindFixedRuntimeExe(dest);
            if (exe != null) return Path.GetDirectoryName(exe);
            LogSetup("Fixed-version extraction finished but msedgewebview2.exe was not found in " + dest);
            return null;
        }
        catch (Exception ex)
        {
            LogSetup("Fixed-version extraction failed: " + ex.Message);
            return null;
        }
    }
    // v4.1.5: the package may already be EXTRACTED (a folder in
    // Downloads, usually nested one level: <name>\<name>\msedgewebview2.exe).
    // Finds it, then copies the folder that holds msedgewebview2.exe
    // next to the app — with a progress window, since the copy takes a
    // while. Returns the runtime folder, or null.
    private static string TryUseExtractedFixedVersion()
    {
        string folder = FindExtractedFixedVersionFolder();
        if (folder == null) { LogSetup("No extracted fixed-version WebView2 folder found."); return null; }
        var form = new SetupProgressForm();
        string result = null;
        var worker = new System.Threading.Thread(() =>
        {
            try
            {
                form.Report("Copying WebView2 Runtime from your downloaded files…", -1, "This can take a minute. Please wait.");
                form.SetInstalling();
                result = CopyExtractedFixedVersion(folder);
            }
            finally { form.Done(); }
        });
        worker.IsBackground = true;
        form.Shown += (s, e) => worker.Start();
        Application.Run(form);
        return result;
    }

    private static string CopyExtractedFixedVersion(string folder)
    {
        string exe = FindFixedRuntimeExe(folder);
        if (exe == null) return null;
        string runtimeFolder = Path.GetDirectoryName(exe);
        string dest = Path.Combine(InstallDir, "WebView2Runtime");
        try
        {
            LogSetup("Copying fixed-version runtime from " + runtimeFolder + " to " + dest);
            CopyDirectory(runtimeFolder, dest);
            string check = FindFixedRuntimeExe(dest);
            if (check != null) return Path.GetDirectoryName(check);
            LogSetup("Copy finished but msedgewebview2.exe was not found in " + dest);
            return null;
        }
        catch (Exception ex)
        {
            LogSetup("Fixed-version copy failed: " + ex.Message);
            return null;
        }
    }

    private static string FindExtractedFixedVersionFolder()
    {
        var dirs = new System.Collections.Generic.List<string>();
        try { dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")); } catch { }
        try { dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)); } catch { }
        try { dirs.Add(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)); } catch { }
        string best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (string dir in dirs)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                foreach (string sub in Directory.GetDirectories(dir, "Microsoft.WebView2.FixedVersionRuntime.*"))
                {
                    if (FindFixedRuntimeExe(sub) == null) continue;
                    DateTime t = Directory.GetLastWriteTime(sub);
                    if (t > bestTime) { bestTime = t; best = sub; }
                }
            }
            catch { }
        }
        return best;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
        foreach (string sub in Directory.GetDirectories(source))
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }

    private static string LogInstallDiagnostics()
    {
        var found = new System.Collections.Generic.List<string>();
        foreach (string pf in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        })
        {
            try
            {
                string appDir = Path.Combine(pf, "Microsoft", "EdgeWebView", "Application");
                if (Directory.Exists(appDir))
                {
                    foreach (string ver in Directory.GetDirectories(appDir))
                    {
                        bool exe = File.Exists(Path.Combine(ver, "msedgewebview2.exe"));
                        found.Add("folder " + ver + " (msedgewebview2.exe: " + (exe ? "yes" : "no") + ")");
                    }
                }
                else found.Add("no folder " + appDir);
            }
            catch (Exception ex) { found.Add("folder check failed: " + ex.Message); }
        }
        string sub = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WebView2ClientId;
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                    using (var key = baseKey.OpenSubKey(sub))
                    {
                        found.Add(hive + "/" + view + " key: " + (key == null ? "missing"
                            : "present, pv=" + Convert.ToString(key.GetValue("pv"))));
                    }
                }
                catch (Exception ex) { found.Add(hive + "/" + view + " key check failed: " + ex.Message); }
            }
        }
        LogSetup("WebView2 diagnostics — " + string.Join(" | ", found.ToArray()));
        return IsRuntimeOnDisk()
            ? "runtime files ARE on this PC but Windows did not register them"
            : "no runtime files found on this PC";
    }

    private static void EnsureWebView2()
    {
        if (IsWebView2Installed()) return;
        // v4.1.4: the user may already have downloaded the fixed-version
        // runtime package (a .cab holding the whole runtime — no system
        // install, nothing to register). Use it before downloading
        // anything from Microsoft.
        string fixedFolder = TryUseDownloadedFixedVersion();
        if (fixedFolder != null)
        {
            LogSetup("Fixed-version WebView2 runtime ready at " + fixedFolder + ". No system install needed.");
            return;
        }
        // v4.1.5: the package may already be extracted in Downloads —
        // the .cab is gone, but the folder with msedgewebview2.exe is
        // still there. Copy it next to the app instead.
        fixedFolder = TryUseExtractedFixedVersion();
        if (fixedFolder != null)
        {
            LogSetup("Fixed-version WebView2 runtime ready at " + fixedFolder + ". No system install needed.");
            return;
        }
        var ask = MessageBox.Show(
            "DarkBox needs the free Microsoft Edge WebView2 Runtime to show its window, and it is not installed on this PC yet.\n\n" +
            "Download and install it now from Microsoft? It is a one-time install (about 205 MB) shared by every app that uses it — most PCs already have it, so this only appears when it is missing.",
            "DarkBox Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ask != DialogResult.Yes) return;
        LogSetup("WebView2 missing — starting automatic install.");
        string installer = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebView2RuntimeInstallerX64.exe");
        var form = new SetupProgressForm();
        bool installed = false, cancelled = false;
        string error = null;
        int exitCode = -1;
        form.CancelRequested += (s, e) => { _webView2Cancelled = true; };
        var worker = new System.Threading.Thread(() =>
        {
            try
            {
                form.Report("Downloading WebView2 Runtime from Microsoft…", 0, "Connecting…");
                DownloadWithProgress(WebView2StandaloneUrl, installer, (done, total) =>
                {
                    int pct = total > 0 ? (int)(done * 100 / total) : -1;
                    string detail = total > 0
                        ? FormatMb(done) + " of " + FormatMb(total) + "  ·  " + pct + "%"
                        : FormatMb(done) + " downloaded";
                    form.Report("Downloading WebView2 Runtime from Microsoft…", pct, detail);
                });
                if (_webView2Cancelled) { cancelled = true; return; }
                LogSetup("WebView2 download finished (" + new FileInfo(installer).Length + " bytes). Installing…");
                form.SetInstalling();
                form.Report("Installing WebView2 Runtime…", -1, "This can take a minute. Please wait.");
                installed = RunInstaller(installer, out exitCode);
                LogSetup("WebView2 installer finished. Exit code: " + exitCode + ". Installed: " + installed);
                if (!installed)
                {
                    // v4.1.2: silent install exited 0 twice but nothing
                    // registered. Run Microsoft's installer visibly once
                    // so its own screen/error shows instead of guessing.
                    form.Report("Silent install did not register — running the installer visibly once…", -1,
                        "If Microsoft's installer asks anything, please follow it.");
                    installed = RunInstallerVisible(installer, out exitCode);
                    LogSetup("WebView2 visible installer finished. Exit code: " + exitCode + ". Installed: " + installed);
                }
                if (!installed)
                {
                    string diag = LogInstallDiagnostics();
                    error = "installer finished (exit code " + exitCode + ") but the runtime did not register — " + diag;
                }
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception ex)
            {
                error = ex.Message;
                LogSetup("WebView2 auto-install failed: " + ex);
            }
            finally
            {
                try { if (File.Exists(installer)) File.Delete(installer); } catch { }
                form.Done();
            }
        });
        worker.IsBackground = true;
        form.Shown += (s, e) => worker.Start();
        Application.Run(form);
        if (installed && IsWebView2Installed()) { LogSetup("WebView2 ready."); return; }
        if (cancelled) { LogSetup("WebView2 install cancelled by user."); return; }
        MessageBox.Show(
            "WebView2 could not be installed automatically"
            + (error != null ? " (" + error + ")" : "") + ".\n\n" +
            "DarkBox will offer the download again when it starts. Details were saved to:\n" + SetupLogPath,
            "DarkBox Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static volatile bool _webView2Cancelled;

    private static string SetupLogPath
    {
        get { return Path.Combine(Path.GetTempPath(), "DarkBoxSetup.log"); }
    }

    private static void LogSetup(string msg)
    {
        try
        {
            File.AppendAllText(SetupLogPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n");
        }
        catch { }
    }

    private static string FormatMb(long bytes)
    {
        double mb = bytes / 1048576.0;
        return (mb >= 100 ? Math.Round(mb).ToString() : mb.ToString("0.0")) + " MB";
    }

    // Download with a live progress bar. Forces TLS 1.2: on .NET Framework
    // the default can fall back to TLS 1.0 on older Windows, and Microsoft's
    // servers refuse that — the silent cause of the v4.1.1 failure.
    private static void DownloadWithProgress(string url, string dest, Action<long, long> progress)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.AllowAutoRedirect = true;
        req.UserAgent = "DarkBoxSetup/4.1.2";
        req.Timeout = 30000;
        req.ReadWriteTimeout = 120000;
        using (var resp = req.GetResponse())
        using (var stream = resp.GetResponseStream())
        using (var file = new FileStream(dest, FileMode.Create, FileAccess.Write))
        {
            long total = resp.ContentLength;
            byte[] buf = new byte[65536];
            long done = 0;
            int read;
            DateTime lastReport = DateTime.MinValue;
            while ((read = stream.Read(buf, 0, buf.Length)) > 0)
            {
                if (_webView2Cancelled) throw new OperationCanceledException("Cancelled by user.");
                file.Write(buf, 0, read);
                done += read;
                if ((DateTime.Now - lastReport).TotalMilliseconds >= 200)
                {
                    lastReport = DateTime.Now;
                    try { progress(done, total); } catch { }
                }
            }
            try { progress(done, total); } catch { }
        }
    }

    // Runs the runtime installer. First elevated (per-machine install,
    // one UAC prompt) — the v4.1.1 silent run without elevation is the
    // other likely cause of its failure. If elevation is declined, falls
    // back to a non-elevated per-user install. Returns true only when
    // the runtime actually registers afterwards.
    private static bool RunInstaller(string path, out int exitCode)
    {
        exitCode = -1;
        foreach (bool elevated in new[] { true, false })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "/silent /install",
                    UseShellExecute = true,
                };
                if (elevated) psi.Verb = "runas";
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) continue;
                    if (!proc.WaitForExit(600000))
                    {
                        try { proc.Kill(); } catch { }
                        LogSetup("WebView2 installer timed out (elevated=" + elevated + ").");
                        return false;
                    }
                    exitCode = proc.ExitCode;
                }
                LogSetup("WebView2 installer exit code " + exitCode + " (elevated=" + elevated + ").");
                if (IsWebView2Installed()) return true;
                if (!elevated) return false;
                // Elevated ran but did not register — try per-user next.
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // UAC declined (1223) or elevation unavailable.
                LogSetup("WebView2 installer elevation failed (elevated=" + elevated + "): " + ex.Message);
                if (!elevated) return false;
            }
            catch (Exception ex)
            {
                LogSetup("WebView2 installer error (elevated=" + elevated + "): " + ex.Message);
                if (!elevated) return false;
            }
        }
        return IsWebView2Installed();
    }

    // Last resort: Microsoft's installer with its own window, so any
    // error it shows (Edge open, pending reboot, policy) is visible
    // instead of hidden inside a silent run. Returns true only when
    // the runtime is detected afterwards.
    private static bool RunInstallerVisible(string path, out int exitCode)
    {
        exitCode = -1;
        try
        {
            using (var proc = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = "/install",
                UseShellExecute = true,
            }))
            {
                if (proc == null) return false;
                if (!proc.WaitForExit(600000))
                {
                    try { proc.Kill(); } catch { }
                    LogSetup("WebView2 visible installer timed out.");
                    return false;
                }
                exitCode = proc.ExitCode;
            }
            LogSetup("WebView2 visible installer exit code " + exitCode + ".");
            return IsWebView2Installed();
        }
        catch (Exception ex)
        {
            LogSetup("WebView2 visible installer error: " + ex.Message);
            return false;
        }
    }

    private static void Uninstall()
    {
        var r = MessageBox.Show(
            "Remove DarkBox from this PC?\n\n" +
            "Your pattern, folder list and settings stay saved in your user data, " +
            "so reinstalling later restores everything.\n\n" +
            "Tip: if you are removing DarkBox for good, unlock your folders first.",
            "DarkBox", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;

        foreach (var p in Process.GetProcessesByName("HamiiFolderLocker"))
        {
            try { p.Kill(); } catch { }
        }
        System.Threading.Thread.Sleep(700);

        foreach (string f in new[]
        {
            "HamiiFolderLocker.exe", "HamiiFolderLocker.exe.config",
            "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll",
            "WebView2Loader.dll", "DirectShowLib.dll", "README.md",
        })
            TryDelete(Path.Combine(InstallDir, f));

        string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        TryDelete(Path.Combine(programs, "DarkBox.lnk"));
        TryDelete(Path.Combine(programs, "DarkBox", "DarkBox.lnk"));
        TryDelete(Path.Combine(programs, "DarkBox", "Uninstall.lnk"));
        try { Directory.Delete(Path.Combine(programs, "DarkBox"), false); } catch { }
        TryDelete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "DarkBox.lnk"));

        try { Registry.CurrentUser.DeleteSubKeyTree(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HamiiFolderLocker", false); }
        catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(
            @"Software\Microsoft\Windows\CurrentVersion\App Paths\DarkBox.exe", false); }
        catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\HamiiFolderLocker", false); }
        catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\DarkBoxLock", false); }
        catch { }
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                if (k != null) k.DeleteValue("HamiiFolderLockerPro", false);
        }
        catch { }

        // Delete this uninstaller itself and the (now empty) install dir.
        string self = Assembly.GetExecutingAssembly().Location;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + self
                    + "\" & rmdir \"" + InstallDir + "\"",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
