using System.Diagnostics;
using System.Text.Json;
using TbotUltra.Worker.Infrastructure;
using Xunit;

namespace TbotUltra.Worker.Tests;

/// <summary>
/// This registry decides which processes get killed at startup. The session runs the user's own Google
/// Chrome, so a mistake here closes the user's browsing — the rules that stop a wrong kill matter far more
/// than the killing itself.
/// </summary>
public sealed class LaunchedBrowserRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"tbot-launched-browsers-{Guid.NewGuid():N}");

    private string RegistryPath => Path.Combine(_root, "config", "cache", "launched-browsers.json");

    [Fact]
    public void DoesNotKillAProcessWhoseStartTimeDoesNotMatch()
    {
        // The PID-reuse case: something else now owns a PID a previous run recorded. Recording this test
        // process with a wrong start time must leave it running — if the guard fails, the run dies here.
        var self = Process.GetCurrentProcess();
        WriteRegistry(self.Id, self.StartTime.ToUniversalTime().Ticks - 1, self.MainModule!.FileName!);

        var killed = LaunchedBrowserRegistry.KillOrphanedBrowsers(_root);

        Assert.Equal(0, killed);
        Assert.False(Process.GetCurrentProcess().HasExited);
    }

    [Fact]
    public void DoesNotKillAProcessWhoseExecutablePathDoesNotMatch()
    {
        // Same PID and start time, different executable: still not the process that was recorded.
        var self = Process.GetCurrentProcess();
        WriteRegistry(self.Id, self.StartTime.ToUniversalTime().Ticks, @"C:\Program Files\Somewhere\other.exe");

        var killed = LaunchedBrowserRegistry.KillOrphanedBrowsers(_root);

        Assert.Equal(0, killed);
        Assert.False(Process.GetCurrentProcess().HasExited);
    }

    [Fact]
    public void IgnoresProcessesThatAlreadyExited()
    {
        // The normal case after a clean shutdown: recorded PIDs are simply gone.
        WriteRegistry(pid: 999999, startedAtUtcTicks: DateTime.UtcNow.Ticks, executablePath: @"C:\gone\chrome.exe");

        Assert.Equal(0, LaunchedBrowserRegistry.KillOrphanedBrowsers(_root));
    }

    [Fact]
    public void ClearsTheRegistryAfterCleanup()
    {
        WriteRegistry(pid: 999999, startedAtUtcTicks: DateTime.UtcNow.Ticks, executablePath: @"C:\gone\chrome.exe");

        LaunchedBrowserRegistry.KillOrphanedBrowsers(_root);

        Assert.False(File.Exists(RegistryPath));
    }

    [Fact]
    public void CleanupConfirmsThatAnExitedRecordedBrowserNoLongerRemains()
    {
        WriteRegistry(pid: 999999, startedAtUtcTicks: DateTime.UtcNow.Ticks, executablePath: @"C:\gone\chrome.exe");

        var result = LaunchedBrowserRegistry.CleanupTrackedBrowsers(_root);

        Assert.Equal(1, result.RecordedCount);
        Assert.Equal(0, result.RemainingCount);
        Assert.True(result.Completed);
        Assert.False(File.Exists(RegistryPath));
    }

    [Fact]
    public void ForgetRemovesTheRegistryAndToleratesAMissingFile()
    {
        WriteRegistry(pid: 999999, startedAtUtcTicks: DateTime.UtcNow.Ticks, executablePath: @"C:\gone\chrome.exe");

        LaunchedBrowserRegistry.Forget(_root);
        Assert.False(File.Exists(RegistryPath));

        LaunchedBrowserRegistry.Forget(_root);
    }

    [Fact]
    public void SurvivesACorruptRegistryFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);
        File.WriteAllText(RegistryPath, "{ not json");

        Assert.Equal(0, LaunchedBrowserRegistry.KillOrphanedBrowsers(_root));
    }

    [Fact]
    public void DoesNothingWithoutAProjectRoot()
    {
        Assert.Equal(0, LaunchedBrowserRegistry.KillOrphanedBrowsers(string.Empty));
    }

    [Fact]
    public async Task TrackReturnsTheLaunchResultAndRecordsNothingWhenNoProcessAppears()
    {
        // No browser starts during this "launch", so nothing may be recorded — the before/after difference
        // must never attribute an unrelated running process to us.
        var result = await LaunchedBrowserRegistry.TrackAsync(_root, "chrome", () => Task.FromResult("launched"));

        Assert.Equal("launched", result);
        Assert.False(File.Exists(RegistryPath));
    }

    [Theory]
    [InlineData("Tbot", "driver-node", true)]
    [InlineData("Tbot", "system-chrome", false)]
    [InlineData("Other", "driver-node", false)]
    [InlineData("Tbot", "empty", false)]
    public void OwnershipRequiresTheExactConfiguredPlaywrightDriver(
        string executableRootName,
        string executableKind,
        bool expected)
    {
        var baseRoot = Path.Combine(Path.GetTempPath(), "tbot-ownership");
        var driverRoot = Path.Combine(baseRoot, "Tbot", ".playwright");
        var executablePath = executableKind switch
        {
            "driver-node" => Path.Combine(
                baseRoot, executableRootName, ".playwright", PlaywrightPlatform.DriverNodeRelativePath),
            "system-chrome" => Path.Combine(baseRoot, "Google", "Chrome", "Application", "chrome"),
            _ => string.Empty,
        };

        Assert.Equal(
            expected,
            LaunchedBrowserRegistry.IsConfiguredPlaywrightDriverExecutable(executablePath, driverRoot));
    }

    private void WriteRegistry(int pid, long startedAtUtcTicks, string executablePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);
        var payload = JsonSerializer.Serialize(new[]
        {
            new { pid, startedAtUtcTicks, executablePath },
        });
        File.WriteAllText(RegistryPath, payload);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
