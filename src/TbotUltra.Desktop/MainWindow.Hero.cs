using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private void HeroAttributePriorityItemsControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _heroPriorityDragStart = e.GetPosition(HeroAttributePriorityItemsControl);
        _heroPriorityDragSource = FindHeroAttributePriorityItem(e.OriginalSource as DependencyObject);
    }

    private void HeroAttributePriorityItemsControl_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _heroPriorityDragSource is null)
        {
            return;
        }

        var position = e.GetPosition(HeroAttributePriorityItemsControl);
        var delta = position - _heroPriorityDragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(HeroAttributePriorityItemsControl, _heroPriorityDragSource, DragDropEffects.Move);
    }

    private void HeroAttributePriorityItemsControl_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(HeroAttributePriorityItem)))
        {
            return;
        }

        if (e.Data.GetData(typeof(HeroAttributePriorityItem)) is not HeroAttributePriorityItem sourceItem)
        {
            return;
        }

        var targetItem = FindHeroAttributePriorityItem(e.OriginalSource as DependencyObject);
        var fromIndex = _heroViewModel.AttributePriorityItems.IndexOf(sourceItem);
        if (fromIndex < 0)
        {
            return;
        }

        var toIndex = targetItem is null
            ? _heroViewModel.AttributePriorityItems.Count - 1
            : _heroViewModel.AttributePriorityItems.IndexOf(targetItem);
        if (toIndex < 0 || fromIndex == toIndex)
        {
            return;
        }

        _heroViewModel.AttributePriorityItems.Move(fromIndex, toIndex);
        _heroViewModel.UpdateOrders();
        PersistHeroPriorityToConfig();
    }

    private HeroAttributePriorityItem? FindHeroAttributePriorityItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: HeroAttributePriorityItem item })
            {
                return item;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void PersistHeroPriorityToConfig()
    {
        try
        {
            var config = _botConfigStore.Load();
            config[BotOptionPayloadKeys.HeroStatPriority] = _heroViewModel.BuildPriorityPayload();
            _botConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save hero attribute priority: {ex.Message}");
        }
    }

    private void ApplyHeroAdventureAvailability(int? count)
    {
        if (count is null)
        {
            _heroViewModel.AdventureCountText = "?";
            return;
        }

        _heroViewModel.AdventureCountText = count.Value.ToString();
        if (count.Value > 0)
        {
            ClearHeroBlockedState();
            return;
        }

        if (!string.Equals(_heroBlockedReasonKey, HeroBlockedReasonNoAdventures, StringComparison.OrdinalIgnoreCase))
        {
            SetHeroBlockedState(HeroBlockedReasonNoAdventures, "No adventures");
        }
    }

    private async void RefreshHeroStatsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshHeroStatsButton.IsEnabled = false;
        var operationId = BeginOperation("Refresh hero stats");
        var operationSw = Stopwatch.StartNew();

        try
        {
            await EnsureChromiumInstalledAsync();
            var snapshot = await RefreshHeroStatsAsync(CancellationToken.None);
            CompleteOperation(operationId, operationSw, $"Hero stats refreshed. Free points: {snapshot.FreePoints}.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
            _heroViewModel.AttributesStatusText = $"Hero stats refresh failed: {ex.Message}";
        }
        finally
        {
            RefreshHeroStatsButton.IsEnabled = true;
        }
    }

    private async Task<HeroAttributeSnapshot> RefreshHeroStatsAsync(CancellationToken cancellationToken)
    {
        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        var snapshot = await _botService.ReadHeroAttributesAsync(options, AppendLog, cancellationToken);
        _heroViewModel.ApplyAttributeSnapshot(snapshot);
        return snapshot;
    }

    private void HeroHideModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        // Checked fires for the XAML-default IsChecked="True" radio during
        // InitializeComponent (before services are wired) and again when
        // LoadConfigToUi pushes the persisted hide mode into the VM through
        // a binding — both should be ignored. Only user-driven toggles after
        // the window is loaded should propagate to the worker.
        if (_suppressHeroHideModeApply || !IsLoaded)
        {
            return;
        }

        var mode = _heroViewModel.HideMode;
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.HeroHideMode] = mode,
        };
        EnqueueQuickTask("hero_set_hide_mode", $"Set hero hide mode to '{mode}'", payload);
    }

    private void QueueHeroManageButton_Click(object sender, RoutedEventArgs e)
    {
        var minHp = _heroViewModel.MinHpForAdventure;
        if (minHp < 1 || minHp > 100)
        {
            BuildingsInfoTextBlock.Text = "Hero minimum HP must be an integer 1-100.";
            return;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.HeroMinHpForAdventure] = minHp.ToString(),
            [BotOptionPayloadKeys.HeroAutoRevive] = _heroViewModel.AutoRevive ? "true" : "false",
            [BotOptionPayloadKeys.HeroAutoAssignPoints] = _heroViewModel.AutoAssignPoints ? "true" : "false",
            [BotOptionPayloadKeys.HeroStatPriority] = _heroViewModel.BuildPriorityPayload(),
            [BotOptionPayloadKeys.HeroAdventurePickOrder] = _heroViewModel.AdventurePickOrder,
            [BotOptionPayloadKeys.HeroHideMode] = _heroViewModel.HideMode,
        };

        var continuous = _heroViewModel.ContinuousAdventures;
        var copies = 1;
        if (continuous && int.TryParse(_heroViewModel.AdventureCountText.Trim(), out var available) && available > 1)
        {
            copies = Math.Min(available, 20); // hard cap to avoid runaway queues if count is wrong
        }

        for (var i = 0; i < copies; i++)
        {
            EnqueueQuickTask("hero_manage", "Hero adventure (with revive/points checks)", payload);
        }
        BuildingsInfoTextBlock.Text = continuous && copies > 1
            ? $"Queued {copies} hero adventures."
            : "Queued hero adventure.";
    }

    private async Task RefreshAdventureCountAfterLoginAsync(BotOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var count = await _botService.RefreshAdventureCountAsync(options, AppendLog, cancellationToken);
            if (count is null)
            {
                ApplyHeroAdventureAvailability(null);
                AppendLog("Adventure count: not found on current page.");
            }
            else
            {
                ApplyHeroAdventureAvailability(count.Value);
                AppendLog($"Adventure count after login: {count.Value}.");
            }
        }
        catch (OperationCanceledException)
        {
            // Login flow was cancelled — leave the count unchanged.
        }
        catch (Exception ex)
        {
            AppendLog($"Adventure count refresh after login failed: {ex.Message}");
        }
    }

    private async void RefreshAdventuresButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshAdventuresButton.IsEnabled = false;
        var operationId = BeginOperation("Refresh adventures");
        var operationSw = Stopwatch.StartNew();
        try
        {
            await EnsureChromiumInstalledAsync();
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            var count = await _botService.RefreshAdventureCountAsync(options, AppendLog, CancellationToken.None);
            if (count is null)
            {
                ApplyHeroAdventureAvailability(null);
                _heroViewModel.AdventureStatusText = "Adventures not found on current page.";
            }
            else
            {
                ApplyHeroAdventureAvailability(count.Value);
                _heroViewModel.AdventureStatusText = $"Adventures available: {count.Value}.";
            }

            CompleteOperation(operationId, operationSw, $"Refresh adventures: {(count?.ToString() ?? "not found")}.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
            _heroViewModel.AdventureStatusText = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            RefreshAdventuresButton.IsEnabled = true;
        }
    }
}
