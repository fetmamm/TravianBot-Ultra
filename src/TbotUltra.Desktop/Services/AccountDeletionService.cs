using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TbotUltra.Core.Accounts;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

public sealed class AccountDeletionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _projectRoot;
    private readonly EnvAccountStore _accountStore;
    private readonly BotConfigStore _botConfigStore;
    private readonly JsonQueueStore _queueStore;

    public AccountDeletionService(
        string projectRoot,
        EnvAccountStore accountStore,
        BotConfigStore botConfigStore,
        JsonQueueStore queueStore)
    {
        _projectRoot = projectRoot;
        _accountStore = accountStore;
        _botConfigStore = botConfigStore;
        _queueStore = queueStore;
    }

    public void DeleteAccount(string accountName, bool deleteAnyway = false)
    {
        var activeQueueItemCount = CountActiveQueueItemsBlockingDeletion(accountName);
        if (activeQueueItemCount > 0)
        {
            if (!deleteAnyway)
            {
                throw new InvalidOperationException(
                    $"Cannot delete the active account while {activeQueueItemCount} queue item(s) are pending, running, or paused. Clear or finish that account's queue first.");
            }

            _queueStore.Clear();
        }

        PruneAccountFromBotConfig(accountName);
        _accountStore.DeleteAccount(accountName);
        DeleteAccountArtifacts(accountName);
    }

    public int CountActiveQueueItemsBlockingDeletion(string accountName)
    {
        if (!string.Equals(
                AccountStoragePaths.NormalizeAccountKey(accountName),
                AccountStoragePaths.NormalizeAccountKey(_accountStore.ActiveAccountName()),
                StringComparison.Ordinal))
        {
            return 0;
        }

        return _queueStore.GetAll().Count(item =>
            item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused);
    }

    private void PruneAccountFromBotConfig(string accountName)
    {
        _botConfigStore.RemoveLegacyReinforcementRulesForAccount(accountName);
    }

    private void DeleteAccountArtifacts(string accountName)
    {
        var accountKey = AccountStoragePaths.NormalizeAccountKey(accountName);

        DeleteFileIfExists(AccountStoragePaths.BrowserStatePath(_projectRoot, accountName));
        DeleteFileIfExists(AccountStoragePaths.LegacyBrowserStatePath(_projectRoot, accountName));
        DeleteFileIfExists(AccountStoragePaths.BuildingsSnapshotPath(_projectRoot, accountName));
        DeleteFileIfExists(AccountStoragePaths.FarmListsSnapshotPath(_projectRoot, accountName));

        DeleteLegacyAnalysisFiles(accountKey);
        RemoveAccountFromLegacyManualFarmingPreferences(accountKey);
        RemoveAccountFromLegacyCapitalState(accountKey);

        var accountDirectory = AccountStoragePaths.AccountDirectory(_projectRoot, accountName);
        if (Directory.Exists(accountDirectory))
        {
            Directory.Delete(accountDirectory, recursive: true);
        }
    }

    private void DeleteLegacyAnalysisFiles(string accountKey)
    {
        var directory = Path.Combine(_projectRoot, AccountAnalysisConstants.RelativeDirectory);
        DeleteLegacyAccountFiles(directory, accountKey);
    }

    private static void DeleteLegacyAccountFiles(string directory, string accountKey)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (string.Equals(fileName, accountKey, StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith($"{accountKey}__", StringComparison.OrdinalIgnoreCase))
            {
                DeleteFileIfExists(filePath);
            }
        }
    }

    private void RemoveAccountFromLegacyManualFarmingPreferences(string accountKey)
    {
        var path = AccountStoragePaths.LegacyManualFarmingPreferencePath(_projectRoot);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var raw = File.ReadAllText(path);
            var all = string.IsNullOrWhiteSpace(raw)
                ? new Dictionary<string, ManualFarmingPreference>(StringComparer.OrdinalIgnoreCase)
                : JsonSerializer.Deserialize<Dictionary<string, ManualFarmingPreference>>(raw, JsonOptions)
                    ?? new Dictionary<string, ManualFarmingPreference>(StringComparer.OrdinalIgnoreCase);
            if (!all.Remove(accountKey))
            {
                return;
            }

            if (all.Count == 0)
            {
                File.Delete(path);
                return;
            }

            File.WriteAllText(path, JsonSerializer.Serialize(all, JsonOptions));
        }
        catch
        {
            // Corrupt legacy cache should not block account deletion.
        }
    }

    private void RemoveAccountFromLegacyCapitalState(string accountKey)
    {
        var path = AccountStoragePaths.LegacyCapitalStatePath(_projectRoot);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var raw = File.ReadAllText(path);
            var document = string.IsNullOrWhiteSpace(raw)
                ? null
                : JsonNode.Parse(raw)?.AsObject();
            if (document?["entries"] is not JsonArray entries)
            {
                return;
            }

            var kept = entries
                .OfType<JsonObject>()
                .Where(entry =>
                {
                    var entryAccount = entry["accountName"]?.GetValue<string>() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(entryAccount))
                    {
                        return true;
                    }

                    return !string.Equals(
                        AccountStoragePaths.NormalizeAccountKey(entryAccount),
                        accountKey,
                        StringComparison.Ordinal);
                })
                .Select(entry => entry.DeepClone())
                .ToArray();

            if (kept.Length == entries.Count)
            {
                return;
            }

            if (kept.Length == 0)
            {
                File.Delete(path);
                return;
            }

            document["entries"] = new JsonArray(kept);
            File.WriteAllText(path, document.ToJsonString(JsonOptions));
        }
        catch
        {
            // Corrupt legacy cache should not block account deletion.
        }
    }

    private static void DeleteFileIfExists(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
}
