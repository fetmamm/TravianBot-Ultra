namespace TbotUltra.Worker.Domain;

/// <summary>
/// One explicitly confirmed task execution or state-changing task step. It is emitted only after
/// the worker has a typed success signal; scheduling attempts and ordinary waits are excluded.
/// </summary>
public sealed record BotTaskActivity(
    string AccountName,
    string TaskName,
    DateTimeOffset OccurredAtUtc);
