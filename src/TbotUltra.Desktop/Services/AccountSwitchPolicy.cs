namespace TbotUltra.Desktop.Services;

public static class AccountSwitchPolicy
{
    public static bool HasLiveBrowserSession(bool isLoggedIn, bool browserSessionOpen)
        => isLoggedIn && browserSessionOpen;

    public static bool RequiresConfirmation(
        bool isLoggedIn,
        bool browserSessionOpen,
        bool isSessionSleeping)
        => isSessionSleeping || HasLiveBrowserSession(isLoggedIn, browserSessionOpen);
}
