using System.Windows;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private readonly HashSet<string> _storageMasterPlanVerificationAlarmKeys = new(StringComparer.OrdinalIgnoreCase);

    private bool IsStorageMasterPlanEnabled(VillageStatus status)
        => _villageSettingsStore.IsStorageMasterPlanEnabledByKey(ResolveStatusVillageKey(status));

    private static bool IsGreatStorageQueueItem(QueueItem item)
    {
        if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase))
        {
            return BuildingConstructPayload.TryFromDictionary(item.Payload, out var construct)
                && construct?.Gid is 38 or 39;
        }

        return (string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
            && BuildingUpgradePayload.TryFromDictionary(item.Payload, out var upgrade)
            && BuildingCatalogService.GidForName(upgrade?.Name) is 38 or 39;
    }

    private async Task<bool> HandleStorageMasterPlanGateAsync(QueueItem item, CancellationToken cancellationToken)
    {
        if (!IsGreatStorageQueueItem(item))
        {
            return false;
        }

        var villageKey = GetQueueItemVillageKey(item);
        var villageName = GetQueueItemVillageName(item) ?? villageKey ?? "unknown";
        if (string.IsNullOrWhiteSpace(villageKey))
        {
            _botService.MarkQueueItemDeferred(item.Id, TimeSpan.FromMinutes(10));
            AppendLog($"ALARM: Storage Master Plan task has no confirmed village identity; task '{item.TaskName}' deferred, no queue items removed.");
            return true;
        }

        if (!_villageSettingsStore.IsStorageMasterPlanEnabledByKey(villageKey))
        {
            RemoveGreatStorageQueueItems(villageKey, villageName, "toggle is off");
            return true;
        }

        bool? active;
        try
        {
            var options = BotOptionsPayloadApplier.Apply(LoadBotOptions(), item.Payload);
            active = await _botService.VerifyStorageMasterPlanAsync(options, AppendLog, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog($"[storage-master-plan] Treasury verification failed for '{villageName}': {ex.Message}");
            active = null;
        }

        if (active == true)
        {
            _storageMasterPlanVerificationAlarmKeys.Remove(villageKey);
            if (!_villageSettingsStore.IsStorageMasterPlanEnabledByKey(villageKey))
            {
                RemoveGreatStorageQueueItems(villageKey, villageName, "toggle was switched off during verification");
                return true;
            }
            return false;
        }

        if (active is null)
        {
            _botService.MarkQueueItemDeferred(item.Id, TimeSpan.FromMinutes(10));
            if (_storageMasterPlanVerificationAlarmKeys.Add(villageKey))
            {
                AppendLog($"ALARM: Storage Master Plan in village '{villageName}' could not be verified in Treasury. Great storage task deferred; toggle and queue preserved for retry.");
            }
            return true;
        }

        var village = await Dispatcher.InvokeAsync(() => GetAllVillageKeyInfos()
            .FirstOrDefault(candidate => string.Equals(
                _villageSettingsStore.ResolveCanonicalKey(candidate.Key),
                villageKey,
                StringComparison.OrdinalIgnoreCase)))
            ?? new VillageSettingsStore.VillageKeyInfo(villageKey, villageName, null, null, false);

        _villageSettingsStore.SetStorageMasterPlan(village, false);
        _storageMasterPlanVerificationAlarmKeys.Remove(villageKey);
        RemoveGreatStorageQueueItems(villageKey, villageName, "Treasury confirms no active plan");
        AppendLog($"ALARM: Storage Master Plan is not active in Treasury for village '{villageName}'. Toggle switched off and Great storage tasks removed; turn it on again to retry.");
        await Dispatcher.InvokeAsync(() =>
        {
            var status = ResolveSelectedVillageBuildingStatus();
            if (status is not null)
            {
                PopulateBuildingsTab(status);
            }
        });
        return true;
    }

    private void RemoveGreatStorageQueueItems(string villageKey, string villageName, string reason)
    {
        var ids = _botService.GetQueueItemsForDisplay()
            .Where(candidate => IsActiveQueueStatus(candidate.Status))
            .Where(IsGreatStorageQueueItem)
            .Where(candidate => string.Equals(GetQueueItemVillageKey(candidate), villageKey, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.Id)
            .ToArray();
        var removed = _botService.RemoveQueueItems(ids);
        AppendLog($"[storage-master-plan] removed {removed} Great storage task(s) for '{villageName}': {reason}.");
        _ = Dispatcher.BeginInvoke(() => RequestQueueUiRefresh());
    }

    private void OpenStorageMasterPlanSettingsWindow()
    {
        if (string.IsNullOrWhiteSpace(_accountStore.ActiveAccountName()))
        {
            AppendLog("[storage-master-plan] cannot open settings without an active account.");
            return;
        }

        var villages = GetAllVillageKeyInfos();
        if (villages.Count == 0)
        {
            AppDialog.Show(this, "Load your villages before configuring Storage Master Plan.",
                "Storage Master Plan", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var rows = villages
            .OrderBy(village => village.Name, StringComparer.OrdinalIgnoreCase)
            .Select(village => new StorageMasterPlanSettingsRow(
                village, _villageSettingsStore.GetStorageMasterPlan(village)))
            .ToList();
        var window = new StorageMasterPlanSettingsWindow(rows) { Owner = this };
        if (window.ShowDialog() != true)
        {
            return;
        }

        foreach (var row in rows)
        {
            _villageSettingsStore.SetStorageMasterPlan(row.Village, row.IsEnabled);
        }

        var status = ResolveSelectedVillageBuildingStatus();
        if (status is not null)
        {
            PopulateBuildingsTab(status);
        }

        AppendLog($"[storage-master-plan] saved eligibility choices for {rows.Count} village(s).");
    }
}
