using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop;

// Startup background work. This used to also run a Chromium "warmup" launch, which was removed: it started
// the bundled Chromium while the session actually runs the user's system Chrome (see
// ResolveInstalledChromeChannel), so it warmed a binary the bot never uses and only cost a spurious browser
// process at every start.
public partial class MainWindow
{
    private void StartBackgroundWarmups()
    {
        _backgroundTasks.Run(
            RunBackgroundWarmupsAsync,
            ex => AppendLog($"Background startup task failed: {ex.Message}"));
    }

    private Task RunBackgroundWarmupsAsync(CancellationToken cancellationToken)
    {
        CleanUpOrphanedBrowsers();
        return Task.CompletedTask;
    }

    // Closes browser windows left behind by a previous run that crashed or was force-stopped, so they don't
    // linger on screen and look like the current session flickering. Only touches processes this app
    // recorded when it launched them — never the user's own Chrome windows.
    private void CleanUpOrphanedBrowsers()
    {
        var orphans = LaunchedBrowserRegistry.KillOrphanedBrowsers(_projectRoot, AppendLog);
        if (orphans > 0)
        {
            AppendLog($"Cleaned up {orphans} leftover browser process(es) from a previous run.");
        }
    }
}
