namespace TbotUltra.Worker;

public static class ProjectRootLocator
{
    public static string FindProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var configPath = Path.Combine(current.FullName, "config", "bot.json");
            if (File.Exists(configPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate project root (missing config/bot.json).");
    }
}
