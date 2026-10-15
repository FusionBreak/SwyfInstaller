using System.Security.Cryptography;
using System.Text.Json;

namespace SwyfInstaller;

public static class Store
{
    public sealed class AppConfig
    {
        public string GameDir { get; set; } = "";
        public string Repo { get; set; } = "manifest-dex/SwyfInstaller";
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
                    if (string.IsNullOrWhiteSpace(cfg.Repo)) cfg.Repo = "manifest-dex/SwyfInstaller";
                    // Migrate configs pointing at the old standalone mod repo (merged into custom-ai/ + deleted).
                    if (string.Equals(cfg.Repo.Trim(), "manifest-dex/swyf-custom-ai-mod", StringComparison.OrdinalIgnoreCase))
                        cfg.Repo = "manifest-dex/SwyfInstaller";
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
