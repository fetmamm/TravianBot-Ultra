using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TbotUltra.Core.Configuration;
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
        if (!payload.TryGetValue(BotOptionPayloadKeys.ResourceUpgradeSlotId, out var slotRaw)
            || !int.TryParse(slotRaw, out slotId))
        {
            return false;
        }

        if (!payload.TryGetValue(BotOptionPayloadKeys.ResourceUpgradeTargetLevel, out var targetRaw)
            || !int.TryParse(targetRaw, out targetLevel))
        {
            return false;
        }

        if (slotId < 1 || slotId > 18 || targetLevel <= 0)
        {
            return false;
        }

        targetLevel = Math.Clamp(targetLevel, 1, ResourceFieldMaxLevel);
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

    private async void LoadResourcesButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("LoadResources");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleResourceTabActionsBusy(true);
        try
        {
            var options = LoadBotOptions();
            AppendLog($"[{operationId}] INFO server={options.ServerName}, headless={options.Headless}");
            await EnsureChromiumInstalledAsync();
            var status = await ReadVillageStatusWithRetryAsync(options, operationToken, resourceOnly: true, forceCurrentVillage: true);
            var queuedTargetsBySlot = GetQueuedResourceTargetsBySlot();
            var resourceMaxLevel = ResolveResourceMaxLevelFromStatus(status);
            _resourcePendingTargetBySlot.Clear();

            var rows = status.ResourceFields
                .Where(item => item.SlotId is not null)
                .OrderBy(item => item.SlotId)
                .Select(item => new ResourceFieldRow
                {
                    SlotId = item.SlotId ?? 0,
                    FieldType = item.FieldType,
                    Name = item.Name,
                    Level = item.Level,
                    Url = item.Url ?? string.Empty,
                    PendingTargetLevel = null,
                    IsMaxLevel = (item.Level ?? 0) >= resourceMaxLevel,
                })
                .ToList();

            SetResourceRows(rows);
            ApplyVillageStatusToUi(status);
            var capitalText = status.IsCapital == true ? "Yes" : status.IsCapital == false ? "No" : "Unknown";
            ResourcesInfoTextBlock.Text = $"Loaded {rows.Count} resource fields. Capital: {capitalText}. {BuildResourceForecastSummary(status)}";
            _inboxAutoEnabled = true;
            UpdateInboxButtons(status.UnreadMessages, status.UnreadReports);
            AppendLog($"Resources loaded: {rows.Count} fields.");
            CompleteOperation(operationId, operationSw, $"Loaded {rows.Count} fields.");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Load resources paused.";
            AppendLog("Load resources paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            ToggleResourceTabActionsBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private void OpenResourceTestFunctionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_resourceTestFunctionsWindow is not null)
        {
            if (!_resourceTestFunctionsWindow.IsVisible)
            {
                _resourceTestFunctionsWindow.Show();
            }

            _resourceTestFunctionsWindow.Activate();
            return;
        }

        _resourceTestFunctionsWindow = new FunctionTestWindow
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        _resourceTestFunctionsWindow.ResourceProductionTestRequested += TestResourceProductionButton_Click;
        _resourceTestFunctionsWindow.Closed += (_, _) =>
        {
            _resourceTestFunctionsWindow.ResourceProductionTestRequested -= TestResourceProductionButton_Click;
            _resourceTestFunctionsWindow = null;
        };

        _resourceTestFunctionsWindow.Show();
    }

    private async void TestResourceProductionButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("TestResourceProduction");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleResourceTabActionsBusy(true);
        try
        {
            var options = LoadBotOptions();
            AppendLog($"[{operationId}] testing production DOM read on current page.");
            var productionByHour = await _botService.ReadCurrentPageResourceProductionPerHourAsync(
                options,
                AppendLog,
                operationToken);

            var summary = string.Join(", ", new[] { "wood", "clay", "iron", "crop" }
                .Select(key =>
                {
                    productionByHour.TryGetValue(key, out var value);
                    var formatted = value?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "-";
                    return $"{key}={formatted}/h";
                }));

            AppendLog($"[{operationId}] production DOM read result: {summary}");
            if (productionByHour.Count > 0 && productionByHour.Values.Any(value => value is not null))
            {
                ApplyResourceProductionOnlyToUi(productionByHour);
                AppendLog($"[{operationId}] applied production DOM read to UI.");
            }
            else
            {
                AppendLog($"[{operationId}] no production values returned from DOM read.");
            }

            CompleteOperation(operationId, operationSw, $"Production DOM read finished: {summary}");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Test production paused.";
            AppendLog("Test production paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            ToggleResourceTabActionsBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async void UpgradeAllResourcesButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("UpgradeAllResources");
        if (ResourceTargetLevelComboBox.SelectedItem is not int targetLevel)
        {
            AppendLog($"[{operationId}] FAIL 0.0s | No target level selected.");
            return;
        }

        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleResourceTabActionsBusy(true);
        try
        {
            await QueueUpgradeAllResourcesAsync(operationId, operationToken, targetLevel);
        }
        catch (OperationCanceledException)
        {
            ClearPendingResourceLevelsFromUi();
            StatusTextBlock.Text = "Upgrade all resources paused.";
            AppendLog("Upgrade all resources paused.");
        }
        catch (Exception ex)
        {
            AppendLog($"[{operationId}] FAIL 0.0s | {FormatExceptionForLog(ex)}");
        }
        finally
        {
            ToggleResourceTabActionsBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async void UpgradeAllResourcesToMaxButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("UpgradeAllResourcesToMax");
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleResourceTabActionsBusy(true);
        try
        {
            await QueueUpgradeAllResourcesAsync(operationId, operationToken, null);
        }
        catch (OperationCanceledException)
        {
            ClearPendingResourceLevelsFromUi();
            StatusTextBlock.Text = "Upgrade all resources paused.";
            AppendLog("Upgrade all resources paused.");
        }
        catch (Exception ex)
        {
            AppendLog($"[{operationId}] FAIL 0.0s | {FormatExceptionForLog(ex)}");
        }
        finally
        {
            ToggleResourceTabActionsBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private void ToggleResourceTabActionsBusy(bool busy)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => ToggleResourceTabActionsBusy(busy));
            return;
        }

        var enabled = !busy;
        SetEnabled(LoadResourcesButton, enabled);
        SetEnabled(ResourceTargetLevelComboBox, enabled);
        SetEnabled(UpgradeAllResourcesButton, enabled);
        SetEnabled(UpgradeAllResourcesToMaxButton, enabled);
        if (_resourceTestFunctionsWindow is not null)
        {
            _resourceTestFunctionsWindow.IsEnabled = enabled;
        }
    }

    private async Task QueueUpgradeAllResourcesAsync(string operationId, CancellationToken operationToken, int? targetLevel)
    {
        await EnsureChromiumInstalledAsync();
        var options = LoadBotOptions();
        var status = await ReadVillageStatusWithRetryAsync(options, operationToken, resourceOnly: true, forceCurrentVillage: true);
        var resourceMaxLevel = ResolveResourceMaxLevelFromStatus(status);
        var requestedTargetLevel = targetLevel.HasValue
            ? Math.Min(targetLevel.Value, resourceMaxLevel)
            : resourceMaxLevel;
        _resourcePendingTargetBySlot.Clear();
        var rows = status.ResourceFields
            .Where(item => item.SlotId is not null && item.Level is not null)
            .Select(item => new ResourceFieldRow
            {
                SlotId = item.SlotId ?? 0,
                FieldType = item.FieldType,
                Name = item.Name,
                Level = item.Level,
                Url = item.Url ?? string.Empty,
                PendingTargetLevel = null,
                IsMaxLevel = (item.Level ?? 0) >= resourceMaxLevel,
            })
            .ToList();

        SetResourceRows(rows);
        ApplyVillageStatusToUi(status);

        var orderedUpgrades = rows
            .Where(row => (row.Level ?? 0) < requestedTargetLevel)
            .OrderBy(row => row.Level ?? 0)
            .ThenBy(row => row.SlotId)
            .ToList();

        if (orderedUpgrades.Count == 0)
        {
            AppendLog($"[{operationId}] OK 0.0s | All resource fields are already at or above level {requestedTargetLevel}.");
            return;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = requestedTargetLevel.ToString(),
        };

        var item = _botService.Enqueue("upgrade_all_resources_to_level", payload, priority: 0, maxRetries: 3);
        RequestQueueUiRefresh(selectId: item.Id);
        TriggerQueueAutoRunFromEnqueue();
        AppendLog($"[{operationId}] OK 0.0s | Queued upgrade-all toward level {requestedTargetLevel}. The worker will upgrade the lowest resource field first.");
    }

    private async Task LoadResourcesAfterUpgradeAsync(CancellationToken cancellationToken = default, bool resourceOnly = false)
    {
        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        var status = await ReadVillageStatusWithRetryAsync(options, cancellationToken, resourceOnly);
        await Dispatcher.InvokeAsync(() =>
        {
            var queuedTargetsBySlot = GetQueuedResourceTargetsBySlot();
            var resourceMaxLevel = ResolveResourceMaxLevelFromStatus(status);
            var rows = status.ResourceFields
                .Where(item => item.SlotId is not null)
                .OrderBy(item => item.SlotId)
                .Select(item => new ResourceFieldRow
                {
                    SlotId = item.SlotId ?? 0,
                    FieldType = item.FieldType,
                    Name = item.Name,
                    Level = item.Level,
                    Url = item.Url ?? string.Empty,
                    PendingTargetLevel = ResolveQueuedResourceTarget(item.SlotId ?? 0, item.Level ?? 0, queuedTargetsBySlot),
                    IsMaxLevel = (item.Level ?? 0) >= resourceMaxLevel,
                })
                .ToList();
            SetResourceRows(rows);
            ApplyVillageStatusToUi(status);
            var capitalText = status.IsCapital == true ? "Yes" : status.IsCapital == false ? "No" : "Unknown";
            ResourcesInfoTextBlock.Text = $"Loaded {rows.Count} resource fields. Capital: {capitalText}. {BuildResourceForecastSummary(status)}";
        });
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

            if (!item.Payload.TryGetValue(BotOptionPayloadKeys.ResourceUpgradeSlotId, out var slotRaw)
                || !int.TryParse(slotRaw, out var slotId))
            {
                continue;
            }

            if (!item.Payload.TryGetValue(BotOptionPayloadKeys.ResourceUpgradeTargetLevel, out var targetRaw)
                || !int.TryParse(targetRaw, out var targetLevel))
            {
                continue;
            }

            if (targetLevel <= 0)
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
        _resourcesViewModel.WoodFields.Clear();
        _resourcesViewModel.ClayFields.Clear();
        _resourcesViewModel.IronFields.Clear();
        _resourcesViewModel.CroplandFields.Clear();

        foreach (var row in rows.OrderBy(item => item.SlotId))
        {
            GetBucket(row).Add(row);
        }

        UpdateCroplandLayout();
    }

    private void UpdateCroplandLayout()
    {
        if (CroplandItemsControl is null)
        {
            return;
        }

        var isDenseCropland = _resourcesViewModel.CroplandFields.Count > 6;
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

    private ObservableCollection<ResourceFieldRow> GetBucket(ResourceFieldRow row)
    {
        var fieldType = row.FieldType?.Trim() ?? string.Empty;
        if (fieldType.Contains("wood", StringComparison.OrdinalIgnoreCase))
        {
            return _resourcesViewModel.WoodFields;
        }

        if (fieldType.Contains("clay", StringComparison.OrdinalIgnoreCase))
        {
            return _resourcesViewModel.ClayFields;
        }

        if (fieldType.Contains("iron", StringComparison.OrdinalIgnoreCase))
        {
            return _resourcesViewModel.IronFields;
        }

        if (fieldType.Contains("crop", StringComparison.OrdinalIgnoreCase))
        {
            return _resourcesViewModel.CroplandFields;
        }

        return row.SlotId switch
        {
            1 or 5 or 6 or 10 or 16 => _resourcesViewModel.WoodFields,
            2 or 4 or 7 or 14 or 17 => _resourcesViewModel.ClayFields,
            3 or 8 or 9 or 11 or 15 => _resourcesViewModel.IronFields,
            12 or 13 or 18 => _resourcesViewModel.CroplandFields,
            _ => _resourcesViewModel.CroplandFields,
        };
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

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ResourceUpgradeSlotId] = row.SlotId.ToString(),
            [BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = target.ToString(),
            [BotOptionPayloadKeys.ResourceUpgradeName] = rowName,
        };

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
            return _terminalEntries.Take(added).ToList();
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

    private async Task TryRefreshResourceProductionOnlyAsync(CancellationToken cancellationToken)
    {
        if (_lastResourceStatusForUi is null)
        {
            AppendLog("[resource-production] skipped: no cached resource status for UI.");
            return;
        }

        try
        {
            AppendLog("[resource-production] start");
            var productionByHour = await _botService.ReadCurrentPageResourceProductionPerHourAsync(
                LoadBotOptions(),
                AppendLog,
                cancellationToken);
            if (productionByHour.Count == 0)
            {
                AppendLog("[resource-production] skipped: no production values were read.");
                return;
            }

            var summary = string.Join(", ", new[] { "wood", "clay", "iron", "crop" }
                .Select(key =>
                {
                    productionByHour.TryGetValue(key, out var value);
                    var formatted = value?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "-";
                    return $"{key}={formatted}/h";
                }));
            AppendLog($"[resource-production] read {summary}");
            await Dispatcher.InvokeAsync(() => ApplyResourceProductionOnlyToUi(productionByHour));
            AppendLog("[resource-production] applied to UI");
        }
        catch (Exception ex)
        {
            AppendLog($"[resource-production] FAIL {ex.Message}");
        }
    }

    private void QueueResourceProductionOnlyRefresh(string source)
    {
        if (_resourceProductionRefreshRunning)
        {
            _resourceProductionRefreshPending = true;
            AppendLog($"[resource-production] pending while previous refresh is running (source={source}).");
            return;
        }

        _resourceProductionRefreshRunning = true;
        _resourceProductionRefreshPending = false;
        AppendLog($"[resource-production] queued from {source}");
        _ = Task.Run(async () =>
        {
            try
            {
                await TryRefreshResourceProductionOnlyAsync(CancellationToken.None);
            }
            finally
            {
                _resourceProductionRefreshRunning = false;
                if (_resourceProductionRefreshPending)
                {
                    _resourceProductionRefreshPending = false;
                    QueueResourceProductionOnlyRefresh("pending_followup");
                }
            }
        });
    }

    private void ApplyResourceProductionOnlyToUi(IReadOnlyDictionary<string, double?> productionByHour)
    {
        if (_lastResourceStatusForUi is null)
        {
            return;
        }

        var currentStatus = _lastResourceStatusForUi;
        var currentForecasts = currentStatus.ResourceStorageForecasts?
            .ToDictionary(item => item.ResourceKey, item => item, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ResourceStorageForecast>(StringComparer.OrdinalIgnoreCase);
        var updatedForecasts = new List<ResourceStorageForecast>(4);

        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            currentForecasts.TryGetValue(key, out var existingForecast);

            var currentAmount = TryParseResourceValueForUi(currentStatus.Resources, key) ?? existingForecast?.Current;
            var capacity = existingForecast?.Capacity
                ?? (string.Equals(key, "crop", StringComparison.OrdinalIgnoreCase)
                    ? currentStatus.GranaryCapacity
                    : currentStatus.WarehouseCapacity);
            var effectiveProduction = productionByHour.TryGetValue(key, out var liveProduction)
                ? liveProduction
                : existingForecast?.ProductionPerHour;

            double? percentOfCapacity = null;
            if (capacity is > 0 && currentAmount is not null)
            {
                percentOfCapacity = Math.Clamp((double)currentAmount.Value / capacity.Value * 100d, 0d, 100d);
            }

            int? secondsToFull = null;
            if (capacity is > 0 && currentAmount is not null && effectiveProduction is > 0)
            {
                var remaining = Math.Max(0L, capacity.Value - currentAmount.Value);
                var computedSeconds = Math.Ceiling((remaining / effectiveProduction.Value) * 3600d);
                secondsToFull = computedSeconds >= int.MaxValue
                    ? int.MaxValue
                    : (int)computedSeconds;
            }

            updatedForecasts.Add(new ResourceStorageForecast(
                ResourceKey: key,
                Current: currentAmount,
                Capacity: capacity,
                PercentOfCapacity: percentOfCapacity,
                ProductionPerHour: effectiveProduction,
                SecondsToFull: secondsToFull));
        }

        var updatedStatus = currentStatus with
        {
            ResourceStorageForecasts = updatedForecasts,
        };

        _lastResourceStatusForUi = updatedStatus;
        ApplyVillageStatusToUi(updatedStatus);
        TriggerDeferredConstructionWaitRefresh(updatedStatus, "resource_production_refresh");
        TriggerDeferredTroopTrainingWaitRefresh(updatedStatus, "resource_production_refresh");

        var rowCount = (ResourcesDataGrid.ItemsSource as IEnumerable<ResourceFieldRow>)?.Count()
            ?? updatedStatus.ResourceFields.Count;
        var capitalText = updatedStatus.IsCapital == true ? "Yes" : updatedStatus.IsCapital == false ? "No" : "Unknown";
        ResourcesInfoTextBlock.Text = $"Loaded {rowCount} resource fields. Capital: {capitalText}. {BuildResourceForecastSummary(updatedStatus)}";
        AppendLog($"[resource-production] UI summary updated: {BuildResourceForecastSummary(updatedStatus)}");
    }

    private void ApplyResourceStatusToUi(VillageStatus status)
    {
        status = MergeResourceStatusForUi(status);
        AppendLog($"[resource-ui] village='{status.ActiveVillage}' | {BuildResourceLogSummary(status)}");
        var queuedTargetsBySlot = GetQueuedResourceTargetsBySlot();
        var resourceMaxLevel = ResolveResourceMaxLevelFromStatus(status);
        var rows = status.ResourceFields
            .Where(item => item.SlotId is not null)
            .OrderBy(item => item.SlotId)
            .Select(item => new ResourceFieldRow
            {
                SlotId = item.SlotId ?? 0,
                FieldType = item.FieldType,
                Name = item.Name,
                Level = item.Level,
                Url = item.Url ?? string.Empty,
                PendingTargetLevel = ResolveQueuedResourceTarget(item.SlotId ?? 0, item.Level ?? 0, queuedTargetsBySlot),
                IsMaxLevel = (item.Level ?? 0) >= resourceMaxLevel,
            })
            .ToList();

        SetResourceRows(rows);
        ApplyVillageStatusToUi(status);
        TriggerDeferredConstructionWaitRefresh(status, "resource_status_refresh");
        TriggerDeferredTroopTrainingWaitRefresh(status, "resource_status_refresh");

        var capitalText = status.IsCapital == true ? "Yes" : status.IsCapital == false ? "No" : "Unknown";
        ResourcesInfoTextBlock.Text = $"Loaded {rows.Count} resource fields. Capital: {capitalText}. {BuildResourceForecastSummary(status)}";
    }

    private VillageStatus MergeResourceStatusForUi(VillageStatus status)
    {
        if (HasCompleteResourceUiSnapshot(status))
        {
            _lastResourceStatusForUi = status;
            return status;
        }

        var previous = _lastResourceStatusForUi;
        if (previous is null)
        {
            return status;
        }

        if (!string.Equals(previous.ActiveVillage, status.ActiveVillage, StringComparison.OrdinalIgnoreCase))
        {
            return status;
        }

        var mergedWarehouse = status.WarehouseCapacity ?? previous.WarehouseCapacity;
        var mergedGranary = status.GranaryCapacity ?? previous.GranaryCapacity;
        var mergedForecasts = BuildMergedResourceForecasts(status, previous, mergedWarehouse, mergedGranary);
        var mergedStatus = status with
        {
            WarehouseCapacity = mergedWarehouse,
            GranaryCapacity = mergedGranary,
            ResourceStorageForecasts = mergedForecasts,
        };

        _lastResourceStatusForUi = mergedStatus;
        AppendLog($"[resource-ui] preserved previous storage/prod data for village='{status.ActiveVillage}'.");
        return mergedStatus;
    }

    private static bool HasCompleteResourceUiSnapshot(VillageStatus status)
    {
        if (status.WarehouseCapacity is null || status.GranaryCapacity is null)
        {
            return false;
        }

        if (status.ResourceStorageForecasts is null || status.ResourceStorageForecasts.Count == 0)
        {
            return false;
        }

        return status.ResourceStorageForecasts.Any(item => item.Capacity is not null || item.ProductionPerHour is not null);
    }

    private static IReadOnlyList<ResourceStorageForecast> BuildMergedResourceForecasts(
        VillageStatus current,
        VillageStatus previous,
        long? warehouseCapacity,
        long? granaryCapacity)
    {
        var currentForecasts = current.ResourceStorageForecasts?
            .ToDictionary(item => item.ResourceKey, item => item, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ResourceStorageForecast>(StringComparer.OrdinalIgnoreCase);
        var previousForecasts = previous.ResourceStorageForecasts?
            .ToDictionary(item => item.ResourceKey, item => item, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ResourceStorageForecast>(StringComparer.OrdinalIgnoreCase);

        var result = new List<ResourceStorageForecast>(4);
        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            currentForecasts.TryGetValue(key, out var currentForecast);
            previousForecasts.TryGetValue(key, out var previousForecast);

            var currentAmount = TryParseResourceValueForUi(current.Resources, key)
                ?? currentForecast?.Current
                ?? previousForecast?.Current;
            var capacity = currentForecast?.Capacity
                ?? previousForecast?.Capacity
                ?? (string.Equals(key, "crop", StringComparison.OrdinalIgnoreCase) ? granaryCapacity : warehouseCapacity);
            var productionPerHour = currentForecast?.ProductionPerHour ?? previousForecast?.ProductionPerHour;

            double? percentOfCapacity = null;
            if (capacity is > 0 && currentAmount is not null)
            {
                percentOfCapacity = Math.Clamp((double)currentAmount.Value / capacity.Value * 100d, 0d, 100d);
            }

            int? secondsToFull = null;
            if (capacity is > 0 && currentAmount is not null && productionPerHour is > 0)
            {
                var remaining = Math.Max(0L, capacity.Value - currentAmount.Value);
                var computedSeconds = Math.Ceiling((remaining / productionPerHour.Value) * 3600d);
                secondsToFull = computedSeconds >= int.MaxValue
                    ? int.MaxValue
                    : (int)computedSeconds;
            }

            result.Add(new ResourceStorageForecast(
                ResourceKey: key,
                Current: currentAmount,
                Capacity: capacity,
                PercentOfCapacity: percentOfCapacity,
                ProductionPerHour: productionPerHour,
                SecondsToFull: secondsToFull));
        }

        return result;
    }

    private static long? TryParseResourceValueForUi(IReadOnlyDictionary<string, string>? resources, string key)
    {
        if (resources is null || !resources.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Replace(" ", string.Empty).Replace("'", string.Empty).Replace(",", string.Empty).Trim();
        return long.TryParse(normalized, out var parsed) ? parsed : null;
    }

    private async Task RefreshResourceSnapshotForUiAsync(
        BotOptions? options = null,
        CancellationToken cancellationToken = default,
        bool forceCurrentVillage = false,
        bool currentPageOnly = false)
    {
        if (_resourceSnapshotRefreshRunning)
        {
            return;
        }

        _resourceSnapshotRefreshRunning = true;
        try
        {
            var effectiveOptions = forceCurrentVillage || currentPageOnly
                ? LoadBotOptions()
                : (options is null ? ApplySelectedVillageToOptions(LoadBotOptions()) : ApplySelectedVillageToOptions(options));
            var selectedVillage = forceCurrentVillage || currentPageOnly ? "(current)" : (GetSelectedVillageName() ?? "-");
            AppendLog($"[resource-refresh] start village='{selectedVillage}'");
            var status = await ReadVillageStatusWithRetryAsync(
                effectiveOptions,
                cancellationToken,
                resourceOnly: true,
                forceCurrentVillage: forceCurrentVillage,
                currentPageOnly: currentPageOnly);
            AppendLog($"[resource-refresh] read village='{status.ActiveVillage}' | {BuildResourceLogSummary(status)}");

            await Dispatcher.InvokeAsync(() =>
            {
                AppendLog($"[resource-refresh] applied village='{status.ActiveVillage}'");
                ApplyResourceStatusToUi(status);
            });
        }
        catch (Exception ex)
        {
            AppendLog($"[resource-refresh] FAIL {ex.Message}");
            throw;
        }
        finally
        {
            AppendLog("[resource-refresh] END");
            _resourceSnapshotRefreshRunning = false;
        }
    }

    private bool ShouldRunBackgroundResourceSnapshotRefresh()
    {
        if (!_isLoggedIn || !_browserSessionLikelyOpen || _resourceSnapshotRefreshRunning)
        {
            return false;
        }

        if (_uiBusy)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_activeFunctionDisplayName))
        {
            return false;
        }

        try
        {
            if (_botService.GetQueueItemsForDisplay().Any(item => item.Status == QueueStatus.Running))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        return true;
    }

    private async Task HandleResourceSnapshotRefreshTickAsync()
    {
        if (!ShouldRunBackgroundResourceSnapshotRefresh())
        {
            return;
        }

        try
        {
            await RefreshResourceSnapshotForUiAsync(cancellationToken: CancellationToken.None, currentPageOnly: true);
        }
        catch (Exception ex)
        {
            AppendLog($"Background resource refresh skipped: {ex.Message}");
        }
    }

    private async void StorageRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_resourceSnapshotRefreshRunning)
        {
            return;
        }

        SetEnabled(StorageRefreshButton, false);
        try
        {
            await EnsureChromiumInstalledAsync();
            AppendLog("[resource-refresh] manual quick refresh requested");
            var options = LoadBotOptions();
            var status = await _botService.ReadCurrentPageResourceStatusQuickAsync(options, AppendLog, CancellationToken.None);
            ApplyResourceStatusToUi(status);
        }
        catch (Exception ex)
        {
            AppendLog($"[resource-refresh] manual quick refresh skipped: {ex.Message}");
        }
        finally
        {
            SetEnabled(StorageRefreshButton, !_uiBusy);
        }
    }

    private int ResolveResourceMaxLevelFromStatus(VillageStatus status)
    {
        if (status.IsCapital == true)
        {
            return ResourceFieldMaxLevel;
        }

        if (status.IsCapital == false)
        {
            return NonCapitalResourceMaxLevel;
        }

        return _activeVillageResourceMaxLevel;
    }

    private void UpdateActiveVillageResourceMaxLevel(VillageStatus status)
    {
        if (status.IsCapital == true)
        {
            _activeVillageResourceMaxLevel = ResourceFieldMaxLevel;
            return;
        }

        if (status.IsCapital == false)
        {
            _activeVillageResourceMaxLevel = NonCapitalResourceMaxLevel;
        }
    }

    private static string BuildResourceForecastSummary(VillageStatus status)
    {
        if (status.ResourceStorageForecasts is null || status.ResourceStorageForecasts.Count == 0)
        {
            return "Storage forecast unavailable.";
        }

        var parts = new List<string>();
        foreach (var forecast in status.ResourceStorageForecasts)
        {
            var key = forecast.ResourceKey switch
            {
                "wood" => "Wood",
                "clay" => "Clay",
                "iron" => "Iron",
                "crop" => "Crop",
                _ => forecast.ResourceKey,
            };
            var percentText = forecast.PercentOfCapacity is double percent
                ? $"{percent:F0}%"
                : "-";
            var etaText = forecast.SecondsToFull is int seconds
                ? FormatCountdown(seconds)
                : "-";
            parts.Add($"{key} {percentText} (full in {etaText})");
        }

        var warehouse = FormatResourceLogNumber(status.WarehouseCapacity);
        var granary = FormatResourceLogNumber(status.GranaryCapacity);
        return $"Warehouse={warehouse}, Granary={granary}. {string.Join(" | ", parts)}";
    }

    private static string BuildResourceLogSummary(VillageStatus status)
    {
        var forecasts = status.ResourceStorageForecasts?
            .ToDictionary(item => item.ResourceKey, item => item, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ResourceStorageForecast>(StringComparer.OrdinalIgnoreCase);

        string Part(string key, string label)
        {
            forecasts.TryGetValue(key, out var forecast);
            var current = FormatResourceLogNumber(forecast?.Current);
            var production = FormatResourceLogNumber(forecast?.ProductionPerHour);
            return $"{label} {current} @{production}/h";
        }

        return $"storage {FormatResourceLogNumber(status.WarehouseCapacity)}/{FormatResourceLogNumber(status.GranaryCapacity)} | {Part("wood", "W")} | {Part("clay", "C")} | {Part("iron", "I")} | {Part("crop", "Crop")}";
    }

    private static string FormatResourceLogNumber(long? value)
    {
        if (value is null)
        {
            return "-";
        }

        return value.Value.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(",", " ");
    }

    private static string FormatResourceLogNumber(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return "-";
        }

        return Math.Round(value.Value, MidpointRounding.AwayFromZero)
            .ToString("#,0", System.Globalization.CultureInfo.InvariantCulture)
            .Replace(",", " ");
    }
}
