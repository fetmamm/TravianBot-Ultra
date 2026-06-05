using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

// Reading village status from the worker (with transient-error retry and a
// completeness re-scan), plus the building-scan sanity checks used to decide
// whether a re-read is needed. Extracted verbatim from MainWindow.xaml.cs to keep
// that file focused; same class, so this is a pure relocation with no behavior
// change.
public partial class MainWindow
{
    private async Task LoadBuildingsAfterLoginAsync(BotOptions options, CancellationToken cancellationToken = default)
    {
        var status = await ReadVillageStatusWithRetryAsync(options, cancellationToken, resourceOnly: false);
        CacheVillageStatus(status);
        SetActiveWorkingVillageFromStatus(status);
        _lastBuildingStatus = status;
        PopulateBuildingsTab(status);
    }

    private async Task LoadCurrentVillageViewsAfterLoginAsync(BotOptions options, CancellationToken cancellationToken = default)
    {
        var status = await ReadVillageStatusWithRetryAsync(options, cancellationToken, resourceOnly: false, forceCurrentVillage: false);
        CacheVillageStatus(status);
        SetActiveWorkingVillageFromStatus(status);
        ApplyResourceRowsAndVillageStatus(status, includeQueuedTargets: true);

        _lastBuildingStatus = status;
        PopulateBuildingsTab(status);

        BuildingsInfoTextBlock.Text = _buildingsViewModel.DescribeLoadedSlots($"active village '{status.ActiveVillage}'");

        TribeInfoTextBlock.Text = $"{status.Tribe}";
        VillagesInfoTextBlock.Text = $"Villages: {status.VillageCount}";
        SyncDashboardVillageUiFromVillages(status.Villages, status.ActiveVillage);
        await RefreshResourceSnapshotForUiAsync(options, cancellationToken);
    }

    private async Task<VillageStatus> ReadVillageStatusWithRetryAsync(BotOptions options, CancellationToken cancellationToken, bool resourceOnly = false, bool forceCurrentVillage = false, bool currentPageOnly = false)
    {
        static bool IsTransientExecutionContextError(Exception ex)
        {
            var current = ex;
            while (current is not null)
            {
                var message = current.Message ?? string.Empty;
                if (message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("cannot find context with specified id", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                current = current.InnerException!;
            }

            return false;
        }

        VillageStatus status;
        var statusAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                status = await ReadVillageStatusAsync(options, cancellationToken, resourceOnly, forceCurrentVillage, currentPageOnly);
                break;
            }
            catch (Exception ex) when (attempt < statusAttempts && IsTransientExecutionContextError(ex))
            {
                AppendLog($"Village status read hit transient navigation context on attempt {attempt}/{statusAttempts}. Retrying...");
                await Task.Delay(250 * attempt, cancellationToken);
            }
        }

        var buildingScanIssue = resourceOnly ? null : DescribeBuildingScanIssue(status.Buildings);
        var requiresRetry = status.ResourceFields.Count < 18
            || (!resourceOnly && buildingScanIssue is not null);
        if (!requiresRetry)
        {
            return status;
        }

        if (status.ResourceFields.Count < 18)
        {
            AppendLog($"Resource scan returned {status.ResourceFields.Count} fields. Retrying once...");
        }

        if (!resourceOnly && buildingScanIssue is not null)
        {
            AppendLog($"Building scan looked incomplete ({buildingScanIssue}). Retrying once...");
        }

        await Task.Delay(350, cancellationToken);
        return await ReadVillageStatusAsync(options, cancellationToken, resourceOnly, forceCurrentVillage, currentPageOnly);
    }

    private static string? DescribeBuildingScanIssue(IReadOnlyList<Building> buildings)
    {
        if (buildings.Count == 0)
        {
            return "0 slots";
        }

        if (buildings.Count < 20)
        {
            return $"{buildings.Count} slots";
        }

        var hasMainBuilding = buildings.Any(item =>
            item.Gid == 15
            || string.Equals(item.Name, "Main Building", StringComparison.OrdinalIgnoreCase));
        if (!hasMainBuilding)
        {
            return "main building missing";
        }

        var hasRallyPoint = buildings.Any(item =>
            item.Gid == 16
            || string.Equals(item.Name, "Rally Point", StringComparison.OrdinalIgnoreCase));
        var suspiciousOccupiedCount = buildings.Count(item =>
            IsLikelyOccupiedBuilding(item)
            && (((item.Gid ?? 0) <= 0) || item.Level is null));

        if (!hasRallyPoint && (buildings.Count < 22 || suspiciousOccupiedCount > 0))
        {
            return "rally point missing";
        }

        if (suspiciousOccupiedCount >= 2)
        {
            return $"{suspiciousOccupiedCount} occupied slots with unknown level/gid";
        }

        return null;
    }

    private static bool IsLikelyOccupiedBuilding(Building building)
    {
        if ((building.Gid ?? 0) > 0 || (building.Level ?? 0) > 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(building.Name))
        {
            return false;
        }

        return !string.Equals(building.Name, "Empty", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(building.Name, "Unknown", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(building.Name, "g0", StringComparison.OrdinalIgnoreCase)
            && !building.Name.StartsWith("Slot ", StringComparison.OrdinalIgnoreCase);
    }

    private Task<VillageStatus> ReadVillageStatusAsync(BotOptions options, CancellationToken cancellationToken, bool resourceOnly, bool forceCurrentVillage = false, bool currentPageOnly = false)
    {
        var villageName = forceCurrentVillage ? null : GetSelectedVillageName();
        var villageUrl = forceCurrentVillage ? null : GetSelectedVillageUrl();

        if (resourceOnly)
        {
            return _botService.ReadVillageResourceStatusAsync(
                options,
                AppendLog,
                villageName,
                villageUrl,
                cancellationToken,
                currentPageOnly);
        }

        return _botService.ReadVillageStatusAsync(
            options,
            AppendLog,
            villageName,
            villageUrl,
            cancellationToken);
    }
}
