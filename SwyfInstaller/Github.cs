using System.Text.Json;

namespace SwyfInstaller;

public sealed class ReleaseInfo
{
    public string Tag { get; set; } = "";
    public string ZipName { get; set; } = "";
    public string ZipUrl { get; set; } = "";
    public long ZipSize { get; set; }
    public string ZipDigest { get; set; } = "";
    public string ChecksumName { get; set; } = "";
    public string ChecksumUrl { get; set; } = "";
}

internal static class Github
{
    private const string ApiBase = "https://api.github.com/repos/";
    private const string RawBase = "https://raw.githubusercontent.com/";

    /// <summary>Validates repo/branch/zipPath and returns the normalized "dir/file.zip" path
    /// inside the branch. Rejects traversal and absolute paths so the raw URL stays inside the repo.</summary>
    public static string NormalizeBranchSource(string repo, string branch, string zipPath)
    {
        repo = (repo ?? "").Trim().Trim('/');
        branch = (branch ?? "").Trim().Trim('/');
        zipPath = (zipPath ?? "").Trim().Trim('/');
        if (!repo.Contains('/') || branch == "")
            throw new InvalidDataException("Branch source needs Repo (owner/name) and Branch to be set.");
        if (zipPath == "" || !zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("BranchZipPath must point to a .zip file inside the branch.");
        if (zipPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(s => s == ".."))
            throw new InvalidDataException("BranchZipPath must stay inside the repository.");
        return string.Join('/', zipPath.Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    public static string BranchRawUrl(string repo, string branch, string relZip) =>
        RawBase + repo.Trim('/') + "/" + Uri.EscapeDataString(branch.Trim('/')) + "/" +
        string.Join('/', relZip.Split('/').Select(Uri.EscapeDataString));

    /// <summary>Branch mode: the package ZIP lives in the branch itself, committed together with a
    /// ".sha256" checksum file (release.ps1 -Commit creates both). Integrity stays mandatory:
    /// without the checksum file the install is refused. The commit SHA (when the API answers)
    /// becomes part of the tag so re-checks on an unchanged branch report "up to date".</summary>
    public static async Task<ReleaseInfo> GetBranchAsync(HttpClient http, string repo, string branch, string zipPath, CancellationToken ct)
    {
        string relZip = NormalizeBranchSource(repo, branch, zipPath);
        string zipUrl = BranchRawUrl(repo, branch, relZip);

        using (var probe = await http.GetAsync(zipUrl + ".sha256", ct))
        {
            if ((int)probe.StatusCode == 404)
                throw new InvalidDataException(
                    "Branch \"" + branch + "\" of " + repo.Trim('/') + " has no " + relZip + ".sha256. " +
                    "Branch installs require the package ZIP and its .sha256 to be committed to the branch " +
                    "(build the package with release.ps1 and commit both files). " +
                    "Clear \"Branch\" in config.json to install from Releases instead.");
            if (!probe.IsSuccessStatusCode)
                throw new HttpRequestException("raw.githubusercontent.com returned HTTP " + (int)probe.StatusCode + " for " + relZip + ".sha256.");
        }

        string sha = await TryGetCommitShaAsync(http, repo, branch, ct);
        return new ReleaseInfo
        {
            Tag = "branch:" + branch.Trim('/') + (sha == "" ? "" : "@" + sha.Substring(0, Math.Min(12, sha.Length))),
            ZipName = Path.GetFileName(relZip),
            ZipUrl = zipUrl,
            ChecksumName = Path.GetFileName(relZip) + ".sha256",
            ChecksumUrl = zipUrl + ".sha256",
        };
    }

    private static async Task<string> TryGetCommitShaAsync(HttpClient http, string repo, string branch, CancellationToken ct)
    {
        try
        {
            using var res = await http.GetAsync(ApiBase + repo.Trim('/') + "/commits/" + Uri.EscapeDataString(branch.Trim('/')), ct);
            if (!res.IsSuccessStatusCode) return "";
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("sha", out var el) ? el.GetString() ?? "" : "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ""; }
    }

    private static ReleaseInfo TryParseRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tagEl))
            return null;

        var rel = new ReleaseInfo { Tag = tagEl.GetString() ?? "" };
        if (string.IsNullOrWhiteSpace(rel.Tag))
            return null;

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var a in assets.EnumerateArray())
        {
            string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string dl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
            if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
            {
                if (rel.ChecksumUrl == "" && dl != "")
                {
                    rel.ChecksumName = name;
                    rel.ChecksumUrl = dl;
                }
                continue;
            }
            if (name.StartsWith("SWYF-Custom-AI-", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase) && dl != "")
            {
                rel.ZipName = name;
                rel.ZipUrl = dl;
                if (a.TryGetProperty("size", out var s) && s.TryGetInt64(out long size)) rel.ZipSize = size;
                if (a.TryGetProperty("digest", out var d))
                {
                    string digest = d.GetString() ?? "";
                    if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        rel.ZipDigest = digest.Substring(7).ToLowerInvariant();
                }
            }
        }

        if (rel.ZipUrl == "" || rel.ChecksumUrl == "")
            return null;
        return rel;
    }

    public static ReleaseInfo ParseLatest(string repo, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var rel = TryParseRelease(root);
        if (rel != null)
            return rel;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out _))
            throw new InvalidDataException("GitHub API response has no tag_name (repo may have no releases).");
        throw new InvalidDataException("Release has no verifiable SWYF-Custom-AI-*-win-x64.zip asset (.zip + .sha256 required). Aborting for safety.");
    }

    // Newest-first scan: returns the first non-draft release carrying a
    // verifiable mod ZIP, so setup-only releases in the same repo are skipped.
    public static async Task<ReleaseInfo> GetLatestAsync(HttpClient http, string repo, CancellationToken ct)
    {
        for (int page = 1; page <= 5; page++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + repo.Trim('/') + "/releases?per_page=20&page=" + page);
            req.Headers.UserAgent.ParseAdd("SwyfInstaller/1.0");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await http.SendAsync(req, ct);
            string body = await res.Content.ReadAsStringAsync(ct);
            if ((int)res.StatusCode == 404)
                throw new InvalidDataException("No published releases found for " + repo + " (HTTP 404).");
            if (!res.IsSuccessStatusCode)
                throw new HttpRequestException("GitHub API returned HTTP " + (int)res.StatusCode + ": " + body.Trim());
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                break;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True && draft.GetBoolean())
                    continue;
                var rel = TryParseRelease(el);
                if (rel != null)
                    return rel;
            }
        }
        throw new InvalidDataException("No published release with a verifiable SWYF-Custom-AI-*-win-x64.zip asset found for " + repo + ".");
    }

    public static HttpClient CreateClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SwyfInstaller/1.0");
        return http;
    }

    public static async Task DownloadAsync(HttpClient http, string url, string destPath, long expectedSize, IProgress<double> progress, CancellationToken ct)
    {
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException("Download returned HTTP " + (int)res.StatusCode + " for " + url);
        long? total = res.Content.Headers.ContentLength;
        if (total == null || total <= 0) total = expectedSize > 0 ? expectedSize : null;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath)) ?? ".");
        using var net = await res.Content.ReadAsStreamAsync(ct);
        using var file = File.Create(destPath);
        byte[] buffer = new byte[81920];
        long done = 0;
        int lastPct = -1;
        while (true)
        {
            int read = await net.ReadAsync(buffer, ct);
            if (read == 0) break;
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0)
            {
                int pct = (int)(done * 100 / total.Value);
                if (pct != lastPct && pct % 5 == 0)
                {
                    lastPct = pct;
                    try { progress?.Report(pct); } catch { }
                }
            }
        }
    }
}
