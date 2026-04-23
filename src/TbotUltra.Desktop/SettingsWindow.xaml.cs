using System.Text.Json.Nodes;
using System.Windows;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

public partial class SettingsWindow : Window
{
    private readonly BotConfigStore _store;
    private JsonObject _config = [];
    private bool _isClosing;

    public SettingsWindow(BotConfigStore store)
    {
        InitializeComponent();
        _store = store;
        LoadConfig();
    }

    private void LoadConfig()
    {
        _config = _store.Load();
        HeadlessCheckBox.IsChecked = _config["headless"]?.GetValue<bool>() ?? false;
        HumanLikeCheckBox.IsChecked = _config["human_like_enabled"]?.GetValue<bool>() ?? false;
        AllowGoldSpendingCheckBox.IsChecked = _config["allow_gold_spending"]?.GetValue<bool>() ?? false;
        AllowSilverSpendingCheckBox.IsChecked = _config["allow_silver_spending"]?.GetValue<bool>() ?? false;
        GoldLimitSlider.Value = Math.Clamp(_config["gold_limit"]?.GetValue<int>() ?? 100, 0, 200);
        SilverLimitSlider.Value = Math.Clamp(_config["silver_limit"]?.GetValue<int>() ?? 100, 0, 200);
        UpdateLimitLabels();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _config["headless"] = HeadlessCheckBox.IsChecked == true;
            _config["human_like_enabled"] = HumanLikeCheckBox.IsChecked == true;
            _config["allow_gold_spending"] = AllowGoldSpendingCheckBox.IsChecked == true;
            _config["allow_silver_spending"] = AllowSilverSpendingCheckBox.IsChecked == true;
            _config["gold_limit"] = (int)Math.Round(GoldLimitSlider.Value);
            _config["silver_limit"] = (int)Math.Round(SilverLimitSlider.Value);
            _store.Save(_config);

            _isClosing = true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _isClosing = true;
        DialogResult = false;
        Close();
    }

    private void SettingsWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        HeadlessCheckBox.IsChecked = false;
        HumanLikeCheckBox.IsChecked = false;
        AllowGoldSpendingCheckBox.IsChecked = false;
        AllowSilverSpendingCheckBox.IsChecked = false;
        GoldLimitSlider.Value = 100;
        SilverLimitSlider.Value = 100;
        UpdateLimitLabels();
    }

    private void GoldLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateLimitLabels();
    }

    private void SilverLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateLimitLabels();
    }

    private void UpdateLimitLabels()
    {
        if (GoldLimitTextBlock is null || SilverLimitTextBlock is null)
        {
            return;
        }

        GoldLimitTextBlock.Text = $"Gold limit: {(int)Math.Round(GoldLimitSlider.Value)}";
        SilverLimitTextBlock.Text = $"Silver limit: {(int)Math.Round(SilverLimitSlider.Value)}";
    }
}
