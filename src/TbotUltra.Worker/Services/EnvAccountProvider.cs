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
        var envValues = ReadEnvFile(_envPath);
        var selectedName = accountName
            ?? GetValue("TBOT_ACTIVE_ACCOUNT", envValues)
            ?? "main";

        var envPrefix = $"TBOT_{selectedName.ToUpperInvariant()}_";
        var username = GetValue($"{envPrefix}USERNAME", envValues);
        var password = GetValue($"{envPrefix}PASSWORD", envValues);
        var serverName = GetValue($"{envPrefix}SERVER_NAME", envValues) ?? string.Empty;
        var serverUrl = (GetValue($"{envPrefix}SERVER_URL", envValues) ?? string.Empty).TrimEnd('/');

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
        };
    }

    private static Dictionary<string, string> ReadEnvFile(string envPath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(envPath))
        {
            return values;
        }

        foreach (var rawLine in File.ReadAllLines(envPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains('='))
            {
                continue;
            }

            var splitIndex = line.IndexOf('=');
            var key = line[..splitIndex].Trim();
            var value = line[(splitIndex + 1)..].Trim().Trim('"').Trim('\'');
            if (key.Length == 0)
            {
                continue;
            }

            values[key] = value;
        }

        return values;
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
