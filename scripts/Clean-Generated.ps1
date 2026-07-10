param(
    [switch]$IncludeLegacyOutput
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$centralOutput = Join-Path $repoRoot "temp_build_out"

if (Get-Process -Name "TbotUltra.Desktop" -ErrorAction SilentlyContinue) {
    Write-Error "Tbot Ultra is running. Close it before cleaning generated files."
    exit 1
}

if (Test-Path -LiteralPath $centralOutput) {
    Write-Host "Removing central generated output except the preserved DOM directory: $centralOutput"
    Get-ChildItem -LiteralPath $centralOutput -Force |
        Where-Object { $_.Name -ne "DOM" } |
        ForEach-Object {
            Write-Host "Removing generated output: $($_.FullName)"
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
        }
}

if ($IncludeLegacyOutput) {
    $sourceRoot = Join-Path $repoRoot "src"
    $legacyDirectories = foreach ($projectDirectory in Get-ChildItem -LiteralPath $sourceRoot -Directory -Force) {
        foreach ($directoryName in @("bin", "obj", "temp_build_out")) {
            $legacyPath = Join-Path $projectDirectory.FullName $directoryName
            if (Test-Path -LiteralPath $legacyPath) {
                Get-Item -LiteralPath $legacyPath
            }
        }
    }

    foreach ($directory in $legacyDirectories) {
        Write-Host "Removing legacy generated output: $($directory.FullName)"
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force
    }
}

Write-Host "Generated output cleaned. It will be recreated by the next build."
