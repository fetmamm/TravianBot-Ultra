param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion,
    [switch]$Launch,
    [switch]$LaunchFromTemporaryCopy,
    [int]$StartupTimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"

$resolvedPackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
$requiredFiles = @(
    "Tbot Ultra.exe",
    "VERSION",
    ".env",
    "README.txt",
    "config\bot.json",
    "config\queue.json",
    "config\servers.user.json",
    ".playwright\node\win32_x64\node.exe"
)

Write-Host "Validating release bundle: $resolvedPackageRoot"
foreach ($relativePath in $requiredFiles) {
    $path = Join-Path $resolvedPackageRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Release bundle is missing required file: $relativePath"
    }
}

$version = (Get-Content -LiteralPath (Join-Path $resolvedPackageRoot "VERSION") -Raw).Trim()
if ($version -ne $ExpectedVersion) {
    throw "Release VERSION mismatch. Expected '$ExpectedVersion', found '$version'."
}

$exePath = Join-Path $resolvedPackageRoot "Tbot Ultra.exe"
$versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
$expectedFileVersion = "$ExpectedVersion.0"
if ($versionInfo.FileVersion -ne $expectedFileVersion) {
    throw "EXE file version mismatch. Expected '$expectedFileVersion', found '$($versionInfo.FileVersion)'."
}
if (-not $versionInfo.ProductVersion.StartsWith($ExpectedVersion, [System.StringComparison]::Ordinal)) {
    throw "EXE product version mismatch. Expected prefix '$ExpectedVersion', found '$($versionInfo.ProductVersion)'."
}

$browserRoot = Join-Path $resolvedPackageRoot "ms-playwright"
if (-not (Test-Path -LiteralPath $browserRoot -PathType Container)) {
    throw "Release bundle is missing the bundled Chromium directory: ms-playwright"
}
# Playwright resolves one exact build revision, so "some chrome.exe is present" is not enough: a bundle
# built against a different package version would ship a browser the app never looks for, and the failure
# would only surface on a user's machine. Require the revision the shipped driver actually asks for.
$browsersMetadataPath = Join-Path $resolvedPackageRoot ".playwright\package\browsers.json"
if (-not (Test-Path -LiteralPath $browsersMetadataPath -PathType Leaf)) {
    throw "Release bundle is missing the Playwright driver metadata: .playwright\package\browsers.json"
}

$expectedChromiumRevision = (Get-Content -LiteralPath $browsersMetadataPath -Raw | ConvertFrom-Json).browsers |
    Where-Object { $_.name -eq "chromium" } |
    Select-Object -First 1 -ExpandProperty revision
if ([string]::IsNullOrWhiteSpace($expectedChromiumRevision)) {
    throw "Could not read the expected Chromium revision from browsers.json."
}

$expectedChromiumRoot = Join-Path $browserRoot "chromium-$expectedChromiumRevision"
$chromiumExe = Get-ChildItem -LiteralPath $expectedChromiumRoot -File -Filter "chrome.exe" -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -like "chrome-win*" } |
    Select-Object -First 1
if (-not $chromiumExe) {
    $present = (Get-ChildItem -LiteralPath $browserRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "chromium-*" } | Select-Object -ExpandProperty Name) -join ", "
    throw ("Release bundle does not contain the Chromium revision Playwright expects " +
        "(chromium-$expectedChromiumRevision). Present: $(if ($present) { $present } else { "none" }).")
}

foreach ($jsonFile in Get-ChildItem -LiteralPath (Join-Path $resolvedPackageRoot "config") -File -Filter "*.json") {
    try {
        Get-Content -LiteralPath $jsonFile.FullName -Raw | ConvertFrom-Json | Out-Null
    }
    catch {
        throw "Release config is invalid JSON: $($jsonFile.Name). $($_.Exception.Message)"
    }
}

$botConfig = Get-Content -LiteralPath (Join-Path $resolvedPackageRoot "config\bot.json") -Raw | ConvertFrom-Json
$retiredConfigKeys = @(
    "headless",
    "continuous_farm_dispatch_delay_minutes",
    "continuous_farm_dispatch_delay_variation_percent",
    "queue_wait_threshold_mode",
    "natar_village_selection"
)
foreach ($retiredKey in $retiredConfigKeys) {
    if ($null -ne $botConfig.PSObject.Properties[$retiredKey]) {
        throw "Release bot.json contains retired key '$retiredKey'."
    }
}
foreach ($requiredKey in @("continuous_farm_dispatch_delay_min_minutes", "continuous_farm_dispatch_delay_max_minutes")) {
    if ($null -eq $botConfig.PSObject.Properties[$requiredKey]) {
        throw "Release bot.json is missing current key '$requiredKey'."
    }
}

$unexpectedRootFiles = Get-ChildItem -LiteralPath $resolvedPackageRoot -File | Where-Object {
    $_.Extension -eq ".dll" `
        -or $_.Name -like "*.deps.json" `
        -or $_.Name -like "*.runtimeconfig.json" `
        -or $_.Name -eq "playwright.ps1"
}
if ($unexpectedRootFiles) {
    $names = ($unexpectedRootFiles | Select-Object -ExpandProperty Name) -join ", "
    throw "Release bundle contains unexpected runtime files at its root: $names"
}

$unexpectedRootDirectories = Get-ChildItem -LiteralPath $resolvedPackageRoot -Directory | Where-Object {
    $_.Name -in @("bin", "obj")
}
if ($unexpectedRootDirectories) {
    $names = ($unexpectedRootDirectories | Select-Object -ExpandProperty FullName) -join ", "
    throw "Release bundle contains build-only root directories: $names"
}

$unexpectedDirectories = Get-ChildItem -LiteralPath $resolvedPackageRoot -Directory -Recurse | Where-Object {
    $_.Name -in @("temp_build_out", "Captcha_solver", "__pycache__", ".venv")
}
if ($unexpectedDirectories) {
    $names = ($unexpectedDirectories | Select-Object -ExpandProperty FullName) -join ", "
    throw "Release bundle contains build-only directories: $names"
}

Write-Host "Release bundle structure and metadata are valid."
if (-not $Launch) {
    exit 0
}

$launchRoot = $resolvedPackageRoot
$temporaryRoot = $null
if ($LaunchFromTemporaryCopy) {
    $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("TbotUltraReleaseSmoke_" + [Guid]::NewGuid().ToString("N"))
    $launchRoot = Join-Path $temporaryRoot "package"
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    Copy-Item -LiteralPath $resolvedPackageRoot -Destination $launchRoot -Recurse -Force
}

$process = $null
try {
    $launchExe = Join-Path $launchRoot "Tbot Ultra.exe"
    $logsRoot = Join-Path $launchRoot "logs"
    New-Item -ItemType Directory -Path $logsRoot -Force | Out-Null
    $existingSessionLogs = @(Get-ChildItem -LiteralPath $logsRoot -File -Filter "TbotUltra_Log_*.txt" -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty FullName)
    $unhandledLog = Join-Path $logsRoot "desktop-unhandled.log"
    $unhandledLogInitialLength = if (Test-Path -LiteralPath $unhandledLog) {
        (Get-Item -LiteralPath $unhandledLog).Length
    } else {
        0
    }
    Write-Host "Starting packaged app smoke test from: $launchRoot"
    $process = Start-Process -FilePath $launchExe -WorkingDirectory $launchRoot -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $warmupCompleted = $false
    $catalogLoaded = $false
    $lastSessionLog = $null

    while ([DateTime]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) {
            throw "Packaged app exited during startup with code $($process.ExitCode)."
        }

        if ((Test-Path -LiteralPath $unhandledLog) -and (Get-Item -LiteralPath $unhandledLog).Length -gt $unhandledLogInitialLength) {
            $details = Get-Content -LiteralPath $unhandledLog -Raw
            throw "Packaged app wrote an unhandled exception during startup:`n$details"
        }

        $lastSessionLog = Get-ChildItem -LiteralPath $logsRoot -File -Filter "TbotUltra_Log_*.txt" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notin $existingSessionLogs } |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($null -ne $lastSessionLog) {
            $logText = Get-Content -LiteralPath $lastSessionLog.FullName -Raw -ErrorAction SilentlyContinue
            if ($logText -match "Construction cost/time estimates are disabled") {
                throw "Packaged app could not load the embedded building catalog."
            }
            $catalogLoaded = $logText -match "\[catalog\] Embedded building catalog loaded\."
            $warmupCompleted = $logText -match "Chromium warmup completed"
            if ($catalogLoaded -and $warmupCompleted) {
                $warmupCompleted = $true
                break
            }
        }

        Start-Sleep -Seconds 1
    }

    if (-not $catalogLoaded -or -not $warmupCompleted) {
        $details = if ($null -ne $lastSessionLog) {
            (Get-Content -LiteralPath $lastSessionLog.FullName -Tail 80) -join [Environment]::NewLine
        } else {
            "No session log was created."
        }
        throw "Packaged app did not load its catalog and complete Chromium warmup within $StartupTimeoutSeconds seconds.`n$details"
    }

    Write-Host "Packaged app loaded its embedded catalog and completed Chromium warmup successfully."
}
finally {
    if ($null -ne $process) {
        $process.Refresh()
        if (-not $process.HasExited) {
            & taskkill.exe /PID $process.Id /T /F | Out-Null
        }
        $process.Dispose()
    }
    if ($null -ne $temporaryRoot -and (Test-Path -LiteralPath $temporaryRoot)) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
