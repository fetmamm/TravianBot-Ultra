using System.Text.Json.Nodes;
using Microsoft.Playwright;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Configuration;

namespace TbotUltra.Worker.Infrastructure;

public sealed partial class BrowserSession
{
    public async Task SaveStateAsync(bool clearTransientOrigins = true)
    {
        if (_context is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(StorageStatePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Storage state path is invalid.");
        }

        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(StorageStatePath)}.{Guid.NewGuid():N}.tmp");

        await StorageStateGate.WaitAsync();
        try
        {
            // Strip cookies/localStorage that belong to a sibling server. A stray sibling
            // session can otherwise persist in this account's saved state and keep triggering
            // cross-server popups on every login. Keep the account's own host plus shared
            // parent-domain cookies (which login may need), and drop only foreign siblings.
            if (clearTransientOrigins)
            {
                await ClearTransientExternalStorageOriginsAsync(force: false);
            }
            var stateJson = await _context.StorageStateAsync();
            stateJson = FilterForeignSubdomainState(stateJson);
            await File.WriteAllTextAsync(tempPath, stateJson);

            await ReplaceStorageStateWithRetryAsync(tempPath, StorageStatePath);
        }
        finally
        {
            StorageStateGate.Release();
            TryDeleteFile(tempPath);
        }

        LegacyBrowserStorageAdapter.DeleteIfPresent(
            AccountStoragePaths.LegacyBrowserStatePath(_projectRoot, _account.Name));
    }

    // Removes cookies and localStorage origins that belong to a sibling subdomain of the
    // account's server. Keeps the account's own host, parent/shared domains, and any
    // sub-host of the account.
    private string FilterForeignSubdomainState(string stateJson)
    {
        string accountHost;
        try
        {
            accountHost = new Uri(_effectiveBaseUrl).Host.ToLowerInvariant();
        }
        catch
        {
            return stateJson; // BaseUrl not absolute — leave state untouched.
        }

        if (string.IsNullOrEmpty(accountHost))
        {
            return stateJson;
        }

        try
        {
            if (JsonNode.Parse(stateJson) is not JsonObject root)
            {
                return stateJson;
            }

            if (root["cookies"] is JsonArray cookies)
            {
                // Also drop the consentmanager (CMP) consent cookies (__cmp*, euconsent, etc.). If they
                // persist, Travian's first-party JS sees stored consent on every page load and runs the
                // bonus-video ad stack, which spawns window.open tabs (network blocking can't stop a
                // window.open-created tab). Consent is re-established transiently during a video.
                var kept = cookies.OfType<JsonObject>()
                    .Where(c => KeepHostForAccount(c["domain"]?.GetValue<string>() ?? string.Empty, accountHost)
                        && !IsConsentStorageName(c["name"]?.GetValue<string>() ?? string.Empty))
                    .Select(c => c.DeepClone())
                    .ToArray();
                root["cookies"] = new JsonArray(kept);
            }

            if (root["origins"] is JsonArray origins)
            {
                var kept = origins.OfType<JsonObject>()
                    .Where(o =>
                    {
                        var origin = o["origin"]?.GetValue<string>() ?? string.Empty;
                        return !Uri.TryCreate(origin, UriKind.Absolute, out var u)
                            || KeepHostForAccount(u.Host, accountHost);
                    })
                    .Select(o => o.DeepClone())
                    .ToArray();
                // Strip consent entries from each origin's localStorage for the same reason as cookies.
                foreach (var origin in kept.OfType<JsonObject>())
                {
                    if (origin["localStorage"] is JsonArray ls)
                    {
                        var keptLs = ls.OfType<JsonObject>()
                            .Where(e => !IsConsentStorageName(e["name"]?.GetValue<string>() ?? string.Empty))
                            .Select(e => e.DeepClone())
                            .ToArray();
                        origin["localStorage"] = new JsonArray(keptLs);
                    }
                }

                root["origins"] = new JsonArray(kept);
            }

            return root.ToJsonString();
        }
        catch
        {
            return stateJson; // On any parse/shape error, keep the original state.
        }
    }

    // Cookie/localStorage names written by the consentmanager CMP (and IAB TCF). Stripped from saved
    // state so stored consent does not make Travian run the bonus-video ad stack on every page.
    internal static bool IsConsentStorageName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var n = name.Trim().ToLowerInvariant();
        return n.StartsWith("__cmp", StringComparison.Ordinal)
            || n.StartsWith("cmp", StringComparison.Ordinal)
            || n.Contains("consent", StringComparison.Ordinal)
            || n.StartsWith("euconsent", StringComparison.Ordinal)
            || n.StartsWith("usprivacy", StringComparison.Ordinal)
            || n.Contains("iab", StringComparison.Ordinal)
            || n.Contains("tcf", StringComparison.Ordinal)
            || n.Contains("gdpr", StringComparison.Ordinal)
            || n.StartsWith("gpp", StringComparison.Ordinal)
            || n.Contains("addtl_consent", StringComparison.Ordinal)
            || n.StartsWith("__gads", StringComparison.Ordinal)
            || n.StartsWith("__gpi", StringComparison.Ordinal)
            || n.StartsWith("_gac", StringComparison.Ordinal)
            || n.StartsWith("_gcl", StringComparison.Ordinal);
    }

    internal static bool KeepHostForAccount(string cookieDomainOrHost, string accountHost)
    {
        var d = cookieDomainOrHost.TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(d))
        {
            return true;
        }

        // Keep: exact host, a parent/shared domain of the account host, or a sub-host of it.
        // Drop: sibling subdomains (different server on the same shared base domain).
        return IsTravianLobbyOrAuthHost(d)
            || d == accountHost
            || accountHost.EndsWith("." + d, StringComparison.Ordinal)
            || d.EndsWith("." + accountHost, StringComparison.Ordinal);
    }

    private static bool IsTravianLobbyOrAuthHost(string host)
    {
        return host.Equals("legends.travian.com", StringComparison.Ordinal)
            || host.EndsWith(".legends.travian.com", StringComparison.Ordinal)
            || host.Equals("auth.travian.com", StringComparison.Ordinal)
            || host.Equals("login.travian.com", StringComparison.Ordinal)
            || host.Equals("accounts.travian.com", StringComparison.Ordinal);
    }

    private static async Task ReplaceStorageStateWithRetryAsync(string sourcePath, string targetPath)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                File.Copy(sourcePath, targetPath, overwrite: true);
                return;
            }
            catch (IOException ex) when (IsTransientStorageStateWriteError(ex) && attempt < 5)
            {
                lastError = ex;
                await Task.Delay(150 * attempt);
            }
            catch (UnauthorizedAccessException ex) when (attempt < 5)
            {
                lastError = ex;
                await Task.Delay(150 * attempt);
            }
        }

        throw new IOException($"Could not replace browser state after retries: {lastError?.Message}", lastError);
    }

    private static bool IsTransientStorageStateWriteError(IOException ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("user-mapped section", StringComparison.OrdinalIgnoreCase)
            || message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot access the file", StringComparison.OrdinalIgnoreCase)
            || message.Contains("begärda åtgärden", StringComparison.OrdinalIgnoreCase)
            || message.Contains("användarmappat", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

}
