using TbotUltra.Core.Accounts;

namespace TbotUltra.Worker.Infrastructure;

public static class BrowserStatePersistence
{
    public static int ClearAllSavedStates(string projectRoot, Action<string>? log = null)
    {
        var deleted = 0;
        var accountsRoot = Path.GetFullPath(Path.Combine(projectRoot, AccountStoragePaths.AccountsRelativeDirectory));
        if (Directory.Exists(accountsRoot))
        {
            try
            {
                foreach (var sessionDirectory in Directory.EnumerateDirectories(accountsRoot, "session", SearchOption.AllDirectories))
                {
                    deleted += DeleteMatchingFiles(sessionDirectory, "playwright-state.json", log);
                    deleted += DeleteMatchingFiles(sessionDirectory, "playwright-state.json.*.tmp", log);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"[browser-session] could not enumerate saved account auth state: {ex.Message}");
            }
        }

        var legacyAuthRoot = Path.GetFullPath(Path.Combine(projectRoot, "playwright", ".auth"));
        if (Directory.Exists(legacyAuthRoot))
        {
            deleted += DeleteMatchingFiles(legacyAuthRoot, "*.json", log);
        }

        if (deleted > 0)
        {
            log?.Invoke($"[browser-session] cleared {deleted} saved browser auth state file(s).");
        }

        return deleted;
    }

    private static int DeleteMatchingFiles(string directory, string searchPattern, Action<string>? log)
    {
        var deleted = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.Delete(path);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke($"[browser-session] could not clear saved auth state '{path}': {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"[browser-session] could not enumerate saved auth state in '{directory}': {ex.Message}");
        }

        return deleted;
    }
}
