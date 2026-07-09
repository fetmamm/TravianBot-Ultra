using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TbotUltra.Desktop.Services;

// Phase 1 update support: ask GitHub for the latest release, compare against the running VERSION, and
// (on demand) download the portable zip asset. No self-install yet — the download is saved where the user
// chooses and revealed in Explorer. All network calls fail soft (offline / rate-limited → "no update known")
// so the UI never blocks or alarms on a failed check.
public static class UpdateChecker
{
    public const string RepoOwner = "fetmamm";
    public const string RepoName = "TravianBot-Ultra";
    public const string ReleasesPageUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases";
    private const string LatestReleaseApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    public sealed record ReleaseInfo(
        string LatestVersion,
        string ReleaseUrl,
        string? PortableAssetName,
        string? PortableDownloadUrl);

    public sealed record UpdateStatus(
        string CurrentVersion,
        ReleaseInfo? Release,
        bool UpdateAvailable);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub's REST API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TbotUltra-UpdateChecker");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    // Reads the running version from the VERSION file shipped next to the exe (and present at the repo root
    // in dev). Returns "dev" when missing/empty so an unknown build never looks older than a real release.
    public static string ReadCurrentVersion(string versionPath)
    {
        try
        {
            if (File.Exists(versionPath))
            {
                var raw = File.ReadAllText(versionPath).Trim();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    return raw;
                }
            }
        }
        catch
        {
            // fall through to "dev"
        }

        return "dev";
    }

    public static async Task<UpdateStatus> CheckAsync(string currentVersion, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateStatus(currentVersion, null, false);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            var latestVersion = NormalizeVersion(tag);
            if (latestVersion is null)
            {
                return new UpdateStatus(currentVersion, null, false);
            }

            var htmlUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null;

            string? assetName = null;
            string? assetUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(name)
                        && name.EndsWith("portable.zip", StringComparison.OrdinalIgnoreCase))
                    {
                        assetName = name;
                        assetUrl = asset.TryGetProperty("browser_download_url", out var dlEl) ? dlEl.GetString() : null;
                        break;
                    }
                }
            }

            var release = new ReleaseInfo(
                latestVersion,
                string.IsNullOrWhiteSpace(htmlUrl) ? ReleasesPageUrl : htmlUrl!,
                assetName,
                assetUrl);
            return new UpdateStatus(currentVersion, release, IsNewer(latestVersion, currentVersion));
        }
        catch
        {
            // Offline, rate-limited, or malformed payload → report "no update known" rather than surfacing
            // an error. The Version button stays grey.
            return new UpdateStatus(currentVersion, null, false);
        }
    }

    // Streams a release asset to disk, reporting fractional progress (0..1) when the server sends a length.
    public static async Task DownloadAsync(
        string url,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            readTotal += read;
            if (total is > 0 && progress is not null)
            {
                progress.Report((double)readTotal / total.Value);
            }
        }
    }

    // Accepts "vX.Y.Z" or "X.Y.Z"; returns the bare "X.Y.Z" or null when it is not semver-shaped.
    private static string? NormalizeVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            value = value[1..];
        }

        return TryParseVersion(value, out _) ? value : null;
    }

    public static bool IsNewer(string latest, string current)
    {
        if (!TryParseVersion(latest, out var latestParts) || !TryParseVersion(current, out var currentParts))
        {
            return false;
        }

        for (var i = 0; i < 3; i++)
        {
            if (latestParts[i] != currentParts[i])
            {
                return latestParts[i] > currentParts[i];
            }
        }

        return false;
    }

    private static bool TryParseVersion(string version, out int[] parts)
    {
        parts = new int[3];
        var segments = version.Split('.');
        if (segments.Length is < 1 or > 3)
        {
            return false;
        }

        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], out parts[i]))
            {
                return false;
            }
        }

        return true;
    }
}
