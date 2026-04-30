using System.Text.Json;
using TbotUltra.Core.Accounts;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed class AccountAnalysisStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _rootPath;

    public AccountAnalysisStore(string rootPath)
    {
        _rootPath = rootPath;
    }

    public string GetFilePath(string accountName, string? serverUrl = null)
    {
        var normalizedAccount = NormalizeAccountName(accountName);
        var normalizedServer = NormalizeServerKey(serverUrl);
        return Path.Combine(_rootPath, AccountAnalysisConstants.RelativeDirectory, $"{normalizedAccount}__{normalizedServer}.json");
    }

    public bool IsAnalyzed(string accountName, string? serverUrl = null)
    {
        if (TryLoad(accountName, out var analysis, serverUrl) && analysis is not null)
        {
            return analysis.SchemaVersion == AccountAnalysisConstants.CurrentSchemaVersion;
        }

        return false;
    }

    public bool TryLoad(string accountName, out AccountAnalysisSnapshot? analysis, string? serverUrl = null)
    {
        analysis = null;
        var filePath = GetFilePath(accountName, serverUrl);
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var raw = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            analysis = JsonSerializer.Deserialize<AccountAnalysisSnapshot>(raw, JsonOptions);
            if (analysis is null)
            {
                return false;
            }

            var requested = NormalizeAccountName(accountName);
            var analyzed = NormalizeAccountName(analysis.AccountName);
            if (!string.Equals(requested, analyzed, StringComparison.Ordinal))
            {
                analysis = null;
                return false;
            }

            var requestedServer = NormalizeServerKey(serverUrl);
            var analyzedServer = NormalizeServerKey(analysis.ServerUrl);
            if (!string.Equals(requestedServer, analyzedServer, StringComparison.Ordinal))
            {
                analysis = null;
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Save(AccountAnalysisSnapshot analysis)
    {
        var filePath = GetFilePath(analysis.AccountName, analysis.ServerUrl);
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Account analysis path is invalid.");
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(filePath, JsonSerializer.Serialize(analysis, JsonOptions));
    }

    public void Delete(string accountName, string? serverUrl = null)
    {
        var filePath = GetFilePath(accountName, serverUrl);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private static string NormalizeAccountName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "main";
        }

        var chars = value.Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')
            .ToArray();
        var joined = string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return joined.Length == 0 ? "main" : joined;
    }

    private static string NormalizeServerKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "default_server";
        }

        var trimmed = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return "default_server";
        }

        var hostPart = string.IsNullOrWhiteSpace(uri.Host) ? "default_server" : uri.Host.ToLowerInvariant();
        var portPart = uri.IsDefaultPort ? string.Empty : $"_{uri.Port}";
        var chars = $"{hostPart}{portPart}"
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        var joined = string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return joined.Length == 0 ? "default_server" : joined;
    }
}
