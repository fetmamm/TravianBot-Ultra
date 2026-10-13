using System.Diagnostics;
using Microsoft.VisualBasic.FileIO;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class MissingConfigStartupTests
{
    [Fact]
    public void NonInteractiveDevLaunch_Exits_WhenConfigDirectoryIsMissing()
    {
        var sourceDirectory = AppContext.BaseDirectory;
        var executableName = "TbotUltra.Desktop.exe";
        Assert.True(File.Exists(Path.Combine(sourceDirectory, executableName)));

        var isolatedDirectory = Path.Combine(
            Path.GetTempPath(),
            $"TbotUltraMissingConfigTest_{Guid.NewGuid():N}");

        try
        {
            FileSystem.CopyDirectory(sourceDirectory, isolatedDirectory);
            var executablePath = Path.Combine(isolatedDirectory, executableName);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                // The public EXE keeps its startup popup open on failure so a user can inspect it.
                // This process-level failure check must use the non-interactive VBS launch path.
                Arguments = "--dev-launcher",
                WorkingDirectory = isolatedDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            Assert.NotNull(process);
            // A fresh Windows runner can spend several seconds scanning the copied executable and
            // framework files before WPF reaches startup. Keep the assertion bounded, but allow the
            // cold process enough time to exercise the missing-config shutdown path.
            var exited = process.WaitForExit(milliseconds: 30_000);
            var unhandledLogPath = Path.Combine(isolatedDirectory, "logs", "desktop-unhandled.log");
            var unhandledLog = File.Exists(unhandledLogPath)
                ? File.ReadAllText(unhandledLogPath)
                : "No desktop-unhandled.log was written.";

            if (!exited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            Assert.True(exited, $"Desktop process remained alive after startup failed. {unhandledLog}");
            Assert.Contains("Could not locate project root", unhandledLog, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(isolatedDirectory))
            {
                Directory.Delete(isolatedDirectory, recursive: true);
            }
        }
    }
}
