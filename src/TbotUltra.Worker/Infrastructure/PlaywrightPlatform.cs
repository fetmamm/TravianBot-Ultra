using System.Runtime.InteropServices;

namespace TbotUltra.Worker.Infrastructure;

/// <summary>
/// Platform-specific file layout of the Playwright driver and browsers. The desktop release only ships
/// for Windows, but the headless host runs on Linux (Docker), where the driver's node binary, the
/// Chromium archive folder and the system Chrome locations all differ.
/// </summary>
public static class PlaywrightPlatform
{
    /// <summary>
    /// Points the bot at a browser folder outside the project root, e.g. one baked into a Docker image so
    /// the data volume does not have to carry a few hundred MB of Chromium.
    /// </summary>
    public const string BrowsersPathOverrideVariable = "TBOT_PLAYWRIGHT_BROWSERS_PATH";

    private const string LocalBrowsersDirectoryName = "ms-playwright";

    /// <summary>The driver's node executable, relative to the <c>.playwright</c> driver folder.</summary>
    public static string DriverNodeRelativePath { get; } = ResolveDriverNodeRelativePath();

    public static string ResolveBrowsersPath(string projectRoot)
    {
        var overridePath = Environment.GetEnvironmentVariable(BrowsersPathOverrideVariable);
        return string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(projectRoot, LocalBrowsersDirectoryName)
            : overridePath;
    }

    /// <summary>
    /// True when <paramref name="browserRoot"/> (a <c>chromium-NNNN</c> folder) holds the Chromium build for
    /// this platform. Matches the archive folder by prefix so Playwright renames (chrome-win → chrome-win64,
    /// chrome-linux → chrome-linux64) keep working.
    /// </summary>
    public static bool ContainsChromiumExecutable(string browserRoot)
    {
        if (!Directory.Exists(browserRoot))
        {
            return false;
        }

        var (executableName, archivePrefix) = ChromiumExecutableLayout();
        return Directory.GetFiles(browserRoot, executableName, SearchOption.AllDirectories)
            .Any(path => Path.GetFileName(Path.GetDirectoryName(path))?
                .StartsWith(archivePrefix, StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// Playwright channel of an installed Google Chrome or Microsoft Edge (which ship the proprietary
    /// codecs bonus videos need), or null when neither is installed at a standard location.
    /// </summary>
    public static string? ResolveInstalledChromeChannel()
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return FirstChannel(
                ("chrome", new[]
                {
                    Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
                }),
                ("msedge", new[]
                {
                    Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                    Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                }));
        }

        if (OperatingSystem.IsLinux())
        {
            return FirstChannel(
                ("chrome", new[] { "/opt/google/chrome/chrome" }),
                ("msedge", new[] { "/opt/microsoft/msedge/msedge" }));
        }

        return null;
    }

    private static string? FirstChannel(params (string Channel, string[] Paths)[] candidates)
        => candidates.FirstOrDefault(candidate => candidate.Paths.Any(File.Exists)).Channel;

    internal static (string ExecutableName, string ArchivePrefix) ChromiumExecutableLayout()
    {
        if (OperatingSystem.IsWindows())
        {
            return ("chrome.exe", "chrome-win");
        }

        return OperatingSystem.IsMacOS()
            ? ("Chromium", "chrome-mac")
            : ("chrome", "chrome-linux");
    }

    private static string ResolveDriverNodeRelativePath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine("node", "win32_x64", "node.exe");
        }

        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var platform = OperatingSystem.IsMacOS() ? "mac" : "linux";
        return Path.Combine("node", $"{platform}-{architecture}", "node");
    }
}
