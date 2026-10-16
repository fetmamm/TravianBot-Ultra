[CmdletBinding()]
param(
    [string]$Path
)

$ErrorActionPreference = "Stop"
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($Path)) {
    $backupRoot = Join-Path $projectRoot "artifacts/license-backup"
    $latest = Get-ChildItem -LiteralPath $backupRoot -Filter "tbot-license-*.json" -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $latest) {
        throw "No license backup was found in $backupRoot."
    }
    $Path = $latest.FullName
}

$backupPath = if ([IO.Path]::IsPathRooted($Path)) {
    [IO.Path]::GetFullPath($Path)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}
if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
    throw "License backup does not exist: $backupPath"
}

$checksumPath = "$backupPath.sha256"
if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
    throw "Backup checksum file is missing: $checksumPath"
}

$expectedHash = ((Get-Content -LiteralPath $checksumPath -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
$actualHash = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    throw "License backup checksum does not match."
}

$snapshot = Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json
if ($snapshot.format_version -ne 1 -or $null -eq $snapshot.created_at -or $null -eq $snapshot.tables) {
    throw "License backup header is invalid."
}

$requiredTables = @(
    "users", "license_configuration", "license_accounts", "license_copies",
    "license_trial_machines", "license_promo_codes", "license_purchases",
    "license_admin_grants", "license_lifetime_grants", "license_daily_usage",
    "license_leases", "license_webhook_events"
)
foreach ($table in $requiredTables) {
    if ($null -eq $snapshot.tables.$table) {
        throw "License backup is missing $table."
    }
}

$userIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($user in @($snapshot.tables.users)) {
    $parsedId = [Guid]::Empty
    if (-not [Guid]::TryParse([string]$user.id, [ref]$parsedId) -or
        [string]::IsNullOrWhiteSpace([string]$user.email) -or
        -not $userIds.Add($parsedId.ToString())) {
        throw "Backup contains an invalid or duplicate Auth user."
    }
}

$tablesWithRequiredUsers = @(
    "license_accounts", "license_copies", "license_purchases", "license_admin_grants",
    "license_lifetime_grants", "license_daily_usage", "license_leases"
)
foreach ($table in $tablesWithRequiredUsers) {
    foreach ($row in @($snapshot.tables.$table)) {
        if (-not $userIds.Contains([string]$row.user_id)) {
            throw "$table references an Auth user missing from the backup."
        }
    }
}

$activeCopiesByUser = @{}
foreach ($copy in @($snapshot.tables.license_copies)) {
    if ($null -eq $copy.revoked_at) {
        $key = [string]$copy.user_id
        $currentCount = if ($activeCopiesByUser.ContainsKey($key)) { [int]$activeCopiesByUser[$key] } else { 0 }
        $activeCopiesByUser[$key] = 1 + $currentCount
        if ($activeCopiesByUser[$key] -gt 2) {
            throw "Backup violates the two-copy limit."
        }
    }
}

$prices = @{ 1 = 299; 2 = 499; 6 = 1399; 12 = 2499 }
foreach ($purchase in @($snapshot.tables.license_purchases)) {
    $months = [int]$purchase.months
    if (-not $prices.ContainsKey($months) -or [int]$purchase.amount_eur_cents -ne $prices[$months]) {
        throw "Backup contains a purchase with an invalid package price."
    }
}

Write-Host "License backup verified: $backupPath"
Write-Host "Users: $(@($snapshot.tables.users).Count); purchases: $(@($snapshot.tables.license_purchases).Count); lifetime grants: $(@($snapshot.tables.license_lifetime_grants).Count)"
