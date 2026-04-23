using System.IO;
using TbotUltra.Desktop.Models;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

public sealed class EnvAccountStore
{
    private readonly string _envPath;
    private readonly AccountAnalysisStore _analysisStore;

    public EnvAccountStore(string envPath)
    {
        _envPath = envPath;
        var root = Path.GetDirectoryName(envPath) ?? Directory.GetCurrentDirectory();
        _analysisStore = new AccountAnalysisStore(root);
    }

    public string ActiveAccountName()
    {
        var values = ReadValues();
        return values.TryGetValue("TBOT_ACTIVE_ACCOUNT", out var active) && !string.IsNullOrWhiteSpace(active)
            ? active
            : "main";
    }

    public List<AccountEntry> ListAccounts()
    {
        var values = ReadValues();
        var names = ParseAccountNames(values);
        var active = ActiveAccountName();

        return names.Select(name =>
        {
            var prefix = $"TBOT_{name.ToUpperInvariant()}_";
            return new AccountEntry
            {
                Name = name,
                Username = values.GetValueOrDefault($"{prefix}USERNAME", string.Empty),
                Password = values.GetValueOrDefault($"{prefix}PASSWORD", string.Empty),
                ServerName = values.GetValueOrDefault($"{prefix}SERVER_NAME", string.Empty),
                ServerUrl = values.GetValueOrDefault($"{prefix}SERVER_URL", string.Empty),
                IsActive = string.Equals(name, active, StringComparison.OrdinalIgnoreCase),
                IsAnalyzed = _analysisStore.IsAnalyzed(name),
            };
        }).ToList();
    }

    public void SaveAccount(AccountEntry account, bool setActive)
    {
        if (string.IsNullOrWhiteSpace(account.Name))
        {
            throw new InvalidOperationException("Account name cannot be empty.");
        }

        var normalized = NormalizeName(account.Name);
        var values = ReadValues();
        var names = ParseAccountNames(values);

        if (!names.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(normalized);
        }

        values["TBOT_ACCOUNTS"] = string.Join(",", names);
        if (setActive)
        {
            values["TBOT_ACTIVE_ACCOUNT"] = normalized;
        }

        var prefix = $"TBOT_{normalized.ToUpperInvariant()}_";
        values[$"{prefix}USERNAME"] = account.Username.Trim();
        values[$"{prefix}PASSWORD"] = account.Password;
        values[$"{prefix}SERVER_NAME"] = account.ServerName.Trim();
        values[$"{prefix}SERVER_URL"] = account.ServerUrl.Trim().TrimEnd('/');

        WriteValues(values);
    }

    public void DeleteAccount(string accountName)
    {
        var normalized = NormalizeName(accountName);
        var values = ReadValues();
        var names = ParseAccountNames(values);
        if (!names.RemoveAll(name => string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase)).Equals(0))
        {
            var prefix = $"TBOT_{normalized.ToUpperInvariant()}_";
            values.Remove($"{prefix}USERNAME");
            values.Remove($"{prefix}PASSWORD");
            values.Remove($"{prefix}SERVER_NAME");
            values.Remove($"{prefix}SERVER_URL");
            values["TBOT_ACCOUNTS"] = string.Join(",", names);

            if (string.Equals(values.GetValueOrDefault("TBOT_ACTIVE_ACCOUNT", string.Empty), normalized, StringComparison.OrdinalIgnoreCase))
            {
                values["TBOT_ACTIVE_ACCOUNT"] = names.Count > 0 ? names[0] : "main";
            }

            WriteValues(values);
            return;
        }

        throw new InvalidOperationException($"Account '{normalized}' does not exist.");
    }

    public void SetActive(string accountName)
    {
        var normalized = NormalizeName(accountName);
        var values = ReadValues();
        var names = ParseAccountNames(values);
        if (!names.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Account '{normalized}' does not exist.");
        }

        values["TBOT_ACTIVE_ACCOUNT"] = normalized;
        WriteValues(values);
    }

    private Dictionary<string, string> ReadValues()
    {
        return EnvFileParser.ReadValues(_envPath);
    }

    private void WriteValues(Dictionary<string, string> values)
    {
        var names = ParseAccountNames(values);
        var active = values.GetValueOrDefault("TBOT_ACTIVE_ACCOUNT", names.Count > 0 ? names[0] : "main")
            ?? (names.Count > 0 ? names[0] : "main");

        var lines = new List<string>
        {
            "# Tbot Ultra local account settings",
            "# Do not commit this file to GitHub.",
            string.Empty,
            $"TBOT_ACTIVE_ACCOUNT={active}",
            $"TBOT_ACCOUNTS={string.Join(",", names)}",
            string.Empty,
        };

        foreach (var name in names)
        {
            var prefix = $"TBOT_{name.ToUpperInvariant()}_";
            lines.Add($"{prefix}USERNAME={values.GetValueOrDefault($"{prefix}USERNAME", string.Empty)}");
            lines.Add($"{prefix}PASSWORD={values.GetValueOrDefault($"{prefix}PASSWORD", string.Empty)}");
            lines.Add($"{prefix}SERVER_NAME={values.GetValueOrDefault($"{prefix}SERVER_NAME", string.Empty)}");
            lines.Add($"{prefix}SERVER_URL={values.GetValueOrDefault($"{prefix}SERVER_URL", string.Empty)}");
            lines.Add(string.Empty);
        }

        File.WriteAllText(_envPath, string.Join(Environment.NewLine, lines));
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

    private static string NormalizeName(string raw)
    {
        var chars = raw.Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')
            .ToArray();
        var joined = string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        if (joined.Length == 0)
        {
            throw new InvalidOperationException("Account name cannot be empty.");
        }

        return joined;
    }
}
