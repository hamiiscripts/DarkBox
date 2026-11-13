using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

// File installation for the modern setup wizard: copies the staged
// payload into %LOCALAPPDATA%\Programs\HamiiFolderLocker and registers
// shortcuts, App Paths and the uninstall entry. User data in
// %APPDATA%\HamiiFolderLockerPro is never touched here — updating or
// reinstalling only replaces program files.
internal static class Installer
{
    public static string SetupExePath = "";

    public static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "HamiiFolderLocker");

    public static string InstallExe
    {
        get { return Path.Combine(InstallDir, "HamiiFolderLocker.exe"); }
    }

    public static readonly string[] PayloadFiles =
    {
        "HamiiFolderLocker.exe", "HamiiFolderLocker.exe.config",
        "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll",
        "WebView2Loader.dll", "DirectShowLib.dll", "README.md",
    };

    public static string VersionOf(string exe)
    {
        try
        {
            if (!File.Exists(exe)) return "";
            string v = FileVersionInfo.GetVersionInfo(exe).FileVersion;
            return v == null ? "" : v.Trim();
        }
        catch { return ""; }
    }

    public static string ShortVersion(string v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        var parts = v.Split('.');
        return parts.Length >= 3 ? parts[0] + "." + parts[1] + "." + parts[2] : v;
    }

    public static void Install(string stagingDir, string currentVersion)
    {
        // 1) stop any running copy that lives in the install dir
        //    (admin panel, tray agent or an unlock window).
        foreach (var p in Process.GetProcessesByName("HamiiFolderLocker"))
        {
            try
            {
                string path = "";
                try { path = p.MainModule.FileName; } catch { }
                if (string.Equals(path, InstallExe, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }

        // 2) copy the payload
        Directory.CreateDirectory(InstallDir);
        foreach (string name in PayloadFiles)
        {
            string src = Path.Combine(stagingDir, name);
            if (!File.Exists(src)) continue;
            CopyWithRetry(src, Path.Combine(InstallDir, name));
        }

        // 3) the uninstaller is the setup bootstrapper itself
        if (!string.IsNullOrEmpty(SetupExePath) && File.Exists(SetupExePath))
            CopyWithRetry(SetupExePath, Path.Combine(InstallDir, "Uninstall.exe"));

        // 4) registry: install dir, uninstall entry, App Paths
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\HamiiFolderLocker"))
            k.SetValue("InstallDir", InstallDir);
        using (var k = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HamiiFolderLocker"))
        {
            k.SetValue("DisplayName", "DarkBox by Hamii");
            k.SetValue("DisplayVersion", ShortVersion(currentVersion));
            k.SetValue("Publisher", "Hamii Scripts");
            k.SetValue("DisplayIcon", InstallExe);
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString",
                "\"" + Path.Combine(InstallDir, "Uninstall.exe") + "\" --uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        using (var k = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\App Paths\DarkBox.exe"))
        {
            k.SetValue("", InstallExe);
            k.SetValue("Path", InstallDir);
        }

        // 5) shortcuts pointing at the installed copy
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        Program.CreateShortcut(Path.Combine(programs, "DarkBox.lnk"), InstallExe, InstallDir);
        string folder = Path.Combine(programs, "DarkBox");
        Directory.CreateDirectory(folder);
        Program.CreateShortcut(Path.Combine(folder, "DarkBox.lnk"), InstallExe, InstallDir);
        Program.CreateShortcut(Path.Combine(folder, "Uninstall.lnk"),
            Path.Combine(InstallDir, "Uninstall.exe"), InstallDir);
        Program.CreateShortcut(
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory), "DarkBox.lnk"),
            InstallExe, InstallDir);
    }

    private static void CopyWithRetry(string src, string dst)
    {
        for (int i = 0; i < 3; i++)
        {
            try { File.Copy(src, dst, true); return; }
            catch (IOException) { System.Threading.Thread.Sleep(350); }
        }
        File.Copy(src, dst, true);
    }

    public static void LaunchInstalled()
    {
        Process.Start(new ProcessStartInfo { FileName = InstallExe, UseShellExecute = true });
    }
}
