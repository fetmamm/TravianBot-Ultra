using System.Diagnostics;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop;

// Background warmups kicked off at startup: Chromium install warmup and orphan cleanup. Extracted
// verbatim from MainWindow.xaml.cs to keep that file focused; same class, so this
// is a pure relocation with no behavior change.
public partial class MainWindow
{
    private void StartBackgroundWarmups()
    {
        _backgroundTasks.Run(
            RunBackgroundWarmupsAsync,
            ex => AppendLog($"Background warmup failed: {ex.Message}"));
    }

    private async Task RunBackgroundWarmupsAsync(CancellationToken cancellationToken)
    {
        await RunChromiumWarmupAsync(cancellationToken);
    }

    private async Task RunChromiumWarmupAsync(CancellationToken cancellationToken)
    {
        if (!BrowserSession.ChromiumAlreadyInstalled(_projectRoot))
        {
            AppendLog("Chromium warmup skipped: Chromium is not installed locally.");
            return;
        }

        // Clean up browser windows orphaned by a previous crashed/force-stopped run so they don't
        // linger on screen and look like the current session flickering.
        var orphans = BrowserSession.KillOrphanedChromium(_projectRoot);
        if (orphans > 0)
        {
            AppendLog($"Cleaned up {orphans} leftover browser process(es) from a previous run.");
        }

        var sw = Stopwatch.StartNew();
        AppendLog("Chromium warmup started.");
        try
        {
            var warmed = await BrowserSession.WarmupAsync(_projectRoot, cancellationToken);
            sw.Stop();
            if (!warmed)
            {
                AppendLog("Chromium warmup skipped: already completed.");
                return;
            }

            AppendLog($"Chromium warmup completed in {sw.Elapsed.TotalSeconds:F1}s.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppendLog($"Chromium warmup skipped: {ex.Message}");
        }
    }

}
