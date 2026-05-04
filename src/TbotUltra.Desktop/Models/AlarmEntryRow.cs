namespace TbotUltra.Desktop.Models;

public sealed class AlarmEntryRow
{
    public string Text { get; init; } = string.Empty;
    public bool IsAcknowledged { get; set; }
}
