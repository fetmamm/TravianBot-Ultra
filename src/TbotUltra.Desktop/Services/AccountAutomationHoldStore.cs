using System.IO;
using System.Text.Json;
using TbotUltra.Core.Accounts;

namespace TbotUltra.Desktop.Services;

internal sealed record AccountAutomationHold(
    string AccountName,
    string AccessState,
    string Reason,
    DateTimeOffset CreatedAtUtc);

internal sealed class AccountAutomationHoldStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _projectRoot;
    private readonly Action<string>? _log;

    public AccountAutomationHoldStore(string projectRoot, Action<string>? log = null)
    {
        _projectRoot = projectRoot;
        _log = log;
    }

    public AccountAutomationHold? Load(string accountName)
    {
        var path = AccountStoragePaths.AutomationHoldPath(_projectRoot, accountName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AccountAutomationHold>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            QuarantineCorruptFile(path);
            _log?.Invoke($"[account-hold] corrupt hold state quarantined for account '{accountName}': {ex.Message}");
            return null;
        }
    }

    public void Save(AccountAutomationHold hold)
    {
        var path = AccountStoragePaths.AutomationHoldPath(_projectRoot, hold.AccountName);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(hold, JsonOptions));
        _log?.Invoke($"[account-hold] persisted for account '{hold.AccountName}' state={hold.AccessState}.");
    }

    public void Clear(string accountName)
    {
        var path = AccountStoragePaths.AutomationHoldPath(_projectRoot, accountName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        _log?.Invoke($"[account-hold] manually cleared for account '{accountName}'.");
    }

    private static void QuarantineCorruptFile(string path)
    {
        try
        {
            var quarantinePath = $"{path}.corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(path, quarantinePath, overwrite: true);
        }
        catch
        {
            // The unreadable file remains visible for diagnostics if quarantine itself fails.
        }
    }
}
