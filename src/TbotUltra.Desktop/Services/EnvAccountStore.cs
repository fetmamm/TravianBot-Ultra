using System.IO;
using TbotUltra.Desktop.Models;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

public sealed class EnvAccountStore
{
    private readonly string _envPath;

    // Parsed-file cache keyed on the .env file's timestamp+length, so per-second callers
    // (ActiveAccountName via the options cache and the account picker) don't hit the disk.
    // Any change to the file (in-app write or external edit) changes the metadata and
    // invalidates the cache automatically.
    private readonly object _cacheSync = new();
    private Dictionary<string, string>? _cachedValues;
    private DateTime _cachedWriteTimeUtc;
    private long _cachedLength;

    public EnvAccountStore(string envPath)
    {
        _envPath = envPath;
    }

    public string ActiveAccountName()
    {
        var values = ReadValues();
        var names = ParseAccountNames(values);
        var configuredActive = values.TryGetValue("TBOT_ACTIVE_ACCOUNT", out var active) && !string.IsNullOrWhiteSpace(active)
            ? active.Trim()
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(configuredActive)
            && names.Contains(configuredActive, StringComparer.OrdinalIgnoreCase))
        {
            return configuredActive;
        }

        return names.Count > 0 ? names[0] : string.Empty;
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
                ProxyEnabled = ParseBool(values.GetValueOrDefault($"{prefix}PROXY_ENABLED", string.Empty)),
                ProxyServer = values.GetValueOrDefault($"{prefix}PROXY_SERVER", string.Empty),
                IsActive = string.Equals(name, active, StringComparison.OrdinalIgnoreCase),
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
        values[$"{prefix}PROXY_ENABLED"] = account.ProxyEnabled ? "true" : "false";
        values[$"{prefix}PROXY_SERVER"] = account.ProxyServer.Trim();

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
            values.Remove($"{prefix}PROXY_ENABLED");
            values.Remove($"{prefix}PROXY_SERVER");
            values["TBOT_ACCOUNTS"] = string.Join(",", names);

            if (string.Equals(values.GetValueOrDefault("TBOT_ACTIVE_ACCOUNT", string.Empty), normalized, StringComparison.OrdinalIgnoreCase))
            {
                values["TBOT_ACTIVE_ACCOUNT"] = names.Count > 0 ? names[0] : string.Empty;
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
        lock (_cacheSync)
        {
            var info = new FileInfo(_envPath);
            if (!info.Exists)
            {
                _cachedValues = null;
                return EnvFileParser.ReadValues(_envPath);
            }

            if (_cachedValues is not null
                && info.LastWriteTimeUtc == _cachedWriteTimeUtc
                && info.Length == _cachedLength)
            {
                // Copy: callers mutate the returned dictionary before writing it back.
                return new Dictionary<string, string>(_cachedValues, _cachedValues.Comparer);
            }

            var values = EnvFileParser.ReadValues(_envPath);
            _cachedValues = new Dictionary<string, string>(values, values.Comparer);
            _cachedWriteTimeUtc = info.LastWriteTimeUtc;
            _cachedLength = info.Length;
            return values;
        }
    }

    private void WriteValues(Dictionary<string, string> values)
    {
        var names = ParseAccountNames(values);
        var active = values.GetValueOrDefault("TBOT_ACTIVE_ACCOUNT", names.Count > 0 ? names[0] : string.Empty)
            ?? (names.Count > 0 ? names[0] : string.Empty);

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
            // Always emit a deterministic true/false so the file never carries an empty enabled flag.
            var proxyEnabled = ParseBool(values.GetValueOrDefault($"{prefix}PROXY_ENABLED", string.Empty));
            lines.Add($"{prefix}PROXY_ENABLED={(proxyEnabled ? "true" : "false")}");
            lines.Add($"{prefix}PROXY_SERVER={values.GetValueOrDefault($"{prefix}PROXY_SERVER", string.Empty)}");
            lines.Add(string.Empty);
        }

        File.WriteAllText(_envPath, string.Join(Environment.NewLine, lines));
        lock (_cacheSync)
        {
            _cachedValues = null;
        }
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

    // Lenient: only an explicit "true" (any casing) is on; empty/missing/anything else is off.
    private static bool ParseBool(string? value)
        => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

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
