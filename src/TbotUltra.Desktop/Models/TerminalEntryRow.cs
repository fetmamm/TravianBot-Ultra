using TbotUltra.Desktop.Services.Logging;

namespace TbotUltra.Desktop.Models;

public sealed class TerminalEntryRow
{
    public string Text { get; init; } = string.Empty;
    public LogCategory Category { get; init; } = LogCategory.Other;

    /// <summary>True for high-volume noise lines that are hidden in "Clean" mode.</summary>
    public bool IsVerbose { get; init; }
}
