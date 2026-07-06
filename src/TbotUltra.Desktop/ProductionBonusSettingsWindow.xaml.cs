using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

/// <summary>
/// Read-only popup showing the per-resource production bonus timers: a yellow row for the +25% (gold)
/// bonuses and a purple row for the +15% (free video) bonuses, or when the +15% video will next be
/// tried. Countdowns are recomputed every second from the store's absolute UTC times.
/// </summary>
public partial class ProductionBonusSettingsWindow : Window
{
    // Display order matches the game (Wood/Clay/Iron/Crop) and the store resource keys.
    private static readonly (string Key, string Label)[] ResourceColumns =
    {
        ("lumber", "Wood"),
        ("clay", "Clay"),
        ("iron", "Iron"),
        ("crop", "Crop"),
    };

    private const int StoreReloadEveryTicks = 3;

    private readonly string _projectRoot;
    private readonly string _accountName;
    private readonly Action? _onScanRequested;
    private readonly Action? _onClearRequested;
    private readonly DispatcherTimer _tick;
    private int _tickCount;

    private Dictionary<string, ProductionBonusResourceTimer> _timersByResource = new(StringComparer.OrdinalIgnoreCase);

    // Badge cells keyed by "<bonus>:<resource>" (e.g. "25:lumber"), so the tick can update them in place.
    private readonly Dictionary<string, Border> _badgeBorders = new();
    private readonly Dictionary<string, TextBlock> _badgeTexts = new();

    public ProductionBonusSettingsWindow(
        string projectRoot,
        string accountName,
        Action? onScanRequested = null,
        Action? onClearRequested = null)
    {
        InitializeComponent();

        _projectRoot = projectRoot;
        _accountName = accountName;
        _onScanRequested = onScanRequested;
        _onClearRequested = onClearRequested;

        BuildGrid();
        LoadDelaySettings();
        ReloadTimersFromStore();
        UpdateTimers();

        // Recompute the countdowns every second (from already-loaded absolute times) but only re-read the
        // store from disk every few seconds, so a "Scan timers"/auto run's fresh state appears while open
        // without hitting the (possibly OneDrive-synced) file on every tick.
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) =>
        {
            _tickCount++;
            if (_tickCount % StoreReloadEveryTicks == 0)
            {
                ReloadTimersFromStore();
            }

            UpdateTimers();
        };
        _tick.Start();

        Closed += (_, _) => _tick.Stop();
    }

    private void ReloadTimersFromStore()
    {
        _timersByResource = ProductionBonusStateStore.Load(_projectRoot, _accountName)
            .GroupBy(timer => timer.Resource, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    private void BuildGrid()
    {
        // Column headers (row 0): one per resource, starting at column 1.
        for (var i = 0; i < ResourceColumns.Length; i++)
        {
            var header = new TextBlock
            {
                Text = ResourceColumns[i].Label,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 8),
                Foreground = (Brush)FindResource("TextMutedBrush"),
            };
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, i + 1);
            TimersGrid.Children.Add(header);
        }

        BuildBadgeRow(rowIndex: 1, bonus: 25, rowLabel: "+25%");
        BuildBadgeRow(rowIndex: 2, bonus: 15, rowLabel: "+15%");
    }

    private void BuildBadgeRow(int rowIndex, int bonus, string rowLabel)
    {
        var label = new TextBlock
        {
            Text = rowLabel,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 8, 6),
            Foreground = (Brush)FindResource(bonus == 25 ? "WarningTextBrush" : "PurpleTextBrush"),
        };
        Grid.SetRow(label, rowIndex);
        Grid.SetColumn(label, 0);
        TimersGrid.Children.Add(label);

        for (var i = 0; i < ResourceColumns.Length; i++)
        {
            var text = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
            };

            var badge = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(4, 6, 4, 6),
                BorderThickness = new Thickness(1),
                MinWidth = 78,
                Child = text,
            };

            Grid.SetRow(badge, rowIndex);
            Grid.SetColumn(badge, i + 1);
            TimersGrid.Children.Add(badge);

            var key = BadgeKey(bonus, ResourceColumns[i].Key);
            _badgeBorders[key] = badge;
            _badgeTexts[key] = text;
        }
    }

    private enum BadgeStyle
    {
        Gray,
        Yellow,
        Purple,
    }

    private void UpdateTimers()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (resourceKey, _) in ResourceColumns)
        {
            _timersByResource.TryGetValue(resourceKey, out var timer);

            var (seconds25, style25) = Resolve25Badge(timer, now);
            UpdateBadge(BadgeKey(25, resourceKey), seconds25, style25);

            var (seconds15, style15) = Resolve15Badge(timer, now);
            UpdateBadge(BadgeKey(15, resourceKey), seconds15, style15);
        }
    }

    // Only one bonus is active per resource (the store holds a single Bonus value), so the +25% badge
    // shows a timer only when +25% is the active bonus.
    private static (int? Seconds, BadgeStyle Style) Resolve25Badge(ProductionBonusResourceTimer? timer, DateTimeOffset now)
    {
        if (Is25Active(timer, now))
        {
            return ((int)(timer!.BonusEndsAtUtc - now).TotalSeconds, BadgeStyle.Yellow);
        }

        return (null, BadgeStyle.Gray);
    }

    // Purple while the free +15% is actually running; gold while it is waiting for the next run
    // (daily cooldown). Never shows when +25% owns the resource — only one timer per resource.
    private static (int? Seconds, BadgeStyle Style) Resolve15Badge(ProductionBonusResourceTimer? timer, DateTimeOffset now)
    {
        if (timer is null || Is25Active(timer, now))
        {
            return (null, BadgeStyle.Gray);
        }

        if (timer.Bonus == 15 && timer.BonusEndsAtUtc > now)
        {
            return ((int)(timer.BonusEndsAtUtc - now).TotalSeconds, BadgeStyle.Purple);
        }

        if (timer.NextAttemptAtUtc > now)
        {
            return ((int)(timer.NextAttemptAtUtc - now).TotalSeconds, BadgeStyle.Yellow);
        }

        return (null, BadgeStyle.Gray);
    }

    private static bool Is25Active(ProductionBonusResourceTimer? timer, DateTimeOffset now)
        => timer is { Bonus: 25 } && timer.BonusEndsAtUtc > now;

    private void UpdateBadge(string key, int? seconds, BadgeStyle style)
    {
        if (!_badgeBorders.TryGetValue(key, out var badge) || !_badgeTexts.TryGetValue(key, out var text))
        {
            return;
        }

        if (style == BadgeStyle.Gray || seconds is null || seconds <= 0)
        {
            badge.Background = (Brush)FindResource("ControlBackgroundBrush");
            badge.BorderBrush = (Brush)FindResource("BorderMutedBrush");
            text.Foreground = (Brush)FindResource("TextMutedBrush");
            text.Text = "Not active";
            return;
        }

        if (style == BadgeStyle.Yellow)
        {
            badge.Background = (Brush)FindResource("WarningBgBrush");
            badge.BorderBrush = (Brush)FindResource("WarningBorderBrush");
            text.Foreground = (Brush)FindResource("WarningTextBrush");
        }
        else
        {
            badge.Background = (Brush)FindResource("PurpleBgBrush");
            badge.BorderBrush = (Brush)FindResource("PurpleBorderBrush");
            text.Foreground = (Brush)FindResource("PurpleTextBrush");
        }

        text.Text = FormatDuration(seconds.Value);
    }

    private static string FormatDuration(int totalSeconds)
    {
        var span = TimeSpan.FromSeconds(totalSeconds);
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return $"{span.Minutes}m {span.Seconds}s";
    }

    private static string BadgeKey(int bonus, string resource) => $"{bonus}:{resource}";

    private void LoadDelaySettings()
    {
        var settings = ProductionBonusStateStore.LoadSettings(_projectRoot, _accountName);
        DelayMinTextBox.Text = settings.DelayMinMinutes.ToString();
        DelayMaxTextBox.Text = settings.DelayMaxMinutes.ToString();
    }

    private void DelaySetting_Changed(object sender, RoutedEventArgs e)
    {
        var min = ParseDelayBox(DelayMinTextBox, ProductionBonusStateStore.DefaultDelayMinMinutes);
        var max = ParseDelayBox(DelayMaxTextBox, ProductionBonusStateStore.DefaultDelayMaxMinutes);
        var (normalizedMin, normalizedMax) = ProductionBonusStateStore.NormalizeDelay(min, max);

        // Reflect the normalized (swapped/clamped) values back into the boxes.
        DelayMinTextBox.Text = normalizedMin.ToString();
        DelayMaxTextBox.Text = normalizedMax.ToString();
        ProductionBonusStateStore.SaveSettings(_projectRoot, _accountName, normalizedMin, normalizedMax);
    }

    private static int ParseDelayBox(TextBox box, int fallback)
        => int.TryParse(box.Text?.Trim(), out var value) ? value : fallback;

    private void ScanTimersButton_Click(object sender, RoutedEventArgs e)
    {
        _onScanRequested?.Invoke();
        // Leave the window open: the scan writes the store and the tick picks up the new timers.
    }

    private void ClearTimersButton_Click(object sender, RoutedEventArgs e)
    {
        _onClearRequested?.Invoke();
        // Reflect the wipe immediately; the tick keeps it in sync afterwards.
        ReloadTimersFromStore();
        UpdateTimers();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
