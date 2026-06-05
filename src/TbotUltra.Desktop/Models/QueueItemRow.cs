using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Models;

public sealed class QueueItemRow
{
    public Guid Id { get; init; }
    public QueueGroup Group { get; init; }
    public string GroupName { get; init; } = string.Empty;
    public string VillageName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string TaskName { get; init; } = string.Empty;
    public QueueStatus Status { get; init; }
    public int Retries { get; init; }
    public int MaxRetries { get; init; }
    public string RetriesText => $"{Retries}/{MaxRetries}";
    public bool IsRuntimeOnly { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string CreatedAtServer { get; init; } = string.Empty;
    public string NextAttemptAtServer { get; init; } = string.Empty;
}
