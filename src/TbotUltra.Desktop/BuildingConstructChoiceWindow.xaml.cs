using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop;

public partial class BuildingConstructChoiceWindow : Window
{
    public BuildingCatalogOption? SelectedOption { get; private set; }

    /// <summary>Target level chosen by user. 0 means "build to max level".</summary>
    public int SelectedTargetLevel { get; private set; } = 1;

    private const string MaxLevelSentinel = "Max";

    public BuildingConstructChoiceWindow(int slotId, IReadOnlyList<BuildingCatalogOption> options)
    {
        InitializeComponent();

        TitleTextBlock.Text = $"Build in slot {slotId}";

        var available = options.Where(o => o.Availability == BuildingConstructAvailability.Available)
            .OrderBy(o => o.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var locked = options.Where(o => o.Availability == BuildingConstructAvailability.Locked)
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var alreadyBuilt = options.Where(o => o.Availability == BuildingConstructAvailability.AlreadyBuilt)
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var unavailable = options.Where(o => o.Availability == BuildingConstructAvailability.Unavailable)
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        PopulateAvailableByCategory(AvailablePanel, available);
        PopulateSection(LockedPanel, locked, clickable: false);
        PopulateSection(AlreadyBuiltPanel, alreadyBuilt, clickable: false);
        PopulateSection(UnavailablePanel, unavailable, clickable: false);

        if (available.Count == 0)
        {
            AddPlaceholder(AvailablePanel, "No buildings ready to construct here.");
        }
        if (locked.Count == 0)
        {
            AddPlaceholder(LockedPanel, "(none)");
        }
        if (alreadyBuilt.Count == 0)
        {
            AddPlaceholder(AlreadyBuiltPanel, "(none)");
        }
        if (unavailable.Count == 0)
        {
            AddPlaceholder(UnavailablePanel, "(none)");
        }
    }

    private void PopulateSection(StackPanel panel, IReadOnlyList<BuildingCatalogOption> items, bool clickable)
    {
        foreach (var option in items)
        {
            panel.Children.Add(BuildOptionElement(option, clickable));
        }
    }

    private static readonly (string Key, string Label)[] CategoryOrder =
    {
        ("infrastructure", "Infrastructure"),
        ("army_buildings", "Army"),
        ("resource_buildings", "Resource buildings"),
    };

    private void PopulateAvailableByCategory(StackPanel panel, IReadOnlyList<BuildingCatalogOption> items)
    {
        var grouped = items
            .GroupBy(o => o.Category ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var (key, label) in CategoryOrder)
        {
            if (!grouped.TryGetValue(key, out var entries) || entries.Count == 0)
            {
                continue;
            }

            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)),
                Margin = new Thickness(0, 6, 0, 4),
            });
            foreach (var option in entries.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
            {
                panel.Children.Add(BuildOptionElement(option, clickable: true));
            }
        }

        // Any leftover categories not in the main three (e.g., resource_field) — shown last.
        var seenKeys = CategoryOrder.Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in grouped)
        {
            if (seenKeys.Contains(pair.Key) || pair.Value.Count == 0)
            {
                continue;
            }

            var fallbackLabel = string.IsNullOrWhiteSpace(pair.Key)
                ? "Other"
                : char.ToUpper(pair.Key[0]) + pair.Key[1..].Replace('_', ' ');
            panel.Children.Add(new TextBlock
            {
                Text = fallbackLabel,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)),
                Margin = new Thickness(0, 6, 0, 4),
            });
            foreach (var option in pair.Value.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
            {
                panel.Children.Add(BuildOptionElement(option, clickable: true));
            }
        }
    }

    private static void AddPlaceholder(StackPanel panel, string text)
    {
        panel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
            FontStyle = FontStyles.Italic,
            FontSize = 11,
            Margin = new Thickness(4, 0, 0, 0),
        });
    }

    private FrameworkElement BuildOptionElement(BuildingCatalogOption option, bool clickable)
    {
        var detail = option.Availability switch
        {
            BuildingConstructAvailability.Available => $"{option.Category} · max level {option.MaxLevel}",
            BuildingConstructAvailability.Locked => $"Requires: {option.MissingRequirementsText}",
            BuildingConstructAvailability.AlreadyBuilt => option.UnavailableReason,
            BuildingConstructAvailability.Unavailable => option.UnavailableReason,
            _ => string.Empty,
        };

        var nameForeground = option.Availability switch
        {
            BuildingConstructAvailability.Available => Color.FromRgb(0x11, 0x18, 0x27),
            BuildingConstructAvailability.Locked => Color.FromRgb(0x6B, 0x72, 0x80),
            BuildingConstructAvailability.AlreadyBuilt => Color.FromRgb(0x25, 0x63, 0xEB),
            BuildingConstructAvailability.Unavailable => Color.FromRgb(0x9C, 0xA3, 0xAF),
            _ => Color.FromRgb(0x11, 0x18, 0x27),
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
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
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
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
            };
            button.Click += OptionButton_Click;
            return button;
        }

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 4),
            Child = stack,
            Opacity = option.Availability == BuildingConstructAvailability.Unavailable ? 0.65 : 1.0,
        };
        return border;
    }

    private void OptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BuildingCatalogOption option })
        {
            return;
        }

        SelectedOption = option;
        SelectedNameTextBlock.Text = option.DisplayLabel;
        SelectedDetailsTextBlock.Text = $"{option.Category} · max level {option.MaxLevel}";

        TargetLevelComboBox.Items.Clear();
        for (var lvl = 1; lvl <= option.MaxLevel; lvl++)
        {
            TargetLevelComboBox.Items.Add(lvl.ToString());
        }
        TargetLevelComboBox.Items.Add(MaxLevelSentinel);
        TargetLevelComboBox.SelectedIndex = 0;
        TargetLevelComboBox.IsEnabled = true;
        BuildButton.IsEnabled = true;
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
            SelectedTargetLevel = 0; // 0 = build to max
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

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
