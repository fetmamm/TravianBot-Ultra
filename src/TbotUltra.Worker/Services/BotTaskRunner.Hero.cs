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
    public async Task<HeroAdventureDispatchResult> SendHeroOnAdventureAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        HeroAdventureDispatchResult? result = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await client.SendHeroOnAdventureAsync(cancellationToken);
                log(result.Message);
            });

        return result ?? throw new InvalidOperationException("Could not dispatch hero on adventure.");
    }

    public async Task<bool> CheckAndReviveDeadHeroAsync(
        BotOptions options,
        bool autoRevive,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var revived = false;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                revived = await client.CheckAndReviveDeadHeroOnCurrentPageAsync(autoRevive, cancellationToken);
            });

        return revived;
    }

    // Last adventure count echoed to the log, so "Adventures available: N" prints once and then only
    // again when the count changes (logging-only — the returned count is unaffected).
    private int? _lastLoggedAdventuresAvailable;

    public async Task<int?> RefreshAdventureCountAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        int? count = null;
        var found = false;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                count = await client.RefreshAdventureCountAsync(cancellationToken: cancellationToken);
                found = count is not null;
            });

        if (!found)
        {
            log("Adventures not found on current page.");
        }
        else if (count != _lastLoggedAdventuresAvailable)
        {
            _lastLoggedAdventuresAvailable = count;
            log($"Adventures available: {count}.");
        }

        return count;
    }

    public async Task<bool> HasHeroLevelUpIndicatorOnCurrentPageAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var found = false;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                found = await client.HasHeroLevelUpIndicatorOnCurrentPageAsync(cancellationToken);
            },
            saveStateMode: BrowserStateSaveMode.Skip);

        return found;
    }

    // Cheap current-page probe (no navigation) used by the periodic refresh to decide whether a
    // hero_manage deferred for the full revive time can be released early (e.g. user bucket-revived).
    // Returns false on any failure so it never disrupts the refresh.
    public async Task<bool> IsHeroRevivingOnCurrentPageAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var reviving = false;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                reviving = await client.IsHeroRevivingOnCurrentPageAsync(cancellationToken);
            },
            saveStateMode: BrowserStateSaveMode.Skip);

        return reviving;
    }

    public async Task<bool> IsHeroHomeOnCurrentPageAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var home = false;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                home = await client.IsHeroHomeOnCurrentPageAsync(cancellationToken);
            },
            saveStateMode: BrowserStateSaveMode.Skip);

        return home;
    }

    // Cheap current-page probe (no navigation) used by the periodic refresh to decide whether
    // to queue collect_tasks. Returns false on any failure so it never disrupts the refresh.
    public async Task<bool> HasClaimableTasksOnCurrentPageAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var claimable = false;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                claimable = await client.HasClaimableTasksOnCurrentPageAsync(cancellationToken);
            },
            saveStateMode: BrowserStateSaveMode.Skip);

        return claimable;
    }

    // Cheap current-page probe (no navigation) used by the periodic refresh to decide whether
    // to queue collect_daily_quests. Returns false on any failure so it never disrupts the refresh.
    public async Task<bool> HasClaimableDailyQuestsOnCurrentPageAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var claimable = false;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                claimable = await client.HasClaimableDailyQuestsOnCurrentPageAsync(cancellationToken);
            },
            saveStateMode: BrowserStateSaveMode.Skip);

        return claimable;
    }

    public async Task<HeroAttributeSnapshot> ReadHeroAttributesAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        HeroAttributeSnapshot? snapshot = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                snapshot = await client.ReadHeroAttributeSnapshotAsync(cancellationToken);
                log(
                    $"Hero attributes: free points={snapshot.FreePoints}, fighting strength={snapshot.FightingStrength}, offence bonus={snapshot.OffenceBonus}, defence bonus={snapshot.DefenceBonus}, resources={snapshot.Resources}, adventures={(snapshot.AdventureCount?.ToString() ?? "?")}.");
            });

        return snapshot ?? throw new InvalidOperationException("Could not read hero attributes.");
    }

    public async Task<HeroInventoryResources> ReadHeroInventoryResourcesAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        HeroInventoryResources? resources = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                resources = await client.ReadHeroInventoryResourcesAsync(cancellationToken);
                log($"Hero inventory: wood={resources.Wood}, clay={resources.Clay}, iron={resources.Iron}, crop={resources.Crop}.");
            });

        return resources ?? throw new InvalidOperationException("Could not read hero inventory resources.");
    }

}
