param(
    [ValidateRange(1, 500)]
    [int]$Count = 50,
    [string]$OutputDirectory = "artifacts/license-promo"
)

$ErrorActionPreference = "Stop"
$alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputDirectory))
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$csvPath = Join-Path $outputRoot "discord-codes.csv"
$sqlPath = Join-Path $outputRoot "insert-code-hashes.sql"
if ((Test-Path -LiteralPath $csvPath) -or (Test-Path -LiteralPath $sqlPath)) {
    throw "Promotion files already exist. Move them to a secure location before generating new codes."
}

$rows = [System.Collections.Generic.List[object]]::new()
$hashes = [System.Collections.Generic.HashSet[string]]::new()
while ($rows.Count -lt $Count) {
    $random = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    $chars = -join ($random | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
    $code = "TBU-$($chars.Substring(0, 4))-$($chars.Substring(4, 4))-$($chars.Substring(8, 4))-$($chars.Substring(12, 4))"
    $hashBytes = [System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($code))
    $hash = [Convert]::ToHexString($hashBytes).ToLowerInvariant()
    if ($hashes.Add($hash)) {
        $rows.Add([pscustomobject]@{ discord_user = ""; code = $code; code_hash = $hash })
    }
}

$rows | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding utf8
$values = ($rows | ForEach-Object { "  ('$($_.code_hash)')" }) -join ",`n"
$sql = @"
-- Run with an administrator database connection after setting the launch time.
insert into public.license_promo_codes (code_hash) values
$values
on conflict (code_hash) do nothing;
"@
[System.IO.File]::WriteAllText($sqlPath, $sql)
Write-Host "Created $Count codes and a hash-only SQL file in $outputRoot. Keep the CSV private."
