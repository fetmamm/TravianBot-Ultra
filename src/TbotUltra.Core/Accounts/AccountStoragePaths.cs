namespace TbotUltra.Core.Accounts;

public static class AccountStoragePaths
{
    public const string AccountsRelativeDirectory = "config/accounts";

    public static string NormalizeAccountKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "main";
        }

        var chars = value.Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')
            .ToArray();
        var joined = string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return joined.Length == 0 ? "main" : joined;
    }

    public static string NormalizeServerKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "default_server";
        }

        var trimmed = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return "default_server";
        }

        var hostPart = string.IsNullOrWhiteSpace(uri.Host) ? "default_server" : uri.Host.ToLowerInvariant();
        var portPart = uri.IsDefaultPort ? string.Empty : $"_{uri.Port}";
        var chars = $"{hostPart}{portPart}"
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        var joined = string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return joined.Length == 0 ? "default_server" : joined;
    }

    public static string NormalizeSelectionMode(string? value)
    {
        return string.Equals(value?.Trim(), "all_villages", StringComparison.OrdinalIgnoreCase)
            ? "all_villages"
            : "farm_villages";
    }

    public static string AccountDirectory(string projectRoot, string accountName)
    {
        return Path.Combine(projectRoot, AccountsRelativeDirectory, NormalizeAccountKey(accountName));
    }

    public static string BrowserStatePath(string projectRoot, string accountName)
    {
        return Path.Combine(AccountDirectory(projectRoot, accountName), "session", "playwright-state.json");
    }

    public static string AccountSettingsPath(string projectRoot, string accountName)
    {
        return Path.Combine(AccountDirectory(projectRoot, accountName), "settings.json");
    }

    // Per-account structured village settings (which villages are enabled for automation, plus
    // cached identity such as name/coords/capital). Kept as its own file because a per-village
    // record does not map cleanly onto the flat key/value settings.json overlay.
    public static string VillageSettingsPath(string projectRoot, string accountName)
    {
        return Path.Combine(AccountDirectory(projectRoot, accountName), "villages.json");
    }

    public static string TravcoListsPath(string projectRoot, string accountName)
    {
        return Path.Combine(AccountDirectory(projectRoot, accountName), "travco_lists.json");
    }

    // Per-account task queue. The queue used to be a single global config/queue.json shared by every
    // account; scoping it per account keeps one account's queued work from leaking into another.
    public static string AccountQueuePath(string projectRoot, string accountName)
    {
        return Path.Combine(AccountDirectory(projectRoot, accountName), "queue.json");
    }

    public static string LegacyGlobalQueuePath(string projectRoot)
    {
        return Path.Combine(projectRoot, "config", "queue.json");
    }

    // Per-account cache of each village's last-read buildings + resource fields, so a village scan is
    // remembered across restarts (something to work on without re-scanning everything every launch).
    public static string VillageCachePath(string projectRoot, string accountName)
    {
        return Path.Combine(AccountDirectory(projectRoot, accountName), "village_cache.json");
    }

    public static string LegacyBrowserStatePath(string projectRoot, string accountName)
    {
        return Path.Combine(projectRoot, "playwright", ".auth", $"{NormalizeAccountKey(accountName)}.json");
    }

    public static string AnalysisPath(string projectRoot, string accountName, string? serverUrl = null)
    {
        return Path.Combine(AccountDirectory(projectRoot, accountName), "analysis", $"{NormalizeServerKey(serverUrl)}.json");
    }

    public static string LegacyAnalysisPath(string projectRoot, string accountName, string? serverUrl = null)
    {
        return Path.Combine(projectRoot, AccountAnalysisConstants.RelativeDirectory, $"{NormalizeAccountKey(accountName)}__{NormalizeServerKey(serverUrl)}.json");
    }

    public static string NatarFarmCachePath(string projectRoot, string accountName, string? serverUrl = null, string? selectionMode = null)
    {
        return Path.Combine(AccountDirectory(projectRoot, accountName), "cache", "natar-farms", $"{NormalizeServerKey(serverUrl)}__{NormalizeSelectionMode(selectionMode)}.json");
    }

    public static string LegacyNatarFarmCachePath(string projectRoot, string accountName, string? serverUrl = null, string? selectionMode = null)
    {
        return Path.Combine(projectRoot, "config", "cache", "natar-farms", $"{NormalizeAccountKey(accountName)}__{NormalizeServerKey(serverUrl)}__{NormalizeSelectionMode(selectionMode)}.json");
    }

    public static string CapitalStatePath(string projectRoot, string accountName)
    {
        return Path.Combine(AccountDirectory(projectRoot, accountName), "cache", "capital-state.json");
    }

    public static string LegacyCapitalStatePath(string projectRoot)
    {
        return Path.Combine(projectRoot, "config", "cache", "capital-state.json");
    }

    public static string ManualFarmingPreferencePath(string projectRoot, string accountName)
    {
        return Path.Combine(AccountDirectory(projectRoot, accountName), "cache", "manual-farming.json");
    }

    public static string HeroAttributeSnapshotPath(string projectRoot, string accountName, string? serverUrl = null)
    {
        return Path.Combine(
            AccountDirectory(projectRoot, accountName),
            "cache",
            "hero-attributes",
            $"{NormalizeServerKey(serverUrl)}.json");
    }

    public static string LegacyManualFarmingPreferencePath(string projectRoot)
    {
        return Path.Combine(projectRoot, "config", "cache", "manual-farming-preferences.json");
    }

    public static string BuildingsSnapshotPath(string projectRoot, string accountName)
    {
        return Path.Combine(projectRoot, "temp_build_out", "buildings-snapshots", $"{NormalizeAccountKey(accountName)}.json");
    }

    public static string FarmListsSnapshotPath(string projectRoot, string accountName)
    {
        return Path.Combine(projectRoot, "temp_build_out", "farmlist-snapshots", $"{NormalizeAccountKey(accountName)}.json");
    }
}
