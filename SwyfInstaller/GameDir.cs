using System.Diagnostics;
using Microsoft.Win32;

namespace SwyfInstaller;

public static class GameDir
{
    public const string ExeName = "Scam With Your Friends.exe";
    public const string FolderName = "Scam With Your Friends";
    public const string SteamAppId = "4954910";
    public const string ProcessName = "Scam With Your Friends";

    public static List<string> FindCandidates() => FindCandidates(SteamLibraries());

    internal static List<string> FindCandidates(IEnumerable<string> libraries)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string lib in libraries)
        {
            string folder = FolderName;
            try
            {
                string manifest = Path.Combine(lib, "steamapps", "appmanifest_" + SteamAppId + ".acf");
                if (File.Exists(manifest))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s*\"([^\"]+)\"");
                    string installed = match.Groups[1].Value;
                    if (match.Success && installed != "." && installed != ".." && Path.GetFileName(installed) == installed)
                        folder = installed;
                }
            }
            catch { }
            string dir;
            try { dir = Path.GetFullPath(Path.Combine(lib, "steamapps", "common", folder)); }
            catch { continue; }
            string key = dir.ToUpperInvariant();
            if (seen.Contains(key)) continue;
            if (IsGameDir(dir))
            {
                seen.Add(key);
                found.Add(dir);
            }
        }
        return found;
    }

    internal static bool MigratePlaytest(Store.AppConfig cfg, IReadOnlyList<string> candidates)
    {
        if (!string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(cfg.GameDir ?? "")),
                "Scam With Your Friends Playtest", StringComparison.OrdinalIgnoreCase) || candidates.Count == 0)
            return false;
        cfg.GameDir = candidates[0];
        // The recorded mod version belongs to the old installation.
        cfg.InstalledTag = "";
        return true;
    }

    public static bool IsGameDir(string dir)
    {
        return !string.IsNullOrWhiteSpace(dir) &&
               Directory.Exists(dir) &&
               File.Exists(Path.Combine(dir, ExeName));
    }

    public static bool IsGameRunning()
    {
        try { return Process.GetProcessesByName(ProcessName).Length > 0; }
        catch { return false; }
    }

    public static string Resolve(Store.AppConfig cfg, bool interactive)
    {
        if (IsGameDir(cfg.GameDir)) return cfg.GameDir;

        var candidates = FindCandidates();
        if (candidates.Count == 1)
        {
            Console.WriteLine("Detected game folder: " + candidates[0]);
            cfg.GameDir = candidates[0];
            return cfg.GameDir;
        }
        if (candidates.Count > 1 && interactive)
        {
            Console.WriteLine("Multiple installs found:");
            for (int i = 0; i < candidates.Count; i++)
                Console.WriteLine("  [" + (i + 1) + "] " + candidates[i]);
            Console.Write("Pick one (number), or paste a path: ");
            string answer = (Console.ReadLine() ?? "").Trim().Trim('"');
            if (int.TryParse(answer, out int n) && n >= 1 && n <= candidates.Count)
            {
                cfg.GameDir = candidates[n - 1];
                return cfg.GameDir;
            }
            if (IsGameDir(answer))
            {
                cfg.GameDir = Path.GetFullPath(answer);
                return cfg.GameDir;
            }
            throw new InvalidOperationException("No valid game folder selected.");
        }
        if (!interactive)
            throw new InvalidOperationException("Game folder not found (missing " + ExeName + ").");
        Console.WriteLine("Could not find the game automatically.");
        Console.Write("Paste the game folder path (Steam > Properties > Installed Files > Browse): ");
        string typed = (Console.ReadLine() ?? "").Trim().Trim('"');
        if (IsGameDir(typed))
        {
            cfg.GameDir = Path.GetFullPath(typed);
            return cfg.GameDir;
        }
        throw new InvalidOperationException("That folder does not contain " + ExeName + ".");
    }

    private static readonly (RegistryHive Hive, RegistryView View, string SubKey, string Value)[] SteamRegistryLocations =
    {
        (RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "SteamPath"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"),
    };

    private static readonly System.Text.RegularExpressions.Regex VdfPathRegex =
        new("\"path\"\\s*\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path.Trim().Replace('/', '\\')); }
        catch { return path.Trim().Replace('/', '\\'); }
    }

    private static string Unescape(string s) => s.Replace(@"\\", @"\");

    private static List<string> SteamLibraries()
    {
        var libs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddLib(string p)
        {
            try
            {
                string full = Normalize(p);
                if (Directory.Exists(full) && seen.Add(full))
                    libs.Add(full);
            }
            catch { }
        }

        // Steam roots from the registry, confirmed by steam.exe actually being there.
        var roots = new List<string>();
        foreach (var (hive, view, subKey, value) in SteamRegistryLocations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKey);
                if (key?.GetValue(value) is not string raw || string.IsNullOrWhiteSpace(raw)) continue;
                string root = Normalize(raw);
                if (File.Exists(Path.Combine(root, "steam.exe")) && seen.Add(root))
                {
                    roots.Add(root);
                    libs.Add(root);
                }
            }
            catch { }
        }

        // Every library across drives, from each root's libraryfolders.vdf.
        foreach (string root in roots)
        {
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            string text;
            try { text = File.ReadAllText(vdf); } catch { continue; }
            foreach (System.Text.RegularExpressions.Match m in VdfPathRegex.Matches(text))
            {
                try { AddLib(Unescape(m.Groups[1].Value)); } catch { }
            }
        }
        return libs;
    }
}
