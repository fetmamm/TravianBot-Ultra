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
    public async Task<VillageStatus> ReadVillageStatusAsync(
        BotOptions options,
        Action<string> log,
        string? villageName = null,
        string? villageUrl = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        VillageStatus? status = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                log($"Reading village status for server {options.ServerName}.");
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, villageName, villageUrl);
                status = await client.ReadVillageStatusAsync(cancellationToken);
            },
            saveStateMode: BrowserStateSaveMode.Skip);

        return status ?? throw new InvalidOperationException("Could not read village status.");
    }

    public async Task<AccountSnapshot> ReadAccountSnapshotForScanAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        AccountSnapshot? snapshot = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                snapshot = await client.ReadAccountSnapshotAsync(
                    forceRefreshVillages: true,
                    preferCurrentPageVillages: false,
                    restorePageAfterProfile: true,
                    suppressEnsureUiSync: true,
                    cancellationToken: cancellationToken);
            });

        return snapshot ?? throw new InvalidOperationException("Could not read villages for account scan.");
    }

    public async Task<VillageStatus> ReadVillageStatusWithSmithyAsync(
        BotOptions options,
        Action<string> log,
        string? villageName = null,
        string? villageUrl = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        VillageStatus? status = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                log($"[account-scan] Reading full status for village '{villageName ?? "-"}'.");
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(
                    client,
                    options,
                    log,
                    cancellationToken,
                    villageName,
                    villageUrl,
                    skipFeatureRefresh: true);
                var villageStatus = await client.ReadVillageStatusAsync(cancellationToken);
                var smithyStatus = await client.ReadSmithyUpgradeStatusAsync(
                    villageStatus.Buildings,
                    cancellationToken);
                status = villageStatus with { SmithyUpgradeStatus = smithyStatus };
            });

        return status ?? throw new InvalidOperationException(
            $"Could not read full account-scan status for village '{villageName ?? "-"}'.");
    }

    public async Task<VillageStatus> ReadVillageResourceStatusAsync(
        BotOptions options,
        Action<string> log,
        string? villageName = null,
        string? villageUrl = null,
        bool currentPageOnly = false,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        VillageStatus? status = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                log($"Reading village resource status for server {options.ServerName}.");
                await client.LoginAsync(cancellationToken);
                if (!currentPageOnly)
                {
                    await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, villageName, villageUrl);
                }

                status = await client.ReadVillageResourceStatusAsync(
                    cancellationToken,
                    allowNavigationToResourcePage: !currentPageOnly);
                var forecastCount = status?.ResourceStorageForecasts?.Count ?? 0;
                var warehouse = FormatResourceStatusNumber(status?.WarehouseCapacity);
                var granary = FormatResourceStatusNumber(status?.GranaryCapacity);
                log($"Resource status: village='{status?.ActiveVillage ?? "-"}', fields={status?.ResourceFields.Count ?? 0}, forecasts={forecastCount}, storage={warehouse}/{granary}.");
            },
            saveStateMode: BrowserStateSaveMode.Skip);

        return status ?? throw new InvalidOperationException("Could not read village resource status.");
    }

    public async Task<IReadOnlyList<VillageStatus>> ReadAllVillageResourceStatusesAsync(
        BotOptions options,
        Action<string> log,
        string? returnVillageName = null,
        string? returnVillageUrl = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<VillageStatus> statuses = [];
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                log($"Scanning resource status for all villages on server {options.ServerName}.");
                await client.LoginAsync(cancellationToken);
                try
                {
                    statuses = await client.ReadAllVillageResourceStatusesAsync(cancellationToken);
                    log($"All-village resource scan read {statuses.Count} village(s).");
                }
                finally
                {
                    var targetName = string.IsNullOrWhiteSpace(returnVillageName) ? options.TargetVillageName : returnVillageName;
                    var targetUrl = string.IsNullOrWhiteSpace(returnVillageUrl) ? options.TargetVillageUrl : returnVillageUrl;
                    if (!string.IsNullOrWhiteSpace(targetName) || !string.IsNullOrWhiteSpace(targetUrl))
                    {
                        try
                        {
                            await client.SwitchToVillageAsync(targetName ?? string.Empty, targetUrl, cancellationToken, skipFeatureRefresh: true);
                            var label = !string.IsNullOrWhiteSpace(targetName) ? targetName : targetUrl;
                            log($"Returned to selected village: {label}");
                        }
                        catch (Exception ex)
                        {
                            log($"Could not return to selected village after scan: {ex.Message}");
                        }
                    }
                }
            });

        return statuses;
    }

    public async Task<VillageStatus> ReadCurrentPageResourceStatusQuickAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        VillageStatus? status = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                status = await client.ReadVillageResourceStatusAsync(
                    cancellationToken,
                    allowNavigationToResourcePage: false);
            },
            saveStateMode: BrowserStateSaveMode.Skip);

        return status ?? throw new InvalidOperationException("Could not read current-page resource status.");
    }

    public async Task<VillageStatus> ReadCurrentPageStorageStatusAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        VillageStatus? status = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                status = await client.ReadCurrentPageStorageStatusAsync(cancellationToken);
            },
            saveStateMode: BrowserStateSaveMode.Skip);

        return status ?? throw new InvalidOperationException("Could not read current-page storage status.");
    }

    public async Task<PageHtmlCapture> ReadCurrentPageHtmlAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        PageHtmlCapture? capture = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                capture = await client.ReadCurrentPageHtmlAsync(cancellationToken);
            },
            saveStateMode: BrowserStateSaveMode.Skip);

        return capture ?? throw new InvalidOperationException("Could not read current page HTML.");
    }

    public async Task<ReportPngResult> SaveReportScreenshotAsync(
        BotOptions options,
        string filePath,
        bool hideAttacker,
        bool hideDefender,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        ReportPngResult? result = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                result = await client.SaveReportScreenshotAsync(filePath, hideAttacker, hideDefender, cancellationToken);
            },
            saveStateMode: BrowserStateSaveMode.Skip);

        return result ?? throw new InvalidOperationException("Could not save report screenshot.");
    }

    public async Task<PageHtmlCapture> NavigateToPageAndReadHtmlAsync(
        BotOptions options,
        string pagePath,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        PageHtmlCapture? capture = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                capture = await client.NavigateToPageAndReadHtmlAsync(pagePath, cancellationToken);
            },
            saveStateMode: BrowserStateSaveMode.Skip);

        return capture ?? throw new InvalidOperationException($"Could not save page HTML for {pagePath}.");
    }

    public async Task<IReadOnlyDictionary<string, double?>> ReadCurrentPageResourceProductionPerHourAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, double?>? productionByHour = null;
        log($"Production-only resource read for server {options.ServerName}.");
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                productionByHour = await client.ReadCurrentPageResourceProductionPerHourAsync(cancellationToken);
            });

        if (productionByHour is not null)
        {
            var parts = new List<string>(4);
            foreach (var key in new[] { "wood", "clay", "iron", "crop" })
            {
                productionByHour.TryGetValue(key, out var value);
                var formatted = value?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "-";
                parts.Add($"{key}={formatted}/h");
            }

            log($"Production-only resource read result: {string.Join(", ", parts)}");
        }

        return productionByHour ?? new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
    }

    private static string FormatResourceStatusNumber(long? value)
    {
        if (value is null)
        {
            return "-";
        }

        return value.Value.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(",", " ");
    }

    public async Task<VillageStatus> ReadBuildingsStatusAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        VillageStatus? status = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                log($"Reading buildings status for server {options.ServerName}.");
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken);
                status = await client.ReadBuildingsStatusAsync(cancellationToken);
            });

        return status ?? throw new InvalidOperationException("Could not read buildings status.");
    }

    public async Task NavigateToVillageResourceFieldsAsync(
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
                await client.NavigateToResourceFieldsAsync(cancellationToken);
            });
    }

    // Reloads the page the browser is currently on (no navigation), used by the continuous loop's
    // idle keep-alive to stop the Travian page from going stale and showing wrong values.
    public async Task RefreshCurrentPageAsync(
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
                await client.LoginAsync(cancellationToken);
                await client.RefreshCurrentPageAsync(cancellationToken);
            },
            saveStateMode: BrowserStateSaveMode.Skip);
    }

}
