using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using TbotUltra.Core.Accounts;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed class AccountAnalysisStore
{
    private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.OrdinalIgnoreCase);
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
            var raw = RetryFileIo(() => File.ReadAllText(filePath), filePath, "read");
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

        var content = JsonSerializer.Serialize(analysis, JsonOptions);
        var fileLock = FileLocks.GetOrAdd(filePath, static _ => new object());
        lock (fileLock)
        {
            Directory.CreateDirectory(directory);
            WriteAtomically(filePath, content);
            DeleteFileIfExists(AccountStoragePaths.LegacyAnalysisPath(_rootPath, analysis.AccountName, analysis.ServerUrl));
        }
    }

    public void SaveWorldUid(string accountName, string serverUrl, string worldUid)
    {
        if (!Guid.TryParse(worldUid, out _))
        {
            throw new ArgumentException("World UID must be a GUID.", nameof(worldUid));
        }

        TryLoad(accountName, out var existing, serverUrl);
        var snapshot = existing is null
            ? new AccountAnalysisSnapshot(
                SchemaVersion: 0,
                AnalyzedAtUtc: DateTimeOffset.UtcNow,
                AccountName: accountName,
                ServerUrl: serverUrl,
                Tribe: "Unknown",
                GoldClubEnabled: false,
                BuildingCatalog: [],
                WorldUid: worldUid)
            : existing with
            {
                AnalyzedAtUtc = DateTimeOffset.UtcNow,
                WorldUid = worldUid,
            };
        Save(snapshot);
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
            RetryFileIo(() =>
            {
                File.Delete(filePath);
                return true;
            }, filePath, "delete");
        }
    }

    private static void WriteAtomically(string filePath, string content)
    {
        var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            RetryFileIo(() =>
            {
                File.WriteAllText(tempPath, content);
                File.Move(tempPath, filePath, overwrite: true);
                return true;
            }, filePath, "save");
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best-effort cleanup. A uniquely named stale temp file cannot corrupt the snapshot.
            }
        }
    }

    private static T RetryFileIo<T>(Func<T> action, string filePath, string operation)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < maxAttempts)
            {
                Debug.WriteLine($"[analysis-store] transient {operation} lock for '{filePath}' on attempt {attempt}/{maxAttempts}: {ex.Message}");
                Thread.Sleep(40 * attempt);
            }
        }
    }
}
