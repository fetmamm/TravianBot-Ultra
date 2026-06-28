using System;
using System.Diagnostics;
using System.IO;

namespace TbotUltra.Desktop.Services;

// Phase 2 self-update for the portable build. The running app cannot overwrite its own loaded files, so we
// hand off to an external PowerShell updater: it waits for this process to exit, overlays the new files onto
// the install folder while preserving user data (config/, .env, logs/, playwright/), then relaunches the app.
// A small visible window with a marquee progress bar shows the user what is happening.
public static class SelfUpdater
{
    // The published portable exe name (CI renames the desktop exe to this). Dev builds use a different name
    // and are not self-updatable.
    public const string PublishedExeName = "Tbot Ultra.exe";

    public static bool IsSupported(string currentVersion)
    {
        return !string.IsNullOrWhiteSpace(currentVersion)
            && !string.Equals(currentVersion, "dev", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Path.GetFileName(GetRunningExePath() ?? string.Empty),
                PublishedExeName,
                StringComparison.OrdinalIgnoreCase);
    }

    // Fresh per-update workspace under LocalAppData (download + extracted files + updater script live here).
    public static string CreateUpdateWorkspace()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TbotUltra", "update");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
        return root;
    }

    // Locates the app folder inside the extracted zip — the directory that contains the published exe.
    public static string? FindExtractedAppDir(string extractRoot)
    {
        if (File.Exists(Path.Combine(extractRoot, PublishedExeName)))
        {
            return extractRoot;
        }

        foreach (var dir in Directory.EnumerateDirectories(extractRoot, "*", SearchOption.AllDirectories))
        {
            if (File.Exists(Path.Combine(dir, PublishedExeName)))
            {
                return dir;
            }
        }

        return null;
    }

    // Writes and launches the detached updater, then it is the caller's job to shut the app down so the
    // updater can replace the (now unlocked) files.
    public static void LaunchUpdater(string stagingAppDir, string tempRoot)
    {
        var exePath = GetRunningExePath()
            ?? throw new InvalidOperationException("Could not resolve the running executable path.");
        var installDir = Path.GetDirectoryName(exePath)!;
        var exeName = Path.GetFileName(exePath);

        var scriptPath = Path.Combine(tempRoot, "update.ps1");
        File.WriteAllText(scriptPath, BuildUpdaterScript());

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = true, // detached: keeps running after this app exits
            WindowStyle = ProcessWindowStyle.Normal,
            ArgumentList =
            {
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-File", scriptPath,
                "-AppPid", Environment.ProcessId.ToString(),
                "-InstallDir", installDir,
                "-StagingDir", stagingAppDir,
                "-ExeName", exeName,
                "-TempRoot", tempRoot,
            },
        };
        Process.Start(psi);
    }

    private static string? GetRunningExePath()
    {
        try
        {
            return Process.GetCurrentProcess().MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    // PowerShell updater: shows a small window (status label + marquee progress bar), waits for the app to
    // close, overlays the new files while excluding user-data paths, then relaunches the app.
    private static string BuildUpdaterScript()
    {
        return """
            param(
              [Parameter(Mandatory=$true)][int]$AppPid,
              [Parameter(Mandatory=$true)][string]$InstallDir,
              [Parameter(Mandatory=$true)][string]$StagingDir,
              [Parameter(Mandatory=$true)][string]$ExeName,
              [Parameter(Mandatory=$true)][string]$TempRoot
            )

            $ErrorActionPreference = 'Continue'
            Add-Type -AssemblyName System.Windows.Forms
            Add-Type -AssemblyName System.Drawing

            $form = New-Object System.Windows.Forms.Form
            $form.Text = 'Updating Tbot Ultra'
            $form.Width = 440
            $form.Height = 160
            $form.StartPosition = 'CenterScreen'
            $form.FormBorderStyle = 'FixedDialog'
            $form.MaximizeBox = $false
            $form.MinimizeBox = $false
            $form.TopMost = $true

            $label = New-Object System.Windows.Forms.Label
            $label.Text = 'Preparing update...'
            $label.AutoSize = $false
            $label.Width = 400
            $label.Height = 40
            $label.Left = 15
            $label.Top = 20
            $form.Controls.Add($label)

            $bar = New-Object System.Windows.Forms.ProgressBar
            $bar.Style = 'Marquee'
            $bar.MarqueeAnimationSpeed = 30
            $bar.Width = 400
            $bar.Height = 24
            $bar.Left = 15
            $bar.Top = 70
            $form.Controls.Add($bar)

            $form.Show()
            [System.Windows.Forms.Application]::DoEvents()

            function Set-Status([string]$text) {
              $label.Text = $text
              [System.Windows.Forms.Application]::DoEvents()
            }

            # 1. Wait for the app to exit so its files unlock.
            Set-Status 'Waiting for Tbot Ultra to close...'
            $deadline = (Get-Date).AddSeconds(60)
            while ((Get-Date) -lt $deadline) {
              $proc = Get-Process -Id $AppPid -ErrorAction SilentlyContinue
              if (-not $proc) { break }
              Start-Sleep -Milliseconds 200
              [System.Windows.Forms.Application]::DoEvents()
            }
            Start-Sleep -Milliseconds 700

            # 2. Overlay the new files onto the install folder, preserving user data. robocopy without /MIR
            #    only adds/overwrites, so files not present in the new build (e.g. session caches) are kept.
            Set-Status 'Installing update... this can take a minute.'
            $log = Join-Path $TempRoot 'robocopy.log'
            $roboArgs = @(
              $StagingDir, $InstallDir, '/E', '/R:2', '/W:2', '/NFL', '/NDL', '/NJH', '/NP',
              '/XD', (Join-Path $StagingDir 'config'), (Join-Path $StagingDir 'logs'), (Join-Path $StagingDir 'playwright'),
              '/XF', (Join-Path $StagingDir '.env'),
              "/LOG:$log"
            )
            $robo = Start-Process -FilePath 'robocopy.exe' -ArgumentList $roboArgs -PassThru -WindowStyle Hidden
            while (-not $robo.HasExited) {
              Start-Sleep -Milliseconds 150
              [System.Windows.Forms.Application]::DoEvents()
            }

            # robocopy exit codes 0-7 are success; 8+ means copy errors.
            if ($robo.ExitCode -ge 8) {
              Set-Status 'Update failed. Starting the current version...'
              Start-Sleep -Seconds 3
            } else {
              Set-Status 'Update complete. Starting Tbot Ultra...'
              Start-Sleep -Milliseconds 600
            }

            # 3. Relaunch the (updated) app.
            $exePath = Join-Path $InstallDir $ExeName
            try { Start-Process -FilePath $exePath -WorkingDirectory $InstallDir } catch {}
            Start-Sleep -Milliseconds 400
            $form.Close()

            # 4. Best-effort cleanup of the extracted staging files (the small log/script are left behind and
            #    get wiped when the next update recreates the workspace).
            try { Remove-Item -LiteralPath $StagingDir -Recurse -Force -ErrorAction SilentlyContinue } catch {}
            """;
    }
}
