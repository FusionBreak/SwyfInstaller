using System.Security.Cryptography;
using System.Text.Json;

namespace SwyfInstaller;

public static class Store
{
    public sealed class AppConfig
    {
        public string GameDir { get; set; } = "";
        public string Repo { get; set; } = "FusionBreak/SwyfInstaller";

        /// <summary>Optional. When set, updates come from this branch instead of Releases:
        /// raw.githubusercontent.com/{Repo}/{Branch}/{BranchZipPath}, verified against
        /// the same path + ".sha256". Leave empty to use Releases.</summary>
        public string Branch { get; set; } = "";

        /// <summary>ZIP path inside the branch (branch mode only).</summary>
        public string BranchZipPath { get; set; } = "SWYF-Custom-AI-win-x64.zip";

        /// <summary>Optional explicit package ZIP (bundled mode). When empty, the installer looks
        /// for SWYF-Custom-AI-win-x64.zip next to the app, in package\, or up to 6 folders above it
        /// (a cloned repository works out of the box). Set only to point somewhere specific.</summary>
        public string BundleZipPath { get; set; } = "";

        public string InstalledTag { get; set; } = "";
    }

    public sealed class Manifest
    {
        public string Tag { get; set; } = "";
        public string Repo { get; set; } = "";
        public Dictionary<string, string> Files { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string AppDirOverride = null;

    public static string AppDir =>
        AppDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwyfInstaller");

    public static string ConfigPath => Path.Combine(AppDir, "config.json");
    public static string ManifestPath => Path.Combine(AppDir, "manifest.json");

    public static AppConfig LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
                if (cfg != null)
                {
                    if (string.IsNullOrWhiteSpace(cfg.Repo)) cfg.Repo = "FusionBreak/SwyfInstaller";
                    if (string.IsNullOrWhiteSpace(cfg.BranchZipPath)) cfg.BranchZipPath = "SWYF-Custom-AI-win-x64.zip";
                    // Migrate configs pointing at the old upstream/standalone repos (merged + superseded by the fork).
                    if (cfg.Repo.Equals("manifest-dex/swyf-custom-ai-mod", StringComparison.OrdinalIgnoreCase) ||
                        cfg.Repo.Equals("manifest-dex/SwyfInstaller", StringComparison.OrdinalIgnoreCase))
                        cfg.Repo = "FusionBreak/SwyfInstaller";
                    if (GameDir.MigratePlaytest(cfg, GameDir.FindCandidates()))
                    {
                        try { SaveConfig(cfg); } catch { }
                    }
                    return cfg;
                }
            }
        }
        catch { }
        return new AppConfig();
    }

    public static void SaveConfig(AppConfig cfg)
    {
        Directory.CreateDirectory(AppDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOpts));
    }

    public static Manifest LoadManifest()
    {
        try
        {
            if (File.Exists(ManifestPath))
            {
                var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath));
                if (m != null)
                {
                    m.Files ??= new Dictionary<string, string>();
                    return m;
                }
            }
        }
        catch { }
        return new Manifest();
    }

    public static void SaveManifest(Manifest m)
    {
        Directory.CreateDirectory(AppDir);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(m, JsonOpts));
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Sha256Bytes(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    public static bool IsHex64(string s)
    {
        if (s == null || s.Length != 64) return false;
        foreach (char c in s)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }
        return true;
    }

    public static string ParseChecksumFile(string text)
    {
        foreach (string rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            string token = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)[0].Trim('*', ' ', '\t');
            if (IsHex64(token)) return token.ToLowerInvariant();
        }
        throw new InvalidDataException("No SHA-256 hash found in checksum file.");
    }
}
