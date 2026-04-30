using System.Windows;
using System.Windows.Controls;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop;

public partial class BuildingConstructChoiceWindow : Window
{
    public BuildingCatalogOption? SelectedOption { get; private set; }

    public BuildingConstructChoiceWindow(int slotId, IReadOnlyList<BuildingCatalogOption> options)
    {
        InitializeComponent();

        TitleTextBlock.Text = $"Build in slot {slotId}";

        foreach (var option in options)
        {
            var button = new Button
            {
                Content = option.DisplayLabel,
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Tag = option,
                ToolTip = option.Requirements == "-"
                    ? "No requirements."
                    : $"Requires: {option.Requirements}",
            };
            button.Click += OptionButton_Click;
            OptionsPanel.Children.Add(button);
        }
    }

    private void OptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BuildingCatalogOption option })
        {
            return;
        }

        SelectedOption = option;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
