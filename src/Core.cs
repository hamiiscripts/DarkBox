using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

// Business logic for DarkBox (no UI in this file).
// Fully standalone system: its own data folder, its own config, its
// own startup entry — no connection to the earlier Python build.
// Data lives at %APPDATA%\HamiiFolderLockerPro\config.json.
public class FolderEntry
{
    public string Id;
    public string Name;
    public string Path;
    public bool Locked;
    // v3.3 additions (all optional; empty/null = fall back to global)
    public Credential Pattern;   // this folder's own pattern
    public Credential Passcode;  // this folder's own passcode
    public string Color = "";    // tile color override ("" = theme)
    public string Note = "";     // owner's small label/note
    // v4.0 additions
    public string VaultName = "";  // random vault dir name ("" = legacy name-derived)
    public bool EncPending;        // locked, but file encryption still pending
    public bool Encrypted;         // files inside the vault are AES-encrypted
    // v4.2.0: per-folder lock mode — "just" (default) moves the folder to
    // the vault and hides it, no encryption, so locking is instant even
    // for huge folders on low-end PCs; "high" also AES-256 encrypts every
    // file (slow on big folders, strongest protection).
    public string LockMode = "just";

    public bool HasOwnCreds { get { return Pattern != null || Passcode != null; } }
}

public class Credential
{
    public string SaltB64;
    public string HashB64;
}

public class LockerConfig
{
    public Credential Pattern;
    public Credential Passcode;
    public int Iterations = 200000;
    public List<FolderEntry> Folders = new List<FolderEntry>();
    public bool Startup = true;
    public bool RelockOnStartup = true;
    public bool AutoLockOnClose = true;
    public string ThemeColor = ""; // "" = brand purple
    public string ThemeColor2 = ""; // gradient end; "" = auto shade
    public bool DarkUi = false;
    // v3.3 additions
    public Credential DecoyPattern;   // fake-mode pattern (opens a decoy panel)
    public Credential DecoyPasscode;  // fake-mode passcode
    public bool Sounds = true;
    public bool LockOnPcLock = true;
    public int AutoLockMinutes = 0;   // 0 = off; else lock open folders after N minutes
    public bool IntruderPhoto = true;
    // v4.0 additions
    public bool DefaultHighLock = false;   // v4.2.0: new folders use High End Lock by default (was: encrypt every folder)
    public bool ContextMenu = true;    // Explorer right-click "Lock with DarkBox"
    public bool LiteMode = false;      // lighter UI for weak PCs
    public bool LiteSet = false;       // user picked Lite on/off explicitly
    public string DekForPattern = "";      // vault key (DEK) wrapped by the pattern
    public string DekForPasscode = "";     // DEK wrapped by the passcode
    public string DekForRecoveryCode = ""; // DEK wrapped by the recovery code
    public string DekForRecoveryAnswer = ""; // DEK wrapped by the security answer
    public string RecoveryQuestion = "";
    public Credential RecoveryAnswer;      // hash of the normalized answer
    public Credential RecoveryCodeHash;    // hash of the normalized recovery code

    public bool HasDecoy { get { return DecoyPattern != null || DecoyPasscode != null; } }
    public bool HasRecovery { get { return RecoveryCodeHash != null || RecoveryAnswer != null; } }
}

public static class Core
{
    public static readonly string DataDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HamiiFolderLockerPro");
    public static readonly string ConfigPath = System.IO.Path.Combine(DataDir, "config.json");
    public static string ExePath = "";

    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "HamiiFolderLockerPro";
    private const string LegacyRunValueName = "HamiiFolderLocker";

    // ---------------- logging ----------------
    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(System.IO.Path.Combine(DataDir, "locker.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n");
        }
        catch { }
    }

    // ---------------- config ----------------
    public static LockerConfig Load()
    {
        var cfg = new LockerConfig();
        try
        {
            if (!File.Exists(ConfigPath)) return cfg;
            var root = MiniJson.Parse(File.ReadAllText(ConfigPath, Encoding.UTF8))
                as Dictionary<string, object>;
            if (root == null) return cfg;

            var creds = GetDict(root, "credentials");
            if (creds != null)
            {
                cfg.Pattern = ReadCred(GetDict(creds, "pattern"));
                cfg.Passcode = ReadCred(GetDict(creds, "passcode"));
                cfg.Iterations = GetInt(creds, "iterations", cfg.Iterations);
            }
            var folders = GetList(root, "folders");
            if (folders != null)
            {
                foreach (object o in folders)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    var e = new FolderEntry
                    {
                        Id = GetStr(d, "id"),
                        Name = GetStr(d, "name"),
                        Path = GetStr(d, "path"),
                        Locked = GetBool(d, "locked", false),
                        Pattern = ReadCred(GetDict(d, "pattern")),
                        Passcode = ReadCred(GetDict(d, "passcode")),
                        Color = GetStr(d, "color"),
                        Note = GetStr(d, "note"),
                        VaultName = GetStr(d, "vault"),
                        EncPending = GetBool(d, "enc_pending", false),
                        Encrypted = GetBool(d, "encrypted", false),
                        LockMode = GetStr(d, "lock_mode"),
                    };
                    // v4.2.0 migration: folders saved before lock modes get
                    // "high" only if they are (or were meant to be) encrypted.
                    if (e.LockMode != "high" && e.LockMode != "just")
                        e.LockMode = (e.Encrypted || e.EncPending) ? "high" : "just";
                    if (e.Id.Length > 0 && e.Path.Length > 0) cfg.Folders.Add(e);
                }
            }
            var settings = GetDict(root, "settings");
            cfg.Startup = GetBool(root, "startup", GetBool(settings, "startup", true));
            cfg.RelockOnStartup = GetBool(root, "relock_on_startup",
                GetBool(settings, "relock_on_startup", true));
            cfg.AutoLockOnClose = GetBool(root, "auto_lock_on_close",
                GetBool(settings, "auto_lock_on_close", true));
            cfg.ThemeColor = GetStr(root, "theme_color");
            cfg.ThemeColor2 = GetStr(root, "theme_color2");
            cfg.DarkUi = GetBool(root, "dark_ui", false);
            cfg.DecoyPattern = ReadCred(GetDict(root, "decoy_pattern"));
            cfg.DecoyPasscode = ReadCred(GetDict(root, "decoy_passcode"));
            cfg.Sounds = GetBool(root, "sounds", true);
            cfg.LockOnPcLock = GetBool(root, "lock_on_pc_lock", true);
            cfg.AutoLockMinutes = GetInt(root, "auto_lock_minutes", 0);
            cfg.IntruderPhoto = GetBool(root, "intruder_photo", true);
            cfg.DefaultHighLock = GetBool(root, "default_high_lock", false);
            cfg.ContextMenu = GetBool(root, "context_menu", true);
            cfg.LiteMode = GetBool(root, "lite_mode", false);
            cfg.LiteSet = GetBool(root, "lite_set", false);
            cfg.DekForPattern = GetStr(root, "dek_pattern");
            cfg.DekForPasscode = GetStr(root, "dek_passcode");
            cfg.DekForRecoveryCode = GetStr(root, "dek_recovery_code");
            cfg.DekForRecoveryAnswer = GetStr(root, "dek_recovery_answer");
            cfg.RecoveryQuestion = GetStr(root, "recovery_question");
            cfg.RecoveryAnswer = ReadCred(GetDict(root, "recovery_answer"));
            cfg.RecoveryCodeHash = ReadCred(GetDict(root, "recovery_code_hash"));
        }
        catch (Exception ex) { Log("Load failed: " + ex.Message); }
        return cfg;
    }

    public static void Save(LockerConfig cfg)
    {
        Directory.CreateDirectory(DataDir);
        var creds = new Dictionary<string, object>();
        creds["kdf"] = "pbkdf2-sha256";
        creds["iterations"] = (double)cfg.Iterations;
        creds["pattern"] = CredToDict(cfg.Pattern);
        creds["passcode"] = CredToDict(cfg.Passcode);
        var folders = new List<object>();
        foreach (var e in cfg.Folders)
        {
            var d = new Dictionary<string, object>();
            d["id"] = e.Id; d["name"] = e.Name; d["path"] = e.Path; d["locked"] = e.Locked;
            d["pattern"] = CredToDict(e.Pattern);
            d["passcode"] = CredToDict(e.Passcode);
            d["color"] = e.Color ?? "";
            d["note"] = e.Note ?? "";
            d["vault"] = e.VaultName ?? "";
            d["enc_pending"] = e.EncPending;
            d["encrypted"] = e.Encrypted;
            d["lock_mode"] = e.LockMode ?? "just";
            folders.Add(d);
        }
        var root = new Dictionary<string, object>();
        root["credentials"] = creds;
        root["folders"] = folders;
        root["startup"] = cfg.Startup;
        root["relock_on_startup"] = cfg.RelockOnStartup;
        root["auto_lock_on_close"] = cfg.AutoLockOnClose;
        root["theme_color"] = cfg.ThemeColor ?? "";
        root["theme_color2"] = cfg.ThemeColor2 ?? "";
        root["dark_ui"] = cfg.DarkUi;
        root["decoy_pattern"] = CredToDict(cfg.DecoyPattern);
        root["decoy_passcode"] = CredToDict(cfg.DecoyPasscode);
        root["sounds"] = cfg.Sounds;
        root["lock_on_pc_lock"] = cfg.LockOnPcLock;
        root["auto_lock_minutes"] = (double)cfg.AutoLockMinutes;
        root["intruder_photo"] = cfg.IntruderPhoto;
        root["default_high_lock"] = cfg.DefaultHighLock;
        root["context_menu"] = cfg.ContextMenu;
        root["lite_mode"] = cfg.LiteMode;
        root["lite_set"] = cfg.LiteSet;
        root["dek_pattern"] = cfg.DekForPattern ?? "";
        root["dek_passcode"] = cfg.DekForPasscode ?? "";
        root["dek_recovery_code"] = cfg.DekForRecoveryCode ?? "";
        root["dek_recovery_answer"] = cfg.DekForRecoveryAnswer ?? "";
        root["recovery_question"] = cfg.RecoveryQuestion ?? "";
        root["recovery_answer"] = CredToDict(cfg.RecoveryAnswer);
        root["recovery_code_hash"] = CredToDict(cfg.RecoveryCodeHash);
        string tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, MiniJson.Write(root), new UTF8Encoding(false));
        File.Copy(tmp, ConfigPath, true);
        File.Delete(tmp);
    }

    private static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
    {
        if (d == null) return null;
        object v;
        return d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
    }

    private static List<object> GetList(Dictionary<string, object> d, string key)
    {
        if (d == null) return null;
        object v;
        return d.TryGetValue(key, out v) ? v as List<object> : null;
    }

    private static string GetStr(Dictionary<string, object> d, string key)
    {
        if (d == null) return "";
        object v;
        return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : "";
    }

    private static bool GetBool(Dictionary<string, object> d, string key, bool fallback)
    {
        if (d == null) return fallback;
        object v;
        if (!d.TryGetValue(key, out v) || v == null) return fallback;
        if (v is bool) return (bool)v;
        bool b;
        return bool.TryParse(Convert.ToString(v), out b) ? b : fallback;
    }

    private static int GetInt(Dictionary<string, object> d, string key, int fallback)
    {
        if (d == null) return fallback;
        object v;
        if (!d.TryGetValue(key, out v) || v == null) return fallback;
        try { return Convert.ToInt32(v); } catch { return fallback; }
    }

    private static Credential ReadCred(Dictionary<string, object> d)
    {
        if (d == null) return null;
        string salt = GetStr(d, "salt");
        if (salt.Length == 0) salt = GetStr(d, "salt_b64");
        string hash = GetStr(d, "hash");
        if (hash.Length == 0) hash = GetStr(d, "hash_b64");
        if (salt.Length == 0 || hash.Length == 0) return null;
        return new Credential { SaltB64 = salt, HashB64 = hash };
    }

    private static object CredToDict(Credential c)
    {
        if (c == null) return null;
        var d = new Dictionary<string, object>();
        d["salt"] = c.SaltB64;
        d["hash"] = c.HashB64;
        return d;
    }

    // ---------------- credentials ----------------
    public static bool IsSetUp(LockerConfig cfg)
    {
        return cfg.Pattern != null || cfg.Passcode != null;
    }

    private static byte[] Pbkdf2(string secret, byte[] salt, int iterations)
    {
        using (var kdf = new Rfc2898DeriveBytes(secret, salt, iterations, HashAlgorithmName.SHA256))
            return kdf.GetBytes(32);
    }

    private static Credential MakeCred(string secret, int iterations)
    {
        byte[] salt = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
        return new Credential
        {
            SaltB64 = Convert.ToBase64String(salt),
            HashB64 = Convert.ToBase64String(Pbkdf2(secret, salt, iterations)),
        };
    }

    private static bool CheckCred(Credential cred, string secret, int iterations)
    {
        if (cred == null || string.IsNullOrEmpty(secret)) return false;
        try
        {
            byte[] salt = Convert.FromBase64String(cred.SaltB64);
            byte[] expected = Convert.FromBase64String(cred.HashB64);
            byte[] actual = Pbkdf2(secret, salt, iterations);
            return FixedEquals(expected, actual);
        }
        catch { return false; }
    }

    private static bool FixedEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    // Pattern is stored as dash-joined dot indices ("0-4-8").
    public static string PatternSecret(IEnumerable<int> dots)
    {
        return string.Join("-", dots);
    }

    public static void SetPattern(LockerConfig cfg, IEnumerable<int> dots)
    {
        cfg.Pattern = MakeCred(PatternSecret(dots), cfg.Iterations);
    }

    public static void SetPasscode(LockerConfig cfg, string passcode)
    {
        cfg.Passcode = MakeCred(passcode ?? "", cfg.Iterations);
    }

    public static bool VerifyPattern(LockerConfig cfg, IEnumerable<int> dots)
    {
        if (cfg.Pattern == null || dots == null) return false;
        return CheckCred(cfg.Pattern, PatternSecret(dots), cfg.Iterations);
    }

    public static bool VerifyPasscode(LockerConfig cfg, string passcode)
    {
        return CheckCred(cfg.Passcode, passcode ?? "", cfg.Iterations);
    }

    public static bool VerifyAny(LockerConfig cfg, IEnumerable<int> dots, string passcode)
    {
        if (dots != null && VerifyPattern(cfg, dots)) return true;
        if (!string.IsNullOrEmpty(passcode) && VerifyPasscode(cfg, passcode)) return true;
        return false;
    }

    // ---- per-folder credentials (v3.3) ----
    public static void SetFolderPattern(LockerConfig cfg, FolderEntry e, IEnumerable<int> dots)
    {
        e.Pattern = MakeCred(PatternSecret(dots), cfg.Iterations);
    }

    public static void SetFolderPasscode(LockerConfig cfg, FolderEntry e, string passcode)
    {
        e.Passcode = MakeCred(passcode ?? "", cfg.Iterations);
    }

    public static bool VerifyFolderPattern(LockerConfig cfg, FolderEntry e, IEnumerable<int> dots)
    {
        if (e == null || e.Pattern == null || dots == null) return false;
        return CheckCred(e.Pattern, PatternSecret(dots), cfg.Iterations);
    }

    public static bool VerifyFolderPasscode(LockerConfig cfg, FolderEntry e, string passcode)
    {
        if (e == null || e.Passcode == null) return false;
        return CheckCred(e.Passcode, passcode ?? "", cfg.Iterations);
    }

    // A folder opens with its own credential when it has one; the panel's
    // global credential always works too (the owner's master key).
    public static bool VerifyForFolder(LockerConfig cfg, FolderEntry e, IEnumerable<int> dots, string passcode)
    {
        if (dots != null && VerifyFolderPattern(cfg, e, dots)) return true;
        if (!string.IsNullOrEmpty(passcode) && VerifyFolderPasscode(cfg, e, passcode)) return true;
        return VerifyAny(cfg, dots, passcode);
    }

    // ---- decoy (fake mode) credentials (v3.3) ----
    public static void SetDecoyPattern(LockerConfig cfg, IEnumerable<int> dots)
    {
        cfg.DecoyPattern = MakeCred(PatternSecret(dots), cfg.Iterations);
    }

    public static void SetDecoyPasscode(LockerConfig cfg, string passcode)
    {
        cfg.DecoyPasscode = MakeCred(passcode ?? "", cfg.Iterations);
    }

    public static bool VerifyDecoy(LockerConfig cfg, IEnumerable<int> dots, string passcode)
    {
        if (dots != null && cfg.DecoyPattern != null
            && CheckCred(cfg.DecoyPattern, PatternSecret(dots), cfg.Iterations)) return true;
        if (!string.IsNullOrEmpty(passcode) && cfg.DecoyPasscode != null
            && CheckCred(cfg.DecoyPasscode, passcode, cfg.Iterations)) return true;
        return false;
    }

    // ---------------- v4.0: vault key (DEK), file encryption, recovery, backup ----------------
    // One random 32-byte DEK protects every folder. The DEK itself is never
    // stored in plain form: it is AES-wrapped by keys derived from the
    // pattern, the passcode and the recovery secrets, so any one of them
    // can unwrap it after a successful sign-in.
    public static byte[] CreateDek()
    {
        byte[] dek = new byte[32];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(dek);
        return dek;
    }

    public static bool HasAnyDekWrap(LockerConfig cfg)
    {
        return !string.IsNullOrEmpty(cfg.DekForPattern) || !string.IsNullOrEmpty(cfg.DekForPasscode)
            || !string.IsNullOrEmpty(cfg.DekForRecoveryCode) || !string.IsNullOrEmpty(cfg.DekForRecoveryAnswer);
    }

    public static string WrapDek(byte[] dek, string secret, int iterations)
    {
        byte[] salt = new byte[16], iv = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(salt); rng.GetBytes(iv); }
        byte[] key = Pbkdf2(secret ?? "", salt, iterations);
        byte[] ct = AesCrypt(dek, key, iv, true);
        byte[] all = new byte[32 + ct.Length];
        Buffer.BlockCopy(salt, 0, all, 0, 16);
        Buffer.BlockCopy(iv, 0, all, 16, 16);
        Buffer.BlockCopy(ct, 0, all, 32, ct.Length);
        return Convert.ToBase64String(all);
    }

    public static byte[] UnwrapDek(string wrapped, string secret, int iterations)
    {
        if (string.IsNullOrEmpty(wrapped) || string.IsNullOrEmpty(secret)) return null;
        try
        {
            byte[] all = Convert.FromBase64String(wrapped);
            if (all.Length < 48) return null;
            byte[] salt = new byte[16], iv = new byte[16], ct = new byte[all.Length - 32];
            Buffer.BlockCopy(all, 0, salt, 0, 16);
            Buffer.BlockCopy(all, 16, iv, 0, 16);
            Buffer.BlockCopy(all, 32, ct, 0, ct.Length);
            byte[] key = Pbkdf2(secret, salt, iterations);
            byte[] dek = AesCrypt(ct, key, iv, false);
            return dek != null && dek.Length == 32 ? dek : null;
        }
        catch { return null; }
    }

    private static byte[] AesCrypt(byte[] data, byte[] key, byte[] iv, bool encrypt)
    {
        using (var aes = Aes.Create())
        {
            aes.Key = key; aes.IV = iv;
            aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using (var t = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor())
                return t.TransformFinalBlock(data, 0, data.Length);
        }
    }

    public static byte[] FolderKey(byte[] dek, string folderId) // public for the DarkBox Rescue tool
    {
        using (var h = new HMACSHA256(dek))
            return h.ComputeHash(Encoding.UTF8.GetBytes("darkbox-enc:" + folderId));
    }

    private static readonly byte[] EncMagic = { (byte)'D', (byte)'B', (byte)'E', (byte)'1' };

    // Encrypts every plain file inside the (already moved) vault, in place.
    // Idempotent and crash-safe: each file is written to a temp name first,
    // and only then replaces the original. Returns false if any file failed.
    public static bool EncryptTree(FolderEntry e, byte[] dek)
    {
        string vault = VaultPath(e);
        if (!Directory.Exists(vault) || dek == null) return false;
        CurrentProgressFolder = e.Id;
        try
        {
        RecoverTempFiles(vault, true);
        byte[] key = FolderKey(dek, e.Id);
        bool allOk = true;
        var files = new List<string>();
        foreach (string f in EnumerateFilesSafe(vault))
        {
            if (f.EndsWith(".dbe", StringComparison.OrdinalIgnoreCase)) continue;
            if (f.EndsWith(".dbtmp", StringComparison.OrdinalIgnoreCase)) continue;
            files.Add(f);
        }
        int done = 0;
        foreach (string file in files)
        {
            try { EncryptFile(file, key); }
            catch (Exception ex) { Log("Encrypt failed " + file + ": " + ex.Message); allOk = false; }
            done++;
            ReportProgress("Encrypting files", done, files.Count);
        }
        return allOk;
        }
        finally { CurrentProgressFolder = ""; }
    }

    // Strict decrypt of every .dbe under root: throws on the first failure,
    // so callers can abort before changing any folder state.
    public static void DecryptTreeAt(string root, byte[] dek, string folderId)
    {
        if (!Directory.Exists(root) || dek == null)
            throw new InvalidOperationException("Vault key missing.");
        CurrentProgressFolder = folderId;
        try
        {
        RecoverTempFiles(root, false);
        byte[] key = FolderKey(dek, folderId);
        var files = new List<string>();
        foreach (string f in EnumerateFilesSafe(root))
            if (f.EndsWith(".dbe", StringComparison.OrdinalIgnoreCase)) files.Add(f);
        int done = 0;
        foreach (string file in files)
        {
            DecryptFile(file, key);
            done++;
            ReportProgress("Decrypting files", done, files.Count);
        }
        }
        finally { CurrentProgressFolder = ""; }
    }

    // Lenient decrypt used by the self-heal pass: bad files are logged and
    // skipped, good files are restored. Returns how many files were fixed.
    public static int DecryptTreeLenient(string root, byte[] dek, string folderId)
    {
        if (!Directory.Exists(root) || dek == null) return 0;
        RecoverTempFiles(root, false);
        byte[] key = FolderKey(dek, folderId);
        int n = 0;
        var files = new List<string>();
        foreach (string f in EnumerateFilesSafe(root))
            if (f.EndsWith(".dbe", StringComparison.OrdinalIgnoreCase)) files.Add(f);
        foreach (string file in files)
        {
            try { DecryptFile(file, key); n++; }
            catch (Exception ex) { Log("Heal decrypt failed " + file + ": " + ex.Message); }
            ReportProgress("Restoring files", n, files.Count);
        }
        return n;
    }

    // A crash mid-file leaves a ".dbtmp" behind. The temp file is always a
    // COMPLETE copy (the stream is closed before the original is touched),
    // so it can safely take the final file's place.
    public static void RecoverTempFiles(string root, bool encrypting)
    {
        foreach (string tmp in EnumerateFilesSafe(root))
        {
            if (!tmp.EndsWith(".dbtmp", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                string basePath = tmp.Substring(0, tmp.Length - 6); // strip ".dbtmp"
                if (encrypting)
                {
                    string finalPath = basePath + ".dbe";
                    if (File.Exists(basePath)) File.Delete(tmp);          // original intact: redo later
                    else if (!File.Exists(finalPath)) File.Move(tmp, finalPath); // temp was finished
                    else File.Delete(tmp);
                }
                else
                {
                    string encPath = basePath + ".dbe";
                    if (!File.Exists(encPath)) File.Delete(tmp);          // already fully decrypted
                    else if (File.Exists(basePath)) File.Delete(tmp);     // original .dbe still there: redo
                    else { File.Move(tmp, basePath); File.Delete(encPath); } // temp was finished
                }
            }
            catch (Exception ex) { Log("Temp recovery failed " + tmp + ": " + ex.Message); }
        }
    }

    // Repairs states an interrupted v4.0.0 unlock could leave behind: the
    // folder was already moved back to its place, but the config still says
    // "locked" (so every unlock then failed with "vault not found").
    // v4.0.5: also the reverse — a lock that died mid-way (entry saved as
    // unlocked, folder already sitting in its vault). Mark it locked and
    // pending so the panel heal finishes the job instead of losing it.
    public static bool FixInconsistentStates(LockerConfig cfg)
    {
        bool changed = false;
        foreach (var e in cfg.Folders)
        {
            if (e.Locked && !Directory.Exists(VaultPath(e)) && Directory.Exists(e.Path))
            {
                e.Locked = false;
                e.Encrypted = false;
                e.EncPending = false;
                changed = true;
                LogActivity("unlock", "Repaired folder state: \"" + e.Name + "\"");
            }
            else if (!e.Locked && !Directory.Exists(e.Path) && Directory.Exists(VaultPath(e)))
            {
                e.Locked = true;
                e.EncPending = e.LockMode == "high";
                changed = true;
                LogActivity("lock", "Repaired folder state: \"" + e.Name + "\" (lock was interrupted)");
            }
        }
        if (changed) Save(cfg);
        return changed;
    }

    // v4.0.5: long file work (encrypt/decrypt) reports progress so the UI
    // can show it instead of looking frozen. Set by the UI layer.
    // v4.1.0: CurrentProgressFolder tells the UI WHICH folder the numbers
    // belong to, so the row under that folder can show its own progress bar
    // (not just the full-screen preloader).
    public static Action<string, int, int> Progress;
    public static string CurrentProgressFolder = "";
    private static void ReportProgress(string op, int done, int total)
    {
        try { if (Progress != null) Progress(op, done, total); } catch { }
    }

    // v4.1.0: how many files a folder actually holds (for the "Locked ✓"
    // success popup). Counts the vault when locked, the live folder when open.
    public static int CountVaultFiles(FolderEntry e)
    {
        try
        {
            string dir = e.Locked ? VaultPath(e) : e.Path;
            int n = 0;
            foreach (string f in EnumerateFilesSafe(dir)) n++;
            return n;
        }
        catch { return 0; }
    }

    public static bool HasEncryptedFiles(string dir)
    {
        foreach (string f in EnumerateFilesSafe(dir))
            if (f.EndsWith(".dbe", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var list = new List<string>();
        var stack = new Stack<string>();
        if (Directory.Exists(root)) stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            try { foreach (string f in Directory.GetFiles(dir)) list.Add(f); } catch { }
            try
            {
                foreach (string d in Directory.GetDirectories(dir))
                {
                    try { if ((new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) != 0) continue; }
                    catch { continue; }
                    stack.Push(d);
                }
            }
            catch { }
        }
        return list;
    }

    private static void EncryptFile(string path, byte[] key)
    {
        string tmp = path + ".dbtmp";
        byte[] iv = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(iv);
        using (var aes = Aes.Create())
        {
            aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using (var inp = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var outp = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                outp.Write(EncMagic, 0, 4);
                outp.Write(iv, 0, 16);
                using (var cs = new CryptoStream(outp, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    inp.CopyTo(cs, 262144);
            }
        }
        File.Delete(path);
        File.Move(tmp, path + ".dbe");
    }

    private static void DecryptFile(string path, byte[] key)
    {
        string dest = path.Substring(0, path.Length - 4);
        string tmp = dest + ".dbtmp";
        using (var inp = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            byte[] magic = new byte[4]; byte[] iv = new byte[16];
            if (inp.Read(magic, 0, 4) != 4 || magic[0] != (byte)'D' || magic[3] != (byte)'1')
                throw new InvalidDataException("Not a DarkBox encrypted file");
            if (inp.Read(iv, 0, 16) != 16) throw new InvalidDataException("Truncated file");
            using (var aes = Aes.Create())
            {
                aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using (var cs = new CryptoStream(inp, aes.CreateDecryptor(), CryptoStreamMode.Read))
                using (var outp = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                    cs.CopyTo(outp, 262144);
            }
        }
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(tmp, dest);
        File.Delete(path);
    }

    // ---- recovery ----
    public static string NormalizeCode(string code)
    {
        return (code ?? "").Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant();
    }

    public static string NormalizeAnswer(string answer)
    {
        return (answer ?? "").Trim().ToLowerInvariant();
    }

    public static string GenerateRecoveryCode()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        byte[] rb = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(rb);
        var sb = new StringBuilder();
        for (int i = 0; i < 16; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append('-');
            sb.Append(alphabet[rb[i] % alphabet.Length]);
        }
        return sb.ToString();
    }

    public static void SetRecoveryCode(LockerConfig cfg, string code, byte[] dek)
    {
        string norm = NormalizeCode(code);
        cfg.RecoveryCodeHash = MakeCred(norm, cfg.Iterations);
        if (dek != null) cfg.DekForRecoveryCode = WrapDek(dek, norm, cfg.Iterations);
    }

    public static bool VerifyRecoveryCode(LockerConfig cfg, string code)
    {
        return CheckCred(cfg.RecoveryCodeHash, NormalizeCode(code), cfg.Iterations);
    }

    public static void SetRecoveryAnswer(LockerConfig cfg, string question, string answer, byte[] dek)
    {
        string norm = NormalizeAnswer(answer);
        cfg.RecoveryQuestion = question ?? "";
        cfg.RecoveryAnswer = MakeCred(norm, cfg.Iterations);
        if (dek != null) cfg.DekForRecoveryAnswer = WrapDek(dek, norm, cfg.Iterations);
    }

    public static bool VerifyRecoveryAnswer(LockerConfig cfg, string answer)
    {
        return CheckCred(cfg.RecoveryAnswer, NormalizeAnswer(answer), cfg.Iterations);
    }

    // ---- encrypted config backup ----
    public static void ExportBackup(string outPath, string password)
    {
        var payload = new Dictionary<string, object>();
        payload["config"] = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath, Encoding.UTF8) : "";
        payload["activity"] = File.Exists(ActivityPath) ? File.ReadAllText(ActivityPath, Encoding.UTF8) : "";
        byte[] plain = Encoding.UTF8.GetBytes(MiniJson.Write(payload));
        byte[] salt = new byte[16], iv = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(salt); rng.GetBytes(iv); }
        byte[] key = Pbkdf2(password ?? "", salt, 200000);
        byte[] ct = AesCrypt(plain, key, iv, true);
        using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
        {
            byte[] magic = Encoding.ASCII.GetBytes("DBBK1");
            fs.Write(magic, 0, 5);
            fs.Write(salt, 0, 16);
            fs.Write(iv, 0, 16);
            fs.Write(ct, 0, ct.Length);
        }
    }

    public static LockerConfig ImportBackup(string inPath, string password)
    {
        byte[] all = File.ReadAllBytes(inPath);
        if (all.Length < 37 || Encoding.ASCII.GetString(all, 0, 5) != "DBBK1")
            throw new InvalidDataException("This is not a DarkBox backup file.");
        byte[] salt = new byte[16], iv = new byte[16], ct = new byte[all.Length - 37];
        Buffer.BlockCopy(all, 5, salt, 0, 16);
        Buffer.BlockCopy(all, 21, iv, 0, 16);
        Buffer.BlockCopy(all, 37, ct, 0, ct.Length);
        byte[] plain;
        try { plain = AesCrypt(ct, Pbkdf2(password ?? "", salt, 200000), iv, false); }
        catch { throw new InvalidDataException("Wrong backup password (or the file is damaged)."); }
        var payload = MiniJson.Parse(Encoding.UTF8.GetString(plain)) as Dictionary<string, object>;
        if (payload == null) throw new InvalidDataException("Backup file is damaged.");
        string configText = GetStr(payload, "config");
        if (configText.Length == 0) throw new InvalidDataException("Backup has no settings in it.");
        MiniJson.Parse(configText); // validate before overwriting anything
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(ConfigPath, configText, new UTF8Encoding(false));
        string act = GetStr(payload, "activity");
        if (act.Length > 0) { try { File.WriteAllText(ActivityPath, act, new UTF8Encoding(false)); } catch { } }
        return Load();
    }

    // ---------------- activity log (v3.3) ----------------
    public static readonly string ActivityPath = System.IO.Path.Combine(DataDir, "activity.json");
    public static readonly string IntruderDir = System.IO.Path.Combine(DataDir, "Intruders");
    private static readonly object ActivityLock = new object();

    public static void LogActivity(string type, string text)
    {
        LogActivity(type, text, "");
    }

    public static void LogActivity(string type, string text, string photo)
    {
        try
        {
            lock (ActivityLock)
            {
                Directory.CreateDirectory(DataDir);
                var list = ReadActivityRaw();
                list.Add(new Dictionary<string, object>
                {
                    { "time", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") },
                    { "type", type ?? "" },
                    { "text", text ?? "" },
                    { "photo", photo ?? "" },
                });
                while (list.Count > 200) list.RemoveAt(0);
                string tmp = ActivityPath + ".tmp";
                File.WriteAllText(tmp, MiniJson.Write(list), new UTF8Encoding(false));
                File.Copy(tmp, ActivityPath, true);
                File.Delete(tmp);
            }
        }
        catch (Exception ex) { Log("Activity log failed: " + ex.Message); }
    }

    private static List<object> ReadActivityRaw()
    {
        var list = new List<object>();
        try
        {
            if (!File.Exists(ActivityPath)) return list;
            var parsed = MiniJson.Parse(File.ReadAllText(ActivityPath, Encoding.UTF8)) as List<object>;
            if (parsed != null) list = parsed;
        }
        catch { }
        return list;
    }

    // Newest first.
    public static List<Dictionary<string, object>> ReadActivity(int max)
    {
        var raw = ReadActivityRaw();
        var outp = new List<Dictionary<string, object>>();
        for (int i = raw.Count - 1; i >= 0 && outp.Count < max; i--)
        {
            var d = raw[i] as Dictionary<string, object>;
            if (d != null) outp.Add(d);
        }
        return outp;
    }

    public static void ClearActivity()
    {
        try { lock (ActivityLock) { if (File.Exists(ActivityPath)) File.Delete(ActivityPath); } }
        catch { }
    }

    // ---------------- folder size (v3.3) ----------------
    public static long FolderSize(string path)
    {
        long total = 0;
        try
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return 0;
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                try
                {
                    foreach (string f in Directory.GetFiles(dir))
                    {
                        try { total += new FileInfo(f).Length; } catch { }
                    }
                    foreach (string sub in Directory.GetDirectories(dir)) stack.Push(sub);
                }
                catch { }
            }
        }
        catch { }
        return total;
    }

    // ---------------- folders ----------------
    public static string NewId()
    {
        return Guid.NewGuid().ToString("N");
    }

    public static FolderEntry Find(LockerConfig cfg, string id)
    {
        foreach (var e in cfg.Folders) if (e.Id == id) return e;
        return null;
    }

    public static string VaultPath(FolderEntry e)
    {
        string parent = Directory.GetParent(e.Path).FullName;
        // v4.0: new vaults get a random name that reveals nothing about the
        // folder (the old name-derived vault could be renamed back by hand).
        if (!string.IsNullOrEmpty(e.VaultName))
            return System.IO.Path.Combine(parent, e.VaultName);
        string id8 = e.Id.Length >= 8 ? e.Id.Substring(0, 8) : e.Id;
        return System.IO.Path.Combine(parent, "." + e.Name + ".hvault-" + id8);
    }

    public static string ShortcutPath(FolderEntry e)
    {
        string parent = Directory.GetParent(e.Path).FullName;
        return System.IO.Path.Combine(parent, e.Name + ".lnk");
    }

    public static FolderEntry AddFolder(LockerConfig cfg, string path)
    {
        return AddFolder(cfg, path, null);
    }

    public static string NewVaultName()
    {
        byte[] rb = new byte[8];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(rb);
        return ".dbx-" + BitConverter.ToString(rb).Replace("-", "").ToLowerInvariant();
    }

    public static FolderEntry AddFolder(LockerConfig cfg, string path, byte[] dek)
    {
        string full = System.IO.Path.GetFullPath(path.TrimEnd('\\'));
        var dir = new DirectoryInfo(full);
        var e = new FolderEntry
        {
            Id = NewId(),
            Name = dir.Name,
            Path = full,
            Locked = false,
            // v4.0.5: the vault name is fixed up-front, and the entry is
            // SAVED BEFORE anything moves. LockFolder can take minutes on a
            // big folder (encryption) — if the app is killed mid-way, the
            // entry (ID + vault name) must already be on disk, or the folder
            // is lost forever. That is exactly what happened to the user's
            // DRIVE AND MY PERSONAL folder.
            VaultName = NewVaultName(),
            // v4.2.0: new folders default to Just Lock (fast, no encryption).
            LockMode = cfg.DefaultHighLock ? "high" : "just",
            EncPending = false,
        };
        cfg.Folders.Add(e);
        Save(cfg);
        // v4.1.0: the preloader must show this folder's real progress from
        // the first second — before this, the UI only showed a toast and
        // the folder silently appeared as "ENCRYPTION PENDING" afterwards.
        ReportProgress("Preparing folder", 0, 1);
        LockFolder(cfg, e, dek);
        ReportProgress("Locking folder", 1, 1);
        Save(cfg);
        LogActivity("add", "Folder added: \"" + e.Name + "\"");
        return e;
    }

    public static void LockFolder(LockerConfig cfg, FolderEntry e)
    {
        LockFolder(cfg, e, null);
    }

    public static void LockFolder(LockerConfig cfg, FolderEntry e, byte[] dek)
    {
        if (e.Locked) return;
        if (string.IsNullOrEmpty(e.VaultName)) e.VaultName = NewVaultName();
        string vault = VaultPath(e);
        if (Directory.Exists(vault))
            throw new IOException("Vault pehle se maujood hai: " + vault);
        if (!Directory.Exists(e.Path))
            throw new DirectoryNotFoundException("Folder nahi mila: " + e.Path);
        ClearHiddenSystem(e.Path);
        Directory.Move(e.Path, vault);
        SetHiddenSystem(vault);
        CreateShortcut(e);
        e.Locked = true;
        // v4.0: encrypt the contents too, but only for High End Lock.
        // Just Lock skips encryption entirely — that is the whole point:
        // locking a huge folder stays instant on a low-end PC. Without the
        // vault key in memory (tray / hotkey locks) encryption is deferred
        // — the panel finishes it on the next sign-in (EncPending). If even
        // one file could not be encrypted, the folder stays "pending"
        // (never falsely marked encrypted) and the rest is retried on the
        // next sign-in.
        if (e.LockMode == "high")
        {
            if (dek != null)
            {
                bool okAll = EncryptTree(e, dek);
                e.Encrypted = okAll;
                e.EncPending = !okAll;
            }
            else e.EncPending = true;
        }
        Save(cfg);
        LogActivity("lock", "Locked: \"" + e.Name + "\"" + (e.Encrypted ? " (encrypted)" : ""));
    }

    // v4.2.0: per-folder lock mode. Applies on the next lock. Switching to
    // Just cancels any pending encryption (files already encrypted stay
    // encrypted until the next unlock, which decrypts them as usual).
    public static void SetLockMode(LockerConfig cfg, FolderEntry e, string mode)
    {
        string m = mode == "high" ? "high" : "just";
        if ((e.LockMode ?? "just") == m) return;
        e.LockMode = m;
        if (m == "just") e.EncPending = false;
        Save(cfg);
        LogActivity("lockmode", "Lock mode for \"" + e.Name + "\": " + (m == "high" ? "High End Lock" : "Just Lock"));
    }

    public static void UnlockFolder(LockerConfig cfg, FolderEntry e, bool openAfter)
    {
        UnlockFolder(cfg, e, openAfter, null);
    }

    public static void UnlockFolder(LockerConfig cfg, FolderEntry e, bool openAfter, byte[] dek)
    {
        if (!e.Locked) { if (openAfter) OpenInExplorer(e.Path); return; }
        string vault = VaultPath(e);
        if (!Directory.Exists(vault))
            throw new DirectoryNotFoundException("Vault nahi mila: " + vault);
        if (Directory.Exists(e.Path))
            throw new IOException("Is naam ka folder pehle se maujood hai: " + e.Path);
        bool needsDecrypt = e.Encrypted || HasEncryptedFiles(vault);
        if (needsDecrypt && dek == null)
            throw new InvalidOperationException("This folder is encrypted. Open the Admin Panel with your pattern or passcode first, then unlock it.");
        // v4.0.2: decrypt INSIDE the vault, before anything moves. If any
        // file fails, the unlock aborts with the folder still safely locked
        // and untouched — the old order (move first, decrypt after) could
        // strand a folder half-moved when one file failed.
        if (needsDecrypt) DecryptTreeAt(vault, dek, e.Id);
        Directory.Move(vault, e.Path);
        ClearHiddenSystem(e.Path);
        DeleteShortcut(e);
        e.Locked = false;
        e.Encrypted = false;
        e.EncPending = false;
        Save(cfg);
        LogActivity("unlock", "Unlocked: \"" + e.Name + "\"");
        if (openAfter) OpenInExplorer(e.Path);
    }

    public static void RemoveFolder(LockerConfig cfg, FolderEntry e)
    {
        RemoveFolder(cfg, e, null);
    }

    public static void RemoveFolder(LockerConfig cfg, FolderEntry e, byte[] dek)
    {
        string name = e.Name;
        if (e.Locked) UnlockFolder(cfg, e, false, dek);
        cfg.Folders.Remove(e);
        Save(cfg);
        LogActivity("remove", "Removed from the list: \"" + name + "\"");
    }

    public static void RelockUnlocked(LockerConfig cfg)
    {
        int n = 0;
        foreach (var e in cfg.Folders)
        {
            if (!e.Locked)
            {
                try { LockFolder(cfg, e); n++; }
                catch (Exception ex) { Log("Relock failed for " + e.Path + ": " + ex.Message); }
            }
        }
        if (n > 0) LogActivity("lockall", "Lock All: " + n + (n == 1 ? " folder" : " folders") + " locked");
    }

    // ---------------- attributes ----------------
    public static void SetHiddenSystem(string path)
    {
        try
        {
            var di = new DirectoryInfo(path);
            di.Attributes |= FileAttributes.Hidden | FileAttributes.System;
        }
        catch { }
    }

    public static void ClearHiddenSystem(string path)
    {
        try
        {
            var di = new DirectoryInfo(path);
            FileAttributes a = di.Attributes;
            a &= ~(FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly);
            if ((a & (FileAttributes.Normal | FileAttributes.Directory)) == 0)
                a = FileAttributes.Normal;
            di.Attributes = a;
        }
        catch { }
    }

    // ---------------- shortcut ----------------
    private static bool IsWindows
    {
        get { return Environment.OSVersion.Platform == PlatformID.Win32NT; }
    }

    public static void CreateShortcut(FolderEntry e)
    {
        if (!IsWindows) return;
        try
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic link = shell.CreateShortcut(ShortcutPath(e));
            link.TargetPath = ExePath;
            link.Arguments = "--unlock " + e.Id;
            link.WorkingDirectory = System.IO.Path.GetDirectoryName(ExePath);
            link.IconLocation = "shell32.dll,3";
            link.Description = "DarkBox - " + e.Name;
            link.Save();
        }
        catch (Exception ex) { Log("Shortcut failed: " + ex.Message); }
    }

    public static void DeleteShortcut(FolderEntry e)
    {
        try
        {
            string p = ShortcutPath(e);
            if (File.Exists(p)) File.Delete(p);
        }
        catch { }
    }

    public static void OpenInExplorer(string path)
    {
        if (!IsWindows) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + path + "\"",
                UseShellExecute = false,
            });
        }
        catch (Exception ex) { Log("Explorer open failed: " + ex.Message); }
    }

    // ---------------- startup ----------------
    public static void SetStartup(bool enabled)
    {
        if (!IsWindows) return;
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key == null) return;
                if (enabled) key.SetValue(RunValueName, "\"" + ExePath + "\" --background");
                else key.DeleteValue(RunValueName, false);
            }
        }
        catch (Exception ex) { Log("Startup set failed: " + ex.Message); }
    }

    // Purani Python build ka startup entry hata do taake do agents
    // aik sath na chalein — ye system ab bilkul apna hai.
    public static void RemoveLegacyStartup()
    {
        if (!IsWindows) return;
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key != null) key.DeleteValue(LegacyRunValueName, false);
            }
        }
        catch (Exception ex) { Log("Legacy startup cleanup failed: " + ex.Message); }
    }

    public static bool IsStartupEnabled()
    {
        if (!IsWindows) return false;
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                if (key == null) return false;
                return key.GetValue(RunValueName) != null;
            }
        }
        catch { return false; }
    }

    // ---------------- explorer watch ----------------
    public static string NormalizePath(string p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        try { return System.IO.Path.GetFullPath(p).TrimEnd('\\').ToLowerInvariant(); }
        catch { return p.TrimEnd('\\').ToLowerInvariant(); }
    }

    // Returns normalized paths of folders currently open in Explorer
    // windows, or null when the state cannot be determined.
    public static HashSet<string> GetOpenExplorerPaths()
    {
        if (!IsWindows) return new HashSet<string>();
        try
        {
            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            dynamic shell = Activator.CreateInstance(shellType);
            var set = new HashSet<string>();
            foreach (dynamic w in shell.Windows())
            {
                string url = null;
                try { url = w.LocationURL; } catch { }
                if (!string.IsNullOrEmpty(url) && url.StartsWith("file:///"))
                {
                    string p = Uri.UnescapeDataString(url.Substring(8)).Replace('/', '\\');
                    set.Add(NormalizePath(p));
                }
            }
            return set;
        }
        catch (Exception ex)
        {
            Log("Explorer watch failed: " + ex.Message);
            return null;
        }
    }
}
