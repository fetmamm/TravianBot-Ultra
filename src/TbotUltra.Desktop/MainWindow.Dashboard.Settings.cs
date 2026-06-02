using System;
using System.Windows;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    // Guards the checkbox handler while we apply loaded options, so seeding the UI does not
    // immediately write back to bot.json.
    private bool _suppressAutoCollectTasksConfigWrite;
    private bool _suppressAutoCollectDailyQuestsConfigWrite;

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
    }

    private bool _suppressHeroResourceTransferConfigWrite;

    private void ApplyHeroResourceTransferConfigToUi(BotOptions options)
    {
        if (HeroResourceTransferCheckBox is null)
        {
            return;
        }

        _suppressHeroResourceTransferConfigWrite = true;
        try
        {
            HeroResourceTransferCheckBox.IsChecked = options.HeroResourceTransferEnabled;
        }
        finally
        {
            _suppressHeroResourceTransferConfigWrite = false;
        }
    }

    private void HeroResourceTransferSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressHeroResourceTransferConfigWrite || _botConfigStore is null)
        {
            return;
        }

        var config = _botConfigStore.Load();
        config[BotOptionPayloadKeys.HeroResourceTransferEnabled] = HeroResourceTransferCheckBox.IsChecked == true;
        _botConfigStore.Save(config);
    }

    // Opens the Hero / Adventures tab so the user can reach the hero inventory settings.
    private void HeroInventorySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (MainTabControl is not null && HeroTabItem is not null)
        {
            MainTabControl.SelectedItem = HeroTabItem;
        }
    }
}
