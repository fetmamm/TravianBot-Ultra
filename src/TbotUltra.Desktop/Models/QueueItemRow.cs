using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Models;

public sealed class QueueItemRow
{
    public Guid Id { get; init; }
    public string TaskName { get; init; } = string.Empty;
    public int Priority { get; init; }
    public QueueStatus Status { get; init; }
    public int Retries { get; init; }
    public int MaxRetries { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string CreatedAtServer { get; init; } = string.Empty;
    public string NextAttemptAtServer { get; init; } = string.Empty;
}
