[CmdletBinding()]
param(
    [string]$OutputDirectory = "artifacts/license-backup"
)

$ErrorActionPreference = "Stop"
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$queryPath = Join-Path $PSScriptRoot "license-backup-query.sql"
$outputRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$raw = & npx --yes supabase db query --linked --output-format json --file $queryPath
if ($LASTEXITCODE -ne 0) {
    throw "Supabase license backup query failed."
}

$response = ($raw -join "`n") | ConvertFrom-Json
if ($response.rows.Count -ne 1 -or $null -eq $response.rows[0].snapshot) {
    throw "Supabase returned an unexpected backup payload."
}

$snapshot = $response.rows[0].snapshot
$requiredTables = @(
    "users", "license_configuration", "license_accounts", "license_copies",
    "license_trial_machines", "license_promo_codes", "license_purchases",
    "license_admin_grants", "license_lifetime_grants", "license_daily_usage",
    "license_leases", "license_webhook_events"
)
foreach ($table in $requiredTables) {
    if ($null -eq $snapshot.tables.$table) {
        throw "Backup payload is missing $table."
    }
}

$stamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd-HHmmss'Z'")
$backupPath = Join-Path $outputRoot "tbot-license-$stamp.json"
$checksumPath = "$backupPath.sha256"
$json = $snapshot | ConvertTo-Json -Depth 100 -Compress
[IO.File]::WriteAllText($backupPath, $json, [Text.UTF8Encoding]::new($false))
$hash = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($checksumPath, "$hash  $([IO.Path]::GetFileName($backupPath))`n",
    [Text.UTF8Encoding]::new($false))

Write-Host "License backup created: $backupPath"
Write-Host "SHA256: $hash"
Write-Warning "The backup contains account emails and license records. Store it securely and never commit it."
