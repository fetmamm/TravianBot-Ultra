namespace TbotUltra.Worker.Services;

public static class TaskCatalog
{
    public static IReadOnlyList<string> AllowedTaskNames => TbotUltra.Core.Tasks.TaskCatalog.AllowedTaskNames;

    public static bool IsAllowed(string taskName)
    {
        return TbotUltra.Core.Tasks.TaskCatalog.IsAllowed(taskName);
    }
}
