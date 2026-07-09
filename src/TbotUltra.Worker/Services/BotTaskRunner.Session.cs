using TbotUltra.Core.Configuration;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Infrastructure;
using TbotUltra.Worker.Services.Automation;
using Microsoft.Playwright;
using System.Text.Json;

namespace TbotUltra.Worker.Services;

public sealed partial class BotTaskRunner
{
    public async Task<bool> IsLoggedInAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var isLoggedIn = false;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                isLoggedIn = await client.CheckLoggedInAsync(cancellationToken);
            });

        return isLoggedIn;
    }

    public async Task ExecuteLoginAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default,
        bool keepBrowserOpenAfterLogin = false)
    {
        _ = keepBrowserOpenAfterLogin;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                log($"Starting login for server {options.ServerName}.");
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, skipFeatureRefresh: true);
                log("Login completed and browser session saved. Browser stays open.");
            });
    }

    public async Task<string?> ReadCurrentLanguageAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        string? language = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                language = await client.ReadCurrentLanguageAsync(cancellationToken);
                if (!string.Equals(language?.Trim(), "en-US", StringComparison.OrdinalIgnoreCase))
                {
                    log($"[language] current Travian language: {language ?? "unknown"}.");
                }
            });

        return language;
    }

    public async Task EnsureExpectedLanguageAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                await client.EnsureExpectedLanguageAsync(cancellationToken);
            });
    }

    public async Task<string?> SetLanguageToEnglishAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        string? language = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                language = await client.SetLanguageToEnglishAsync(cancellationToken);
                log("[language] Travian language set to English.");
            });

        return language;
    }

    public async Task<PostLoginSnapshot> ExecuteLoginAndLoadPostLoginSnapshotAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default,
        bool keepBrowserOpenAfterLogin = false)
    {
        _ = keepBrowserOpenAfterLogin;
        PostLoginSnapshot? snapshot = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                log($"Starting login for server {options.ServerName}.");
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, skipFeatureRefresh: true);
                log("Login completed and browser session saved. Browser stays open.");

                snapshot = await LoadPostLoginSnapshotAsync(client, options, log, cancellationToken);
            });

        return snapshot ?? throw new InvalidOperationException("Could not load post-login snapshot.");
    }

    public async Task<PostLoginSnapshot> LoadPostLoginSnapshotAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        PostLoginSnapshot? snapshot = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, skipFeatureRefresh: true);
                snapshot = await LoadPostLoginSnapshotAsync(client, options, log, cancellationToken);
            });

        return snapshot ?? throw new InvalidOperationException("Could not load post-login snapshot.");
    }

    private async Task<PostLoginSnapshot> LoadPostLoginSnapshotAsync(
        TravianClient client,
        BotOptions options,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        log($"Loading post-login data for server {options.ServerName}.");

        // When enabled, read the hero inventory FIRST — right after login and before the profile
        // navigation (ReadAccountSnapshotAsync reads villages from spieler.php/profile).
        HeroInventoryResources? heroInventory = null;
        if (options.PostLoginAnalyzeHeroInventory)
        {
            // Suppress the village/profile UI-sync so the inventory is read before the profile nav.
            // Non-fatal: a transient nav timeout here must NOT abort the whole login (it once left the bot
            // parked idle overnight). Continue with heroInventory=null so skipOverviewNavigation stays false
            // and ReadAccountSnapshotAsync does its normal dorf1 hop; the rest of the snapshot proceeds.
            try
            {
                heroInventory = await client.ReadHeroInventoryResourcesAsync(cancellationToken, suppressUiSync: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log($"[hero-inventory] post-login read failed (continuing without it): {ex.Message}");
            }
        }

        var accountSnapshot = await client.ReadAccountSnapshotAsync(
            forceRefreshVillages: true,
            preferCurrentPageVillages: false,
            restorePageAfterProfile: false,
            suppressEnsureUiSync: true,
            // We just read the hero inventory and will refresh villages from the profile next —
            // skip the redundant dorf1 hop in that case.
            skipOverviewNavigation: heroInventory is not null,
            cancellationToken);

        var buildingStatus = await client.ReadBuildingsStatusAsync(cancellationToken);
        var villageStatus = await client.ReadVillageStatusAsync(
            accountSnapshot.Villages,
            buildingStatus.Buildings,
            cancellationToken);

        if (options.PostLoginReadTroopTrainingQueue)
        {
            var troopQueues = await client.ReadTroopTrainingQueuesAsync(villageStatus.Buildings, cancellationToken);
            villageStatus = villageStatus with { TroopTrainingQueues = troopQueues };
        }

        var inboxStatus = new InboxStatus(villageStatus.UnreadMessages, villageStatus.UnreadReports);
        var adventureCount = await client.RefreshAdventureCountAsync(forceReload: false, cancellationToken);

        PersistStableAccountSignals(client, accountSnapshot.Tribe, log);

        return new PostLoginSnapshot(villageStatus, inboxStatus, adventureCount, heroInventory);
    }

    private void PersistStableAccountSignals(
        TravianClient client,
        string? fallbackTribe,
        Action<string> log)
    {
        _accountAnalysisStore.TryLoad(client.AccountName, out var existing, client.ServerUrl);

        var tribe = IsKnownTribe(client.KnownTribe)
            ? client.KnownTribe!
            : IsKnownTribe(fallbackTribe)
                ? fallbackTribe!
                : existing?.Tribe ?? "Unknown";
        var goldClubEnabled = client.KnownGoldClubEnabled == true || existing?.GoldClubEnabled == true;

        if (!IsKnownTribe(tribe) && !goldClubEnabled)
        {
            return;
        }

        var completed = new AccountAnalysisSnapshot(
            SchemaVersion: AccountAnalysisConstants.CurrentSchemaVersion,
            AnalyzedAtUtc: DateTimeOffset.UtcNow,
            AccountName: client.AccountName,
            ServerUrl: client.ServerUrl,
            Tribe: IsKnownTribe(tribe) ? tribe : "Unknown",
            GoldClubEnabled: goldClubEnabled,
            BuildingCatalog: existing?.BuildingCatalog ?? (IsKnownTribe(tribe) ? BuildingCatalogService.GetCatalogForTribe(tribe) : []),
            AutoCelebrationEnabled: existing?.AutoCelebrationEnabled,
            AutomationLoopEnabledGroups: existing?.AutomationLoopEnabledGroups,
            AutomationLoopVisibleGroups: existing?.AutomationLoopVisibleGroups);

        _accountAnalysisStore.Save(completed);
        log($"[cache] stable account signals saved for '{completed.AccountName}' (tribe={completed.Tribe}, goldclub={completed.GoldClubEnabled}).");
        // Emit the real-time signal the desktop UI parses (GoldClubStatusRegex) so the Gold Club
        // indicator flips at login instead of waiting for the next stored-analysis read (~1 min later).
        log($"[goldclub] active={goldClubEnabled}");
    }

    private static bool IsKnownTribe(string? tribe)
        => !string.IsNullOrWhiteSpace(tribe)
           && !string.Equals(tribe, "Unknown", StringComparison.OrdinalIgnoreCase);

    public async Task<bool> ReadAndPersistGoldClubStatusAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var account = _accountProvider.LoadAccount(accountName);
        _accountAnalysisStore.TryLoad(account.Name, out var existing, options.BaseUrl);
        var detectedGoldClubEnabled = false;
        var serverUrl = options.BaseUrl.TrimEnd('/');
        var tribe = existing?.Tribe ?? "Unknown";

        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                detectedGoldClubEnabled = await client.ReadGoldClubStatusAsync(cancellationToken);
                serverUrl = client.ServerUrl;
                if (string.IsNullOrWhiteSpace(tribe) || string.Equals(tribe, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    var snapshot = await client.ReadAccountSnapshotAsync(cancellationToken: cancellationToken);
                    tribe = snapshot.Tribe;
                }
            });

        var effectiveGoldClubEnabled = detectedGoldClubEnabled || (existing?.GoldClubEnabled ?? false);
        var completed = new AccountAnalysisSnapshot(
            SchemaVersion: AccountAnalysisConstants.CurrentSchemaVersion,
            AnalyzedAtUtc: DateTimeOffset.UtcNow,
            AccountName: account.Name,
            ServerUrl: serverUrl,
            Tribe: string.IsNullOrWhiteSpace(tribe) ? "Unknown" : tribe,
            GoldClubEnabled: effectiveGoldClubEnabled,
            BuildingCatalog: existing?.BuildingCatalog ?? [],
            AutoCelebrationEnabled: existing?.AutoCelebrationEnabled,
            AutomationLoopEnabledGroups: existing?.AutomationLoopEnabledGroups,
            AutomationLoopVisibleGroups: existing?.AutomationLoopVisibleGroups);

        _accountAnalysisStore.Save(completed);
        log($"Gold Club status saved for '{completed.AccountName}': {(completed.GoldClubEnabled ? "Yes" : "No")}.");
        return completed.GoldClubEnabled;
    }

    public async Task ExecuteLogoutAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                log($"Starting logout for server {options.ServerName}.");
                await client.LogoutAsync(cancellationToken);
                log("Logout completed.");
                // Drop all session-scoped cache (villages, population, plus/gold, logged-in state)
                // so a subsequent login on this shared browser starts from a clean slate and never
                // reuses the logged-out account's data.
                _sharedVisibleSessionCache = new TravianSessionCache();
            });
    }

}
