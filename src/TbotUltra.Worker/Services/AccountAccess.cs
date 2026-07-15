namespace TbotUltra.Worker.Services;

public enum AccountAccessState
{
    LoggedIn,
    LoggedOut,
    Unavailable,
    Restricted,
    Challenge,
    Unknown,
}

public sealed class AccountAccessException : InvalidOperationException
{
    public AccountAccessException(string accountName, AccountAccessState state, string reason)
        : base(reason)
    {
        AccountName = accountName;
        State = state;
    }

    public string AccountName { get; }
    public AccountAccessState State { get; }
}

internal static class AccountAccessClassifier
{
    public static AccountAccessState? ClassifyExplicit(string? url, string? pageSignal, bool captchaInputPresent)
    {
        var currentUrl = url?.ToLowerInvariant() ?? string.Empty;
        var signal = pageSignal?.ToLowerInvariant() ?? string.Empty;
        if (captchaInputPresent
            || currentUrl.Contains("captcha", StringComparison.Ordinal)
            || currentUrl.Contains("challenge", StringComparison.Ordinal)
            || ContainsAny(signal,
                "verify that you are human",
                "security check",
                "unusual activity detected",
                "complete the captcha"))
        {
            return AccountAccessState.Challenge;
        }

        if (currentUrl.Contains("banned", StringComparison.Ordinal)
            || currentUrl.Contains("suspended", StringComparison.Ordinal)
            || currentUrl.Contains("restricted", StringComparison.Ordinal)
            || ContainsAny(signal,
                "your account has been banned",
                "account is banned",
                "account has been locked",
                "account is suspended",
                "access to this account has been restricted"))
        {
            return AccountAccessState.Restricted;
        }

        return null;
    }

    public static (int ConsecutiveUnknown, bool Stop) RegisterVerifiedState(
        int currentConsecutiveUnknown,
        AccountAccessState state)
    {
        if (state != AccountAccessState.Unknown)
        {
            return (0, false);
        }

        var next = Math.Max(0, currentConsecutiveUnknown) + 1;
        return (next, next >= 3);
    }

    private static bool ContainsAny(string value, params string[] candidates)
        => candidates.Any(candidate => value.Contains(candidate, StringComparison.Ordinal));
}
