using System.Diagnostics;
using System.IO.Compression;

namespace SwyfInstaller;

public static class Ops
{
    private static readonly string[] PackageCmds =
    {
        "Install Custom AI.cmd",
        "Play with Custom AI.cmd",
        "Uninstall Custom AI.cmd",
    };

    private static readonly string[] PackagePaths =
    {
        "Install Custom AI.cmd",
        "Play with Custom AI.cmd",
        "Uninstall Custom AI.cmd",
        "CustomAI/README.md",
        "CustomAI/package",
    };

    private static void Say(IProgress<string> log, string msg)
    {
        Console.WriteLine(msg);
        try { log?.Report(msg); } catch { }
    }

    public static void WarnIfGameRunning(IProgress<string> log = null)
    {
        if (GameDir.IsGameRunning())
            Say(log, "WARNING: the game appears to be running. Close it (and the F8 panel) before continuing.");
    }

    /// <summary>Update source: branch mode when cfg.Branch is set (raw ZIP + committed .sha256),
    /// otherwise GitHub Releases of cfg.Repo.</summary>
    public static async Task<ReleaseInfo> FetchSourceAsync(Store.AppConfig cfg, CancellationToken ct)
    {
        using var http = Github.CreateClient(TimeSpan.FromSeconds(30));
        return string.IsNullOrWhiteSpace(cfg.Branch)
            ? await Github.GetLatestAsync(http, cfg.Repo, ct)
            : await Github.GetBranchAsync(http, cfg.Repo, cfg.Branch, cfg.BranchZipPath, ct);
    }

    public static string SourceLabel(Store.AppConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.Branch) ? cfg.Repo + " (Releases)" : cfg.Repo + " (branch " + cfg.Branch.Trim() + ")";

    /// <summary>Finds a package ZIP shipped with the app or the cloned repo: the explicit
    /// cfg.BundleZipPath, then SWYF-Custom-AI-win-x64.zip in the app folder, in package\ next to
    /// it, or in any of the 6 parent folders (covers bin/Debug output and the repo root).</summary>
    public static string FindBundledZip(Store.AppConfig cfg, string startDir = null)
    {
        if (!string.IsNullOrWhiteSpace(cfg.BundleZipPath))
        {
            if (File.Exists(cfg.BundleZipPath)) return Path.GetFullPath(cfg.BundleZipPath);
            throw new InvalidDataException("BundleZipPath is set but the file does not exist: " + cfg.BundleZipPath);
        }
        const string name = "SWYF-Custom-AI-win-x64.zip";
        var dir = new DirectoryInfo(startDir ?? AppContext.BaseDirectory);
        for (int i = 0; i <= 6 && dir != null; i++, dir = dir.Parent)
        {
            string direct = Path.Combine(dir.FullName, name);
            if (File.Exists(direct)) return Path.GetFullPath(direct);
            string packaged = Path.Combine(dir.FullName, "package", name);
            if (File.Exists(packaged)) return Path.GetFullPath(packaged);
        }
        return "";
    }

    /// <summary>Release description for a local package ZIP; verified against its .sha256 sibling
    /// when one exists. The tag carries the digest so unchanged packages report "up to date".</summary>
    public static ReleaseInfo BundledRelease(string zipPath)
    {
        string sha = Store.Sha256File(zipPath);
        string shaPath = zipPath + ".sha256";
        if (File.Exists(shaPath))
        {
            string want = Store.ParseChecksumFile(File.ReadAllText(shaPath));
            if (want != sha)
                throw new InvalidDataException("Bundled package does not match its .sha256 (" + zipPath + "). " +
                    "The checkout is inconsistent - run \"git pull\" (or re-download) and retry.");
        }
        return new ReleaseInfo { Tag = "bundle@" + sha.Substring(0, Math.Min(12, sha.Length)), ZipName = Path.GetFileName(zipPath), ZipUrl = "" };
    }

    /// <summary>Source resolution order: explicit "Branch" (raw files on GitHub), then a bundled
    /// package ZIP (offline: next to the app, in package\, or in the cloned repo), then Releases.</summary>
    public static async Task<(ReleaseInfo Rel, string LocalZip)> ResolveSourceAsync(Store.AppConfig cfg, IProgress<string> log, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(cfg.Branch))
        {
            Say(log, "Checking latest package from " + SourceLabel(cfg) + " ...");
            return (await FetchSourceAsync(cfg, ct), "");
        }
        string bundle = FindBundledZip(cfg);
        if (bundle != "")
        {
            Say(log, "Using bundled package (offline): " + bundle);
            return (BundledRelease(bundle), bundle);
        }
        Say(log, "Checking latest release of " + SourceLabel(cfg) + " ...");
        return (await FetchSourceAsync(cfg, ct), "");
    }

    public static async Task<string> DownloadAndVerifyAsync(ReleaseInfo rel, string workDir, IProgress<string> log, IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        string zipPath = Path.Combine(workDir, rel.ZipName);
        string shaPath = Path.Combine(workDir, rel.ChecksumName);

        using var http = Github.CreateClient(TimeSpan.FromMinutes(10));
        Say(log, "Downloading " + rel.ZipName + " ...");
        await Github.DownloadAsync(http, rel.ZipUrl, zipPath, rel.ZipSize, progress, ct);
        Say(log, "Downloading " + rel.ChecksumName + " ...");
        await Github.DownloadAsync(http, rel.ChecksumUrl, shaPath, 0, null, ct);

        string expected = Store.ParseChecksumFile(await File.ReadAllTextAsync(shaPath, ct));
        string actual = Store.Sha256File(zipPath);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Download check failed: the file does not match the published checksum. Deleted nothing, aborting.");
        if (rel.ZipDigest != "" && !string.Equals(rel.ZipDigest, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Download check failed: the file does not match GitHub's checksum. Aborting.");
        Say(log, "Download verified.");
        return zipPath;
    }

    public static List<string> ExtractZip(string zipPath, string gameDir)
    {
        var relPaths = new List<string>();
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/")) continue;
            string rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (rel.Contains("..")) throw new InvalidDataException("ZIP contains unsafe path: " + entry.FullName);
            string dest = Path.Combine(gameDir, rel);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(dest);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? gameDir);
            entry.ExtractToFile(dest, overwrite: true);
            relPaths.Add(rel);
        }
        return relPaths;
    }

    public static Store.Manifest RecordManifest(string repo, string tag, string gameDir, List<string> relPaths)
    {
        var m = new Store.Manifest { Tag = tag, Repo = repo };
        foreach (string rel in relPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string full = Path.Combine(gameDir, rel);
            if (File.Exists(full))
                m.Files[rel] = Store.Sha256File(full);
        }
        Store.SaveManifest(m);
        return m;
    }

    public static int VerifyAgainstManifest(string gameDir, Store.Manifest m, IProgress<string> log = null)
    {
        int bad = 0;
        if (m.Files.Count == 0)
        {
            Say(log, "Nothing recorded yet. Run install or update first.");
            return 1;
        }
        foreach (var kv in m.Files.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            string full = Path.Combine(gameDir, kv.Key);
            if (!File.Exists(full))
            {
                Say(log, "MISSING  " + kv.Key);
                bad++;
            }
            else if (!string.Equals(Store.Sha256File(full), kv.Value, StringComparison.OrdinalIgnoreCase))
            {
                Say(log, "CHANGED  " + kv.Key);
                bad++;
            }
        }
        if (bad == 0)
            Say(log, "OK: all " + m.Files.Count + " files intact (" + m.Tag + ").");
        else
            Say(log, bad + " problem(s) out of " + m.Files.Count + " files (" + m.Tag + "). Reinstall or update to repair.");
        return bad == 0 ? 0 : 2;
    }

    public static void RemovePackageFiles(string gameDir, IProgress<string> log = null)
    {
        foreach (string rel in PackagePaths)
        {
            string full = Path.Combine(gameDir, rel);
            try
            {
                if (File.Exists(full)) File.Delete(full);
                else if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
            }
            catch (Exception ex)
            {
                Say(log, "Could not remove " + rel + ": " + ex.Message);
            }
        }
    }

    public static int RunPackageScript(string gameDir, string cmdFile, bool visibleWindow, IProgress<string> log = null)
    {
        string full = Path.Combine(gameDir, cmdFile);
        if (!File.Exists(full))
        {
            Say(log, "Script not found: " + cmdFile);
            return 1;
        }
        if (visibleWindow)
        {
            Say(log, "Running " + cmdFile + " in its own window - answer its prompts there.");
            var wpsi = new ProcessStartInfo("cmd.exe", "/c \"" + cmdFile + "\"")
            {
                WorkingDirectory = gameDir,
                UseShellExecute = true,
            };
            using var wp = Process.Start(wpsi);
            if (wp == null) return 1;
            wp.WaitForExit();
            Say(log, "Exit code: " + wp.ExitCode);
            return wp.ExitCode;
        }
        Say(log, "Running " + cmdFile + " ...");
        var psi = new ProcessStartInfo("cmd.exe", "/c \"" + cmdFile + "\"")
        {
            WorkingDirectory = gameDir,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi);
        if (p == null) return 1;
        p.WaitForExit();
        Say(log, "Exit code: " + p.ExitCode);
        return p.ExitCode;
    }

    public static bool Confirm(string question, bool autoYes)
    {
        if (autoYes) return true;
        Console.Write(question + " [y/N] ");
        string ans = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        return ans == "y" || ans == "yes";
    }

    public static async Task<int> InstallFlowAsync(Store.AppConfig cfg, string workDir, bool autoYes, bool applyPatch, bool visibleWindow, IProgress<string> log = null, IProgress<double> progress = null, CancellationToken ct = default)
    {
        WarnIfGameRunning(log);
        var (rel, localZip) = await ResolveSourceAsync(cfg, log, ct);
        Say(log, "Package: " + rel.Tag + " (" + rel.ZipName + ")");
        string zip = localZip != "" ? localZip : await DownloadAndVerifyAsync(rel, workDir, log, progress, ct);
        Say(log, "Extracting into game folder ...");
        var files = ExtractZip(zip, cfg.GameDir);
        RecordManifest(cfg.Repo, rel.Tag, cfg.GameDir, files);
        cfg.InstalledTag = rel.Tag;
        Store.SaveConfig(cfg);
        Say(log, "Installed " + files.Count + " files (" + rel.Tag + "). Settings and backups untouched.");
        if (applyPatch)
        {
            if (!Confirm("Run Install Custom AI.cmd now to apply the patch?", autoYes)) return 0;
            return RunPackageScript(cfg.GameDir, "Install Custom AI.cmd", visibleWindow, log);
        }
        Say(log, "Skipped patch step. Run Install Custom AI.cmd yourself before playing.");
        return 0;
    }

    public static async Task<int> UpdateFlowAsync(Store.AppConfig cfg, string workDir, bool autoYes, bool applyPatch, bool force, bool visibleWindow, IProgress<string> log = null, IProgress<double> progress = null, CancellationToken ct = default)
    {
        WarnIfGameRunning(log);
        var (rel, localZip) = await ResolveSourceAsync(cfg, log, ct);
        Say(log, "Package: " + rel.Tag + ". Installed: " + (cfg.InstalledTag == "" ? "(unknown)" : cfg.InstalledTag));
        if (!force && cfg.InstalledTag != "" && string.Equals(cfg.InstalledTag, rel.Tag, StringComparison.OrdinalIgnoreCase))
        {
            Say(log, "Already up to date.");
            return 0;
        }
        if (!Confirm("Update to " + rel.Tag + "? Old package files are removed first (settings and backups are kept).", autoYes)) return 0;
        string zip = localZip != "" ? localZip : await DownloadAndVerifyAsync(rel, workDir, log, progress, ct);
        Say(log, "Removing old package files (settings and backups are kept) ...");
        RemovePackageFiles(cfg.GameDir, log);
        Say(log, "Extracting " + rel.Tag + " ...");
        var files = ExtractZip(zip, cfg.GameDir);
        RecordManifest(cfg.Repo, rel.Tag, cfg.GameDir, files);
        cfg.InstalledTag = rel.Tag;
        Store.SaveConfig(cfg);
        Say(log, "Updated to " + rel.Tag + " (" + files.Count + " files).");
        if (applyPatch)
        {
            if (!Confirm("Run Install Custom AI.cmd now to re-apply the patch?", autoYes)) return 0;
            return RunPackageScript(cfg.GameDir, "Install Custom AI.cmd", visibleWindow, log);
        }
        Say(log, "Skipped patch step. Run Install Custom AI.cmd yourself before playing.");
        return 0;
    }

    public static int VerifyFlow(Store.AppConfig cfg, IProgress<string> log = null)
    {
        if (!GameDir.IsGameDir(cfg.GameDir))
        {
            Say(log, "Game folder is not set. Pick it in Settings first.");
            return 1;
        }
        return VerifyAgainstManifest(cfg.GameDir, Store.LoadManifest(), log);
    }

    public static int UninstallFlow(Store.AppConfig cfg, bool autoYes, bool full, bool visibleWindow, IProgress<string> log = null)
    {
        WarnIfGameRunning(log);
        int code = RunPackageScript(cfg.GameDir, "Uninstall Custom AI.cmd", visibleWindow, log);
        if (code != 0 && !Confirm("Uninstaller exited with code " + code + ". Remove leftover package files anyway?", autoYes)) return code;
        if (full)
        {
            if (!Confirm("Full removal: delete the whole CustomAI folder INCLUDING settings, session and backups?", autoYes)) return 0;
            try { Directory.Delete(Path.Combine(cfg.GameDir, "CustomAI"), recursive: true); }
            catch (Exception ex) { Say(log, "Could not remove CustomAI folder: " + ex.Message); return 1; }
        }
        else
        {
            RemovePackageFiles(cfg.GameDir, log);
        }
        foreach (string cmd in PackageCmds)
        {
            try
            {
                string f = Path.Combine(cfg.GameDir, cmd);
                if (File.Exists(f)) File.Delete(f);
            }
            catch { }
        }
        cfg.InstalledTag = "";
        Store.SaveConfig(cfg);
        try { if (File.Exists(Store.ManifestPath)) File.Delete(Store.ManifestPath); } catch { }
        Say(log, full ? "Full uninstall done." : "Uninstall done. Settings and backups were kept.");
        return 0;
    }
}
