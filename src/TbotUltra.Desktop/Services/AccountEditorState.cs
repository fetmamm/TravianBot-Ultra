using TbotUltra.Core.Accounts;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop.Services;

internal sealed record AccountEditorSnapshot(
    string Username,
    string Password,
    string ServerUrl,
    bool ProxyEnabled,
    string ProxyServer,
    bool NeverUseOwnIp);

internal sealed record SavedProxyOption(ProxyLibraryEntry? Entry, string DisplayText);

internal sealed record AccountEditorInput(
    string Username,
    string Password,
    string ServerName,
    string ServerUrl,
    bool ProxyEnabled,
    bool NeverUseOwnIp,
    string ProxyScheme,
    string ProxyHost,
    string ProxyPort,
    bool EditingExistingAccount,
    string ExistingAccountName);

/// <summary>
/// Stateless account-editor state comparisons and saved-proxy presentation ordering.
/// </summary>
internal static class AccountEditorState
{
    internal static AccountEntry BuildAccountEntry(AccountEditorInput input)
    {
        var username = input.Username.Trim();
        var password = input.Password;
        if (username.Length == 0 || password.Length == 0)
        {
            throw new InvalidOperationException("Username and password are required.");
        }

        var proxyEnabled = input.ProxyEnabled;
        var neverUseOwnIp = input.NeverUseOwnIp;
        var proxyHost = input.ProxyHost.Trim();
        var proxyPort = input.ProxyPort.Trim();
        var proxyScheme = string.IsNullOrWhiteSpace(input.ProxyScheme) ? "socks5" : input.ProxyScheme.Trim();
        var proxyServer = proxyHost.Length == 0 && proxyPort.Length == 0
            ? string.Empty
            : $"{proxyScheme}://{proxyHost}:{proxyPort}";
        if (proxyEnabled || neverUseOwnIp)
        {
            if (proxyHost.Length == 0 || proxyPort.Length == 0)
            {
                throw new InvalidOperationException("Proxy host/IP and port are required when proxy protection is on.");
            }

            if (proxyHost.Any(char.IsWhiteSpace)
                || proxyHost.Contains("://", StringComparison.Ordinal)
                || proxyHost.Contains(':'))
            {
                throw new InvalidOperationException("Proxy host/IP must not contain spaces, scheme, or port. Use the separate type and port fields.");
            }

            if (!int.TryParse(proxyPort, out var parsedPort) || parsedPort is < 1 or > 65535)
            {
                throw new InvalidOperationException("Proxy port must be a number between 1 and 65535.");
            }

            if (neverUseOwnIp)
            {
                proxyEnabled = true;
                if (!ProxyParser.TryBuild(proxyServer, out _, out _))
                {
                    throw new InvalidOperationException("Never use own IP address requires a valid proxy.");
                }
            }
        }

        return new AccountEntry
        {
            Name = input.EditingExistingAccount
                ? input.ExistingAccountName
                : AccountKeyNormalizer.MakeKey(username, input.ServerUrl),
            Username = username,
            Password = password,
            ServerName = input.ServerName,
            ServerUrl = input.ServerUrl,
            ProxyEnabled = proxyEnabled,
            ProxyServer = proxyServer,
            NeverUseOwnIp = neverUseOwnIp,
        };
    }

    internal static bool HasChanges(AccountEditorSnapshot baseline, AccountEditorSnapshot current)
    {
        return !string.Equals(current.Username, baseline.Username, StringComparison.Ordinal)
            || !string.Equals(current.Password, baseline.Password, StringComparison.Ordinal)
            || !string.Equals(current.ServerUrl, baseline.ServerUrl, StringComparison.OrdinalIgnoreCase)
            || current.ProxyEnabled != baseline.ProxyEnabled
            || !string.Equals(current.ProxyServer, baseline.ProxyServer, StringComparison.Ordinal)
            || current.NeverUseOwnIp != baseline.NeverUseOwnIp;
    }

    internal static List<SavedProxyOption> BuildSavedProxyOptions(
        IEnumerable<ProxyLibraryEntry> entries,
        string? accountName)
    {
        var normalizedAccount = accountName?.Trim() ?? string.Empty;
        var orderedEntries = entries
            .OrderByDescending(entry => normalizedAccount.Length > 0
                && string.Equals(entry.AssignedAccount, normalizedAccount, StringComparison.OrdinalIgnoreCase))
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.HostPort, StringComparer.OrdinalIgnoreCase);

        var options = new List<SavedProxyOption>
        {
            new(null, "Select saved proxy..."),
        };
        options.AddRange(orderedEntries.Select(entry => new SavedProxyOption(entry, BuildSavedProxyDisplay(entry))));
        return options;
    }

    private static string BuildSavedProxyDisplay(ProxyLibraryEntry entry)
    {
        var display = entry.DisplayName;
        if (!string.IsNullOrWhiteSpace(entry.AssignedAccount))
        {
            return $"{display} [locked: {entry.AssignedAccount}]";
        }

        var usedCount = entry.UsedByAccounts.Count(item => !string.IsNullOrWhiteSpace(item));
        return usedCount > 0 ? $"{display} [used: {usedCount}]" : display;
    }
}
