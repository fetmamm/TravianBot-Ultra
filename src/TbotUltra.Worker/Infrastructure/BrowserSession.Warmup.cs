using System.Text.Json.Nodes;
using Microsoft.Playwright;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Configuration;

namespace TbotUltra.Worker.Infrastructure;

public sealed partial class BrowserSession
{
    public static async Task<bool> WarmupAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        if (_warmupCompleted || !ChromiumAlreadyInstalled(projectRoot))
        {
            return false;
        }

        await WarmupGate.WaitAsync(cancellationToken);
        try
        {
            if (_warmupCompleted)
            {
                return false;
            }

            ConfigureLocalPlaywrightEnvironment(projectRoot);

            using var playwright = await Playwright.CreateAsync();
            IBrowser? browser = null;
            try
            {
                browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                });

                await browser.CloseAsync();
                browser = null;
                _warmupCompleted = true;
                return true;
            }
            finally
            {
                if (browser is not null)
                {
                    try
                    {
                        await browser.CloseAsync();
                    }
                    catch
                    {
                        // Best-effort cleanup if warmup was cancelled or launch partially failed.
                    }
                }
            }
        }
        finally
        {
            WarmupGate.Release();
        }
    }

    // Kills Chromium processes left over from a previous run that crashed or was force-stopped
    // (so the MainWindow_Closing cleanup never ran). Those orphaned browser windows linger on screen
    // — including stale cross-promo tabs — and look like the current session "flickering". Only runs
    // when this is the single app instance, so it never kills a concurrently-running instance's live
    // browser. Returns the number of processes terminated.
    public static int KillOrphanedChromium(string projectRoot)
    {
        var killed = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(projectRoot)
                || System.Diagnostics.Process.GetProcessesByName("TbotUltra.Desktop").Length > 1)
            {
                return 0;
            }

            var playwrightPath = Path.Combine(projectRoot, LocalPlaywrightBrowsersDirectoryName);
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("chrome"))
            {
                try
                {
                    var exePath = proc.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath)
                        && exePath.StartsWith(playwrightPath, StringComparison.OrdinalIgnoreCase))
                    {
                        proc.Kill(entireProcessTree: true);
                        killed++;
                    }
                }
                catch
                {
                    // Access denied / already exited / different bitness — skip.
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
            // Best-effort cleanup; never block startup.
        }

        return killed;
    }

    public static bool ChromiumAlreadyInstalled(string projectRoot, Action<string>? log = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return false;
            }

            var playwrightRoot = Path.Combine(projectRoot, LocalPlaywrightBrowsersDirectoryName);
            if (!Directory.Exists(playwrightRoot))
            {
                return false;
            }

            // Playwright looks for one exact build revision, so an older chromium-* folder left over
            // from a previous package version does not count as installed: the launch would fail on a
            // missing executable instead. Ask the shipped driver which revision it needs.
            var expectedRevision = ResolveExpectedChromiumRevision();
            if (expectedRevision is not null)
            {
                var expectedExecutable = Path.Combine(
                    playwrightRoot, $"chromium-{expectedRevision}", "chrome-win", "chrome.exe");
                var installed = File.Exists(expectedExecutable);
                log?.Invoke(installed
                    ? $"[browser] chromium-{expectedRevision} is installed."
                    : $"[browser] chromium-{expectedRevision} is missing (Playwright upgrade?); install required.");
                return installed;
            }

            // Driver metadata unreadable: keep the previous any-revision behavior rather than forcing
            // an unnecessary download. A stale revision then still fails at launch, as it did before.
            log?.Invoke("[browser] could not read the expected Chromium revision; falling back to any installed revision.");
            var executables = Directory.GetFiles(playwrightRoot, "chrome.exe", SearchOption.AllDirectories);
            return executables.Any(path =>
                path.Contains("chromium-", StringComparison.OrdinalIgnoreCase) &&
                path.Contains("chrome-win", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the Chromium build revision the referenced Microsoft.Playwright package expects from the
    /// driver metadata shipped next to the app, so the check follows package upgrades on its own.
    /// Returns null when the metadata is missing or unreadable.
    /// </summary>
    private static string? ResolveExpectedChromiumRevision()
    {
        try
        {
            var metadataPath = Path.Combine(
                AppContext.BaseDirectory, ".playwright", "package", "browsers.json");
            if (!File.Exists(metadataPath))
            {
                return null;
            }

            var browsers = JsonNode.Parse(File.ReadAllText(metadataPath))?["browsers"]?.AsArray();
            if (browsers is null)
            {
                return null;
            }

            foreach (var browser in browsers)
            {
                if (string.Equals(browser?["name"]?.GetValue<string>(), "chromium", StringComparison.OrdinalIgnoreCase))
                {
                    var revision = browser?["revision"]?.GetValue<string>();
                    return string.IsNullOrWhiteSpace(revision) ? null : revision;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

}