using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Core.Configuration;
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
        SelectQueueWaitThresholdMode(_config[BotOptionPayloadKeys.QueueWaitThresholdMode]?.GetValue<string>() ?? "10");
        SelectFarmDispatchDelayMinutes(_config[BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinutes]?.GetValue<int>() ?? 1);
        PostLoginAnalyzeFarmlistsCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginAnalyzeFarmlists]?.GetValue<bool>() ?? false;
        PostLoginAnalyzeHeroCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginAnalyzeHero]?.GetValue<bool>() ?? false;
        PostLoginReadTroopTrainingQueueCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue]?.GetValue<bool>() ?? false;
        GoldLimitSlider.Value = Math.Clamp(_config["gold_limit"]?.GetValue<int>() ?? 100, 0, 200);
        SilverLimitSlider.Value = Math.Clamp(_config["silver_limit"]?.GetValue<int>() ?? 100, 0, 1000);
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
            _config[BotOptionPayloadKeys.QueueWaitThresholdMode] = GetSelectedQueueWaitThresholdMode();
            _config[BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinutes] = GetSelectedFarmDispatchDelayMinutes();
            _config[BotOptionPayloadKeys.PostLoginAnalyzeFarmlists] = PostLoginAnalyzeFarmlistsCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.PostLoginAnalyzeHero] = PostLoginAnalyzeHeroCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue] = PostLoginReadTroopTrainingQueueCheckBox.IsChecked == true;
            _config["gold_limit"] = (int)Math.Round(GoldLimitSlider.Value);
            _config["silver_limit"] = (int)Math.Round(SilverLimitSlider.Value);
            _store.Save(_config);

            _isClosing = true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, ex.Message, "Save settings", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        SelectQueueWaitThresholdMode("10");
        SelectFarmDispatchDelayMinutes(1);
        PostLoginAnalyzeFarmlistsCheckBox.IsChecked = false;
        PostLoginAnalyzeHeroCheckBox.IsChecked = false;
        PostLoginReadTroopTrainingQueueCheckBox.IsChecked = false;
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

    private void SelectQueueWaitThresholdMode(string mode)
    {
        var normalized = string.IsNullOrWhiteSpace(mode) ? "10" : mode.Trim();
        foreach (var item in QueueWaitThresholdComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                QueueWaitThresholdComboBox.SelectedItem = item;
                return;
            }
        }

        QueueWaitThresholdComboBox.SelectedIndex = 2;
    }

    private string GetSelectedQueueWaitThresholdMode()
    {
        if (QueueWaitThresholdComboBox.SelectedItem is ComboBoxItem item
            && !string.IsNullOrWhiteSpace(item.Tag?.ToString()))
        {
            return item.Tag!.ToString()!;
        }

        return "10";
    }

    private void SelectFarmDispatchDelayMinutes(int minutes)
    {
        var normalized = Math.Clamp(minutes, 1, 5).ToString();
        foreach (var item in FarmDispatchDelayComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                FarmDispatchDelayComboBox.SelectedItem = item;
                return;
            }
        }

        FarmDispatchDelayComboBox.SelectedIndex = 0;
    }

    private int GetSelectedFarmDispatchDelayMinutes()
    {
        if (FarmDispatchDelayComboBox.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out var minutes))
        {
            return Math.Clamp(minutes, 1, 5);
        }

        return 1;
    }
}

