using System;
using System.Text.RegularExpressions;

namespace TbotUltra.Desktop.Models;

public sealed class AccountEntry
{
    private const string ManageAccountsOptionName = "__manage_accounts__";

    public string Name { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty;
    public bool ProxyEnabled { get; set; }
    public string ProxyServer { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string ServerSpeedLabel => ExtractServerSpeedLabel(ServerName);

    public string PickerName =>
        string.Equals(Name, ManageAccountsOptionName, StringComparison.OrdinalIgnoreCase)
            ? "Manage accounts..."
            : $"{(string.IsNullOrWhiteSpace(Username) ? Name : Username)} | {ServerSpeedLabel}";

    private static string ExtractServerSpeedLabel(string? serverName)
    {
        var value = serverName?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return "-";
        }

        var tokenMatch = Regex.Match(value, @"\b[A-Za-z]{1,6}-\d+[xX]\b");
        if (tokenMatch.Success)
        {
            return tokenMatch.Value;
        }

        var speedMatch = Regex.Match(value, @"(\d+)\s*[xX]");
        if (speedMatch.Success)
        {
            return $"{speedMatch.Groups[1].Value}x";
        }

        return value;
    }
}
