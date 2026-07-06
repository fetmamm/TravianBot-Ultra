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
    public async Task<IReadOnlyList<TroopTrainingQueueStatus>> ReadTroopTrainingQueuesAsync(
        BotOptions options,
        Action<string> log,
        IReadOnlyList<Building>? knownBuildings = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TroopTrainingQueueStatus> statuses = [];
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
                statuses = await client.ReadTroopTrainingQueuesAsync(knownBuildings, cancellationToken);
            });

        return statuses;
    }

    public async Task<BreweryCelebrationStatus> ReadBreweryCelebrationStatusAsync(
        BotOptions options,
        Action<string> log,
        IReadOnlyList<Building>? knownBuildings = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        BreweryCelebrationStatus? status = null;
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
                status = await client.ReadBreweryCelebrationStatusAsync(knownBuildings, cancellationToken);
            });

        return status ?? new BreweryCelebrationStatus(false, null, false, null, false, null, "N/A", "Status unavailable.");
    }

    public async Task<SmithyUpgradeStatus> ReadSmithyUpgradeStatusAsync(
        BotOptions options,
        Action<string> log,
        IReadOnlyList<Building>? knownBuildings = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        SmithyUpgradeStatus? status = null;
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
                status = await client.ReadSmithyUpgradeStatusAsync(knownBuildings, cancellationToken);
            });

        return status ?? new SmithyUpgradeStatus(false, null, 0, null, [], "N/A", "Status unavailable.");
    }

    public async Task<string> RunNpcTradeForBuildingTestAsync(
        BotOptions options,
        Action<string> log,
        TbotUltra.Core.Travian.TroopTrainingBuildingType buildingType,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var result = "NPC trade test: no result.";
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
                result = await client.RunNpcTradeForBuildingTestAsync(buildingType, cancellationToken);
            });

        log(result);
        return result;
    }

    public async Task<string> RunNpcTradeForCurrentBuildingPageTestAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var result = "NPC trade building test: no result.";
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                result = await client.RunNpcTradeForCurrentBuildingPageTestAsync(cancellationToken);
            });

        log(result);
        return result;
    }

    public async Task<string> RunIncreaseAdventuresToHardAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var result = "Increase adventure danger: no result.";
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await client.IncreaseAdventuresToHardAsync(cancellationToken);
            });

        log(result);
        return result;
    }

    public async Task<string> RunReduceAdventuresTimeAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var result = "Reduce adventure time: no result.";
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await client.ReduceAdventuresTimeAsync(cancellationToken);
            });

        log(result);
        return result;
    }

    // Manual read-only scan of the Advantages timers, runnable on its own session even when the loop is
    // idle/paused (used by the popup's "Scan timers" button).
    public async Task<string> RunScanProductionBonusTimersAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var result = "Production bonus scan: no result.";
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await client.ScanProductionBonusTimersAsync(cancellationToken);
            });

        log(result);
        return result;
    }

    public async Task<string> ReadSmithyQueueFromCurrentPageTestAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var result = "Smithy queue test: no result.";
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                result = await client.ReadSmithyQueueFromCurrentPageTestAsync(cancellationToken);
            });

        log(result);
        return result;
    }

    public async Task<string> RunReinforcementsTestAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var result = "Reinforcements test: no result.";
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await client.TestSendReinforcementsBetweenOwnVillagesAsync(cancellationToken);
            });

        log(result);
        return result;
    }

    public async Task<string> RunBreweryCelebrationAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var result = "Brewery celebration: status unavailable.";
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
                result = await client.RunBreweryCelebrationAsync(cancellationToken);
            });

        return result;
    }

    public async Task<string> RunTownHallCelebrationAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var result = "Town Hall celebration: status unavailable.";
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
                result = await client.RunTownHallCelebrationAsync(options.TownHallCelebrationMode, cancellationToken);
            });

        return result;
    }

    public async Task<InboxStatus> ReadInboxStatusAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        InboxStatus? status = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                log($"Reading inbox status for server {options.ServerName}.");
                status = await client.ReadInboxStatusAsync(cancellationToken);
            });

        return status ?? new InboxStatus();
    }

    public async Task MarkMessagesAsReadAsync(
        BotOptions options,
        Action<string> log,
        string? villageName = null,
        string? villageUrl = null,
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
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, villageName, villageUrl);
                var changed = await client.MarkMessagesAsReadAsync(cancellationToken);
                log(changed ? "[inbox] messages marked as read." : "[inbox] no unread messages to mark as read.");
            });
    }

    public async Task MarkReportsAsReadAsync(
        BotOptions options,
        Action<string> log,
        string? villageName = null,
        string? villageUrl = null,
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
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, villageName, villageUrl);
                var changed = await client.MarkReportsAsReadAsync(cancellationToken);
                log(changed ? "[inbox] reports marked as read." : "[inbox] no unread reports to mark as read.");
            });
    }

}