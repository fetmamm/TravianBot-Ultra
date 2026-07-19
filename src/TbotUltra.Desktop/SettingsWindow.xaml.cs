using System.Globalization;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

public enum SettingsCategory
{
    General,
    Pacing,
    Construction,
    Hero,
    Farming,
    Troops,
    Celebrations,
    NpcTrade,
}

public partial class SettingsWindow : Window
{
    private const int DefaultGoldLimit = 100;
    private const int DefaultDailyGoldSpendingLimit = 20;
    private const int DefaultSilverLimit = 100;
    private const int DefaultDailySilverSpendingLimit = 10000;
    private readonly BotConfigStore _store;
    private JsonObject _config = [];
    private bool _isClosing;
    private readonly bool _sessionSleeping;
    // Server-local hour the bot auto-detected for the active account (null when not yet detected). Display-only.
    private readonly int? _detectedDailyResetHour;
    private readonly Func<JsonObject, string?>? _validateBeforeSave;
    private readonly Action? _resetDailyGoldSpending;
    private readonly Action? _resetDailySilverSpending;
    private bool _suppressDetailedBrowserLoggingConfirmation;
    private string _initialTownHallFingerprint = string.Empty;

    public ObservableCollection<TownHallOverviewRow> TownHallRows { get; } = [];
    public TownHallQueueSettings TownHallQueue { get; } = new(
        TownHallCelebrationDefaults.DefaultRestartDelayEnabled,
        TownHallCelebrationDefaults.DefaultCount,
        TownHallCelebrationDefaults.DefaultRestartDelayMinMinutes,
        TownHallCelebrationDefaults.DefaultRestartDelayMaxMinutes);
    public RestartDelaySettings BreweryRestartDelay { get; } = new(
        BreweryCelebrationDefaults.DefaultRestartDelayEnabled,
        BreweryCelebrationDefaults.DefaultRestartDelayMinMinutes,
        BreweryCelebrationDefaults.DefaultRestartDelayMaxMinutes,
        BreweryCelebrationDefaults.DefaultRestartDelayMinMinutes,
        BreweryCelebrationDefaults.DefaultRestartDelayMaxMinutes);
    public RestartDelaySettings HeroAdventureRestartDelay { get; } = new(
        HeroAdventureRestartDelayDefaults.Enabled,
        HeroAdventureRestartDelayDefaults.MinMinutes,
        HeroAdventureRestartDelayDefaults.MaxMinutes,
        HeroAdventureRestartDelayDefaults.MinMinutes,
        HeroAdventureRestartDelayDefaults.MaxMinutes);
    public IReadOnlyList<int> HeroHpRegenOptions { get; } = [20, 30, 40, 50, 60, 70, 80, 90, 100];
    public RestartDelaySettings SmithyUpgradeRestartDelay { get; } = new(
        SmithyUpgradeRestartDelayDefaults.Enabled,
        SmithyUpgradeRestartDelayDefaults.MinMinutes,
        SmithyUpgradeRestartDelayDefaults.MaxMinutes,
        SmithyUpgradeRestartDelayDefaults.MinMinutes,
        SmithyUpgradeRestartDelayDefaults.MaxMinutes);
    public IReadOnlyList<TownHallOverviewResult> TownHallResults { get; private set; } = [];
    public bool TownHallSettingsChanged { get; private set; }

    // Set when the user confirms "Sleep now"; MainWindow reads it after ShowDialog to trigger the sleep.
    public bool SleepNowRequested { get; private set; }

    public SettingsWindow(
        BotConfigStore store,
        bool sessionSleeping = false,
        int? detectedDailyResetHour = null,
        Func<JsonObject, string?>? validateBeforeSave = null,
        SettingsCategory initialCategory = SettingsCategory.General,
        IReadOnlyList<TownHallOverviewRow>? townHallRows = null,
        Action? resetDailyGoldSpending = null,
        Action? resetDailySilverSpending = null)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        _store = store;
        _sessionSleeping = sessionSleeping;
        _detectedDailyResetHour = detectedDailyResetHour;
        _validateBeforeSave = validateBeforeSave;
        _resetDailyGoldSpending = resetDailyGoldSpending;
        _resetDailySilverSpending = resetDailySilverSpending;
        foreach (var row in townHallRows ?? [])
        {
            TownHallRows.Add(row);
        }
        DataContext = this;
        InitializeSessionPacingChoices();
        InitializeConstructionChoices();
        PopulateDailyServerResetHours();
        LoadConfig();
        SettingsCategoryTabControl.SelectedIndex = (int)initialCategory;
        _initialTownHallFingerprint = BuildTownHallFingerprint();
        SleepNowButton.IsEnabled = !_sessionSleeping;
        ResetDailyGoldLimitButton.IsEnabled = _resetDailyGoldSpending is not null;
        ResetDailySilverLimitButton.IsEnabled = _resetDailySilverSpending is not null;
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
        AllowGoldSpendingCheckBox.IsChecked = _config[BotOptionPayloadKeys.AllowGoldSpending]?.GetValue<bool>() ?? false;
        GoldLimitTextBox.Text = Math.Max(
            0,
            _config[BotOptionPayloadKeys.GoldLimit]?.GetValue<int>() ?? DefaultGoldLimit).ToString(CultureInfo.InvariantCulture);
        DailyGoldSpendingLimitTextBox.Text = Math.Max(
            0,
            _config[BotOptionPayloadKeys.DailyGoldSpendingLimit]?.GetValue<int>() ?? DefaultDailyGoldSpendingLimit).ToString(CultureInfo.InvariantCulture);
        LoadDailyServerResetToUi();
        LoadPacingConfigToUi();
        StorageUpgradeLevelsAheadComboBox.SelectedItem = ConstructionDefaults.NormalizeStorageUpgradeLevelsAhead(
            _config[BotOptionPayloadKeys.ConstructionStorageUpgradeLevelsAhead]?.GetValue<int>()
            ?? ConstructionDefaults.StorageUpgradeLevelsAhead);
        LoadConstructionHumanizeConfigToUi();
        PostLoginAnalyzeFarmlistsCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginAnalyzeFarmlists]?.GetValue<bool>() ?? false;
        PostLoginAnalyzeHeroCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginAnalyzeHero]?.GetValue<bool>() ?? false;
        PostLoginReadTroopTrainingQueueCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue]?.GetValue<bool>() ?? false;
        PostLoginAnalyzeBreweryCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginAnalyzeBrewery]?.GetValue<bool>() ?? false;
        PostLoginAnalyzeHeroInventoryCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginAnalyzeHeroInventory]?.GetValue<bool>() ?? true;
        PostLoginAnalyzeNewVillagesCheckBox.IsChecked = _config[BotOptionPayloadKeys.PostLoginAnalyzeNewVillages]?.GetValue<bool>() ?? true;
        SilverLimitTextBox.Text = Math.Max(
            0,
            _config[BotOptionPayloadKeys.SilverLimit]?.GetValue<int>() ?? DefaultSilverLimit).ToString(CultureInfo.InvariantCulture);
        DailySilverSpendingLimitTextBox.Text = Math.Max(
            0,
            _config[BotOptionPayloadKeys.DailySilverSpendingLimit]?.GetValue<int>() ?? DefaultDailySilverSpendingLimit).ToString(CultureInfo.InvariantCulture);
        if (TownHallCelebrationDefaults.NormalizeCount(
                ReadInt(BotOptionPayloadKeys.TownHallCelebrationCount, TownHallCelebrationDefaults.DefaultCount))
            >= TownHallCelebrationDefaults.MaxCount)
        {
            TownHallQueue.IsTwo = true;
        }
        else
        {
            TownHallQueue.IsOne = true;
        }
        TownHallQueue.DelayMinMinutes = FormatDelay(ReadDouble(
            BotOptionPayloadKeys.TownHallCelebrationRestartDelayMinMinutes,
            TownHallCelebrationDefaults.DefaultRestartDelayMinMinutes));
        TownHallQueue.DelayMaxMinutes = FormatDelay(ReadDouble(
            BotOptionPayloadKeys.TownHallCelebrationRestartDelayMaxMinutes,
            TownHallCelebrationDefaults.DefaultRestartDelayMaxMinutes));
        TownHallQueue.IsRestartDelayEnabled =
            _config[BotOptionPayloadKeys.TownHallCelebrationRestartDelayEnabled]?.GetValue<bool>()
            ?? TownHallCelebrationDefaults.DefaultRestartDelayEnabled;
        BreweryRestartDelay.IsEnabled =
            _config[BotOptionPayloadKeys.BreweryCelebrationRestartDelayEnabled]?.GetValue<bool>()
            ?? BreweryCelebrationDefaults.DefaultRestartDelayEnabled;
        BreweryRestartDelay.DelayMinMinutes = FormatDelay(ReadDouble(
            BotOptionPayloadKeys.BreweryCelebrationRestartDelayMinMinutes,
            BreweryCelebrationDefaults.DefaultRestartDelayMinMinutes));
        BreweryRestartDelay.DelayMaxMinutes = FormatDelay(ReadDouble(
            BotOptionPayloadKeys.BreweryCelebrationRestartDelayMaxMinutes,
            BreweryCelebrationDefaults.DefaultRestartDelayMaxMinutes));
        HeroAdventureRestartDelay.IsEnabled =
            _config[BotOptionPayloadKeys.HeroAdventureRestartDelayEnabled]?.GetValue<bool>()
            ?? HeroAdventureRestartDelayDefaults.Enabled;
        HeroAdventureRestartDelay.DelayMinMinutes = FormatDelay(ReadDouble(
            BotOptionPayloadKeys.HeroAdventureRestartDelayMinMinutes,
            HeroAdventureRestartDelayDefaults.MinMinutes));
        HeroAdventureRestartDelay.DelayMaxMinutes = FormatDelay(ReadDouble(
            BotOptionPayloadKeys.HeroAdventureRestartDelayMaxMinutes,
            HeroAdventureRestartDelayDefaults.MaxMinutes));
        var heroHpRegen = Math.Clamp(ReadInt(BotOptionPayloadKeys.HeroHpRegenPerDayPercent, 40), 20, 100);
        HeroHpRegenPerDayComboBox.SelectedItem = Math.Clamp(((heroHpRegen + 5) / 10) * 10, 20, 100);
        SmithyUpgradeRestartDelay.IsEnabled =
            _config[BotOptionPayloadKeys.SmithyUpgradeRestartDelayEnabled]?.GetValue<bool>()
            ?? SmithyUpgradeRestartDelayDefaults.Enabled;
        SmithyUpgradeRestartDelay.DelayMinMinutes = FormatDelay(ReadDouble(
            BotOptionPayloadKeys.SmithyUpgradeRestartDelayMinMinutes,
            SmithyUpgradeRestartDelayDefaults.MinMinutes));
        SmithyUpgradeRestartDelay.DelayMaxMinutes = FormatDelay(ReadDouble(
            BotOptionPayloadKeys.SmithyUpgradeRestartDelayMaxMinutes,
            SmithyUpgradeRestartDelayDefaults.MaxMinutes));
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

    private void InitializeConstructionChoices()
    {
        StorageUpgradeLevelsAheadComboBox.ItemsSource = Enumerable.Range(
            ConstructionDefaults.StorageUpgradeLevelsAheadMin,
            ConstructionDefaults.StorageUpgradeLevelsAheadMax - ConstructionDefaults.StorageUpgradeLevelsAheadMin + 1);
    }

    private void LoadConstructionHumanizeConfigToUi()
    {
        ConstructionHumanizeCheckBox.IsChecked = ReadBool(
            BotOptionPayloadKeys.ConstructionHumanizeDelayEnabled,
            PacingDefaults.ConstructionHumanizeDelayEnabled);
        ConstructionHumanizeQueuePercentMinTextBox.Text = FormatDelay(ReadDouble(
            BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMin,
            PacingDefaults.ConstructionHumanizeQueuePercentMin));
        ConstructionHumanizeQueuePercentMaxTextBox.Text = FormatDelay(ReadDouble(
            BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMax,
            PacingDefaults.ConstructionHumanizeQueuePercentMax));
        ConstructionHumanizeMaxDelayTextBox.Text = FormatDelay(ReadDouble(
            BotOptionPayloadKeys.ConstructionHumanizeMaxDelayMinutes,
            PacingDefaults.ConstructionHumanizeMaxDelayMinutes));
        ConstructionHumanizeNoPlusMinTextBox.Text = FormatDelay(ReadDouble(
            BotOptionPayloadKeys.ConstructionHumanizeNoPlusMinMinutes,
            PacingDefaults.ConstructionHumanizeNoPlusMinMinutes));
        ConstructionHumanizeNoPlusMaxTextBox.Text = FormatDelay(ReadDouble(
            BotOptionPayloadKeys.ConstructionHumanizeNoPlusMaxMinutes,
            PacingDefaults.ConstructionHumanizeNoPlusMaxMinutes));
    }

    private void SaveConstructionHumanizeConfigFromUi()
    {
        var wasEnabled = ReadBool(
            BotOptionPayloadKeys.ConstructionHumanizeDelayEnabled,
            PacingDefaults.ConstructionHumanizeDelayEnabled);
        var enabled = ConstructionHumanizeCheckBox.IsChecked == true;
        var percentMin = Math.Clamp(ReadDoubleText(
            ConstructionHumanizeQueuePercentMinTextBox,
            PacingDefaults.ConstructionHumanizeQueuePercentMin), 0, 100);
        var percentMax = Math.Clamp(Math.Max(percentMin, ReadDoubleText(
            ConstructionHumanizeQueuePercentMaxTextBox,
            PacingDefaults.ConstructionHumanizeQueuePercentMax)), 0, 100);
        var maxDelay = Math.Clamp(ReadDoubleText(
            ConstructionHumanizeMaxDelayTextBox,
            PacingDefaults.ConstructionHumanizeMaxDelayMinutes), 0, 600);
        var noPlusMin = Math.Clamp(ReadDoubleText(
            ConstructionHumanizeNoPlusMinTextBox,
            PacingDefaults.ConstructionHumanizeNoPlusMinMinutes), 0, 600);
        var noPlusMax = Math.Clamp(Math.Max(noPlusMin, ReadDoubleText(
            ConstructionHumanizeNoPlusMaxTextBox,
            PacingDefaults.ConstructionHumanizeNoPlusMaxMinutes)), 0, 600);

        _config[BotOptionPayloadKeys.ConstructionHumanizeDelayEnabled] = enabled;
        _config[BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMin] = percentMin;
        _config[BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMax] = percentMax;
        _config[BotOptionPayloadKeys.ConstructionHumanizeMaxDelayMinutes] = maxDelay;
        _config[BotOptionPayloadKeys.ConstructionHumanizeNoPlusMinMinutes] = noPlusMin;
        _config[BotOptionPayloadKeys.ConstructionHumanizeNoPlusMaxMinutes] = noPlusMax;
        if (wasEnabled != enabled)
        {
            var stateVersion = ReadInt(BotOptionPayloadKeys.ConstructionHumanizeStateVersion, 0);
            _config[BotOptionPayloadKeys.ConstructionHumanizeStateVersion] = stateVersion == int.MaxValue
                ? 1
                : stateVersion + 1;
        }
    }

    private void SettingsCategoryTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == sender && SettingsContentScrollViewer is not null)
        {
            SettingsContentScrollViewer.ScrollToTop();
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
        CaptureTownHallResults();
        DialogResult = true;
        Close();
    }

    // Writes the current UI values to the config store. Returns false (and shows the error) on failure so
    // callers can abort closing. Shared by Save and the "Sleep now" button.
    private bool PersistConfig()
    {
        try
        {
            if (!TryReadSpendingLimits(
                    out var goldLimit,
                    out var dailyGoldSpendingLimit,
                    out var silverLimit,
                    out var dailySilverSpendingLimit))
            {
                return false;
            }

            // The browser always runs visible; headless mode has been removed entirely.
            _config.Remove("headless");
            _config[BotOptionPayloadKeys.DontNotifyNewVersion] = DontNotifyNewVersionCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.PostLoginQuickReloginEnabled] = QuickReloginCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.AutomaticallyCheckLanguage] = AutomaticallyCheckLanguageCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.DetailedBrowserLoggingEnabled] = DetailedBrowserLoggingCheckBox.IsChecked == true;
            _config["allow_silver_spending"] = AllowSilverSpendingCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.AllowGoldSpending] = AllowGoldSpendingCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.GoldLimit] = goldLimit;
            _config[BotOptionPayloadKeys.DailyGoldSpendingLimit] = dailyGoldSpendingLimit;
            _config[BotOptionPayloadKeys.TownHallCelebrationCount] =
                TownHallCelebrationDefaults.NormalizeCount(TownHallQueue.Count);
            _config[BotOptionPayloadKeys.TownHallCelebrationRestartDelayMinMinutes] =
                TownHallQueue.ResolvedDelayMinMinutes;
            _config[BotOptionPayloadKeys.TownHallCelebrationRestartDelayMaxMinutes] =
                TownHallQueue.ResolvedDelayMaxMinutes;
            _config[BotOptionPayloadKeys.TownHallCelebrationRestartDelayEnabled] =
                TownHallQueue.IsRestartDelayEnabled;
            _config[BotOptionPayloadKeys.BreweryCelebrationRestartDelayEnabled] =
                BreweryRestartDelay.IsEnabled;
            _config[BotOptionPayloadKeys.BreweryCelebrationRestartDelayMinMinutes] =
                BreweryRestartDelay.ResolvedDelayMinMinutes;
            _config[BotOptionPayloadKeys.BreweryCelebrationRestartDelayMaxMinutes] =
                BreweryRestartDelay.ResolvedDelayMaxMinutes;
            _config[BotOptionPayloadKeys.HeroAdventureRestartDelayEnabled] =
                HeroAdventureRestartDelay.IsEnabled;
            _config[BotOptionPayloadKeys.HeroAdventureRestartDelayMinMinutes] =
                HeroAdventureRestartDelay.ResolvedDelayMinMinutes;
            _config[BotOptionPayloadKeys.HeroAdventureRestartDelayMaxMinutes] =
                HeroAdventureRestartDelay.ResolvedDelayMaxMinutes;
            _config[BotOptionPayloadKeys.HeroHpRegenPerDayPercent] =
                HeroHpRegenPerDayComboBox.SelectedItem is int heroHpRegenPercent
                    ? heroHpRegenPercent
                    : 40;
            _config[BotOptionPayloadKeys.SmithyUpgradeRestartDelayEnabled] =
                SmithyUpgradeRestartDelay.IsEnabled;
            _config[BotOptionPayloadKeys.SmithyUpgradeRestartDelayMinMinutes] =
                SmithyUpgradeRestartDelay.ResolvedDelayMinMinutes;
            _config[BotOptionPayloadKeys.SmithyUpgradeRestartDelayMaxMinutes] =
                SmithyUpgradeRestartDelay.ResolvedDelayMaxMinutes;
            SaveDailyServerResetFromUi();
            SavePacingConfigFromUi();
            _config[BotOptionPayloadKeys.ConstructionStorageUpgradeLevelsAhead] =
                ConstructionDefaults.NormalizeStorageUpgradeLevelsAhead(
                    StorageUpgradeLevelsAheadComboBox.SelectedItem is int selectedLevels
                        ? selectedLevels
                        : ConstructionDefaults.StorageUpgradeLevelsAhead);
            SaveConstructionHumanizeConfigFromUi();
            // Queue-wait handling is always "smart" (defer); drop the removed threshold key.
            _config.Remove("queue_wait_threshold_mode");
            _config[BotOptionPayloadKeys.PostLoginAnalyzeFarmlists] = PostLoginAnalyzeFarmlistsCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.PostLoginAnalyzeHero] = PostLoginAnalyzeHeroCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue] = PostLoginReadTroopTrainingQueueCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.PostLoginAnalyzeBrewery] = PostLoginAnalyzeBreweryCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.PostLoginAnalyzeHeroInventory] = PostLoginAnalyzeHeroInventoryCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.PostLoginAnalyzeNewVillages] = PostLoginAnalyzeNewVillagesCheckBox.IsChecked == true;
            _config[BotOptionPayloadKeys.SilverLimit] = silverLimit;
            _config[BotOptionPayloadKeys.DailySilverSpendingLimit] = dailySilverSpendingLimit;
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
        CaptureTownHallResults();
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

    private void ResetDailySilverLimitButton_Click(object sender, RoutedEventArgs e)
    {
        ResetDailySpending(_resetDailySilverSpending, "silver");
    }

    private void ResetDailyGoldLimitButton_Click(object sender, RoutedEventArgs e)
    {
        ResetDailySpending(_resetDailyGoldSpending, "gold");
    }

    private void ResetDailySpending(Action? reset, string currency)
    {
        if (reset is null)
        {
            return;
        }

        try
        {
            reset();
            AppDialog.Show(
                this,
                $"Today's {currency} spending counter was reset.",
                "Daily spending reset",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, ex.Message, "Could not reset daily spending", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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

    private bool TryReadSpendingLimits(
        out int goldLimit,
        out int dailyGoldSpendingLimit,
        out int silverLimit,
        out int dailySilverSpendingLimit)
    {
        var fields = new[]
        {
            (TextBox: GoldLimitTextBox, Label: "Minimum gold balance"),
            (TextBox: DailyGoldSpendingLimitTextBox, Label: "Daily gold spending limit"),
            (TextBox: SilverLimitTextBox, Label: "Minimum silver balance"),
            (TextBox: DailySilverSpendingLimitTextBox, Label: "Daily silver spending limit"),
        };
        var values = new int[fields.Length];
        for (var index = 0; index < fields.Length; index++)
        {
            if (!int.TryParse(fields[index].TextBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out values[index]))
            {
                AppDialog.Show(
                    this,
                    $"{fields[index].Label} must be a whole number between 0 and {int.MaxValue}.",
                    "Invalid spending limit",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                fields[index].TextBox.Focus();
                fields[index].TextBox.SelectAll();
                goldLimit = dailyGoldSpendingLimit = silverLimit = dailySilverSpendingLimit = 0;
                return false;
            }
        }

        goldLimit = values[0];
        dailyGoldSpendingLimit = values[1];
        silverLimit = values[2];
        dailySilverSpendingLimit = values[3];
        return true;
    }

    private void CaptureTownHallResults()
    {
        TownHallResults = TownHallRows
            .Select(row => new TownHallOverviewResult(
                row.VillageKey,
                row.VillageName,
                row.IsTownHallEnabled,
                row.Mode))
            .ToList();
        TownHallSettingsChanged = !string.Equals(
            _initialTownHallFingerprint,
            BuildTownHallFingerprint(),
            StringComparison.Ordinal);
    }

    private string BuildTownHallFingerprint()
    {
        var villages = string.Join(
            ";",
            TownHallRows
                .OrderBy(row => row.VillageKey, StringComparer.OrdinalIgnoreCase)
                .Select(row => $"{row.VillageKey}|{row.IsTownHallEnabled}|{row.Mode}"));
        return $"{villages}#{TownHallQueue.Count}|{TownHallQueue.ResolvedDelayMinMinutes:0.##}|{TownHallQueue.ResolvedDelayMaxMinutes:0.##}";
    }

}
