using System.Text.Json.Nodes;

namespace TbotUltra.Desktop.Services;

public static class ServerTimeClock
{
    public static TimeSpan ResolveUtcOffset(JsonObject config, DateTime utcNow)
    {
        var configuredHours = config["server_time_utc_offset_hours"]?.GetValue<double?>();
        return configuredHours.HasValue
            ? TimeSpan.FromHours(configuredHours.Value)
            : TimeZoneInfo.Local.GetUtcOffset(utcNow);
    }

    public static string Format(DateTimeOffset serverTime)
        => serverTime.ToString("yyyy-MM-dd HH:mm:ss");
}
