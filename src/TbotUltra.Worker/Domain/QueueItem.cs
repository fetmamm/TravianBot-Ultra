namespace TbotUltra.Worker.Domain;

public sealed class QueueItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TaskName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public QueueGroup Group { get; set; } = QueueGroup.Construction;
    public Dictionary<string, string> Payload { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int Priority { get; set; }
    public QueueStatus Status { get; set; } = QueueStatus.Pending;
    public int Retries { get; set; }
    public int MaxRetries { get; set; } = 3;
    public bool IsRuntimeOnly { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

}
