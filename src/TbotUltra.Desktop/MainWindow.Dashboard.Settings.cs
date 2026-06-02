using System;
using System.Windows;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    // Guards the checkbox handler while we apply loaded options, so seeding the UI does not
    // immediately write back to bot.json.
    private bool _suppressAutoCollectTasksConfigWrite;

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
}
