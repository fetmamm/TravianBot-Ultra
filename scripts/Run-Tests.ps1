param(
    [string[]]$Project = @(),
    [string]$Configuration = "Debug",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

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
    $buildDir = Join-Path $repoRoot "temp_build_out\test-bin\$resultName"
    $resultsDir = Join-Path $repoRoot "temp_build_out\test-results\$resultName"
    New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
    New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

    if (-not $NoBuild) {
        Write-Host "Building $($testProject.FullName)"
        $outputPathProperty = "-p:OutputPath=$buildDir\"
        dotnet build $testProject.FullName -c $Configuration --no-restore $outputPathProperty /p:BuildInParallel=false
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }

    $assemblyPath = Join-Path $buildDir "$resultName.dll"
    if (-not (Test-Path -LiteralPath $assemblyPath)) {
        Write-Error "Test assembly not found: $assemblyPath"
        exit 1
    }

    Write-Host "Testing $($testProject.FullName)"
    dotnet vstest $assemblyPath --Logger:"console;verbosity=minimal" --ResultsDirectory:$resultsDir
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
