namespace TbotUltra.Core.Accounts;

public static class AccountKeyNormalizer
{
    public static string MakeKey(string username, string serverUrl)
    {
        var userPart = NormalizeIdentifier(username);
        if (userPart.Length == 0)
        {
            throw new InvalidOperationException("Username cannot be normalized to account key.");
        }

        var serverPart = NormalizeServerHost(serverUrl);
        return string.IsNullOrEmpty(serverPart) ? userPart : $"{userPart}__{serverPart}";
    }

    public static string NormalizeIdentifier(string raw)
    {
        var chars = (raw ?? string.Empty).Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')
            .ToArray();
        return string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
    }

    public static string NormalizeServerHost(string serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)) return string.Empty;
        var trimmed = serverUrl.Trim();
        var hostPart = trimmed;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            hostPart = uri.Host;
            if (!uri.IsDefaultPort) hostPart += "_" + uri.Port;
        }
        return NormalizeIdentifier(hostPart);
    }
}
