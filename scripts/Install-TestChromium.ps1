[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$testProject = Join-Path $repositoryRoot "src\TbotUltra.Worker.Tests\TbotUltra.Worker.Tests.csproj"
$browserRoot = Join-Path $repositoryRoot "temp_build_out\playwright-browsers"

$targetDir = dotnet msbuild $testProject `
    -nologo `
    -getProperty:TargetDir `
    -p:Configuration=$Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Could not resolve the Worker test output directory."
}

$targetDir = ($targetDir | Select-Object -Last 1).Trim()
$playwrightScript = Join-Path $targetDir "playwright.ps1"
if (-not (Test-Path -LiteralPath $playwrightScript -PathType Leaf)) {
    throw "Playwright installer is missing from the Worker test output: $playwrightScript"
}

New-Item -ItemType Directory -Path $browserRoot -Force | Out-Null
$env:PLAYWRIGHT_BROWSERS_PATH = $browserRoot
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_ENV)) {
    "PLAYWRIGHT_BROWSERS_PATH=$browserRoot" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
}

Write-Host "Installing test Chromium in $browserRoot"
& $playwrightScript install chromium --no-shell
if ($LASTEXITCODE -ne 0) {
    throw "Playwright Chromium install failed with exit code $LASTEXITCODE."
}
