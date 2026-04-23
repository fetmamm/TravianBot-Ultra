namespace TbotUltra.Worker.Domain;

public enum QueueStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Paused = 4,
}
