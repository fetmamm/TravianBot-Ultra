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

    $process = Start-Process -FilePath "dotnet" -ArgumentList $Arguments -NoNewWindow -PassThru
    try {
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

if ([string]::IsNullOrWhiteSpace($RunName)) {
    $RunName = Get-Date -Format "yyyyMMdd-HHmmss"
}

$projectPath = if ([System.IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $repoRoot $Project }
$artifactsDir = Join-Path $repoRoot "temp_build_out\build-check\$RunName\artifacts"
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
