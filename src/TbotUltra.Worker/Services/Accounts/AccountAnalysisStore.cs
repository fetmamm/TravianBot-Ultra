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

    public string GetFilePath(string accountName)
    {
        var normalized = NormalizeAccountName(accountName);
        return Path.Combine(_rootPath, AccountAnalysisConstants.RelativeDirectory, $"{normalized}.json");
    }

    public bool IsAnalyzed(string accountName)
    {
        if (TryLoad(accountName, out var analysis) && analysis is not null)
        {
            return analysis.SchemaVersion == AccountAnalysisConstants.CurrentSchemaVersion;
        }

        return false;
    }

    public bool TryLoad(string accountName, out AccountAnalysisSnapshot? analysis)
    {
        analysis = null;
        var filePath = GetFilePath(accountName);
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
            return analysis is not null;
        }
        catch
        {
            return false;
        }
    }

    public void Save(AccountAnalysisSnapshot analysis)
    {
        var filePath = GetFilePath(analysis.AccountName);
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Account analysis path is invalid.");
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(filePath, JsonSerializer.Serialize(analysis, JsonOptions));
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
}
