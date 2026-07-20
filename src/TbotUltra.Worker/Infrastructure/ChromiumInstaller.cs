using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TbotUltra.Worker.Infrastructure;

/// <summary>Progress of a bundled-Chromium download, for a status label and a progress bar.</summary>
public readonly record struct ChromiumInstallProgress(string Status, int? PercentComplete);

/// <summary>
/// Downloads the bundled Chromium build Playwright expects, by running the Playwright driver that ships
/// next to the app (<c>.playwright/node/win32_x64/node.exe</c> + <c>package/cli.js</c>).
///
/// The driver is used directly rather than the <c>playwright.ps1</c> wrapper because the release package
/// deliberately drops that script, so a wrapper-based install works in development and fails for users.
/// Going through the driver keeps development and release on one code path, and avoids depending on
/// PowerShell and its execution policy.
/// </summary>
public static class ChromiumInstaller
{
    // "|■■■     |  10% of 183.6 MiB" — the driver's download progress line.
    private static readonly Regex ProgressLinePattern = new(
        @"(?<percent>\d{1,3})%\s+of\s+(?<size>[\d.,]+\s*\w+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// True when the shipped driver needed for an install is present. False means the installation is
    /// incomplete and the app cannot repair itself — the user has to re-download the release.
    /// </summary>
    public static bool DriverAvailable()
    {
        return File.Exists(ResolveNodePath()) && File.Exists(ResolveCliPath());
    }

    /// <summary>
    /// Runs the download. Throws <see cref="InvalidOperationException"/> with a message meant to be shown
    /// to the user; callers should surface it as-is rather than adding developer detail.
    /// </summary>
    public static async Task InstallChromiumAsync(
        string projectRoot,
        Action<string>? log = null,
        IProgress<ChromiumInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var nodePath = ResolveNodePath();
        var cliPath = ResolveCliPath();
        if (!File.Exists(nodePath) || !File.Exists(cliPath))
        {
            log?.Invoke($"[browser] Playwright driver missing (node='{nodePath}', cli='{cliPath}').");
            throw new InvalidOperationException(
                "The browser installer that ships with the app is missing. Please download the latest release again.");
        }

        var browsersPath = Path.Combine(projectRoot, "ms-playwright");
        Directory.CreateDirectory(browsersPath);
        log?.Invoke($"[browser] installing Chromium into '{browsersPath}' via the bundled Playwright driver.");
        progress?.Report(new ChromiumInstallProgress("Preparing download...", null));

        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            WorkingDirectory = projectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(cliPath);
        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add("chromium");
        // "install chromium" also pulls chromium_headless_shell (~270 MB), a browser build this app can
        // never use: every session launches with Headless=false. --no-shell skips that download.
        startInfo.ArgumentList.Add("--no-shell");
        startInfo.Environment["PLAYWRIGHT_BROWSERS_PATH"] = browsersPath;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var failureLines = new List<string>();

        // Read incrementally instead of ReadToEnd: the progress bar is only useful while the download runs.
        process.OutputDataReceived += (_, e) => HandleDriverLine(e.Data, log, progress, failureLines);
        process.ErrorDataReceived += (_, e) => HandleDriverLine(e.Data, log, progress, failureLines);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            log?.Invoke($"[browser] could not start the Playwright driver: {ex.Message}");
            throw new InvalidOperationException(
                "Could not start the browser installer. Please try again, or download the latest release.", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The download can take minutes, so a cancelled install must not leave the driver running and
            // writing into ms-playwright behind the user's back.
            TryKill(process, log);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var detail = failureLines.Count > 0 ? failureLines[^1] : $"exit code {process.ExitCode}";
            log?.Invoke($"[browser] Chromium install failed: {detail}");
            throw new InvalidOperationException(
                "The browser download did not finish. Check your internet connection and try again.");
        }

        log?.Invoke("[browser] Chromium install complete.");
        progress?.Report(new ChromiumInstallProgress("Download complete.", 100));
    }

    private static void HandleDriverLine(
        string? line,
        Action<string>? log,
        IProgress<ChromiumInstallProgress>? progress,
        List<string> failureLines)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var trimmed = line.Trim();
        if (TryParseProgressLine(trimmed, out var downloadProgress))
        {
            // Progress bars arrive many times a second; report them but keep them out of the log.
            progress?.Report(downloadProgress);
            return;
        }

        failureLines.Add(trimmed);
        log?.Invoke($"[browser] {trimmed}");

        if (trimmed.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new ChromiumInstallProgress("Starting download...", 0));
        }
    }

    /// <summary>
    /// Recognizes the driver's download progress line ("|■■■   |  10% of 183.6 MiB") and turns it into a
    /// status for the progress bar. Non-progress lines are left for the log.
    /// </summary>
    internal static bool TryParseProgressLine(string line, out ChromiumInstallProgress progress)
    {
        progress = default;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = ProgressLinePattern.Match(line);
        if (!match.Success)
        {
            return false;
        }

        var percent = int.Parse(match.Groups["percent"].Value, CultureInfo.InvariantCulture);
        if (percent > 100)
        {
            return false;
        }

        progress = new ChromiumInstallProgress(
            $"Downloading browser... {percent}% of {match.Groups["size"].Value}", percent);
        return true;
    }

    private static void TryKill(Process process, Action<string>? log)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                log?.Invoke("[browser] Chromium install cancelled; installer stopped.");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[browser] could not stop the cancelled installer: {ex.Message}");
        }
    }

    private static string ResolveNodePath()
        => Path.Combine(AppContext.BaseDirectory, ".playwright", "node", "win32_x64", "node.exe");

    private static string ResolveCliPath()
        => Path.Combine(AppContext.BaseDirectory, ".playwright", "package", "cli.js");
}
