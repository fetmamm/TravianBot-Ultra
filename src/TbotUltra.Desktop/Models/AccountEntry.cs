using System;

namespace TbotUltra.Desktop.Models;

public sealed class AccountEntry
{
    private const string ManageAccountsOptionName = "__manage_accounts__";

    public string Name { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsAnalyzed { get; set; }
    public string AnalysisStatus => IsAnalyzed ? "Yes" : "No";

    public string PickerName =>
        string.Equals(Name, ManageAccountsOptionName, StringComparison.OrdinalIgnoreCase)
            ? "Manage accounts..."
            : $"{(string.IsNullOrWhiteSpace(Username) ? Name : Username)} (Analysis: {AnalysisStatus})";
}
