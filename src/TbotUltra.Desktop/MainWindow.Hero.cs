using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private string _heroCountdownLabel = "Hero away";

    private void LoadHeroPriorityToUi(string? configuredPriority)
    {
        var order = ParseHeroPriorityForUi(configuredPriority);
        var existingPoints = _heroAttributePriorityItems.ToDictionary(item => item.Key, item => item.PointsText, StringComparer.OrdinalIgnoreCase);
        _heroAttributePriorityItems.Clear();

        for (var i = 0; i < order.Count; i++)
        {
            _heroAttributePriorityItems.Add(new HeroAttributePriorityItem
            {
                Key = order[i],
                Title = GetHeroAttributeTitle(order[i]),
                Order = i + 1,
                PointsText = existingPoints.GetValueOrDefault(order[i], "-"),
            });
        }
    }

    private void UpdateHeroPriorityOrders()
    {
        for (var i = 0; i < _heroAttributePriorityItems.Count; i++)
        {
            _heroAttributePriorityItems[i].Order = i + 1;
        }
    }

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
        var fromIndex = _heroAttributePriorityItems.IndexOf(sourceItem);
        if (fromIndex < 0)
        {
            return;
        }

        var toIndex = targetItem is null
            ? _heroAttributePriorityItems.Count - 1
            : _heroAttributePriorityItems.IndexOf(targetItem);
        if (toIndex < 0 || fromIndex == toIndex)
        {
            return;
        }

        _heroAttributePriorityItems.Move(fromIndex, toIndex);
        UpdateHeroPriorityOrders();
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

    private void ApplyHeroAttributeSnapshotToUi(HeroAttributeSnapshot snapshot)
    {
        AppendLog(
            $"[ui-apply] free={snapshot.FreePoints} fight={snapshot.FightingStrength} off={snapshot.OffenceBonus} def={snapshot.DefenceBonus} res={snapshot.Resources}, items={_heroAttributePriorityItems.Count}, thread=" +
            (Dispatcher.CheckAccess() ? "ui" : "background"));

        foreach (var item in _heroAttributePriorityItems)
        {
            var points = item.Key switch
            {
                "fighting_strength" => snapshot.FightingStrength,
                "offence_bonus" => snapshot.OffenceBonus,
                "defence_bonus" => snapshot.DefenceBonus,
                "resources" => snapshot.Resources,
                _ => 0,
            };
            item.PointsText = points.ToString();
        }

        HeroAttributesStatusTextBlock.Text = $"Free points: {snapshot.FreePoints}";
    }

    private string BuildHeroPriorityPayload()
    {
        return string.Join(",", _heroAttributePriorityItems.Select(item => item.Key));
    }

    private void PersistHeroPriorityToConfig()
    {
        try
        {
            var config = _botConfigStore.Load();
            config[BotOptionPayloadKeys.HeroStatPriority] = BuildHeroPriorityPayload();
            _botConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save hero attribute priority: {ex.Message}");
        }
    }

    private static List<string> ParseHeroPriorityForUi(string? value)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fighting_strength"] = "fighting_strength",
            ["fighting strength"] = "fighting_strength",
            ["fight"] = "fighting_strength",
            ["strength"] = "fighting_strength",
            ["offence_bonus"] = "offence_bonus",
            ["offence bonus"] = "offence_bonus",
            ["offense_bonus"] = "offence_bonus",
            ["offense bonus"] = "offence_bonus",
            ["offence"] = "offence_bonus",
            ["offense"] = "offence_bonus",
            ["off"] = "offence_bonus",
            ["attack"] = "offence_bonus",
            ["defence_bonus"] = "defence_bonus",
            ["defence bonus"] = "defence_bonus",
            ["defense_bonus"] = "defence_bonus",
            ["defense bonus"] = "defence_bonus",
            ["defence"] = "defence_bonus",
            ["defense"] = "defence_bonus",
            ["def"] = "defence_bonus",
            ["resources"] = "resources",
            ["resource"] = "resources",
            ["production"] = "resources",
        };

        var parsed = (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => map.GetValueOrDefault(item, string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var fallback in new[] { "fighting_strength", "offence_bonus", "defence_bonus", "resources" })
        {
            if (!parsed.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            {
                parsed.Add(fallback);
            }
        }

        return parsed;
    }

    private static string GetHeroAttributeTitle(string key) => key switch
    {
        "fighting_strength" => "Fighting strength",
        "offence_bonus" => "Offence bonus",
        "defence_bonus" => "Defence bonus",
        "resources" => "Resources",
        _ => key,
    };

    private void ApplyHeroAdventureAvailability(int? count)
    {
        if (count is null)
        {
            HeroAdventureCountTextBlock.Text = "?";
            return;
        }

        HeroAdventureCountTextBlock.Text = count.Value.ToString();
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

    private void HeroPriorityMoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HeroAttributePriorityItem item })
        {
            return;
        }

        var index = _heroAttributePriorityItems.IndexOf(item);
        if (index <= 0)
        {
            return;
        }

        _heroAttributePriorityItems.Move(index, index - 1);
        UpdateHeroPriorityOrders();
        PersistHeroPriorityToConfig();
    }

    private void HeroPriorityMoveDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HeroAttributePriorityItem item })
        {
            return;
        }

        var index = _heroAttributePriorityItems.IndexOf(item);
        if (index < 0 || index >= _heroAttributePriorityItems.Count - 1)
        {
            return;
        }

        _heroAttributePriorityItems.Move(index, index + 1);
        UpdateHeroPriorityOrders();
        PersistHeroPriorityToConfig();
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
            HeroAttributesStatusTextBlock.Text = $"Hero stats refresh failed: {ex.Message}";
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
        ApplyHeroAttributeSnapshotToUi(snapshot);
        return snapshot;
    }

    private void HeroHideModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        // Checked fires during InitializeComponent for the XAML-default IsChecked="True", before
        // services and other controls are wired — bail out until the window has finished loading.
        if (_suppressHeroHideModeApply || !IsLoaded)
        {
            return;
        }

        var mode = HeroFightRadio?.IsChecked == true ? "fight" : "hide";
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.HeroHideMode] = mode,
        };
        EnqueueQuickTask("hero_set_hide_mode", $"Set hero hide mode to '{mode}'", payload);
    }

    private void QueueHeroManageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(HeroMinHpTextBox.Text.Trim(), out var minHp) || minHp < 1 || minHp > 100)
        {
            BuildingsInfoTextBlock.Text = "Hero minimum HP must be an integer 1-100.";
            return;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.HeroMinHpForAdventure] = minHp.ToString(),
            [BotOptionPayloadKeys.HeroAutoRevive] = HeroAutoReviveCheckBox.IsChecked == true ? "true" : "false",
            [BotOptionPayloadKeys.HeroAutoAssignPoints] = HeroAutoAssignPointsCheckBox.IsChecked == true ? "true" : "false",
            [BotOptionPayloadKeys.HeroStatPriority] = BuildHeroPriorityPayload(),
            [BotOptionPayloadKeys.HeroAdventurePickOrder] = HeroAdventureTopRadio?.IsChecked == true ? "top" : "shortest",
            [BotOptionPayloadKeys.HeroHideMode] = HeroFightRadio?.IsChecked == true ? "fight" : "hide",
        };

        var continuous = ContinuousAdventuresCheckBox?.IsChecked == true;
        var copies = 1;
        if (continuous && int.TryParse(HeroAdventureCountTextBlock.Text.Trim(), out var available) && available > 1)
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
                HeroAdventureStatusTextBlock.Text = "Adventures not found on current page.";
            }
            else
            {
                ApplyHeroAdventureAvailability(count.Value);
                HeroAdventureStatusTextBlock.Text = $"Adventures available: {count.Value}.";
            }

            CompleteOperation(operationId, operationSw, $"Refresh adventures: {(count?.ToString() ?? "not found")}.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
            HeroAdventureStatusTextBlock.Text = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            RefreshAdventuresButton.IsEnabled = true;
        }
    }

    private void StartHeroCountdown(int seconds, int adventuresLeft, string label)
    {
        StopHeroCountdown();
        _heroCountdownRemainingSeconds = Math.Max(0, seconds);
        _heroCountdownLabel = label;
        UpdateHeroCountdownText(adventuresLeft);

        _heroCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _heroCountdownTimer.Tick += (_, _) =>
        {
            if (_heroCountdownRemainingSeconds > 0)
            {
                _heroCountdownRemainingSeconds -= 1;
            }
            UpdateHeroCountdownText(adventuresLeft);

            if (_heroCountdownRemainingSeconds <= 0)
            {
                StopHeroCountdown();
            }
        };
        _heroCountdownTimer.Start();
    }

    private void StopHeroCountdown()
    {
        if (_heroCountdownTimer is null)
        {
            return;
        }

        _heroCountdownTimer.Stop();
        _heroCountdownTimer = null;
    }

    private void UpdateHeroCountdownText(int adventuresLeft)
    {
        var formatted = FormatHeroDuration(_heroCountdownRemainingSeconds);
        HeroAdventureStatusTextBlock.Text =
            $"{_heroCountdownLabel}. Returns in {formatted}. Adventures left: {adventuresLeft}.";
    }

    private static string FormatHeroDuration(int seconds)
    {
        var clamped = Math.Max(0, seconds);
        var ts = TimeSpan.FromSeconds(clamped);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }
}
