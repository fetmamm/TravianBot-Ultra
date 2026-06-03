using System.Diagnostics;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop;

// Background warmups kicked off at startup / lazily at login: Chromium install
// warmup (and orphan cleanup) and the SS-Travi captcha-solver warmup. Extracted
// verbatim from MainWindow.xaml.cs to keep that file focused; same class, so this
// is a pure relocation with no behavior change.
public partial class MainWindow
{
    private void StartBackgroundWarmups()
    {
        _ = Task.Run(RunBackgroundWarmupsAsync);
    }

    private async Task RunBackgroundWarmupsAsync()
    {
        // Captcha warmup is intentionally NOT done here — that captcha only exists on SS-Travi,
        // so warming it at startup just slows the program for official servers. It is triggered
        // lazily at login instead (see ExecuteLoginFlowAsync), where RunCaptchaWarmupAsync gates
        // on IsPrivateServer + CaptchaAutoSolveEnabled.
        await RunChromiumWarmupAsync();
    }

    private async Task RunChromiumWarmupAsync()
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
            var warmed = await BrowserSession.WarmupAsync(_projectRoot);
            sw.Stop();
            if (!warmed)
            {
                AppendLog("Chromium warmup skipped: already completed.");
                return;
            }

            AppendLog($"Chromium warmup completed in {sw.Elapsed.TotalSeconds:F1}s.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppendLog($"Chromium warmup skipped: {ex.Message}");
        }
    }

    private async Task RunCaptchaWarmupAsync()
    {
        BotOptions options;
        try
        {
            options = LoadBotOptions();
        }
        catch (Exception ex)
        {
            AppendLog($"Captcha warmup skipped: could not load config ({ex.Message}).");
            return;
        }

        if (!options.IsPrivateServer)
        {
            AppendLog("Captcha warmup skipped: not an SS-Travi server.");
            return;
        }

        if (!options.CaptchaAutoSolveEnabled)
        {
            AppendLog("Captcha warmup skipped: captcha auto-solve is disabled.");
            return;
        }

        var sw = Stopwatch.StartNew();
        AppendLog("Captcha warmup started.");
        try
        {
            var warmed = await _captchaAutoSolver.WarmupAsync(CancellationToken.None);
            sw.Stop();
            if (!warmed)
            {
                AppendLog("Captcha warmup skipped: dependencies missing or warmup already completed.");
                return;
            }

            AppendLog($"Captcha warmup completed in {sw.Elapsed.TotalSeconds:F1}s.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppendLog($"Captcha warmup skipped: {ex.Message}");
        }
    }
}
