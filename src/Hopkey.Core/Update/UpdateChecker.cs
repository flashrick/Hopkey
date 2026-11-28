using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Hopkey.Core.Util;

namespace Hopkey.Core.Update;

/// <summary>A published release: version, the win-x64 zip and its SHA-256 (from GitHub's asset digest).</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string ZipName, string ZipUrl, string Sha256, string PageUrl, long Size);

/// <summary>
/// Looks for a newer release on GitHub (no server or domain of our own: the GitHub Releases API is the feed) and
/// downloads + verifies + unpacks it into a staging folder under %LocalAppData%\Hopkey\updates.
/// </summary>
public static class UpdateChecker
{
    public const string Repo = "flashrick/Hopkey";
    public const string LatestUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";

    /// <summary>The asset each release carries, e.g. Hopkey-v0.5.0-win-x64.zip.</summary>
    public static string AssetName(string tag) => $"Hopkey-{tag}-win-x64.zip";

    /// <summary>The running app's version (the entry assembly, Hopkey.exe — not this library).</summary>
    public static Version CurrentVersion { get; set; } =
        Normalize(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var s = tag.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s, out var v)) return false;
        version = Normalize(v);
        return true;
    }

    public static bool IsNewer(ReleaseInfo release, Version current) => release.Version > Normalize(current);

    /// <summary>Parse the GitHub "latest release" JSON. Null when it has no usable win-x64 zip with a SHA-256 digest.</summary>
    public static ReleaseInfo? ParseLatest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (!TryParseTag(tag, out var version)) return null;
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        var want = AssetName(tag!);
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!string.Equals(name, want, StringComparison.OrdinalIgnoreCase)) continue;
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            var digest = a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
            long size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var l) ? l : 0;
            if (url == null || !IsTrustedUrl(url)) return null;
            if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
            var sha = digest[7..].Trim().ToLowerInvariant();
            if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) return null;
            return new ReleaseInfo(version, tag!, name!, url, sha, page, size);
        }
        return null;
    }

    /// <summary>Only download from GitHub over HTTPS.</summary>
    public static bool IsTrustedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        // GitHub's API requires a User-Agent; a generic product token only.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Hopkey", CurrentVersion.ToString(3)));
        return http;
    }

    /// <summary>Latest published release, or null if none is usable. Throws on network errors.</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        using var http = CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseLatest(json);
    }

    public static string UpdatesDirectory => Path.Combine(Config.AppPaths.LocalDirectory, "updates");

    /// <summary>
    /// Download the zip, check its SHA-256 and unpack it into updates\stage-&lt;tag&gt;. Returns the staging folder.
    /// The file is fetched by our own HTTP client, so it carries no Mark-of-the-Web (no SmartScreen "downloaded from
    /// the internet" prompt) — which is exactly why the hash check is mandatory.
    /// </summary>
    public static async Task<string> DownloadAndStageAsync(ReleaseInfo r, CancellationToken ct)
    {
        Directory.CreateDirectory(UpdatesDirectory);
        var zip = Path.Combine(UpdatesDirectory, r.ZipName);
        var part = zip + ".part";
        using (var http = CreateClient())
        using (var resp = await http.GetAsync(r.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            if (resp.RequestMessage?.RequestUri is { } final && !IsTrustedUrl(final.ToString()))
                throw new InvalidDataException($"Update download was redirected to an unexpected host: {final.Host}");
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }
        var actual = Sha256Of(part);
        if (!string.Equals(actual, r.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(part);
            throw new InvalidDataException($"Update checksum mismatch (expected {r.Sha256}, got {actual})");
        }
        File.Move(part, zip, overwrite: true);
        var stage = Path.Combine(UpdatesDirectory, "stage-" + r.Tag);
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        System.IO.Compression.ZipFile.ExtractToDirectory(zip, stage); // rejects entries that escape the folder
        TryDelete(zip);
        UpdateApplier.ValidateStage(stage);
        Logger.Info($"Update {r.Tag} downloaded, verified (sha256 {actual}) and staged in {stage}");
        return stage;
    }

    public static string Sha256Of(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>Remove leftovers of earlier updates (staging folders, backups, partial downloads), except <paramref name="keep"/>.</summary>
    public static void CleanUp(string? keep = null)
    {
        try
        {
            if (!Directory.Exists(UpdatesDirectory)) return;
            foreach (var d in Directory.EnumerateDirectories(UpdatesDirectory))
                if (keep == null || !PathUtil.Equal(d, keep))
                    try { Directory.Delete(d, true); } catch { /* still in use: next time */ }
            foreach (var f in Directory.EnumerateFiles(UpdatesDirectory)) TryDelete(f);
        }
        catch (Exception ex) { Logger.Debug($"Update cleanup: {ex.Message}"); }
    }

    private static void TryDelete(string f) { try { File.Delete(f); } catch { } }
}
