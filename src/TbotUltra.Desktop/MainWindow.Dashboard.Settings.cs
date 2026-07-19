using System;
using System.Threading.Tasks;
using System.Windows;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    // Guards the checkbox handler while we apply loaded options, so seeding the UI does not
    // immediately write back to bot.json.
    private bool _suppressAutoCollectTasksConfigWrite;
    private bool _suppressAutoCollectDailyQuestsConfigWrite;
    private bool _suppressProductionBonusVideoConfigWrite;

    private void ApplyAutoCollectTasksConfigToUi(BotOptions options)
    {
        if (AutoCollectTasksCheckBox is null)
        {
            return;
        }

        _suppressAutoCollectTasksConfigWrite = true;
        try
        {
            AutoCollectTasksCheckBox.IsChecked = options.AutoCollectTasksEnabled;
        }
        finally
        {
            _suppressAutoCollectTasksConfigWrite = false;
        }
    }

    private void AutoCollectTasksSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoCollectTasksConfigWrite || _botConfigStore is null)
        {
            return;
        }

        var config = _botConfigStore.Load();
        config[BotOptionPayloadKeys.AutoCollectTasksEnabled] = AutoCollectTasksCheckBox.IsChecked == true;
        _botConfigStore.Save(config);

        // Checked while the loop is already running → check/queue immediately instead of waiting
        // for the next 20s refresh.
        if (AutoCollectTasksCheckBox.IsChecked == true)
        {
            TriggerImmediateIfLoopRunning(options => TryQueueAutoCollectTasksAsync(options));
        }
    }

    private void ApplyAutoCollectDailyQuestsConfigToUi(BotOptions options)
    {
        if (AutoCollectDailyQuestsCheckBox is null)
        {
            return;
        }

        _suppressAutoCollectDailyQuestsConfigWrite = true;
        try
        {
            AutoCollectDailyQuestsCheckBox.IsChecked = options.AutoCollectDailyQuestsEnabled;
        }
        finally
        {
            _suppressAutoCollectDailyQuestsConfigWrite = false;
        }
    }

    private void AutoCollectDailyQuestsSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoCollectDailyQuestsConfigWrite || _botConfigStore is null)
        {
            return;
        }

        var config = _botConfigStore.Load();
        config[BotOptionPayloadKeys.AutoCollectDailyQuestsEnabled] = AutoCollectDailyQuestsCheckBox.IsChecked == true;
        _botConfigStore.Save(config);

        // Checked while the loop is already running → check/queue immediately.
        if (AutoCollectDailyQuestsCheckBox.IsChecked == true)
        {
            TriggerImmediateIfLoopRunning(options => TryQueueAutoCollectDailyQuestsAsync(options));
        }
    }

    private void ApplyProductionBonusVideoConfigToUi(BotOptions options)
    {
        if (ProductionBonusVideoCheckBox is null)
        {
            return;
        }

        _suppressProductionBonusVideoConfigWrite = true;
        try
        {
            ProductionBonusVideoCheckBox.IsChecked = options.ProductionBonusVideoEnabled;
        }
        finally
        {
            _suppressProductionBonusVideoConfigWrite = false;
        }
    }

    private void ProductionBonusVideoSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressProductionBonusVideoConfigWrite || _botConfigStore is null)
        {
            return;
        }

        var config = _botConfigStore.Load();
        config[BotOptionPayloadKeys.ProductionBonusVideoEnabled] = ProductionBonusVideoCheckBox.IsChecked == true;
        _botConfigStore.Save(config);

        // Enabled while the loop is already running -> try to queue immediately (subject to the 09:00 reset gate).
        if (ProductionBonusVideoCheckBox.IsChecked == true)
        {
            TriggerImmediateIfLoopRunning(options =>
            {
                TryQueueReadDailyResetHour(options);
                TryQueueActivateProductionBonus(options);
                return Task.CompletedTask;
            });
        }
    }

    // Fire the given immediate check only while the continuous loop is actually running and the
    // browser session is usable. The action (the same one the 20s refresh runs) self-guards and
    // de-duplicates, so this won't double-queue.
    private void TriggerImmediateIfLoopRunning(Func<BotOptions, Task> action)
    {
        var loopRunning = IsContinuousLoopRunning();
        if (!loopRunning || !_isLoggedIn || !_browserSessionLikelyOpen)
        {
            return;
        }

        var options = LoadBotOptions();
        _ = action(options);
    }

    // Opens the NPC / Trade workspace where the detailed trade controls live.
    private void GoldSpendingSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (MainTabControl is not null && NpcTradeTabItem is not null)
        {
            MainTabControl.SelectedItem = NpcTradeTabItem;
        }
    }
}
