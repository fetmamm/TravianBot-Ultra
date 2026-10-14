using TbotUltra.Worker.Infrastructure;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BrowserSessionDriverPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tbot-driver-path-{Guid.NewGuid():N}");

    [Fact]
    public void ResolvesBundledDriverFromAppDirectory_WhenProjectRootOnlyContainsRuntimeData()
    {
        var appDirectory = Path.Combine(_root, "app");
        var projectRoot = Path.Combine(_root, "runtime-data");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(appDirectory, ".playwright", PlaywrightPlatform.DriverNodeRelativePath))!);
        Directory.CreateDirectory(Path.Combine(appDirectory, ".playwright", "package"));
        File.WriteAllText(Path.Combine(appDirectory, ".playwright", PlaywrightPlatform.DriverNodeRelativePath), string.Empty);
        File.WriteAllText(Path.Combine(appDirectory, ".playwright", "package", "cli.js"), string.Empty);

        var path = BrowserSession.ResolvePlaywrightDriverPath(projectRoot, appDirectory);

        Assert.Equal(Path.Combine(appDirectory, ".playwright"), path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
