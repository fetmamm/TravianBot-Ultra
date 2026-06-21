using System.Globalization;
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
    private readonly bool _sessionSleeping;

    // Set when the user confirms "Sleep now"; MainWindow reads it after ShowDialog to trigger the sleep.
    public bool SleepNowRequested { get; private set; }

    public SettingsWindow(BotConfigStore store, bool sessionSleeping = false)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        _store = store;
        _sessionSleeping = sessionSleeping;
        InitializeSessionPacingChoices();
        LoadConfig();
        SleepNowButton.IsEnabled = !_sessionSleeping;
    }

    private void LoadConfig()
    {
        _config = _store.Load();
        // Headless is forced off and the checkbox is disabled so the bot can never start headless.
        HeadlessCheckBox.IsChecked = false;
        AllowSilverSpendingCheckBox.IsChecked = _config["allow_silver_spending"]?.GetValue<bool>() ?? false;
        LoadPacingConfigToUi();
        SelectQueueWaitThresholdMode(_config[BotOptionPayloadKeys.QueueWaitThresholdMode]?.GetValue<string>() ?? "smart");
        PostLoginAnalyzeFarmlistsCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginAnalyzeFarmlists]?.GetValue<bool>() ?? false;
        PostLoginAnalyzeHeroCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginAnalyzeHero]?.GetValue<bool>() ?? false;
        PostLoginReadTroopTrainingQueueCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue]?.GetValue<bool>() ?? false;
        PostLoginAnalyzeBreweryCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginAnalyzeBrewery]?.GetValue<bool>() ?? false;
        PostLoginAnalyzeHeroInventoryCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginAnalyzeHeroInventory]?.GetValue<bool>() ?? true;
        PostLoginAnalyzeNewVillagesCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginAnalyzeNewVillages]?.GetValue<bool>() ?? true;
        SilverLimitSlider.Value = Math.Clamp(_config["silver_limit"]?.GetValue<int>() ?? 100, 0, 1000);
        UpdateLimitLabels();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PersistConfig())
        {
            return;
        }

        _isClosing = true;
        DialogResult = true;
        Close();
    }

    // Writes the current UI values to the config store. Returns false (and shows the error) on failure so
    // callers can abort closing. Shared by Save and the "Sleep now" button.
    private bool PersistConfig()
    {
        try
        {
            _config["headless"] = false; // Forced false: the bot must not run headless. Checkbox is disabled in the UI.
            _config["allow_silver_spending"] = AllowSilverSpendingCheckBox.IsChecked == true;
            SavePacingConfigFromUi();
            _config[BotOptionPayloadKeys.QueueWaitThresholdMode] = GetSelectedQueueWaitThresholdMode();
            _config[BotOptionPayloadKeys.PostLoginAnalyzeFarmlists] = PostLoginAnalyzeFarmlistsCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.PostLoginAnalyzeHero] = PostLoginAnalyzeHeroCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue] = PostLoginReadTroopTrainingQueueCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.PostLoginAnalyzeBrewery] = PostLoginAnalyzeBreweryCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.PostLoginAnalyzeHeroInventory] = PostLoginAnalyzeHeroInventoryCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.PostLoginAnalyzeNewVillages] = PostLoginAnalyzeNewVillagesCheckBox.IsChecked == true;
            _config["silver_limit"] = (int)Math.Round(SilverLimitSlider.Value);
            _store.Save(_config);
            return true;
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, ex.Message, "Save settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void SleepNowButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = AppDialog.Show(
            this,
            "Put the bot to sleep now? It will stop automation, log out, and stay asleep for the configured sleep time before resuming automatically.",
            "Sleep now",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        // Persist first so the sleep uses the current sleep-time/variation values.
        if (!PersistConfig())
        {
            return;
        }

        SleepNowRequested = true;
        _isClosing = true;
        DialogResult = true;
        Close();
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

    private void ResetSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = AppDialog.Show(
            this,
            "Reset saved settings to default for the current account? Other accounts and the selected server are kept.",
            "Reset settings",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _store.ResetSettingsToDefaults();
            LoadConfig();
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, ex.Message, "Reset settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SilverLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateLimitLabels();
    }

    private void ResetPacingButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyPacingDefaultsToUi();
    }

    private void LoadPacingConfigToUi()
    {
        SessionPacingEnabledCheckBox.IsChecked = ReadBool(BotOptionPayloadKeys.SessionPacingEnabled, PacingDefaults.SessionPacingEnabled);
        SessionMaxRunMinutesTextBox.Text = ReadInt(BotOptionPayloadKeys.SessionPacingMaxRunMinutes, PacingDefaults.SessionPacingMaxRunMinutes).ToString();
        SessionSleepMinutesTextBox.Text = Math.Max(30, ReadInt(BotOptionPayloadKeys.SessionPacingSleepMinutes, PacingDefaults.SessionPacingSleepMinutes)).ToString();
        SelectVariationPercent(ReadInt(BotOptionPayloadKeys.SessionPacingVariationPercent, PacingDefaults.SessionPacingVariationPercent));
        SelectDailyMaxHours(ReadInt(BotOptionPayloadKeys.SessionPacingDailyMaxHours, PacingDefaults.SessionPacingDailyMaxHours));
        var allowedHours = ReadAllowedHours();
        foreach (var checkBox in SessionAllowedHoursGrid.Children.OfType<CheckBox>())
        {
            checkBox.IsChecked = int.TryParse(checkBox.Tag?.ToString(), out var hour) && allowedHours.Contains(hour);
        }

        ActionPacingEnabledCheckBox.IsChecked = ReadBool(BotOptionPayloadKeys.ActionPacingEnabled, PacingDefaults.ActionPacingEnabled);
        ActionTaskMinTextBox.Text = FormatDelay(ReadDouble(BotOptionPayloadKeys.ActionPacingTaskMinSeconds, PacingDefaults.ActionPacingTaskMinSeconds));
        ActionTaskMaxTextBox.Text = FormatDelay(ReadDouble(BotOptionPayloadKeys.ActionPacingTaskMaxSeconds, PacingDefaults.ActionPacingTaskMaxSeconds));
        ActionPageLoadMinTextBox.Text = FormatDelay(ReadDouble(BotOptionPayloadKeys.ActionPacingPageLoadMinSeconds, PacingDefaults.ActionPacingPageLoadMinSeconds));
        ActionPageLoadMaxTextBox.Text = FormatDelay(ReadDouble(BotOptionPayloadKeys.ActionPacingPageLoadMaxSeconds, PacingDefaults.ActionPacingPageLoadMaxSeconds));
        ActionClickMinTextBox.Text = FormatDelay(ReadDouble(BotOptionPayloadKeys.ActionPacingClickMinSeconds, PacingDefaults.ActionPacingClickMinSeconds));
        ActionClickMaxTextBox.Text = FormatDelay(ReadDouble(BotOptionPayloadKeys.ActionPacingClickMaxSeconds, PacingDefaults.ActionPacingClickMaxSeconds));
        ActionLoopMinTextBox.Text = FormatDelay(ReadDouble(BotOptionPayloadKeys.ActionPacingLoopMinSeconds, PacingDefaults.ActionPacingLoopMinSeconds));
        ActionLoopMaxTextBox.Text = FormatDelay(ReadDouble(BotOptionPayloadKeys.ActionPacingLoopMaxSeconds, PacingDefaults.ActionPacingLoopMaxSeconds));
        FarmListStepDelayMinTextBox.Text = FormatDelay(ReadDouble(BotOptionPayloadKeys.FarmListStepDelayMinSeconds, PacingDefaults.FarmListStepDelayMinSeconds));
        FarmListStepDelayMaxTextBox.Text = FormatDelay(ReadDouble(BotOptionPayloadKeys.FarmListStepDelayMaxSeconds, PacingDefaults.FarmListStepDelayMaxSeconds));

        CollectStepDelayMinTextBox.Text = ReadInt(BotOptionPayloadKeys.CollectStepDelayMinMs, PacingDefaults.CollectStepDelayMinMs).ToString();
        CollectStepDelayMaxTextBox.Text = ReadInt(BotOptionPayloadKeys.CollectStepDelayMaxMs, PacingDefaults.CollectStepDelayMaxMs).ToString();
    }

    private void SavePacingConfigFromUi()
    {
        _config[BotOptionPayloadKeys.SessionPacingEnabled] = SessionPacingEnabledCheckBox.IsChecked == true;
        _config[BotOptionPayloadKeys.SessionPacingMaxRunMinutes] = ReadIntText(SessionMaxRunMinutesTextBox, PacingDefaults.SessionPacingMaxRunMinutes, 1, 10080);
        _config[BotOptionPayloadKeys.SessionPacingSleepMinutes] = ReadIntText(SessionSleepMinutesTextBox, PacingDefaults.SessionPacingSleepMinutes, 30, 10080);
        _config[BotOptionPayloadKeys.SessionPacingVariationPercent] = GetSelectedVariationPercent();
        _config[BotOptionPayloadKeys.SessionPacingDailyMaxHours] = GetSelectedDailyMaxHours();
        _config[BotOptionPayloadKeys.SessionPacingAllowedHours] = new JsonArray(
            SessionAllowedHoursGrid.Children.OfType<CheckBox>()
                .Where(checkBox => checkBox.IsChecked == true)
                .Select(checkBox => JsonValue.Create(int.Parse(checkBox.Tag!.ToString()!)))
                .ToArray());

        _config[BotOptionPayloadKeys.ActionPacingEnabled] = ActionPacingEnabledCheckBox.IsChecked == true;
        WriteDelayRange(BotOptionPayloadKeys.ActionPacingTaskMinSeconds, BotOptionPayloadKeys.ActionPacingTaskMaxSeconds, ActionTaskMinTextBox, ActionTaskMaxTextBox, PacingDefaults.ActionPacingTaskMinSeconds, PacingDefaults.ActionPacingTaskMaxSeconds);
        WriteDelayRange(BotOptionPayloadKeys.ActionPacingPageLoadMinSeconds, BotOptionPayloadKeys.ActionPacingPageLoadMaxSeconds, ActionPageLoadMinTextBox, ActionPageLoadMaxTextBox, PacingDefaults.ActionPacingPageLoadMinSeconds, PacingDefaults.ActionPacingPageLoadMaxSeconds);
        WriteDelayRange(BotOptionPayloadKeys.ActionPacingClickMinSeconds, BotOptionPayloadKeys.ActionPacingClickMaxSeconds, ActionClickMinTextBox, ActionClickMaxTextBox, PacingDefaults.ActionPacingClickMinSeconds, PacingDefaults.ActionPacingClickMaxSeconds);
        WriteDelayRange(BotOptionPayloadKeys.ActionPacingLoopMinSeconds, BotOptionPayloadKeys.ActionPacingLoopMaxSeconds, ActionLoopMinTextBox, ActionLoopMaxTextBox, PacingDefaults.ActionPacingLoopMinSeconds, PacingDefaults.ActionPacingLoopMaxSeconds);
        WriteDelayRange(
            BotOptionPayloadKeys.FarmListStepDelayMinSeconds,
            BotOptionPayloadKeys.FarmListStepDelayMaxSeconds,
            FarmListStepDelayMinTextBox,
            FarmListStepDelayMaxTextBox,
            PacingDefaults.FarmListStepDelayMinSeconds,
            PacingDefaults.FarmListStepDelayMaxSeconds);

        // Collect step delay (ms). Clamp and keep max >= min so the worker always has a valid window.
        var collectMin = ReadIntText(CollectStepDelayMinTextBox, PacingDefaults.CollectStepDelayMinMs, 0, 5000);
        var collectMax = Math.Max(collectMin, ReadIntText(CollectStepDelayMaxTextBox, PacingDefaults.CollectStepDelayMaxMs, 0, 6000));
        _config[BotOptionPayloadKeys.CollectStepDelayMinMs] = collectMin;
        _config[BotOptionPayloadKeys.CollectStepDelayMaxMs] = collectMax;
    }

    private void ApplyPacingDefaultsToUi()
    {
        SessionPacingEnabledCheckBox.IsChecked = PacingDefaults.SessionPacingEnabled;
        SessionMaxRunMinutesTextBox.Text = PacingDefaults.SessionPacingMaxRunMinutes.ToString();
        SessionSleepMinutesTextBox.Text = PacingDefaults.SessionPacingSleepMinutes.ToString();
        SelectVariationPercent(PacingDefaults.SessionPacingVariationPercent);
        SelectDailyMaxHours(PacingDefaults.SessionPacingDailyMaxHours);
        foreach (var checkBox in SessionAllowedHoursGrid.Children.OfType<CheckBox>())
        {
            checkBox.IsChecked = true;
        }
        ActionPacingEnabledCheckBox.IsChecked = PacingDefaults.ActionPacingEnabled;
        ActionTaskMinTextBox.Text = FormatDelay(PacingDefaults.ActionPacingTaskMinSeconds);
        ActionTaskMaxTextBox.Text = FormatDelay(PacingDefaults.ActionPacingTaskMaxSeconds);
        ActionPageLoadMinTextBox.Text = FormatDelay(PacingDefaults.ActionPacingPageLoadMinSeconds);
        ActionPageLoadMaxTextBox.Text = FormatDelay(PacingDefaults.ActionPacingPageLoadMaxSeconds);
        ActionClickMinTextBox.Text = FormatDelay(PacingDefaults.ActionPacingClickMinSeconds);
        ActionClickMaxTextBox.Text = FormatDelay(PacingDefaults.ActionPacingClickMaxSeconds);
        ActionLoopMinTextBox.Text = FormatDelay(PacingDefaults.ActionPacingLoopMinSeconds);
        ActionLoopMaxTextBox.Text = FormatDelay(PacingDefaults.ActionPacingLoopMaxSeconds);
        FarmListStepDelayMinTextBox.Text = FormatDelay(PacingDefaults.FarmListStepDelayMinSeconds);
        FarmListStepDelayMaxTextBox.Text = FormatDelay(PacingDefaults.FarmListStepDelayMaxSeconds);
        CollectStepDelayMinTextBox.Text = PacingDefaults.CollectStepDelayMinMs.ToString();
        CollectStepDelayMaxTextBox.Text = PacingDefaults.CollectStepDelayMaxMs.ToString();
    }

    private bool ReadBool(string key, bool defaultValue) => _config[key]?.GetValue<bool>() ?? defaultValue;

    private int ReadInt(string key, int defaultValue) => _config[key]?.GetValue<int>() ?? defaultValue;

    private double ReadDouble(string key, double defaultValue) => _config[key]?.GetValue<double>() ?? defaultValue;

    // Fixed variation choices offered in the Sleep/Run "Variation" dropdown.
    private static readonly (string Label, int Percent)[] VariationPercentChoices =
    {
        ("No variation", 0),
        ("10%", 10),
        ("20%", 20),
        ("30%", 30),
        ("40%", 40),
        ("50%", 50),
        ("90%", 90),
    };

    private void InitializeSessionPacingChoices()
    {
        foreach (var (label, percent) in VariationPercentChoices)
        {
            SessionVariationPercentComboBox.Items.Add(new ComboBoxItem
            {
                Content = label,
                Tag = percent.ToString(CultureInfo.InvariantCulture),
            });
        }

        SessionDailyMaxHoursComboBox.Items.Add(new ComboBoxItem { Content = "No limit", Tag = "0" });
        for (var hour = 1; hour <= 24; hour++)
        {
            SessionDailyMaxHoursComboBox.Items.Add(new ComboBoxItem
            {
                Content = $"{hour} h",
                Tag = hour.ToString(CultureInfo.InvariantCulture),
            });
        }

        for (var hour = 0; hour < 24; hour++)
        {
            SessionAllowedHoursGrid.Children.Add(new CheckBox
            {
                Content = $"{hour:00}:00",
                Tag = hour.ToString(CultureInfo.InvariantCulture),
                IsChecked = true,
                Margin = new Thickness(0, 2, 8, 2),
            });
        }
    }

    private HashSet<int> ReadAllowedHours()
    {
        if (_config[BotOptionPayloadKeys.SessionPacingAllowedHours] is not JsonArray array)
        {
            return Enumerable.Range(0, 24).ToHashSet();
        }

        return array
            .Select(node => node?.GetValue<int>() ?? -1)
            .Where(hour => hour is >= 0 and <= 23)
            .ToHashSet();
    }

    private void SelectVariationPercent(int percent)
    {
        var items = SessionVariationPercentComboBox.Items.OfType<ComboBoxItem>().ToList();
        // Snap a stored/legacy value that isn't an exact option to the nearest available choice so the
        // dropdown always shows a valid selection.
        var match = items.FirstOrDefault(item =>
                string.Equals(item.Tag?.ToString(), percent.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            ?? items.OrderBy(item => Math.Abs(ParseTag(item) - percent)).FirstOrDefault();
        SessionVariationPercentComboBox.SelectedItem = match;
    }

    private int GetSelectedVariationPercent()
    {
        return SessionVariationPercentComboBox.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out var percent)
                ? Math.Clamp(percent, 0, 100)
                : PacingDefaults.SessionPacingVariationPercent;
    }

    private static int ParseTag(ComboBoxItem item)
        => int.TryParse(item.Tag?.ToString(), out var value) ? value : 0;

    private void SelectDailyMaxHours(int hours)
    {
        var normalized = Math.Clamp(hours, 0, 24).ToString(CultureInfo.InvariantCulture);
        SessionDailyMaxHoursComboBox.SelectedItem = SessionDailyMaxHoursComboBox.Items
            .OfType<ComboBoxItem>()
            .First(item => string.Equals(item.Tag?.ToString(), normalized, StringComparison.Ordinal));
    }

    private int GetSelectedDailyMaxHours()
    {
        return SessionDailyMaxHoursComboBox.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out var hours)
                ? Math.Clamp(hours, 0, 24)
                : PacingDefaults.SessionPacingDailyMaxHours;
    }

    private static int ReadIntText(TextBox textBox, int defaultValue, int min, int max)
    {
        return int.TryParse(textBox.Text, out var value)
            ? Math.Clamp(value, min, max)
            : defaultValue;
    }

    private static double ReadDoubleText(TextBox textBox, double defaultValue)
    {
        return double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0, 3600)
            : defaultValue;
    }

    private static string FormatDelay(double value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void WriteDelayRange(string minKey, string maxKey, TextBox minTextBox, TextBox maxTextBox, double defaultMin, double defaultMax)
    {
        var min = ReadDoubleText(minTextBox, defaultMin);
        var max = Math.Max(min, ReadDoubleText(maxTextBox, defaultMax));
        _config[minKey] = min;
        _config[maxKey] = max;
    }

    private void UpdateLimitLabels()
    {
        if (SilverLimitTextBlock is null)
        {
            return;
        }

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

}
