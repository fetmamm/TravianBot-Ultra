namespace TbotUltra.Desktop.Services;

public static class AccountSwitchPolicy
{
    public static bool HasLiveBrowserSession(bool isLoggedIn, bool browserSessionOpen)
        => isLoggedIn && browserSessionOpen;
}
