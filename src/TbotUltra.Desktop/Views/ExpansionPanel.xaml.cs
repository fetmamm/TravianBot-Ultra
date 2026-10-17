using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace TbotUltra.Desktop.Views;

public partial class ExpansionPanel : UserControl
{
    public event Action? TrainSettlersRequested;
    public event Action<int, int, int?>? FoundVillageRequested;

    public ExpansionPanel() => InitializeComponent();

    public void ShowStatus(string text) => ExpansionStatusText.Text = text;

    private void TrainSettlers_Click(object sender, RoutedEventArgs e) => TrainSettlersRequested?.Invoke();

    private void FoundVillage_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(FoundXTextBox.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(FoundYTextBox.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var y))
        {
            ShowStatus("Enter whole-number X and Y coordinates.");
            return;
        }

        int? tribeId = FoundTribeComboBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var parsed)
            ? parsed
            : null;
        FoundVillageRequested?.Invoke(x, y, tribeId);
    }
}
