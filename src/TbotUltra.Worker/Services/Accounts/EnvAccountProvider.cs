using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Configuration;

namespace TbotUltra.Worker.Services;

public sealed class EnvAccountProvider : IAccountProvider
{
    private static readonly HashSet<string> ExampleValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "your_username_or_email",
        "your_password",
        "ditt_anvandarnamn",
        "ditt_losenord",
    };

    private readonly string _envPath;

    public EnvAccountProvider(string envPath)
    {
        _envPath = envPath;
    }

    public AccountOptions LoadAccount(string? accountName = null)
    {
        var envValues = EnvFileParser.ReadValues(_envPath);
        var accountNames = ParseAccountNames(envValues);
        var requestedName = accountName
            ?? GetValue("TBOT_ACTIVE_ACCOUNT", envValues)
            ?? string.Empty;

        var selectedName = !string.IsNullOrWhiteSpace(requestedName)
            && accountNames.Contains(requestedName, StringComparer.OrdinalIgnoreCase)
            ? requestedName
            : accountNames.FirstOrDefault() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(selectedName))
        {
            throw new InvalidOperationException("No account configured. Open Manage and create an account first.");
        }

        var envPrefix = $"TBOT_{selectedName.ToUpperInvariant()}_";
        var username = GetValue($"{envPrefix}USERNAME", envValues);
        var password = GetValue($"{envPrefix}PASSWORD", envValues);
        var serverName = GetValue($"{envPrefix}SERVER_NAME", envValues) ?? string.Empty;
        var serverUrl = (GetValue($"{envPrefix}SERVER_URL", envValues) ?? string.Empty).TrimEnd('/');
        var proxyEnabled = string.Equals(
            (GetValue($"{envPrefix}PROXY_ENABLED", envValues) ?? string.Empty).Trim(),
            "true",
            StringComparison.OrdinalIgnoreCase);
        var proxyServer = (GetValue($"{envPrefix}PROXY_SERVER", envValues) ?? string.Empty).Trim();
        var neverUseOwnIp = string.Equals(
            (GetValue($"{envPrefix}NEVER_USE_OWN_IP", envValues) ?? string.Empty).Trim(),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                $"Missing credentials for account '{selectedName}'. Add {envPrefix}USERNAME and {envPrefix}PASSWORD to .env."
            );
        }

        if (ExampleValues.Contains(username) || ExampleValues.Contains(password))
        {
            throw new InvalidOperationException(
                $"Credentials for account '{selectedName}' still look like example values. Open .env and add real credentials."
            );
        }

        return new AccountOptions
        {
            Name = selectedName,
            Username = username,
            Password = password,
            ServerName = serverName,
            ServerUrl = serverUrl,
            ProxyEnabled = proxyEnabled,
            ProxyServer = proxyServer,
            NeverUseOwnIp = neverUseOwnIp,
        };
    }

    private static List<string> ParseAccountNames(Dictionary<string, string> values)
    {
        return values.GetValueOrDefault("TBOT_ACCOUNTS", string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? GetValue(string key, Dictionary<string, string> fileValues)
    {
        if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } envValue)
        {
            return envValue;
        }

        return fileValues.TryGetValue(key, out var fileValue) ? fileValue : null;
    }
}
