using System.IO.Compression;

namespace SwyfInstaller;

internal static class SelfTest
{
    private const string CannedRelease = """
        {"tag_name":"v9.9.9","assets":[
        {"name":"SWYF-Custom-AI-v9.9.9-win-x64.zip","browser_download_url":"https://example.com/a.zip","size":123,"digest":"sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"},
        {"name":"SWYF-Custom-AI-v9.9.9-win-x64.zip.sha256","browser_download_url":"https://example.com/a.zip.sha256"}]}
        """;

    public static int Run()
    {
        int failures = 0;
        void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok) failures++;
        }

        string root = Path.Combine(Path.GetTempPath(), "SwyfInstallerSelfTest_" + Guid.NewGuid().ToString("N"));
        string savedOverride = Store.AppDirOverride;
        Store.AppDirOverride = Path.Combine(root, "appdata");
        try
        {
            string library = Path.Combine(root, "steam-library");
            string common = Path.Combine(library, "steamapps", "common");
            string playtest = Path.Combine(common, "Scam With Your Friends Playtest");
            string fullGame = Path.Combine(common, "Scam With Your Friends");
            Directory.CreateDirectory(playtest);
            File.WriteAllText(Path.Combine(playtest, GameDir.ExeName), "fake-game");
            Check(GameDir.FindCandidates([library]).Count == 0, "automatic detection does not select the Playtest");
            Directory.CreateDirectory(fullGame);
            Check(GameDir.FindCandidates([library]).Count == 0, "automatic detection requires the game executable");
            File.WriteAllText(Path.Combine(fullGame, GameDir.ExeName), "fake-game");
            var candidates = GameDir.FindCandidates([library, library.ToUpperInvariant()]);
            Check(candidates.SequenceEqual([fullGame]), "full release detected once when both editions are installed");
            var saved = new Store.AppConfig { GameDir = playtest + Path.DirectorySeparatorChar, InstalledTag = "custom-ai-v1.1.2", Repo = "o/r" };
            Check(GameDir.MigratePlaytest(saved, candidates) && saved.GameDir == fullGame && saved.InstalledTag == "" && saved.Repo == "o/r", "saved Playtest path migrates and clears the old install version");
            saved.GameDir = playtest;
            Check(!GameDir.MigratePlaytest(saved, []) && saved.GameDir == playtest, "saved Playtest remains available when full release is absent");
            saved.GameDir = Path.Combine(root, "manual-install");
            Check(!GameDir.MigratePlaytest(saved, candidates) && saved.GameDir.EndsWith("manual-install"), "manually selected folders are preserved");
            string renamedGame = Path.Combine(common, "SWYF Full");
            Directory.CreateDirectory(renamedGame);
            File.WriteAllText(Path.Combine(renamedGame, GameDir.ExeName), "fake-game");
            string steamManifest = Path.Combine(library, "steamapps", "appmanifest_4954910.acf");
            File.WriteAllText(steamManifest, "\"AppState\" { \"appid\" \"4954910\" \"installdir\" \"SWYF Full\" }");
            Check(GameDir.FindCandidates([library]).SequenceEqual([renamedGame]), "Steam full release manifest selects its actual install directory");
            File.WriteAllText(steamManifest, "\"installdir\" \"../outside\"");
            Check(GameDir.FindCandidates([library]).SequenceEqual([fullGame]), "invalid manifest path falls back to the standard full release folder");

            string gameDir = Path.Combine(root, "game");
            string payload = Path.Combine(root, "payload");
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(Path.Combine(payload, "CustomAI", "package"));
            Directory.CreateDirectory(Path.Combine(gameDir, "CustomAI"));
            File.WriteAllText(Path.Combine(gameDir, GameDir.ExeName), "fake-game");
            File.WriteAllText(Path.Combine(gameDir, "CustomAI", "settings.json"), "{}");
            File.WriteAllText(Path.Combine(gameDir, "CustomAI", "manifestdex-session.dat"), "secret");
            Directory.CreateDirectory(Path.Combine(gameDir, "CustomAI", "backup", "abc"));
            File.WriteAllText(Path.Combine(gameDir, "CustomAI", "backup", "abc", "orig.dll"), "orig");
            File.WriteAllText(Path.Combine(gameDir, "Install Custom AI.cmd"), "old");
            File.WriteAllText(Path.Combine(payload, "CustomAI", "package", "new.dll"), "new-bytes");

            string h1 = Store.Sha256Bytes(new byte[] { 1, 2, 3 });
            Check(h1.Length == 64 && Store.IsHex64(h1), "sha256 helper produces 64 hex chars");
            Check(!Store.IsHex64("xyz"), "IsHex64 rejects garbage");
            Check(Store.ParseChecksumFile(h1 + "  file.zip\n") == h1, "checksum parse 'hash  filename'");
            Check(Store.ParseChecksumFile("  " + h1.ToUpperInvariant() + "\n") == h1, "checksum parse bare hash");

            string zipPath = Path.Combine(root, "pkg.zip");
            ZipFile.CreateFromDirectory(payload, zipPath);
            var files = Ops.ExtractZip(zipPath, gameDir);
            Check(files.Count == 1 && File.Exists(Path.Combine(gameDir, "CustomAI", "package", "new.dll")), "zip extract");
            var manifest = Ops.RecordManifest("o/r", "v9.9.9", gameDir, files);
            Check(manifest.Files.Count == 1, "manifest recorded");
            Check(Ops.VerifyAgainstManifest(gameDir, manifest) == 0, "verify clean install passes");

            File.WriteAllText(Path.Combine(gameDir, "CustomAI", "package", "new.dll"), "tampered");
            Check(Ops.VerifyAgainstManifest(gameDir, manifest) == 2, "verify detects tampered file");
            File.Delete(Path.Combine(gameDir, "CustomAI", "package", "new.dll"));
            Check(Ops.VerifyAgainstManifest(gameDir, manifest) == 2, "verify detects missing file");

            Ops.RemovePackageFiles(gameDir);
            Check(!File.Exists(Path.Combine(gameDir, "Install Custom AI.cmd")), "update removes old cmd");
            Check(File.Exists(Path.Combine(gameDir, "CustomAI", "settings.json")), "update keeps settings.json");
            Check(File.Exists(Path.Combine(gameDir, "CustomAI", "manifestdex-session.dat")), "update keeps session");
            Check(File.Exists(Path.Combine(gameDir, "CustomAI", "backup", "abc", "orig.dll")), "update keeps backups");

            var rel = Github.ParseLatest("o/r", CannedRelease);
            Check(rel.Tag == "v9.9.9" && rel.ZipName.EndsWith(".zip") && rel.ChecksumUrl.EndsWith(".sha256"), "release asset parsing");
            Check(rel.ZipDigest.Length == 64, "release digest parsing");

            bool threw = false;
            try { Github.ParseLatest("o/r", "{}"); } catch { threw = true; }
            Check(threw, "release parse rejects empty payload");

            Check(Github.NormalizeBranchSource("o/r", " dev ", " /x/y.zip ") == "x/y.zip", "branch source normalizes");
            Check(Github.BranchRawUrl("o/r", "dev", "x/y.zip") == "https://raw.githubusercontent.com/o/r/dev/x/y.zip", "branch raw URL");
            threw = false;
            try { Github.NormalizeBranchSource("o/r", "dev", "../evil.zip"); } catch { threw = true; }
            Check(threw, "branch source rejects traversal");
            threw = false;
            try { Github.NormalizeBranchSource("o/r", "dev", "notes.txt"); } catch { threw = true; }
            Check(threw, "branch source requires a .zip");

            var bundleRoot = Path.Combine(root, "bundle");
            Directory.CreateDirectory(Path.Combine(bundleRoot, "bin", "Debug"));
            string bundleZip = Path.Combine(bundleRoot, "SWYF-Custom-AI-win-x64.zip");
            File.WriteAllText(bundleZip, "bundled package bytes");
            Check(Ops.FindBundledZip(new Store.AppConfig(), Path.Combine(bundleRoot, "bin", "Debug")) == bundleZip,
                "bundled package discovered in a parent folder");
            var packaged = Path.Combine(root, "app");
            Directory.CreateDirectory(Path.Combine(packaged, "package"));
            File.WriteAllText(Path.Combine(packaged, "package", "SWYF-Custom-AI-win-x64.zip"), "x");
            Check(Ops.FindBundledZip(new Store.AppConfig(), packaged).EndsWith(Path.Combine("package", "SWYF-Custom-AI-win-x64.zip")),
                "bundled package discovered in package folder");
            var bundled = Ops.BundledRelease(bundleZip);
            Check(bundled.Tag.StartsWith("bundle@") && bundled.ZipUrl == "" && bundled.ZipName == "SWYF-Custom-AI-win-x64.zip", "bundled release tag from digest");
            File.WriteAllText(bundleZip + ".sha256", new string('0', 64) + "  SWYF-Custom-AI-win-x64.zip");
            threw = false;
            try { Ops.BundledRelease(bundleZip); } catch { threw = true; }
            Check(threw, "bundled package checksum mismatch is refused");
            File.Delete(bundleZip + ".sha256");
            threw = false;
            try { Ops.FindBundledZip(new Store.AppConfig { BundleZipPath = Path.Combine(root, "missing.zip") }, root); } catch { threw = true; }
            Check(threw, "missing explicit BundleZipPath is reported");

            threw = false;
            try { Store.ParseChecksumFile("not a hash\n"); } catch { threw = true; }
            Check(threw, "checksum parse rejects garbage");
        }
        finally
        {
            Store.AppDirOverride = savedOverride;
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        Console.WriteLine(failures == 0 ? "SELFTEST: ALL PASS" : "SELFTEST: " + failures + " FAILURES");
        return failures == 0 ? 0 : 1;
    }
}
