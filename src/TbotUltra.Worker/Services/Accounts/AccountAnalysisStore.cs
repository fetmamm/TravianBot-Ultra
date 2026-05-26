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
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _rootPath;

    public AccountAnalysisStore(string rootPath)
    {
        _rootPath = rootPath;
    }

    public string GetFilePath(string accountName, string? serverUrl = null)
    {
        return AccountStoragePaths.AnalysisPath(_rootPath, accountName, serverUrl);
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
        if (TryLoadFromPath(filePath, accountName, serverUrl, out analysis))
        {
            return true;
        }

        var legacyPath = AccountStoragePaths.LegacyAnalysisPath(_rootPath, accountName, serverUrl);
        if (TryLoadFromPath(legacyPath, accountName, serverUrl, out analysis))
        {
            Save(analysis!);
            DeleteFileIfExists(legacyPath);
            return true;
        }

        return false;
    }

    private static bool TryLoadFromPath(string filePath, string accountName, string? serverUrl, out AccountAnalysisSnapshot? analysis)
    {
        analysis = null;
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

            var requested = AccountStoragePaths.NormalizeAccountKey(accountName);
            var analyzed = AccountStoragePaths.NormalizeAccountKey(analysis.AccountName);
            if (!string.Equals(requested, analyzed, StringComparison.Ordinal))
            {
                analysis = null;
                return false;
            }

            var requestedServer = AccountStoragePaths.NormalizeServerKey(serverUrl);
            var analyzedServer = AccountStoragePaths.NormalizeServerKey(analysis.ServerUrl);
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
        DeleteFileIfExists(AccountStoragePaths.LegacyAnalysisPath(_rootPath, analysis.AccountName, analysis.ServerUrl));
    }

    public void Delete(string accountName, string? serverUrl = null)
    {
        DeleteFileIfExists(GetFilePath(accountName, serverUrl));
        DeleteFileIfExists(AccountStoragePaths.LegacyAnalysisPath(_rootPath, accountName, serverUrl));
    }

    private static void DeleteFileIfExists(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
}
