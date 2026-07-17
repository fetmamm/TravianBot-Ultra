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
    // Server-local hour the bot auto-detected for the active account (null when not yet detected). Display-only.
    private readonly int? _detectedDailyResetHour;
    private readonly Func<JsonObject, string?>? _validateBeforeSave;
    private bool _suppressDetailedBrowserLoggingConfirmation;

    // Set when the user confirms "Sleep now"; MainWindow reads it after ShowDialog to trigger the sleep.
    public bool SleepNowRequested { get; private set; }

    public SettingsWindow(
        BotConfigStore store,
        bool sessionSleeping = false,
        int? detectedDailyResetHour = null,
        Func<JsonObject, string?>? validateBeforeSave = null)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        _store = store;
        _sessionSleeping = sessionSleeping;
        _detectedDailyResetHour = detectedDailyResetHour;
        _validateBeforeSave = validateBeforeSave;
        InitializeSessionPacingChoices();
        PopulateDailyServerResetHours();
        LoadConfig();
        SleepNowButton.IsEnabled = !_sessionSleeping;
    }

    private void LoadConfig()
    {
        _config = _store.Load();
        DontNotifyNewVersionCheckBox.IsChecked = _config[BotOptionPayloadKeys.DontNotifyNewVersion]?.GetValue<bool>() ?? false;
        QuickReloginCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginQuickReloginEnabled]?.GetValue<bool>() ?? true;
        AutomaticallyCheckLanguageCheckBox.IsChecked = _config[BotOptionPayloadKeys.AutomaticallyCheckLanguage]?.GetValue<bool>() ?? true;
        _suppressDetailedBrowserLoggingConfirmation = true;
        try
        {
            DetailedBrowserLoggingCheckBox.IsChecked =
                _config[BotOptionPayloadKeys.DetailedBrowserLoggingEnabled]?.GetValue<bool>() ?? false;
        }
        finally
        {
            _suppressDetailedBrowserLoggingConfirmation = false;
        }
        AllowSilverSpendingCheckBox.IsChecked = _config["allow_silver_spending"]?.GetValue<bool>() ?? false;
        LoadDailyServerResetToUi();
        LoadPacingConfigToUi();
        PostLoginAnalyzeFarmlistsCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginAnalyzeFarmlists]?.GetValue<bool>() ?? false;
        PostLoginAnalyzeHeroCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginAnalyzeHero]?.GetValue<bool>() ?? false;
        PostLoginReadTroopTrainingQueueCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue]?.GetValue<bool>() ?? false;
        PostLoginAnalyzeBreweryCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginAnalyzeBrewery]?.GetValue<bool>() ?? false;
        PostLoginAnalyzeHeroInventoryCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginAnalyzeHeroInventory]?.GetValue<bool>() ?? true;
        PostLoginAnalyzeNewVillagesCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginAnalyzeNewVillages]?.GetValue<bool>() ?? true;
        SilverLimitSlider.Value = Math.Clamp(_config["silver_limit"]?.GetValue<int>() ?? 100, 0, 1000);
        UpdateLimitLabels();
    }

    // Fills the reset-hour dropdown with 00:00..23:00 (Tag = the whole hour 0..23).
    private void PopulateDailyServerResetHours()
    {
        for (var hour = 0; hour < 24; hour++)
        {
            DailyServerResetHourComboBox.Items.Add(new ComboBoxItem
            {
                Content = $"{hour:00}:00",
                Tag = hour,
            });
        }
    }

    private void LoadDailyServerResetToUi()
    {
        var overrideEnabled = _config[BotOptionPayloadKeys.DailyServerResetManualOverrideEnabled]?.GetValue<bool>() ?? false;
        var manualHour = Math.Clamp(_config[BotOptionPayloadKeys.DailyServerResetManualHour]?.GetValue<int>() ?? 0, 0, 23);
        DailyServerResetOverrideCheckBox.IsChecked = overrideEnabled;
        SelectDailyServerResetHour(manualHour);
        DailyServerResetHourComboBox.IsEnabled = overrideEnabled;
        DailyServerResetDetectedTextBlock.Text = _detectedDailyResetHour is int detected
            ? $"detected: {detected:00}:00"
            : "detected: —";
    }

    private void SaveDailyServerResetFromUi()
    {
        _config[BotOptionPayloadKeys.DailyServerResetManualOverrideEnabled] = DailyServerResetOverrideCheckBox.IsChecked == true;
        _config[BotOptionPayloadKeys.DailyServerResetManualHour] = GetSelectedDailyServerResetHour();
    }

    private void DailyServerResetOverride_Changed(object sender, RoutedEventArgs e)
    {
        // Toggle only enables/disables the hour dropdown; the value is persisted on Save.
        if (DailyServerResetHourComboBox is not null)
        {
            DailyServerResetHourComboBox.IsEnabled = DailyServerResetOverrideCheckBox.IsChecked == true;
        }
    }

    private void SelectDailyServerResetHour(int hour)
    {
        var clamped = Math.Clamp(hour, 0, 23);
        foreach (var item in DailyServerResetHourComboBox.Items)
        {
            if (item is ComboBoxItem { Tag: int tag } comboItem && tag == clamped)
            {
                DailyServerResetHourComboBox.SelectedItem = comboItem;
                return;
            }
        }
    }

    private int GetSelectedDailyServerResetHour()
        => DailyServerResetHourComboBox.SelectedItem is ComboBoxItem { Tag: int hour }
            ? Math.Clamp(hour, 0, 23)
            : 0;

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
            // The browser always runs visible; headless mode has been removed entirely.
            _config.Remove("headless");
            _config[BotOptionPayloadKeys.DontNotifyNewVersion] = DontNotifyNewVersionCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.PostLoginQuickReloginEnabled] = QuickReloginCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.AutomaticallyCheckLanguage] = AutomaticallyCheckLanguageCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.DetailedBrowserLoggingEnabled] = DetailedBrowserLoggingCheckBox.IsChecked == true;
            _config["allow_silver_spending"] = AllowSilverSpendingCheckBox.IsChecked == true;
            SaveDailyServerResetFromUi();
            SavePacingConfigFromUi();
            // Queue-wait handling is always "smart" (defer); drop the removed threshold key.
            _config.Remove("queue_wait_threshold_mode");
            _config[BotOptionPayloadKeys.PostLoginAnalyzeFarmlists] = PostLoginAnalyzeFarmlistsCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.PostLoginAnalyzeHero] = PostLoginAnalyzeHeroCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue] = PostLoginReadTroopTrainingQueueCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.PostLoginAnalyzeBrewery] = PostLoginAnalyzeBreweryCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.PostLoginAnalyzeHeroInventory] = PostLoginAnalyzeHeroInventoryCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.PostLoginAnalyzeNewVillages] = PostLoginAnalyzeNewVillagesCheckBox.IsChecked == true;
            _config["silver_limit"] = (int)Math.Round(SilverLimitSlider.Value);
            var validationError = _validateBeforeSave?.Invoke(_config);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                AppDialog.Show(this, validationError, "Proxy setup conflict", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
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
            var previous = (JsonObject)_config.DeepClone();
            _store.ResetSettingsToDefaults();
            var reset = _store.Load();
            var validationError = _validateBeforeSave?.Invoke(reset);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                _store.Save(previous);
                LoadConfig();
                AppDialog.Show(
                    this,
                    validationError + "\n\nThe previous Settings values were restored.",
                    "Proxy setup conflict",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

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

    private void DetailedBrowserLoggingCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressDetailedBrowserLoggingConfirmation
            || DetailedBrowserLoggingCheckBox.IsChecked != true)
        {
            return;
        }

        var result = AppDialog.ShowCustomContent(
            this,
            BuildDetailedBrowserLoggingConfirmContent(),
            "Enable detailed browser logging?",
            [("Toggle ON", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel)],
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel,
            MessageBoxResult.Cancel,
            warningResult: MessageBoxResult.Yes);
        if (result == MessageBoxResult.Yes)
        {
            return;
        }

        _suppressDetailedBrowserLoggingConfirmation = true;
        try
        {
            DetailedBrowserLoggingCheckBox.IsChecked = false;
        }
        finally
        {
            _suppressDetailedBrowserLoggingConfirmation = false;
        }
    }

    // Structured content for the detailed-browser-logging confirmation dialog (headline, bullet list
    // and a warning note) instead of one long text paragraph. Brushes are set via resource references
    // so the dialog follows the active theme.
    private static StackPanel BuildDetailedBrowserLoggingConfirmContent()
    {
        static TextBlock CreateText(string text, string foregroundResource, double topMargin = 0)
        {
            var block = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Margin = new Thickness(0, topMargin, 0, 0),
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, foregroundResource);
            return block;
        }

        var panel = new StackPanel();

        var headline = CreateText("Development and troubleshooting only", "TextPrimaryBrush");
        headline.FontSize = 14;
        headline.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(headline);

        panel.Children.Add(CreateText(
            "Records high-volume technical details about browser activity:",
            "TextSecondaryBrush",
            topMargin: 8));
        foreach (var line in new[]
                 {
                     "Navigation, reloads and refreshes",
                     "Page reads, waits and retries",
                     "Cache decisions",
                 })
        {
            var bullet = CreateText($"•  {line}", "TextSecondaryBrush", topMargin: 4);
            bullet.Margin = new Thickness(10, 4, 0, 0);
            panel.Children.Add(bullet);
        }

        var noteText = CreateText(
            "Can create large log files and may slightly affect performance. Do not enable during normal use.",
            "TextSecondaryBrush");
        noteText.FontSize = 12;
        var noteBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 12, 0, 0),
            Child = noteText,
        };
        noteBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        noteBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        panel.Children.Add(noteBorder);

        return panel;
    }

    private void ResetPacingButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyPacingDefaultsToUi();
    }

    private void LoadPacingConfigToUi()
    {
        SessionPacingEnabledCheckBox.IsChecked = ReadBool(BotOptionPayloadKeys.SessionPacingEnabled, PacingDefaults.SessionPacingEnabled);
        SessionRunMinMinutesTextBox.Text = ReadInt(BotOptionPayloadKeys.SessionPacingRunMinMinutes, PacingDefaults.SessionPacingRunMinMinutes).ToString();
        SessionRunMaxMinutesTextBox.Text = ReadInt(BotOptionPayloadKeys.SessionPacingRunMaxMinutes, PacingDefaults.SessionPacingRunMaxMinutes).ToString();
        SessionSleepMinMinutesTextBox.Text = ReadInt(BotOptionPayloadKeys.SessionPacingSleepMinMinutes, PacingDefaults.SessionPacingSleepMinMinutes).ToString();
        SessionSleepMaxMinutesTextBox.Text = ReadInt(BotOptionPayloadKeys.SessionPacingSleepMaxMinutes, PacingDefaults.SessionPacingSleepMaxMinutes).ToString();
        SelectDailyMaxHours(ReadInt(BotOptionPayloadKeys.SessionPacingDailyMaxHours, PacingDefaults.SessionPacingDailyMaxHours));
        SelectDailyMaxVariationPercent(ReadInt(BotOptionPayloadKeys.SessionPacingDailyMaxVariationPercent, PacingDefaults.SessionPacingDailyMaxVariationPercent));
        var allowedHours = ReadAllowedHours();
        foreach (var checkBox in SessionAllowedHoursGrid.Children.OfType<CheckBox>())
        {
            checkBox.IsChecked = int.TryParse(checkBox.Tag?.ToString(), out var hour) && allowedHours.Contains(hour);
        }

        SelectHoursVariationPercent(ReadInt(BotOptionPayloadKeys.SessionPacingHoursVariationPercent, PacingDefaults.SessionPacingHoursVariationPercent));

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

        IdleBreakEnabledCheckBox.IsChecked = ReadBool(BotOptionPayloadKeys.ActionPacingIdleBreakEnabled, PacingDefaults.ActionPacingIdleBreakEnabled);
        IdleBreakIntervalMinTextBox.Text = FormatDelay(ReadDouble(BotOptionPayloadKeys.ActionPacingIdleBreakIntervalMinMinutes, PacingDefaults.ActionPacingIdleBreakIntervalMinMinutes));
        IdleBreakIntervalMaxTextBox.Text = FormatDelay(ReadDouble(BotOptionPayloadKeys.ActionPacingIdleBreakIntervalMaxMinutes, PacingDefaults.ActionPacingIdleBreakIntervalMaxMinutes));
        IdleBreakDurationMinTextBox.Text = FormatDelay(ReadDouble(BotOptionPayloadKeys.ActionPacingIdleBreakDurationMinMinutes, PacingDefaults.ActionPacingIdleBreakDurationMinMinutes));
        IdleBreakDurationMaxTextBox.Text = FormatDelay(ReadDouble(BotOptionPayloadKeys.ActionPacingIdleBreakDurationMaxMinutes, PacingDefaults.ActionPacingIdleBreakDurationMaxMinutes));

        IdleBrowseEnabledCheckBox.IsChecked = ReadBool(BotOptionPayloadKeys.ActionPacingIdleBrowseEnabled, PacingDefaults.ActionPacingIdleBrowseEnabled);
        IdleBrowseIntervalMinTextBox.Text = FormatDelay(ReadDouble(BotOptionPayloadKeys.ActionPacingIdleBrowseIntervalMinMinutes, PacingDefaults.ActionPacingIdleBrowseIntervalMinMinutes));
        IdleBrowseIntervalMaxTextBox.Text = FormatDelay(ReadDouble(BotOptionPayloadKeys.ActionPacingIdleBrowseIntervalMaxMinutes, PacingDefaults.ActionPacingIdleBrowseIntervalMaxMinutes));
        IdleBrowsePageMapCheckBox.IsChecked = ReadBool(BotOptionPayloadKeys.ActionPacingIdleBrowsePageMap, PacingDefaults.ActionPacingIdleBrowsePageMap);
        IdleBrowsePageStatisticsCheckBox.IsChecked = ReadBool(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatistics, PacingDefaults.ActionPacingIdleBrowsePageStatistics);
        IdleBrowsePageStatisticsHeroCheckBox.IsChecked = ReadBool(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsHero, PacingDefaults.ActionPacingIdleBrowsePageStatisticsHero);
        IdleBrowsePageStatisticsTop10CheckBox.IsChecked = ReadBool(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsTop10, PacingDefaults.ActionPacingIdleBrowsePageStatisticsTop10);
        IdleBrowsePageStatisticsDefendersCheckBox.IsChecked = ReadBool(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsDefenders, PacingDefaults.ActionPacingIdleBrowsePageStatisticsDefenders);
        IdleBrowsePageStatisticsAttackersCheckBox.IsChecked = ReadBool(BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsAttackers, PacingDefaults.ActionPacingIdleBrowsePageStatisticsAttackers);
        IdleBrowsePageReportsCheckBox.IsChecked = ReadBool(BotOptionPayloadKeys.ActionPacingIdleBrowsePageReports, PacingDefaults.ActionPacingIdleBrowsePageReports);
        IdleBrowsePageMessagesCheckBox.IsChecked = ReadBool(BotOptionPayloadKeys.ActionPacingIdleBrowsePageMessages, PacingDefaults.ActionPacingIdleBrowsePageMessages);

        CollectStepDelayMinTextBox.Text = FormatDelay(ReadDouble(BotOptionPayloadKeys.CollectStepDelayMinSeconds, PacingDefaults.CollectStepDelayMinSeconds));
        CollectStepDelayMaxTextBox.Text = FormatDelay(ReadDouble(BotOptionPayloadKeys.CollectStepDelayMaxSeconds, PacingDefaults.CollectStepDelayMaxSeconds));
    }

    private void SavePacingConfigFromUi()
    {
        _config[BotOptionPayloadKeys.SessionPacingEnabled] = SessionPacingEnabledCheckBox.IsChecked == true;
        _config[BotOptionPayloadKeys.SessionPacingRunMinMinutes] = ReadIntText(SessionRunMinMinutesTextBox, PacingDefaults.SessionPacingRunMinMinutes, 1, 10080);
        _config[BotOptionPayloadKeys.SessionPacingRunMaxMinutes] = ReadIntText(SessionRunMaxMinutesTextBox, PacingDefaults.SessionPacingRunMaxMinutes, 1, 10080);
        _config[BotOptionPayloadKeys.SessionPacingSleepMinMinutes] = ReadIntText(SessionSleepMinMinutesTextBox, PacingDefaults.SessionPacingSleepMinMinutes, 5, 10080);
        _config[BotOptionPayloadKeys.SessionPacingSleepMaxMinutes] = ReadIntText(SessionSleepMaxMinutesTextBox, PacingDefaults.SessionPacingSleepMaxMinutes, 5, 10080);
        _config[BotOptionPayloadKeys.SessionPacingDailyMaxHours] = GetSelectedDailyMaxHours();
        _config[BotOptionPayloadKeys.SessionPacingDailyMaxVariationPercent] = GetSelectedDailyMaxVariationPercent();
        _config[BotOptionPayloadKeys.SessionPacingAllowedHours] = new JsonArray(
            SessionAllowedHoursGrid.Children.OfType<CheckBox>()
                .Where(checkBox => checkBox.IsChecked == true)
                .Select(checkBox => JsonValue.Create(int.Parse(checkBox.Tag!.ToString()!)))
                .ToArray());
        _config[BotOptionPayloadKeys.SessionPacingHoursVariationPercent] = GetSelectedHoursVariationPercent();

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

        // Idle "step away" break (minutes). WriteDelayRange clamps and keeps max >= min.
        _config[BotOptionPayloadKeys.ActionPacingIdleBreakEnabled] = IdleBreakEnabledCheckBox.IsChecked == true;
        WriteDelayRange(
            BotOptionPayloadKeys.ActionPacingIdleBreakIntervalMinMinutes,
            BotOptionPayloadKeys.ActionPacingIdleBreakIntervalMaxMinutes,
            IdleBreakIntervalMinTextBox,
            IdleBreakIntervalMaxTextBox,
            PacingDefaults.ActionPacingIdleBreakIntervalMinMinutes,
            PacingDefaults.ActionPacingIdleBreakIntervalMaxMinutes);
        WriteDelayRange(
            BotOptionPayloadKeys.ActionPacingIdleBreakDurationMinMinutes,
            BotOptionPayloadKeys.ActionPacingIdleBreakDurationMaxMinutes,
            IdleBreakDurationMinTextBox,
            IdleBreakDurationMaxTextBox,
            PacingDefaults.ActionPacingIdleBreakDurationMinMinutes,
            PacingDefaults.ActionPacingIdleBreakDurationMaxMinutes);

        // Idle browse (interval minutes + per-page toggles). WriteDelayRange clamps and keeps max >= min.
        _config[BotOptionPayloadKeys.ActionPacingIdleBrowseEnabled] = IdleBrowseEnabledCheckBox.IsChecked == true;
        WriteDelayRange(
            BotOptionPayloadKeys.ActionPacingIdleBrowseIntervalMinMinutes,
            BotOptionPayloadKeys.ActionPacingIdleBrowseIntervalMaxMinutes,
            IdleBrowseIntervalMinTextBox,
            IdleBrowseIntervalMaxTextBox,
            PacingDefaults.ActionPacingIdleBrowseIntervalMinMinutes,
            PacingDefaults.ActionPacingIdleBrowseIntervalMaxMinutes);
        _config[BotOptionPayloadKeys.ActionPacingIdleBrowsePageMap] = IdleBrowsePageMapCheckBox.IsChecked == true;
        _config[BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatistics] = IdleBrowsePageStatisticsCheckBox.IsChecked == true;
        _config[BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsHero] = IdleBrowsePageStatisticsHeroCheckBox.IsChecked == true;
        _config[BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsTop10] = IdleBrowsePageStatisticsTop10CheckBox.IsChecked == true;
        _config[BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsDefenders] = IdleBrowsePageStatisticsDefendersCheckBox.IsChecked == true;
        _config[BotOptionPayloadKeys.ActionPacingIdleBrowsePageStatisticsAttackers] = IdleBrowsePageStatisticsAttackersCheckBox.IsChecked == true;
        _config[BotOptionPayloadKeys.ActionPacingIdleBrowsePageReports] = IdleBrowsePageReportsCheckBox.IsChecked == true;
        _config[BotOptionPayloadKeys.ActionPacingIdleBrowsePageMessages] = IdleBrowsePageMessagesCheckBox.IsChecked == true;

        // Collect step delay (seconds). WriteDelayRange clamps and keeps max >= min.
        WriteDelayRange(
            BotOptionPayloadKeys.CollectStepDelayMinSeconds,
            BotOptionPayloadKeys.CollectStepDelayMaxSeconds,
            CollectStepDelayMinTextBox,
            CollectStepDelayMaxTextBox,
            PacingDefaults.CollectStepDelayMinSeconds,
            PacingDefaults.CollectStepDelayMaxSeconds);
    }

    private void ApplyPacingDefaultsToUi()
    {
        SessionPacingEnabledCheckBox.IsChecked = PacingDefaults.SessionPacingEnabled;
        SessionRunMinMinutesTextBox.Text = PacingDefaults.SessionPacingRunMinMinutes.ToString();
        SessionRunMaxMinutesTextBox.Text = PacingDefaults.SessionPacingRunMaxMinutes.ToString();
        SessionSleepMinMinutesTextBox.Text = PacingDefaults.SessionPacingSleepMinMinutes.ToString();
        SessionSleepMaxMinutesTextBox.Text = PacingDefaults.SessionPacingSleepMaxMinutes.ToString();
        SelectDailyMaxHours(PacingDefaults.SessionPacingDailyMaxHours);
        SelectDailyMaxVariationPercent(PacingDefaults.SessionPacingDailyMaxVariationPercent);
        foreach (var checkBox in SessionAllowedHoursGrid.Children.OfType<CheckBox>())
        {
            checkBox.IsChecked = true;
        }
        SelectHoursVariationPercent(PacingDefaults.SessionPacingHoursVariationPercent);
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
        IdleBreakEnabledCheckBox.IsChecked = PacingDefaults.ActionPacingIdleBreakEnabled;
        IdleBreakIntervalMinTextBox.Text = FormatDelay(PacingDefaults.ActionPacingIdleBreakIntervalMinMinutes);
        IdleBreakIntervalMaxTextBox.Text = FormatDelay(PacingDefaults.ActionPacingIdleBreakIntervalMaxMinutes);
        IdleBreakDurationMinTextBox.Text = FormatDelay(PacingDefaults.ActionPacingIdleBreakDurationMinMinutes);
        IdleBreakDurationMaxTextBox.Text = FormatDelay(PacingDefaults.ActionPacingIdleBreakDurationMaxMinutes);
        IdleBrowseEnabledCheckBox.IsChecked = PacingDefaults.ActionPacingIdleBrowseEnabled;
        IdleBrowseIntervalMinTextBox.Text = FormatDelay(PacingDefaults.ActionPacingIdleBrowseIntervalMinMinutes);
        IdleBrowseIntervalMaxTextBox.Text = FormatDelay(PacingDefaults.ActionPacingIdleBrowseIntervalMaxMinutes);
        IdleBrowsePageMapCheckBox.IsChecked = PacingDefaults.ActionPacingIdleBrowsePageMap;
        IdleBrowsePageStatisticsCheckBox.IsChecked = PacingDefaults.ActionPacingIdleBrowsePageStatistics;
        IdleBrowsePageStatisticsHeroCheckBox.IsChecked = PacingDefaults.ActionPacingIdleBrowsePageStatisticsHero;
        IdleBrowsePageStatisticsTop10CheckBox.IsChecked = PacingDefaults.ActionPacingIdleBrowsePageStatisticsTop10;
        IdleBrowsePageStatisticsDefendersCheckBox.IsChecked = PacingDefaults.ActionPacingIdleBrowsePageStatisticsDefenders;
        IdleBrowsePageStatisticsAttackersCheckBox.IsChecked = PacingDefaults.ActionPacingIdleBrowsePageStatisticsAttackers;
        IdleBrowsePageReportsCheckBox.IsChecked = PacingDefaults.ActionPacingIdleBrowsePageReports;
        IdleBrowsePageMessagesCheckBox.IsChecked = PacingDefaults.ActionPacingIdleBrowsePageMessages;
        CollectStepDelayMinTextBox.Text = FormatDelay(PacingDefaults.CollectStepDelayMinSeconds);
        CollectStepDelayMaxTextBox.Text = FormatDelay(PacingDefaults.CollectStepDelayMaxSeconds);
    }

    private bool ReadBool(string key, bool defaultValue) => _config[key]?.GetValue<bool>() ?? defaultValue;

    private int ReadInt(string key, int defaultValue) => _config[key]?.GetValue<int>() ?? defaultValue;

    private double ReadDouble(string key, double defaultValue) => _config[key]?.GetValue<double>() ?? defaultValue;

    private void InitializeSessionPacingChoices()
    {
        SessionDailyMaxHoursComboBox.Items.Add(new ComboBoxItem { Content = "No limit", Tag = "0" });
        for (var hour = 1; hour <= 24; hour++)
        {
            SessionDailyMaxHoursComboBox.Items.Add(new ComboBoxItem
            {
                Content = $"{hour} h",
                Tag = hour.ToString(CultureInfo.InvariantCulture),
            });
        }

        // Daily-max variation: 0..50% in 10% steps. Independent of the run/sleep "Variation" dropdown.
        for (var percent = 0; percent <= 50; percent += 10)
        {
            SessionDailyMaxVariationComboBox.Items.Add(new ComboBoxItem
            {
                Content = percent == 0 ? "No variation" : $"±{percent}%",
                Tag = percent.ToString(CultureInfo.InvariantCulture),
            });
        }

        // Daily hours variation: 0..30% in 10% steps. Jitters the allowed-hours boundaries.
        for (var percent = 0; percent <= 30; percent += 10)
        {
            SessionHoursVariationComboBox.Items.Add(new ComboBoxItem
            {
                Content = $"{percent} %",
                Tag = percent.ToString(CultureInfo.InvariantCulture),
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

    private void SelectDailyMaxVariationPercent(int percent)
    {
        var items = SessionDailyMaxVariationComboBox.Items.OfType<ComboBoxItem>().ToList();
        var match = items.FirstOrDefault(item =>
                string.Equals(item.Tag?.ToString(), percent.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            ?? items.OrderBy(item => Math.Abs(ParseTag(item) - percent)).FirstOrDefault();
        SessionDailyMaxVariationComboBox.SelectedItem = match;
    }

    private int GetSelectedDailyMaxVariationPercent()
    {
        return SessionDailyMaxVariationComboBox.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out var percent)
                ? Math.Clamp(percent, 0, 50)
                : PacingDefaults.SessionPacingDailyMaxVariationPercent;
    }

    private void SelectHoursVariationPercent(int percent)
    {
        var items = SessionHoursVariationComboBox.Items.OfType<ComboBoxItem>().ToList();
        var match = items.FirstOrDefault(item =>
                string.Equals(item.Tag?.ToString(), percent.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            ?? items.OrderBy(item => Math.Abs(ParseTag(item) - percent)).FirstOrDefault();
        SessionHoursVariationComboBox.SelectedItem = match;
    }

    private int GetSelectedHoursVariationPercent()
    {
        return SessionHoursVariationComboBox.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out var percent)
                ? Math.Clamp(percent, 0, 49)
                : PacingDefaults.SessionPacingHoursVariationPercent;
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

}
