namespace TbotUltra.Core.Accounts;

using System.Security.Cryptography;
using System.Text;

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

    public static string MakeCollisionResistantKey(string username, string serverUrl)
    {
        var readableKey = MakeKey(username, serverUrl);
        var canonicalIdentity = $"{username.Trim().ToLowerInvariant()}\n{NormalizeServerIdentity(serverUrl)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalIdentity));
        return $"{readableKey}__{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    public static bool IsSameIdentity(
        string leftUsername,
        string leftServerUrl,
        string rightUsername,
        string rightServerUrl)
        => string.Equals(leftUsername?.Trim(), rightUsername?.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                NormalizeServerIdentity(leftServerUrl),
                NormalizeServerIdentity(rightServerUrl),
                StringComparison.OrdinalIgnoreCase);

    public static string NormalizeServerIdentity(string serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return string.Empty;
        }

        var trimmed = serverUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return trimmed.ToLowerInvariant();
        }

        var authority = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        var path = uri.AbsolutePath == "/" ? string.Empty : uri.AbsolutePath.TrimEnd('/');
        return authority + path;
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
