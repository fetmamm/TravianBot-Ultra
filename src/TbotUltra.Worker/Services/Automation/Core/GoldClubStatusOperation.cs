using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services.Automation;

/// <summary>Owns the Gold Club read and stable account-analysis update after login.</summary>
internal sealed class GoldClubStatusOperation(IGoldClubStatusClient client, AccountAnalysisStore accountAnalysisStore)
{
    public async Task<bool> ReadAndPersistAsync(
        AccountOptions account,
        BotOptions options,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        accountAnalysisStore.TryLoad(account.Name, out var existing, options.BaseUrl);
        if (existing?.GoldClubEnabled == true)
        {
            return true;
        }

        var detectedGoldClubEnabled = await client.ReadGoldClubStatusAsync(cancellationToken);
        if (!detectedGoldClubEnabled)
        {
            return false;
        }

        var tribe = existing?.Tribe ?? "Unknown";
        if (string.IsNullOrWhiteSpace(tribe) || string.Equals(tribe, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = await client.ReadAccountSnapshotAsync(cancellationToken: cancellationToken);
            tribe = snapshot.Tribe;
        }

        var completed = accountAnalysisStore.Update(account.Name, client.ServerUrl, latest => new AccountAnalysisSnapshot(
            SchemaVersion: AccountAnalysisConstants.CurrentSchemaVersion,
            AnalyzedAtUtc: DateTimeOffset.UtcNow,
            AccountName: account.Name,
            ServerUrl: client.ServerUrl,
            Tribe: string.IsNullOrWhiteSpace(tribe) ? latest?.Tribe ?? "Unknown" : tribe,
            GoldClubEnabled: true,
            BuildingCatalog: latest?.BuildingCatalog ?? existing?.BuildingCatalog ?? [],
            AutoCelebrationEnabled: latest?.AutoCelebrationEnabled ?? existing?.AutoCelebrationEnabled,
            AutomationLoopEnabledGroups: latest?.AutomationLoopEnabledGroups ?? existing?.AutomationLoopEnabledGroups,
            AutomationLoopVisibleGroups: latest?.AutomationLoopVisibleGroups ?? existing?.AutomationLoopVisibleGroups,
            WorldUid: latest?.WorldUid ?? existing?.WorldUid,
            Villages: latest?.Villages ?? existing?.Villages,
            NewAccountAnalysisCompleted: latest?.NewAccountAnalysisCompleted ?? existing?.NewAccountAnalysisCompleted))!;
        log($"Gold Club activated and saved for '{completed.AccountName}'.");
        return true;
    }
}
