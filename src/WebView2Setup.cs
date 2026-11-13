using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using Microsoft.Win32;

// v4.1.2: WebView2 auto-install (shared by the admin panel, the unlock
// window and the portable EXE — all of them need the runtime to show
// any window at all).
// The runtime is a free Microsoft component (~205 MB, one-time, shared
// by every app that uses it). Most Windows 10/11 PCs already have it
// (it ships with Edge and Office), so in practice this check usually
// finds it and downloads nothing. Only a PC without it ever downloads
// anything — once, from Microsoft, with a progress bar.
public static class WebView2Setup
{
    public const string RuntimeClientId = "{F3017226-FE2A-4295-9251-3EDEE3F2E132}";
    // Full standalone installer (checked 12 Nov 2026: 214,990,032 bytes).
    // v4.1.1 used the small bootstrapper, which hides the real download
    // inside its silent install — that is why its failure was invisible.
    public const string StandaloneUrl =
        "https://go.microsoft.com/fwlink/p/?LinkId=2124701";

    // NOTE: no CoreWebView2Environment API call here on purpose — the
    // version-string API is not in this WebView2 SDK version, and the
    // registry check below is the documented detection anyway.
    // v4.1.3: registry key counts even before its version value is
    // written, and the runtime files themselves count too — v4.1.2
    // proved the silent installer can exit 0 without the key appearing.
    // Fixed-version runtime: a folder holding msedgewebview2.exe, no
    // system install needed. Checked next to the EXE first (the setup
    // extracts a downloaded package there), then in the app data dir,
    // then in the install dir — v4.1.6: the installer wizard runs from
    // a staging folder, not the install dir, so it must look there too
    // or it cannot even open its own window.
    public static string GetFixedVersionFolder()
    {
        foreach (string baseDir in new[]
        {
            AppDomain.CurrentDomain.BaseDirectory,
            Path.Combine(Core.DataDir, "WebView2Runtime"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "HamiiFolderLocker", "WebView2Runtime"),
        })
        {
            string exe = FindFixedRuntimeExe(baseDir);
            if (exe != null) return Path.GetDirectoryName(exe);
        }
        return null;
    }

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

    public static bool IsRuntimeInstalled()
    {
        if (GetFixedVersionFolder() != null) return true;
        string sub = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + RuntimeClientId;
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
    public static bool IsRuntimeOnDisk()
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

    // After a failed install: record exactly what is on this PC.
    private static string LogDiagnostics()
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
        string sub = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + RuntimeClientId;
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
        Core.Log("WebView2 diagnostics — " + string.Join(" | ", found.ToArray()));
        return IsRuntimeOnDisk()
            ? "runtime files ARE on this PC but Windows did not register them"
            : "no runtime files found on this PC";
    }

    public static bool TryInstall()
    {
        return TryInstall(null);
    }

    // progress(percent, detail): percent 0-100 while downloading,
    // -1 while installing. Never throws — failures return false.
    public static bool TryInstall(Action<int, string> progress)
    {
        string installer = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebView2RuntimeInstallerX64.exe");
        try
        {
            if (progress != null) progress(0, "Connecting to Microsoft…");
            DownloadWithProgress(StandaloneUrl, installer, (done, total) =>
            {
                if (progress == null) return;
                int pct = total > 0 ? (int)(done * 100 / total) : -1;
                string detail = total > 0
                    ? FormatMb(done) + " of " + FormatMb(total) + "  ·  " + pct + "%"
                    : FormatMb(done) + " downloaded";
                progress(pct, detail);
            });
            if (progress != null) progress(-1, "Installing… this can take a minute.");
            int exitCode;
            bool ok = RunInstaller(installer, out exitCode);
            Core.Log("WebView2 installer finished. Exit code: " + exitCode + ". Installed: " + ok);
            if (!ok)
            {
                // Silent install exited but nothing registered — run
                // Microsoft's installer visibly once so its own
                // screen/error shows instead of guessing.
                if (progress != null) progress(-1, "Silent install did not register — running the installer visibly once…");
                ok = RunInstallerVisible(installer, out exitCode);
                Core.Log("WebView2 visible installer finished. Exit code: " + exitCode + ". Installed: " + ok);
            }
            if (!ok) Core.Log("WebView2 install diagnostics — " + LogDiagnostics());
            return ok;
        }
        catch (Exception ex)
        {
            Core.Log("WebView2 auto-install failed: " + ex.Message);
            return false;
        }
        finally
        {
            try { if (File.Exists(installer)) File.Delete(installer); } catch { }
        }
    }

    private static string FormatMb(long bytes)
    {
        double mb = bytes / 1048576.0;
        return (mb >= 100 ? Math.Round(mb).ToString() : mb.ToString("0.0")) + " MB";
    }

    private static void DownloadWithProgress(string url, string dest, Action<long, long> progress)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.AllowAutoRedirect = true;
        req.UserAgent = "DarkBox/4.1.2";
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

    // First elevated (per-machine install, one UAC prompt), then falls
    // back to a non-elevated per-user install if elevation is declined.
    // Returns true only when the runtime actually registers afterwards.
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
                        Core.Log("WebView2 installer timed out (elevated=" + elevated + ").");
                        return false;
                    }
                    exitCode = proc.ExitCode;
                }
                Core.Log("WebView2 installer exit code " + exitCode + " (elevated=" + elevated + ").");
                if (IsRuntimeInstalled()) return true;
                if (!elevated) return false;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Core.Log("WebView2 installer elevation failed (elevated=" + elevated + "): " + ex.Message);
                if (!elevated) return false;
            }
            catch (Exception ex)
            {
                Core.Log("WebView2 installer error (elevated=" + elevated + "): " + ex.Message);
                if (!elevated) return false;
            }
        }
        return IsRuntimeInstalled();
    }

    // Last resort: Microsoft's installer with its own window, so any
    // error it shows (Edge open, pending reboot, policy) is visible
    // instead of hidden inside a silent run.
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
                    Core.Log("WebView2 visible installer timed out.");
                    return false;
                }
                exitCode = proc.ExitCode;
            }
            Core.Log("WebView2 visible installer exit code " + exitCode + ".");
            return IsRuntimeInstalled();
        }
        catch (Exception ex)
        {
            Core.Log("WebView2 visible installer error: " + ex.Message);
            return false;
        }
    }
}
