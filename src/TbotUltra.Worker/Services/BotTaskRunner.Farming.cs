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
                overview = await client.ReadFarmListsOverviewAsync(cancellationToken);
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
                remainingSeconds = await client.SendFarmListNowAsync(farmListName, cancellationToken);
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
                listCount = await client.SendAllFarmListsNowAsync(cancellationToken);
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
                result = await client.AddFarmsFromCoordinatesAsync(
                    farmListName,
                    troopType,
                    troopCount,
                    requestedCount,
                    coordinates,
                    useDefaultTroops,
                    progress,
                    cancellationToken);
            });

        return result ?? throw new InvalidOperationException("Could not add farms from Travco list.");
    }

    public async Task<FarmListCreateBatchResult> CreateFarmListsAsync(
        BotOptions options,
        FarmListCreateRequest request,
        Action<string> log,
        string? accountName = null,
        IProgress<FarmListCreateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
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
                result = await client.CreateFarmListsAsync(request, progress, cancellationToken);
            });

        return result ?? throw new InvalidOperationException("Could not create farm lists.");
    }

}
