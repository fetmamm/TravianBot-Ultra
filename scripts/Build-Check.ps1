param(
    [string]$Project = "TbotUltra.sln",
    [string]$Configuration = "Debug",
    [int]$CommandTimeoutSeconds = 900,
    [string]$RunName = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

function Invoke-DotNetWithTimeout {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    Write-Host $Description
    Write-Host "dotnet $($Arguments -join ' ')"

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "dotnet"
    $startInfo.Arguments = ($Arguments | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join " "
    $startInfo.UseShellExecute = $false

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            Write-Host "ERROR: Could not start dotnet for $Description."
            exit 1
        }

        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Write-Host "ERROR: $Description timed out after $TimeoutSeconds seconds. Killing only this dotnet process tree (PID $($process.Id))."
            & taskkill /PID $process.Id /T /F | Out-Host
            exit 124
        }

        if ($process.ExitCode -ne 0) {
            exit $process.ExitCode
        }
    }
    finally {
        $process.Dispose()
    }
}

function ConvertTo-CommandLineArgument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    return '"' + $Value.Replace('"', '\"') + '"'
}

if ([string]::IsNullOrWhiteSpace($RunName)) {
    $RunName = "latest"
}

$projectPath = if ([System.IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $repoRoot $Project }
$artifactsDir = Join-Path $repoRoot "temp_build_out\build-check\$RunName\artifacts"
if (Test-Path -LiteralPath $artifactsDir) {
    Write-Host "Removing previous build-check output: $artifactsDir"
    Remove-Item -LiteralPath $artifactsDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

$arguments = @(
    "build",
    $projectPath,
    "-c",
    $Configuration,
    "--nologo",
    "--disable-build-servers",
    "--artifacts-path",
    $artifactsDir,
    "-m:1",
    "-p:NuGetAudit=false",
    "-p:UseSharedCompilation=false",
    "-p:BuildInParallel=false"
)

Invoke-DotNetWithTimeout `
    -Arguments $arguments `
    -TimeoutSeconds $CommandTimeoutSeconds `
    -Description "Building $projectPath"

Write-Host "Build-check output: $artifactsDir"
