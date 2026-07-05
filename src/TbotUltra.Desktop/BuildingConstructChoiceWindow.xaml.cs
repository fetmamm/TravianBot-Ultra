using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

public partial class BuildingConstructChoiceWindow : Window
{
    public BuildingCatalogOption? SelectedOption { get; private set; }

    /// <summary>Target level chosen by user. 0 means "build to max level".</summary>
    public int SelectedTargetLevel { get; private set; } = 1;

    private const string MaxLevelSentinel = "Max";

    // Returns the cumulative build time + cost from level 1 up to the given target level, or null when
    // unavailable.
    private readonly Func<int, int, BuildingNextLevelEstimate?>? _estimateProvider;

    public BuildingConstructChoiceWindow(
        int slotId,
        IReadOnlyList<BuildingCatalogOption> options,
        Func<int, int, BuildingNextLevelEstimate?>? estimateProvider = null)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);

        _estimateProvider = estimateProvider;
        TitleTextBlock.Text = $"Build in slot {slotId}";

        var available = options.Where(o => o.Availability == BuildingConstructAvailability.Available).ToList();
        var locked = options.Where(o => o.Availability == BuildingConstructAvailability.Locked).ToList();
        var alreadyBuilt = options.Where(o => o.Availability == BuildingConstructAvailability.AlreadyBuilt).ToList();
        var unavailable = options.Where(o => o.Availability == BuildingConstructAvailability.Unavailable)
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        PopulateCategorySection(InfrastructureAvailablePanel, available, "infrastructure", clickable: true);
        PopulateCategorySection(InfrastructureLockedPanel, locked, "infrastructure", clickable: false);
        PopulateCategorySection(InfrastructureAlreadyBuiltPanel, alreadyBuilt, "infrastructure", clickable: false);

        PopulateCategorySection(ArmyAvailablePanel, available, "army_buildings", clickable: true);
        PopulateCategorySection(ArmyLockedPanel, locked, "army_buildings", clickable: false);
        PopulateCategorySection(ArmyAlreadyBuiltPanel, alreadyBuilt, "army_buildings", clickable: false);

        PopulateCategorySection(ResourcesAvailablePanel, available, "resource_buildings", clickable: true);
        PopulateCategorySection(ResourcesLockedPanel, locked, "resource_buildings", clickable: false);
        PopulateCategorySection(ResourcesAlreadyBuiltPanel, alreadyBuilt, "resource_buildings", clickable: false);

        PopulateSection(UnavailablePanel, unavailable, clickable: false);
        if (unavailable.Count == 0)
        {
            AddPlaceholder(UnavailablePanel, "(none)");
        }
    }

    private void PopulateCategorySection(StackPanel panel, IReadOnlyList<BuildingCatalogOption> items, string categoryKey, bool clickable)
    {
        var entries = items
            .Where(o => string.Equals(o.Category?.Trim(), categoryKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (entries.Count <= 0)
        {
            AddPlaceholder(panel, "(none)");
            return;
        }

        foreach (var option in entries)
        {
            panel.Children.Add(BuildOptionElement(option, clickable));
        }
    }

    private void PopulateSection(StackPanel panel, IReadOnlyList<BuildingCatalogOption> items, bool clickable)
    {
        foreach (var option in items)
        {
            panel.Children.Add(BuildOptionElement(option, clickable));
        }
    }

    private static void AddPlaceholder(StackPanel panel, string text)
    {
        panel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(ThemeColors.Get("BorderMutedBrush")),
            FontStyle = FontStyles.Italic,
            FontSize = 11,
            Margin = new Thickness(4, 0, 0, 0),
        });
    }

    private FrameworkElement BuildOptionElement(BuildingCatalogOption option, bool clickable)
    {
        var detail = option.Availability switch
        {
            BuildingConstructAvailability.Available => $"{HumanizeCategory(option.Category)} · max level {option.MaxLevel}",
            BuildingConstructAvailability.Locked => $"Requires: {option.MissingRequirementsText}",
            BuildingConstructAvailability.AlreadyBuilt => option.UnavailableReason,
            BuildingConstructAvailability.Unavailable => option.UnavailableReason,
            _ => string.Empty,
        };

        var nameForeground = option.Availability switch
        {
            BuildingConstructAvailability.Available => ThemeColors.Get("TextPrimaryBrush"),
            BuildingConstructAvailability.Locked => ThemeColors.Get("TextSubtleBrush"),
            BuildingConstructAvailability.AlreadyBuilt => ThemeColors.Get("InfoBrush"),
            BuildingConstructAvailability.Unavailable => ThemeColors.Get("BorderMutedBrush"),
            _ => ThemeColors.Get("TextPrimaryBrush"),
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = option.DisplayLabel,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(nameForeground),
        });
        stack.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 11,
            Foreground = new SolidColorBrush(ThemeColors.Get("TextSubtleBrush")),
            TextWrapping = TextWrapping.Wrap,
        });

        if (clickable)
        {
            var button = new Button
            {
                Content = stack,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 4),
                Tag = option,
                Background = new SolidColorBrush(ThemeColors.Get("SurfaceBrush")),
                BorderBrush = new SolidColorBrush(ThemeColors.Get("BorderBrush")),
            };
            button.Click += OptionButton_Click;
            return button;
        }

        return new Border
        {
            Background = new SolidColorBrush(ThemeColors.Get("SurfaceAltBrush")),
            BorderBrush = new SolidColorBrush(ThemeColors.Get("ControlBackgroundBrush")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 4),
            Child = stack,
            Opacity = option.Availability == BuildingConstructAvailability.Unavailable ? 0.65 : 1.0,
        };
    }

    private static string HumanizeCategory(string? category)
    {
        return category?.Trim().ToLowerInvariant() switch
        {
            "infrastructure" => "Infrastructure",
            "army_buildings" => "Army",
            "resource_buildings" => "Resources",
            _ => "Other",
        };
    }

    private void OptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BuildingCatalogOption option })
        {
            return;
        }

        SelectedOption = option;
        SelectedNameTextBlock.Text = option.DisplayLabel;
        SelectedDetailsTextBlock.Text = $"{HumanizeCategory(option.Category)} · max level {option.MaxLevel}";

        TargetLevelComboBox.SelectionChanged -= TargetLevelComboBox_SelectionChanged;
        TargetLevelComboBox.Items.Clear();
        for (var lvl = 1; lvl <= option.MaxLevel; lvl++)
        {
            TargetLevelComboBox.Items.Add(lvl.ToString());
        }
        TargetLevelComboBox.Items.Add(MaxLevelSentinel);
        TargetLevelComboBox.SelectedIndex = 0;
        TargetLevelComboBox.IsEnabled = true;
        TargetLevelComboBox.SelectionChanged += TargetLevelComboBox_SelectionChanged;
        BuildButton.IsEnabled = true;
        BuildMaxButton.IsEnabled = true;
        UpdateEstimate();
    }

    private void TargetLevelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEstimate();

    // Refreshes the estimate box for the selected building and target level ("Max" => the building's max
    // level). Hidden when no provider or catalog data is available.
    private void UpdateEstimate()
    {
        if (_estimateProvider is null || SelectedOption is null)
        {
            EstimatePanel.Visibility = Visibility.Collapsed;
            return;
        }

        var selected = TargetLevelComboBox.SelectedItem?.ToString();
        var targetLevel = string.Equals(selected, MaxLevelSentinel, StringComparison.OrdinalIgnoreCase)
            ? SelectedOption.MaxLevel
            : int.TryParse(selected, out var lvl) ? lvl : 1;

        var estimate = _estimateProvider(SelectedOption.Gid, targetLevel);
        if (estimate is null)
        {
            EstimatePanel.Visibility = Visibility.Collapsed;
            return;
        }

        TimeTextBlock.Text = estimate.TimeText;
        ConstructFasterTimeTextBlock.Text =
            $"(25% {QueueItemRowFactory.FormatBuildDuration(estimate.Seconds * 0.75)})";
        WoodTextBlock.Text = estimate.WoodText;
        ClayTextBlock.Text = estimate.ClayText;
        IronTextBlock.Text = estimate.IronText;
        CropTextBlock.Text = estimate.CropText;
        EstimatePanel.Visibility = Visibility.Visible;
    }

    private void BuildButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedOption is null)
        {
            return;
        }

        var selected = TargetLevelComboBox.SelectedItem?.ToString();
        if (string.Equals(selected, MaxLevelSentinel, StringComparison.OrdinalIgnoreCase))
        {
            SelectedTargetLevel = 0;
        }
        else if (int.TryParse(selected, out var lvl))
        {
            SelectedTargetLevel = lvl;
        }
        else
        {
            SelectedTargetLevel = 1;
        }

        DialogResult = true;
        Close();
    }

    private void BuildMaxButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedOption is null)
        {
            return;
        }

        SelectedTargetLevel = 0;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
