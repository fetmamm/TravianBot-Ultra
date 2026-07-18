namespace TbotUltra.Desktop.Services;

internal static class AccountServerInvariant
{
    internal static void EnsureMatches(string accountName, string accountServerUrl, string configuredBaseUrl)
    {
        var accountOrigin = NormalizeOrigin(accountServerUrl, "account server URL");
        var configuredOrigin = NormalizeOrigin(configuredBaseUrl, "configured base URL");
        if (!string.Equals(accountOrigin, configuredOrigin, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Login blocked because account '{accountName}' targets '{accountOrigin}' but the active configuration targets '{configuredOrigin}'. Reopen Manage accounts or restart the login after the server sync completes.");
        }
    }

    internal static string NormalizeOrigin(string value, string fieldName = "server URL")
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException($"The {fieldName} is not a valid absolute HTTP(S) URL.");
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }
}
