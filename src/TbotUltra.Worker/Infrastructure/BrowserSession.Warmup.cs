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
                    // Channel pins this to the regular Chromium build. Without it Playwright resolves a
                    // plain Headless=true to chromium_headless_shell, a second ~270 MB download the app
                    // otherwise never needs and no longer installs.
                    Channel = "chromium",
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
                var expectedBrowserRoot = Path.Combine(playwrightRoot, $"chromium-{expectedRevision}");
                var installed = ContainsWindowsChromiumExecutable(expectedBrowserRoot);
                log?.Invoke(installed
                    ? $"[browser] chromium-{expectedRevision} is installed."
                    : $"[browser] chromium-{expectedRevision} is missing (Playwright upgrade?); install required.");
                return installed;
            }

            // Driver metadata unreadable: keep the previous any-revision behavior rather than forcing
            // an unnecessary download. A stale revision then still fails at launch, as it did before.
            log?.Invoke("[browser] could not read the expected Chromium revision; falling back to any installed revision.");
            return Directory.GetDirectories(playwrightRoot, "chromium-*")
                .Any(ContainsWindowsChromiumExecutable);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes browser folders this app cannot use and returns how many were removed: revisions left by an
    /// earlier Playwright version, plus the headless shell at any revision (the bot always launches with
    /// Headless=false). Each folder is a few hundred MB, and nothing else clears them: the app updater
    /// overlays files without mirroring, and Playwright's own cleanup only runs during an install, which
    /// the update path skips because the browsers arrive with the package.
    ///
    /// Deliberately a no-op unless the expected revision is confirmed present, so a partial or unreadable
    /// installation can never be made worse by deleting the only browser the user has.
    /// </summary>
    public static int RemoveOutdatedChromiumRevisions(string projectRoot, Action<string>? log = null)
    {
        var removed = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return 0;
            }

            var playwrightRoot = Path.Combine(projectRoot, LocalPlaywrightBrowsersDirectoryName);
            var expectedRevision = ResolveExpectedChromiumRevision();
            if (expectedRevision is null || !Directory.Exists(playwrightRoot))
            {
                return 0;
            }

            var expectedBrowserRoot = Path.Combine(playwrightRoot, $"chromium-{expectedRevision}");
            if (!ContainsWindowsChromiumExecutable(expectedBrowserRoot))
            {
                log?.Invoke("[browser] skipping cleanup of old browser revisions: the expected one is not installed.");
                return 0;
            }

            foreach (var directory in Directory.GetDirectories(playwrightRoot))
            {
                var name = Path.GetFileName(directory);
                if (!IsRemovableChromiumFolder(name, expectedRevision))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(directory, recursive: true);
                    removed++;
                    log?.Invoke($"[browser] removed unused browser '{name}'.");
                }
                catch (Exception ex)
                {
                    // A locked file only means the space is reclaimed on a later run; never fail startup.
                    log?.Invoke($"[browser] could not remove outdated browser '{name}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[browser] browser cleanup failed: {ex.Message}");
        }

        return removed;
    }

    /// <summary>
    /// Finds Chromium in the exact revision folder without pinning Playwright's Windows archive name.
    /// Recent packages use chrome-win64 while older packages used chrome-win.
    /// </summary>
    private static bool ContainsWindowsChromiumExecutable(string browserRoot)
    {
        if (!Directory.Exists(browserRoot))
        {
            return false;
        }

        return Directory.GetFiles(browserRoot, "chrome.exe", SearchOption.AllDirectories)
            .Any(path => Path.GetFileName(Path.GetDirectoryName(path))?
                .StartsWith("chrome-win", StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// True for a Chromium browser folder this app has no use for: any "chromium-1161" whose revision
    /// differs from the expected one, and EVERY "chromium_headless_shell-1161" regardless of revision —
    /// the bot always launches with Headless=false, so the shell (~270 MB) is dead weight even at the
    /// current revision. Other Playwright content (ffmpeg, winldd, .links) is never matched.
    /// </summary>
    private static bool IsRemovableChromiumFolder(string folderName, string expectedRevision)
    {
        const string headlessShellPrefix = "chromium_headless_shell-";
        var isHeadlessShell = folderName.StartsWith(headlessShellPrefix, StringComparison.OrdinalIgnoreCase);
        var prefix = isHeadlessShell ? headlessShellPrefix : "chromium-";
        if (!folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var revision = folderName[prefix.Length..];
        // Digits only: never delete a folder whose suffix is not a plain revision, such as a
        // "chromium-tip-of-tree" build a developer installed on purpose.
        if (revision.Length == 0 || !revision.All(char.IsAsciiDigit))
        {
            return false;
        }

        return isHeadlessShell || !string.Equals(revision, expectedRevision, StringComparison.Ordinal);
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
