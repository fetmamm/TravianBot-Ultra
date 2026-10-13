using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class DevLauncherPreflightTests
{
    [Fact]
    public void VbsLauncher_GatesAppStartOnCiEquivalentPreflight()
    {
        var root = FindRepositoryRoot();
        var launcher = File.ReadAllText(Path.Combine(root, "Start_Tbot_UI.vbs"));
        var preflight = File.ReadAllText(Path.Combine(root, "scripts", "Verify-DevLaunch.ps1"));

        Assert.Contains("UpdateLatestModified srcDesktopTests, latestCheckSource", launcher);
        Assert.Contains("UpdateLatestModified srcWorkerTests, latestCheckSource", launcher);
        Assert.Contains("For Each sourceFile In folder.Files", launcher);
        Assert.Contains("-WindowStyle Hidden -File", launcher);
        Assert.Contains("ReadPreflightFailure(fso, checkLogPath)", launcher);
        Assert.True(launcher.IndexOf("If checkExitCode <> 0 Then", StringComparison.Ordinal)
            < launcher.IndexOf("LaunchAndActivate shell, exePath", StringComparison.Ordinal));

        Assert.Contains("Invoke-PreflightStep \"Release build\"", preflight);
        Assert.Contains("Invoke-PreflightStep \"Worker tests\"", preflight);
        Assert.Contains("Invoke-PreflightStep \"Desktop tests\"", preflight);
        Assert.Contains("-WindowStyle Hidden -PassThru", preflight);
        Assert.Contains("$process.WaitForExit(200)", preflight);
        Assert.Contains("taskkill.exe", preflight);
        Assert.Contains("Exit-IfCancelled", preflight);
        Assert.Contains("-CancelPath \"\"\" & progressCancelPath", launcher);
        Assert.Contains("exit $process.ExitCode", preflight);
    }

    [Fact]
    public void VbsLauncher_ShowsProgressBeforeChecksAndKeepsBuildProcessesHidden()
    {
        var root = FindRepositoryRoot();
        var launcher = File.ReadAllText(Path.Combine(root, "Start_Tbot_UI.vbs"));
        var preflight = File.ReadAllText(Path.Combine(root, "scripts", "Verify-DevLaunch.ps1"));
        var progressWindow = File.ReadAllText(Path.Combine(root, "scripts", "Show-DevLaunchProgress.ps1"));

        Assert.True(launcher.IndexOf("shell.Run progressCommand, 0, False", StringComparison.Ordinal)
            < launcher.IndexOf("dotnetExe = ResolveDotnetExe", StringComparison.Ordinal));
        Assert.Contains("If Not fso.FileExists(progressReadyPath) Then", launcher);
        Assert.Contains("-StatusPath \"\"\" & progressStatusPath", launcher);
        Assert.Contains("WriteLaunchFailure progressStatusPath", launcher);
        Assert.DoesNotContain("MsgBox \"Local CI check failed", launcher, StringComparison.Ordinal);
        Assert.Contains("Write-LaunchStatus $Progress", preflight);
        Assert.Contains("-WindowStyle Hidden -PassThru", preflight);
        Assert.Contains("Invoke-PreflightStep \"Desktop debug build\"", preflight);
        Assert.Contains("ProgressBar x:Name=\"LaunchProgress\"", progressWindow);
        Assert.Contains("x:Name=\"StepText\" Grid.Column=\"1\" Text=\"STARTING\"", progressWindow);
        Assert.Contains("x:Name=\"ElapsedText\" Grid.Column=\"1\"", progressWindow);
        Assert.Contains("$elapsedText.Text = \"Elapsed:", progressWindow);
        Assert.Contains("Border x:Name=\"ErrorPanel\"", progressWindow);
        Assert.Contains("Button x:Name=\"OpenLogButton\"", progressWindow);
        Assert.Contains("Button x:Name=\"CloseButton\"", progressWindow);
        Assert.Contains("Button x:Name=\"CloseButton\" Grid.Column=\"3\"", progressWindow);
        Assert.Contains("Path Data=\"M 2,2 L 10,10 M 10,2 L 2,10\"", progressWindow);
        Assert.Contains("Button x:Name=\"MinimizeButton\"", progressWindow);
        Assert.Contains("<Rectangle Width=\"10\" Height=\"1\" Fill=\"#ADBAC7\" />", progressWindow);
        Assert.DoesNotContain("Content=\"—\"", progressWindow);
        Assert.Contains("Background=\"#2D1213\" Foreground=\"#FF7B72\"", progressWindow);
        Assert.Contains("BorderBrush=\"#B62324\"", progressWindow);
        Assert.Contains("ShowInTaskbar=\"True\"", progressWindow);
        Assert.Contains("$window.DragMove()", progressWindow);
        Assert.Contains("$window.Add_Closing", progressWindow);
        Assert.Contains("[System.IO.File]::WriteAllText($CancelPath, \"cancel\")", progressWindow);
        Assert.Contains("progressCancelPath", launcher);
        Assert.Contains("QuitIfLaunchCancelled", launcher);
        Assert.Contains("$window.Add_ContentRendered", progressWindow);
        Assert.Contains("$script:launchTimer = [System.Diagnostics.Stopwatch]::StartNew()", progressWindow);
        Assert.Contains("$script:launchTimer.Elapsed", progressWindow);
        Assert.DoesNotContain("stageStartedAt", progressWindow, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TbotUltra.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate TbotUltra.sln from the test output directory.");
    }
}
