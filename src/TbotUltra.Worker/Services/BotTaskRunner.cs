using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Worker.Services;

public sealed class BotTaskRunner
{
    private readonly IAccountProvider _accountProvider;
    private readonly ProjectContext _projectContext;

    public BotTaskRunner(IAccountProvider accountProvider, ProjectContext projectContext)
    {
        _accountProvider = accountProvider;
        _projectContext = projectContext;
    }

    public async Task ExecuteOnceAsync(
        BotOptions options,
        Action<string> log,
        IEnumerable<string>? tasksOverride = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var account = _accountProvider.LoadAccount(accountName);
        var tasks = tasksOverride?.ToList() ?? (options.LoopTasks is { Count: > 0 } configuredTasks ? configuredTasks : ["status"]);

        log($"Starting tick for server {options.ServerName} with account {account.Name}.");
        log($"Tasks: {string.Join(",", tasks)}");

        await using var browserSession = new BrowserSession(options, account, _projectContext.RootPath);
        var page = await browserSession.OpenPageAsync(cancellationToken);
        var client = new TravianClient(
            page,
            options,
            account,
            interactive: false,
            browserVisible: !options.Headless,
            statusCallback: log);

        await client.LoginAsync(cancellationToken);
        foreach (var taskName in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.Equals(taskName, "status", StringComparison.OrdinalIgnoreCase))
            {
                var status = await client.ReadVillageStatusAsync(cancellationToken);
                log($"Village status read. ActiveVillage={status.ActiveVillage}, Villages={status.Villages.Count}, Resources={status.Resources.Count}, ResourceFields={status.ResourceFields.Count}, Buildings={status.Buildings.Count}, Queue={status.BuildQueue.Count}");
                continue;
            }

            if (string.Equals(taskName, "scan_all_villages", StringComparison.OrdinalIgnoreCase))
            {
                var statuses = await client.ReadAllVillageStatusesAsync(cancellationToken);
                log($"All villages scanned. StatusCount={statuses.Count}");
                continue;
            }

            if (string.Equals(taskName, "account_snapshot", StringComparison.OrdinalIgnoreCase))
            {
                var snapshot = await client.ReadAccountSnapshotAsync(cancellationToken);
                log($"Account snapshot read. Tribe={snapshot.Tribe}, ActiveVillage={snapshot.ActiveVillage}, VillageCount={snapshot.VillageCount}, ServerTimeUtc={snapshot.ServerTimeUtc}");
                continue;
            }

            if (string.Equals(taskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase))
            {
                if (options.ResourceUpgradeSlotId is null || options.ResourceUpgradeTargetLevel is null)
                {
                    log($"Task '{taskName}' requires config values resource_upgrade_slot_id and resource_upgrade_target_level.");
                    continue;
                }

                var result = await client.UpgradeResourceToLevelAsync(
                    options.ResourceUpgradeSlotId.Value,
                    options.ResourceUpgradeTargetLevel.Value,
                    cancellationToken);
                log(result);
                continue;
            }

            if (string.Equals(taskName, "upgrade_resource_to_max", StringComparison.OrdinalIgnoreCase))
            {
                if (options.ResourceUpgradeSlotId is null)
                {
                    log($"Task '{taskName}' requires config value resource_upgrade_slot_id.");
                    continue;
                }

                var result = await client.UpgradeResourceToMaxAsync(
                    options.ResourceUpgradeSlotId.Value,
                    options.ResourceUpgradeMaxAttempts,
                    cancellationToken);
                log(result);
                continue;
            }

            if (string.Equals(taskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase))
            {
                if (options.ResourceUpgradeTargetLevel is null)
                {
                    log($"Task '{taskName}' requires config value resource_upgrade_target_level.");
                    continue;
                }

                var result = await client.UpgradeAllResourcesToLevelAsync(
                    options.ResourceUpgradeTargetLevel.Value,
                    cancellationToken);
                log(result);
                continue;
            }

            if (string.Equals(taskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase))
            {
                if (options.BuildingUpgradeSlotId is null || options.BuildingUpgradeTargetLevel is null)
                {
                    log($"Task '{taskName}' requires config values building_upgrade_slot_id and building_upgrade_target_level.");
                    continue;
                }

                var result = await client.UpgradeBuildingToLevelAsync(
                    options.BuildingUpgradeSlotId.Value,
                    options.BuildingUpgradeTargetLevel.Value,
                    cancellationToken);
                log(result);
                continue;
            }

            if (string.Equals(taskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
            {
                if (options.BuildingUpgradeSlotId is null)
                {
                    log($"Task '{taskName}' requires config value building_upgrade_slot_id.");
                    continue;
                }

                var result = await client.UpgradeBuildingToMaxAsync(
                    options.BuildingUpgradeSlotId.Value,
                    options.BuildingUpgradeMaxAttempts,
                    cancellationToken);
                log(result);
                continue;
            }

            if (string.Equals(taskName, "construct_building", StringComparison.OrdinalIgnoreCase))
            {
                if (options.BuildingConstructSlotId is null || options.BuildingConstructGid is null)
                {
                    log($"Task '{taskName}' requires config values building_construct_slot_id and building_construct_gid.");
                    continue;
                }

                var buildingName = string.IsNullOrWhiteSpace(options.BuildingConstructName)
                    ? $"gid {options.BuildingConstructGid.Value}"
                    : options.BuildingConstructName;

                var result = await client.ConstructBuildingAsync(
                    options.BuildingConstructSlotId.Value,
                    options.BuildingConstructGid.Value,
                    buildingName,
                    cancellationToken);
                log(result);
                continue;
            }

            log($"Task '{taskName}' is not migrated in C# yet.");
        }

        await browserSession.SaveStateAsync();
    }

    public async Task ExecuteLoginAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var account = _accountProvider.LoadAccount(accountName);
        log($"Starting login for server {options.ServerName} with account {account.Name}.");

        await using var browserSession = new BrowserSession(options, account, _projectContext.RootPath);
        var page = await browserSession.OpenPageAsync(cancellationToken);
        var client = new TravianClient(
            page,
            options,
            account,
            interactive: true,
            browserVisible: !options.Headless,
            statusCallback: log);

        await client.LoginAsync(cancellationToken);
        await browserSession.SaveStateAsync();
        log("Login completed and browser session saved.");
    }
}
