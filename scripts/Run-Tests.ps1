param(
    [string[]]$Project = @(),
    [string]$Configuration = "Debug",
    [switch]$NoBuild,
    [int]$CommandTimeoutSeconds = 900,
    [string]$HangTimeout = "120s",
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

$runRoot = Join-Path $repoRoot "temp_build_out\test-runs\$RunName"
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null

if ($Project.Count -gt 0) {
    $testProjects = foreach ($item in $Project) {
        $path = if ([System.IO.Path]::IsPathRooted($item)) { $item } else { Join-Path $repoRoot $item }
        Get-Item -LiteralPath $path
    }
} else {
    $testProjects = Get-ChildItem -Path (Join-Path $repoRoot "src") -Recurse -Filter "*.Tests.csproj" |
        Sort-Object FullName
}

if ($testProjects.Count -eq 0) {
    Write-Error "No test projects found."
    exit 1
}

foreach ($testProject in $testProjects) {
    $resultName = [System.IO.Path]::GetFileNameWithoutExtension($testProject.Name)
    $projectRunRoot = Join-Path $runRoot $resultName
    $artifactsDir = Join-Path $projectRunRoot "artifacts"
    $resultsDir = Join-Path $projectRunRoot "results"
    New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null
    New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

    $arguments = @(
        "test",
        $testProject.FullName,
        "-c",
        $Configuration,
        "--nologo",
        "--disable-build-servers",
        "--artifacts-path",
        $artifactsDir,
        "--results-directory",
        $resultsDir,
        "--logger",
        "console;verbosity=minimal",
        "--blame-hang",
        "--blame-hang-dump-type",
        "none",
        "--blame-hang-timeout",
        $HangTimeout,
        "-m:1",
        "-p:NuGetAudit=false",
        "-p:UseSharedCompilation=false",
        "-p:BuildInParallel=false"
    )

    if ($NoBuild) {
        $arguments += "--no-build"
    }

    Invoke-DotNetWithTimeout `
        -Arguments $arguments `
        -TimeoutSeconds $CommandTimeoutSeconds `
        -Description "Testing $($testProject.FullName)"
}

Write-Host "Test run output: $runRoot"
