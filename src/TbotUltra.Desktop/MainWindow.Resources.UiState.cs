using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private static bool TryReadResourceUpgradePayload(IReadOnlyDictionary<string, string> payload, out int slotId, out int targetLevel)
    {
        slotId = 0;
        targetLevel = 0;
        if (!ResourceUpgradePayload.TryFromDictionary(payload, out var parsed, ResourceFieldMaxLevel)
            || parsed is null)
        {
            return false;
        }

        slotId = parsed.SlotId;
        targetLevel = parsed.TargetLevel;
        return true;
    }

    private static bool IsActiveResourceQueueStatus(QueueStatus status)
    {
        return status is QueueStatus.Pending or QueueStatus.Paused or QueueStatus.Running;
    }

    private QueueItem? EnqueueResourceUpgradeTaskCoalesced(
        Dictionary<string, string> payload,
        int slotId,
        int requestedTargetLevel,
        out int effectiveTargetLevel,
        out bool enqueued,
        out int removedCount)
    {
        var relatedItems = _botService.GetQueueItemsForDisplay()
            .Where(item => string.Equals(item.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase))
            .Where(item => IsActiveResourceQueueStatus(item.Status))
            .Select(item =>
            {
                var parsed = TryReadResourceUpgradePayload(item.Payload, out var parsedSlotId, out var parsedTargetLevel);
                return new
                {
                    Item = item,
                    Parsed = parsed,
                    SlotId = parsedSlotId,
                    TargetLevel = parsedTargetLevel,
                };
            })
            .Where(item => item.Parsed && item.SlotId == slotId)
            .ToList();

        var highestExistingTarget = relatedItems.Count == 0
            ? 0
            : relatedItems.Max(item => item.TargetLevel);
        effectiveTargetLevel = Math.Max(requestedTargetLevel, highestExistingTarget);

        if (highestExistingTarget >= requestedTargetLevel)
        {
            enqueued = false;
            removedCount = 0;
            return relatedItems
                .OrderByDescending(item => item.TargetLevel)
                .ThenBy(item => item.Item.CreatedAt)
                .Select(item => item.Item)
                .FirstOrDefault();
        }

        removedCount = 0;
        foreach (var related in relatedItems.Where(item => item.Item.Status is QueueStatus.Pending or QueueStatus.Paused))
        {
            if (_botService.RemoveQueueItem(related.Item.Id))
            {
                removedCount += 1;
            }
        }

        payload[BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = effectiveTargetLevel.ToString();
        var created = _botService.Enqueue("upgrade_resource_to_level", payload, priority: 0, maxRetries: 3);
        enqueued = true;
        return created;
    }

    private List<ResourceFieldRow> BuildResourceRows(VillageStatus status, bool includeQueuedTargets)
    {
        var queuedTargetsBySlot = includeQueuedTargets
            ? GetQueuedResourceTargetsBySlot()
            : null;
        var resourceMaxLevel = ResolveResourceMaxLevelFromStatus(status);

        return status.ResourceFields
            .Where(item => item.SlotId is not null)
            .OrderBy(item => item.SlotId)
            .Select(item => new ResourceFieldRow
            {
                SlotId = item.SlotId ?? 0,
                FieldType = item.FieldType,
                Name = item.Name,
                Level = item.Level,
                Url = item.Url ?? string.Empty,
                PendingTargetLevel = includeQueuedTargets
                    ? ResolveQueuedResourceTarget(item.SlotId ?? 0, item.Level ?? 0, queuedTargetsBySlot!)
                    : null,
                IsMaxLevel = (item.Level ?? 0) >= resourceMaxLevel,
            })
            .ToList();
    }

    private List<ResourceFieldRow> ApplyResourceRowsAndVillageStatus(VillageStatus status, bool includeQueuedTargets)
    {
        var rows = BuildResourceRows(status, includeQueuedTargets);
        SetResourceRows(rows);
        ApplyVillageStatusToUi(status);
        UpdateResourcesInfoText(status, rows.Count);
        return rows;
    }

    private void UpdateResourcesInfoText(VillageStatus status, int rowCount)
    {
        var capitalText = status.IsCapital == true ? "Yes" : status.IsCapital == false ? "No" : "Unknown";
        ResourcesInfoTextBlock.Text = $"Loaded {rowCount} resource fields. Capital: {capitalText}. {BuildResourceForecastSummary(status)}";
    }

    private void SetResourceRows(IReadOnlyList<ResourceFieldRow> rows)
    {
        ResourcesDataGrid.ItemsSource = rows.ToList();
        RepopulateResourceGroups(rows);
    }

    private IReadOnlyDictionary<int, int> GetQueuedResourceTargetsBySlot()
    {
        var targetsBySlot = new Dictionary<int, int>();
        IReadOnlyList<QueueItem> queueItems;
        try
        {
            queueItems = _botService.GetQueueItemsForDisplay();
        }
        catch
        {
            return targetsBySlot;
        }

        foreach (var item in queueItems)
        {
            if (!string.Equals(item.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (item.Status is QueueStatus.Succeeded or QueueStatus.Failed)
            {
                continue;
            }

            if (!TryReadResourceUpgradePayload(item.Payload, out var slotId, out var targetLevel))
            {
                continue;
            }

            if (!targetsBySlot.TryGetValue(slotId, out var existing) || targetLevel > existing)
            {
                targetsBySlot[slotId] = targetLevel;
            }
        }

        return targetsBySlot;
    }

    private int? ResolveQueuedResourceTarget(int slotId, int currentLevel, IReadOnlyDictionary<int, int> queuedTargetsBySlot)
    {
        var hasQueuedTarget = queuedTargetsBySlot.TryGetValue(slotId, out var queuedTarget) && queuedTarget > 0;
        if (!hasQueuedTarget)
        {
            _resourcePendingTargetBySlot.Remove(slotId);
            return null;
        }

        var effectiveTarget = queuedTarget;
        var hasPendingTarget = _resourcePendingTargetBySlot.TryGetValue(slotId, out var rememberedTarget) && rememberedTarget > 0;
        if (hasPendingTarget && rememberedTarget > effectiveTarget)
        {
            effectiveTarget = rememberedTarget;
        }

        if (effectiveTarget <= currentLevel)
        {
            _resourcePendingTargetBySlot.Remove(slotId);
            return null;
        }

        _resourcePendingTargetBySlot[slotId] = effectiveTarget;
        return effectiveTarget;
    }

    private void SyncPendingResourceTargetsInUi()
    {
        if (ResourcesDataGrid.ItemsSource is not IEnumerable<ResourceFieldRow> sourceRows)
        {
            return;
        }

        var queuedTargetsBySlot = GetQueuedResourceTargetsBySlot();
        var changed = false;
        var updatedRows = sourceRows
            .Select(row =>
            {
                var currentLevel = row.Level ?? 0;
                var pendingTarget = ResolveQueuedResourceTarget(row.SlotId, currentLevel, queuedTargetsBySlot);
                if (row.PendingTargetLevel == pendingTarget)
                {
                    return row;
                }

                changed = true;
                return new ResourceFieldRow
                {
                    SlotId = row.SlotId,
                    FieldType = row.FieldType,
                    Name = row.Name,
                    Level = row.Level,
                    Url = row.Url,
                    PendingTargetLevel = pendingTarget,
                    IsMaxLevel = row.IsMaxLevel,
                };
            })
            .ToList();

        if (!changed)
        {
            return;
        }

        SetResourceRows(updatedRows);
    }

    private void ClearPendingResourceLevelsFromUi()
    {
        _resourcePendingTargetBySlot.Clear();
        if (ResourcesDataGrid.ItemsSource is not IEnumerable<ResourceFieldRow> sourceRows)
        {
            return;
        }

        var updatedRows = sourceRows
            .Select(row => new ResourceFieldRow
            {
                SlotId = row.SlotId,
                FieldType = row.FieldType,
                Name = row.Name,
                Level = row.Level,
                Url = row.Url,
                PendingTargetLevel = null,
                IsMaxLevel = row.IsMaxLevel,
            })
            .ToList();

        SetResourceRows(updatedRows);
    }

    private void SetPendingResourceLevel(int slotId, int targetLevel)
    {
        var normalizedTarget = Math.Clamp(targetLevel, 1, _activeVillageResourceMaxLevel);
        if (_resourcePendingTargetBySlot.TryGetValue(slotId, out var existingTarget) && existingTarget > normalizedTarget)
        {
            normalizedTarget = existingTarget;
        }

        _resourcePendingTargetBySlot[slotId] = normalizedTarget;

        if (ResourcesDataGrid.ItemsSource is not IEnumerable<ResourceFieldRow> sourceRows)
        {
            return;
        }

        var updated = sourceRows
            .Select(row => row.SlotId == slotId
                ? new ResourceFieldRow
                {
                    SlotId = row.SlotId,
                    FieldType = row.FieldType,
                    Name = row.Name,
                    Level = row.Level,
                    Url = row.Url,
                    PendingTargetLevel = normalizedTarget > (row.Level ?? 0) ? normalizedTarget : null,
                    IsMaxLevel = row.IsMaxLevel,
                }
                : row)
            .ToList();

        if (updated.FirstOrDefault(row => row.SlotId == slotId)?.PendingTargetLevel is null)
        {
            _resourcePendingTargetBySlot.Remove(slotId);
        }

        SetResourceRows(updated);
    }

    private void MarkResourceAsMax(int slotId)
    {
        _resourcePendingTargetBySlot.Remove(slotId);
        if (ResourcesDataGrid.ItemsSource is not IEnumerable<ResourceFieldRow> sourceRows)
        {
            return;
        }

        var updated = sourceRows
            .Select(row => row.SlotId == slotId
                ? new ResourceFieldRow
                {
                    SlotId = row.SlotId,
                    FieldType = row.FieldType,
                    Name = row.Name,
                    Level = row.Level,
                    Url = row.Url,
                    PendingTargetLevel = null,
                    IsMaxLevel = true,
                }
                : row)
            .ToList();
        SetResourceRows(updated);
    }

    private void RepopulateResourceGroups(IEnumerable<ResourceFieldRow> rows)
    {
        _resourcesViewModel.RebuildFieldGroups(rows);
        UpdateCroplandLayout();
    }

    private void UpdateCroplandLayout()
    {
        if (CroplandItemsControl is null)
        {
            return;
        }

        var isDenseCropland = _resourcesViewModel.UseDenseCroplandLayout;
        var columns = isDenseCropland ? 2 : 1;
        var factory = new FrameworkElementFactory(typeof(UniformGrid));
        factory.SetValue(UniformGrid.ColumnsProperty, columns);
        var template = new ItemsPanelTemplate(factory);
        template.Seal();
        CroplandItemsControl.ItemsPanel = template;

        if (CroplandColumnPanel is not null)
        {
            CroplandColumnPanel.Width = isDenseCropland ? 350 : 190;
        }
    }
    private void ResourceLevelBadge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ResourceFieldRow row })
        {
            return;
        }

        var liveRow = (ResourcesDataGrid.ItemsSource as IEnumerable<ResourceFieldRow>)
            ?.FirstOrDefault(item => item.SlotId == row.SlotId) ?? row;
        var currentLevel = liveRow.Level ?? 0;
        var rowName = string.IsNullOrWhiteSpace(liveRow.Name) ? row.Name : liveRow.Name;

        var now = DateTimeOffset.UtcNow;
        if (_resourceClickCooldownBySlot.TryGetValue(row.SlotId, out var lastClickAt)
            && (now - lastClickAt).TotalMilliseconds < 120)
        {
            return;
        }

        _resourceClickCooldownBySlot[row.SlotId] = now;

        if (liveRow.IsMaxLevel || currentLevel >= _activeVillageResourceMaxLevel)
        {
            MarkResourceAsMax(row.SlotId);
            AppDialog.Show(this, "Max level reached", "Resources", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var pendingLevel = liveRow.PendingTargetLevel ?? currentLevel;
        var baseLevel = Math.Max(currentLevel, pendingLevel);
        var target = Math.Clamp(baseLevel + 1, 1, _activeVillageResourceMaxLevel);
        if (_resourceLastQueuedTargetBySlot.TryGetValue(row.SlotId, out var lastQueued)
            && lastQueued.Target == target
            && (now - lastQueued.At).TotalMilliseconds < 2500)
        {
            return;
        }

        var payload = new ResourceUpgradePayload(row.SlotId, target, rowName).ToDictionary();

        EnqueueQuickTask("upgrade_resource_to_level", $"Upgrade {rowName} to level {target}", payload);
        _resourceLastQueuedTargetBySlot[row.SlotId] = (target, now);
        SetPendingResourceLevel(row.SlotId, target);
        ResourcesInfoTextBlock.Text = $"Queued {rowName} to level {target}.";
        AppendLog($"Queued single resource upgrade: slot {row.SlotId} -> level {target}.");
    }

    private static bool IsResourceUpgradeTask(string taskName)
    {
        return string.Equals(taskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
               || string.Equals(taskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TryApplyFastResourceLevelUpdateAsync(string taskName, int terminalCountBefore)
    {
        if (!IsResourceUpgradeTask(taskName))
        {
            return false;
        }

        var newLines = await Dispatcher.InvokeAsync(() =>
        {
            var nowCount = _terminalEntries.Count;
            var added = Math.Max(0, nowCount - terminalCountBefore);
            return _terminalEntries.Take(added).Select(entry => entry.Text).ToList();
        });

        if (newLines.Count == 0)
        {
            return false;
        }

        var updates = new Dictionary<int, int>();
        var maxedSlots = new HashSet<int>();
        foreach (var line in newLines)
        {
            var levelUp = Regex.Match(line, @"Resource slot\s+(?<slot>\d+)\s+level increased from\s+\d+\s+to\s+(?<to>\d+)", RegexOptions.IgnoreCase);
            if (levelUp.Success)
            {
                var slot = int.Parse(levelUp.Groups["slot"].Value);
                var toLevel = int.Parse(levelUp.Groups["to"].Value);
                updates[slot] = toLevel;
                continue;
            }

            var reached = Regex.Match(line, @"Resource slot\s+(?<slot>\d+)\s+is level\s+(?<lvl>\d+)\.", RegexOptions.IgnoreCase);
            if (reached.Success)
            {
                var slot = int.Parse(reached.Groups["slot"].Value);
                var level = int.Parse(reached.Groups["lvl"].Value);
                updates[slot] = level;
                continue;
            }

            var maxed = Regex.Match(line, @"Resource slot\s+(?<slot>\d+)\s+reached max level\s+(?<lvl>\d+)", RegexOptions.IgnoreCase);
            if (maxed.Success)
            {
                var slot = int.Parse(maxed.Groups["slot"].Value);
                var level = int.Parse(maxed.Groups["lvl"].Value);
                updates[slot] = level;
                maxedSlots.Add(slot);
            }
        }

        if (updates.Count == 0)
        {
            return false;
        }

        return await Dispatcher.InvokeAsync(() =>
        {
            if (ResourcesDataGrid.ItemsSource is not IEnumerable<ResourceFieldRow> sourceRows)
            {
                return false;
            }

            var queuedTargetsBySlot = GetQueuedResourceTargetsBySlot();
            var rows = sourceRows.ToList();
            var changed = false;
            var updatedRows = rows.Select(row =>
            {
                if (!updates.TryGetValue(row.SlotId, out var nextLevel))
                {
                    return row;
                }

                if (row.Level is int existing && existing >= nextLevel)
                {
                    return row;
                }

                changed = true;
                return new ResourceFieldRow
                {
                    SlotId = row.SlotId,
                    FieldType = row.FieldType,
                    Name = row.Name,
                    Level = nextLevel,
                    Url = row.Url,
                    PendingTargetLevel = ResolveQueuedResourceTarget(row.SlotId, nextLevel, queuedTargetsBySlot),
                    IsMaxLevel = nextLevel >= _activeVillageResourceMaxLevel || row.IsMaxLevel || maxedSlots.Contains(row.SlotId),
                };
            }).ToList();

            if (!changed)
            {
                return false;
            }

            SetResourceRows(updatedRows);
            ResourcesInfoTextBlock.Text = $"Resource UI fast-updated for {updates.Count} slot(s).";
            if (maxedSlots.Count > 0)
            {
                AppDialog.Show(this, "Max level reached", "Resources", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return true;
        });
    }
}
