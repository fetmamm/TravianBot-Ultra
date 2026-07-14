namespace TbotUltra.Worker.Infrastructure;

internal enum BonusVideoFailureKind
{
    None,
    NoAdOrCookies,
    Network,
    Session,
    Codec,
    Timeout,
    Unavailable,
    Unknown,
}

internal static class BonusVideoFailureClassifier
{
    internal static BonusVideoFailureKind Classify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return BonusVideoFailureKind.Unknown;
        }

        var text = value.ToLowerInvariant();
        if (ContainsAny(text, " video completed", " activated;", "already active", "bonus is now active"))
        {
            return BonusVideoFailureKind.None;
        }

        if (ContainsAny(text, "h.264", "aac codec", "missing codec", "cannot play the ad video"))
        {
            return BonusVideoFailureKind.Codec;
        }

        if (ContainsAny(text, "not logged in", "stale cookies", "session expired"))
        {
            return BonusVideoFailureKind.Session;
        }

        if (ContainsAny(
                text,
                "ad blocker",
                "third-party cook",
                "no ad available",
                "no ad inventory",
                "likely no ad",
                "player did not open",
                "video player did not open"))
        {
            return BonusVideoFailureKind.NoAdOrCookies;
        }

        if (ContainsAny(
                text,
                "err_proxy",
                "err_socks",
                "err_tunnel",
                "err_connection",
                "err_name_not_resolved",
                "network",
                "dns",
                "connection reset"))
        {
            return BonusVideoFailureKind.Network;
        }

        if (ContainsAny(text, "timeout", "timed out", "hard cap", "not confirmed within", "completion was not confirmed"))
        {
            return BonusVideoFailureKind.Timeout;
        }

        if (ContainsAny(text, "button disabled", "button missing", "feature was not found", "not available", "attempts are paused", "cooldown"))
        {
            return BonusVideoFailureKind.Unavailable;
        }

        return BonusVideoFailureKind.Unknown;
    }

    internal static TimeSpan Cooldown(BonusVideoFailureKind kind)
        => kind switch
        {
            BonusVideoFailureKind.NoAdOrCookies => TimeSpan.FromMinutes(20),
            BonusVideoFailureKind.Network => TimeSpan.FromMinutes(10),
            BonusVideoFailureKind.Session => TimeSpan.FromMinutes(5),
            BonusVideoFailureKind.Codec => TimeSpan.FromHours(6),
            BonusVideoFailureKind.Timeout => TimeSpan.FromMinutes(30),
            BonusVideoFailureKind.Unavailable => TimeSpan.FromMinutes(20),
            BonusVideoFailureKind.Unknown => TimeSpan.FromMinutes(10),
            _ => TimeSpan.Zero,
        };

    internal static bool ShouldRetryImmediately(BonusVideoFailureKind kind)
        => kind == BonusVideoFailureKind.Unknown;

    private static bool ContainsAny(string value, params string[] markers)
        => markers.Any(value.Contains);
}
