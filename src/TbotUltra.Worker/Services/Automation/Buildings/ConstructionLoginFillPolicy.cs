namespace TbotUltra.Worker.Services;

internal static class ConstructionLoginFillPolicy
{
    public static bool IsActive(bool enabled, long? expiresAtUnixSeconds, DateTimeOffset now)
    {
        return enabled
            && expiresAtUnixSeconds is long expiry
            && expiry > now.ToUnixTimeSeconds();
    }
}
