using TbotUltra.Core.Configuration;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Infrastructure;
using TbotUltra.Worker.Services.Automation;
using Microsoft.Playwright;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TbotUltra.Worker.Services;

public sealed partial class BotTaskRunner
{
    public async Task<CapitalProfileCheckResult> CheckCapitalFromProfileAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        using var priorityRequest = _priorityBrowserWork.EnterPriorityRequest();
        CapitalProfileCheckResult? result = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await new CapitalProfileOperation(client).CheckAsync(cancellationToken);
            });

        return result ?? throw new InvalidOperationException("The player profile returned no capital village.");
    }

    public async Task SetVerifiedCapitalStateAsync(
        BotOptions options,
        CapitalProfileCheckResult capital,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        using var priorityRequest = _priorityBrowserWork.EnterPriorityRequest();
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            client => new CapitalProfileOperation(client).SetVerifiedStateAsync(capital, cancellationToken));
    }

    private static FarmListLossHandlingRequest CreateFarmListLossHandlingRequest(
        BotOptions options,
        FarmListLossColors lossColor,
        int? maxTargets = null)
    {
        var isRed = lossColor == FarmListLossColors.Red;
        var deactivateLosses = isRed
            ? options.ContinuousFarmDeactivateRedLosses
            : options.ContinuousFarmDeactivateYellowLosses;
        var includeOasis = isRed
            ? options.ContinuousFarmDeactivateRedOasisLosses
            : options.ContinuousFarmDeactivateYellowOasisLosses;
        var configuredMove = isRed
            ? options.ContinuousFarmMoveRedLosses
            : options.ContinuousFarmMoveYellowLosses;
        var destinationListId = isRed
            ? options.ContinuousFarmRedLossDestinationListId
            : options.ContinuousFarmYellowLossDestinationListId;
        var destinationListName = isRed
            ? options.ContinuousFarmRedLossDestinationListName
            : options.ContinuousFarmYellowLossDestinationListName;
        var destinationBaseName = isRed
            ? options.ContinuousFarmRedLossDestinationBaseName
            : options.ContinuousFarmYellowLossDestinationBaseName;
        var moveEnabled = deactivateLosses && configuredMove && !string.IsNullOrWhiteSpace(destinationListName);
        var villageIdMatch = Regex.Match(
            options.TargetVillageUrl ?? string.Empty,
            @"[?&]newdid=(\d+)",
            RegexOptions.IgnoreCase);
        var createTemplate = string.IsNullOrWhiteSpace(options.TargetVillageName)
            ? null
            : new FarmListCreateRequest(
                [destinationListName],
                options.TargetVillageName,
                villageIdMatch.Success ? villageIdMatch.Groups[1].Value : null,
                "First available troop",
                1,
                TroopIndexOverride: 1,
                OnlyCreateReportsWithLosses: options.FarmListOnlyCreateReportsWithLosses);

        return new FarmListLossHandlingRequest(
            includeOasis,
            moveEnabled,
            destinationListId,
            destinationListName,
            string.IsNullOrWhiteSpace(destinationBaseName) ? destinationListName : destinationBaseName,
            createTemplate,
            maxTargets,
            LossColors: lossColor,
            IncludeNonOasisLosses: deactivateLosses);
    }

    private static IReadOnlyList<FarmListLossHandlingRequest> CreateFarmListLossHandlingRequests(BotOptions options)
    {
        var requests = new List<FarmListLossHandlingRequest>(2);
        if (options.ContinuousFarmDeactivateRedLosses || options.ContinuousFarmDeactivateRedOasisLosses)
            requests.Add(CreateFarmListLossHandlingRequest(options, FarmListLossColors.Red));
        if (options.ContinuousFarmDeactivateYellowLosses || options.ContinuousFarmDeactivateYellowOasisLosses)
            requests.Add(CreateFarmListLossHandlingRequest(options, FarmListLossColors.Yellow));
        return requests;
    }

    // Manual farming actions still use this path. The queued continuous-farming
    // task owns the same decision through ContinuousFarmingOperation.
    private static async Task RunFarmListLossDeactivationIfEnabledAsync(TaskExecutionContext context)
    {
        var requests = CreateFarmListLossHandlingRequests(context.Options);
        if (requests.Count == 0)
        {
            context.Log("Continuous farming loss deactivation disabled.");
            return;
        }

        foreach (var request in requests)
        {
            var result = await new ManualFarmingOperation(context.Client).HandleLossTargetsAsync(request, context.CancellationToken);
            context.Log($"Continuous farming {request.LossColors.ToString().ToLowerInvariant()} loss handling result: found={result.RowsFound}, deactivated={result.RowsDeactivated}, moved={result.RowsMoved}, moveFailures={result.MoveFailures}, skippedOasis={result.SkippedOasisRows}.");
            context.Runner.PublishFarmLossDestinationChange(context.Options, result);
        }
    }

    private void PublishFarmLossDestinationChange(BotOptions options, FarmListLossDeactivationResult result)
    {
        if (!result.DestinationChanged
            || string.IsNullOrWhiteSpace(result.DestinationListId)
            || string.IsNullOrWhiteSpace(result.DestinationListName))
        {
            return;
        }

        var isRed = result.LossColors == FarmListLossColors.Red;
        var baseName = isRed
            ? options.ContinuousFarmRedLossDestinationBaseName
            : options.ContinuousFarmYellowLossDestinationBaseName;
        var configuredName = isRed
            ? options.ContinuousFarmRedLossDestinationListName
            : options.ContinuousFarmYellowLossDestinationListName;
        RaiseFarmLossDestinationChanged(new FarmLossDestinationChange(
            _accountProvider.LoadAccount().Name,
            result.DestinationListId,
            result.DestinationListName,
            string.IsNullOrWhiteSpace(baseName) ? configuredName : baseName,
            options.TargetVillageName,
            result.LossColors));
    }

    public async Task<FarmListLossDeactivationResult> RunFarmLossMoveDebugAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var requests = CreateFarmListLossHandlingRequests(options)
            .Where(request => request.MoveLosses)
            .Select(request => request with { IncludeUnoccupiedOasis = false })
            .ToList();
        if (requests.Count == 0)
        {
            throw new InvalidOperationException(
                "Enable red or yellow loss deactivation and its matching move option, then select a destination list first.");
        }

        var results = new List<FarmListLossDeactivationResult>();
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken);
                foreach (var request in requests)
                {
                    log($"[farm-list:debug] running {request.LossColors.ToString().ToLowerInvariant()} farm move/deactivate to '{request.DestinationListName}'.");
                    results.Add(await new ManualFarmingOperation(client).HandleLossTargetsAsync(request, cancellationToken));
                }
            });

        if (results.Count == 0)
            throw new InvalidOperationException("The selected farm-loss move returned no result.");
        foreach (var result in results)
            PublishFarmLossDestinationChange(options, result);
        return new FarmListLossDeactivationResult(
            results.Sum(result => result.RowsFound),
            results.Sum(result => result.RowsDeactivated),
            results.Sum(result => result.SkippedOasisRows),
            results.Sum(result => result.RowsMoved),
            results.Sum(result => result.MoveFailures));
    }

    public async Task<IReadOnlyList<FarmListOverview>> ReadFarmListsOverviewAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FarmListOverview> overview = [];
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                overview = await new ManualFarmingOperation(client).ReadOverviewAsync(cancellationToken);
            });

        return overview;
    }

    public async Task<int?> SendFarmListNowAsync(
        BotOptions options,
        string farmListName,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        int? remainingSeconds = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                remainingSeconds = await new ManualFarmingOperation(client).SendOneAsync(farmListName, cancellationToken);
            });

        return remainingSeconds;
    }

    public async Task<int> SendAllFarmListsNowAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var listCount = 0;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                await RunFarmListLossDeactivationIfEnabledAsync(new TaskExecutionContext(this, options, client, log, cancellationToken, _ => { }));
                listCount = await new ManualFarmingOperation(client).SendAllAsync(cancellationToken);
            });

        return listCount;
    }

    public async Task<int> SendSelectedFarmListsNowAsync(
        BotOptions options,
        IReadOnlyCollection<string> selectedNames,
        IReadOnlyCollection<string> selectedIds,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var sent = 0;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                await RunFarmListLossDeactivationIfEnabledAsync(new TaskExecutionContext(this, options, client, log, cancellationToken, _ => { }));
                sent = await new ManualFarmingOperation(client).SendSelectedAsync(selectedNames, selectedIds, cancellationToken);
            });

        return sent;
    }

    public async Task<int> SendAllFarmListsViaStartAllButtonAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        var listCount = 0;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                await RunFarmListLossDeactivationIfEnabledAsync(new TaskExecutionContext(this, options, client, log, cancellationToken, _ => { }));
                listCount = await new ManualFarmingOperation(client).SendAllViaStartAllButtonAsync(cancellationToken);
            });

        return listCount;
    }

    public async Task<FarmAddBatchResult> AddFarmsFromCoordinatesAsync(
        BotOptions options,
        string farmListName,
        string troopType,
        int troopCount,
        int requestedCount,
        IReadOnlyList<FarmCoordinate> coordinates,
        bool useDefaultTroops,
        Action<string> log,
        string? accountName = null,
        IProgress<FarmAddProgress>? progress = null,
        FarmTargetProtectionContext? protection = null,
        CancellationToken cancellationToken = default)
    {
        FarmAddBatchResult? result = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await new ManualFarmingOperation(client).AddFarmsAsync(
                    farmListName,
                    troopType,
                    troopCount,
                    requestedCount,
                    coordinates,
                    useDefaultTroops,
                    progress,
                    protection,
                    cancellationToken);
            });

        return result ?? throw new InvalidOperationException("Could not add farms from Travco list.");
    }

    public async Task<FarmTargetIdentity> ReadFarmTargetProtectionIdentityAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        FarmTargetIdentity? identity = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                identity = await client.ReadCurrentFarmTargetIdentityAsync(cancellationToken);
            });

        return identity ?? new FarmTargetIdentity(false, null, null);
    }

    public async Task<FarmListCreateBatchResult> CreateFarmListsAsync(
        BotOptions options,
        FarmListCreateRequest request,
        Action<string> log,
        string? accountName = null,
        IProgress<FarmListCreateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var priorityRequest = _priorityBrowserWork.EnterPriorityRequest();
        FarmListCreateBatchResult? result = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await new ManualFarmingOperation(client).CreateListsAsync(request, progress, cancellationToken);
            });

        return result ?? throw new InvalidOperationException("Could not create farm lists.");
    }

}
