namespace TbotUltra.Desktop;

// Server-time clock and queue-clear settings: reading the queue clear-on-
// startup/shutdown flags, resolving the server UTC offset, and formatting the
// dashboard clock / queue timestamps in server time. Extracted verbatim from
// MainWindow.xaml.cs to keep that file focused; same class, so this is a pure
// relocation with no behavior change.
public partial class MainWindow
{
    // Default OFF so the per-account build queue PERSISTS across restarts (a long multi-village queue
    // survives an accidental close). The queue is still cleared by Stop (with confirmation) and the
    // Clear account/village queue buttons. Users can opt back in via the bot.json keys.
    private bool ShouldClearQueueOnStartup()
    {
        return ReadQueueClearSetting("queue_clear_on_startup", defaultValue: false);
    }

    private bool ShouldClearQueueOnShutdown()
    {
        return ReadQueueClearSetting("queue_clear_on_shutdown", defaultValue: false);
    }

    private bool ReadQueueClearSetting(string key, bool defaultValue)
    {
        try
        {
            var config = _botConfigStore.Load();
            return config[key]?.GetValue<bool>() ?? defaultValue;
        }
        catch (Exception ex)
        {
            AppendLog($"Could not read {key}: {ex.Message}");
            return defaultValue;
        }
    }

    private TimeSpan ResolveQueueServerTimeOffset()
    {
        try
        {
            var config = _botConfigStore.Load();
            var configuredHours = config["server_time_utc_offset_hours"]?.GetValue<double?>();
            if (configuredHours.HasValue)
            {
                return TimeSpan.FromHours(configuredHours.Value);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not read server_time_utc_offset_hours: {ex.Message}");
        }

        return TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
    }

    private DateTimeOffset GetServerNow()
    {
        return DateTimeOffset.UtcNow.ToOffset(_queueServerTimeOffset);
    }

    private void UpdateClockText()
    {
        ClockTextBlock.Text = $"{GetServerNow():yyyy-MM-dd HH:mm:ss}";
    }

    private string FormatQueueServerTime(DateTimeOffset utcTimestamp)
    {
        var utc = utcTimestamp.ToUniversalTime();
        var serverTime = utc.ToOffset(_queueServerTimeOffset);
        return serverTime.ToString("yyyy-MM-dd HH:mm:ss");
    }
}
