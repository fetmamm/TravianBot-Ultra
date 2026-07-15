namespace TbotUltra.Desktop.Models;

public sealed class AlarmEntryRow
{
    public string Text { get; set; } = string.Empty;
    public string Signature { get; init; } = string.Empty;
    public DateTimeOffset FirstSeenUtc { get; init; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public int OccurrenceCount { get; set; } = 1;
    public bool IsAcknowledged { get; set; }
}
