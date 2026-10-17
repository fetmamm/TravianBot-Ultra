[CmdletBinding()]
param(
    [string]$PublicRepository = "fetmamm/TravianBot-Ultra",
    [string]$PrivateRepository = "fetmamm/Tbot-Ultra-Source",
    [string]$PreparedRepositoryPath = "",
    [switch]$Apply,
    [string]$Confirmation = ""
)

$ErrorActionPreference = "Stop"

function Invoke-GitHubJson {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $raw = & gh @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CLI failed: gh $($Arguments -join ' ')"
    }

    return ($raw -join "`n") | ConvertFrom-Json
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($PreparedRepositoryPath)) {
    $PreparedRepositoryPath = Join-Path $repositoryRoot "artifacts\repository-migration\public-release-prepared"
}
$PreparedRepositoryPath = (Resolve-Path $PreparedRepositoryPath).Path

$publicState = Invoke-GitHubJson -Arguments @("api", "repos/$PublicRepository")
$privateState = Invoke-GitHubJson -Arguments @("api", "repos/$PrivateRepository")
if ($publicState.visibility -ne "public") {
    throw "$PublicRepository must remain public to preserve its stars and update endpoint."
}
if ($publicState.stargazers_count -lt 12) {
    throw "$PublicRepository has fewer than the expected 12 stars; stop and investigate."
}
if ($privateState.visibility -ne "private") {
    throw "$PrivateRepository is not private."
}

$preparedFiles = @(& git -C $PreparedRepositoryPath ls-tree -r --name-only HEAD)
if ($LASTEXITCODE -ne 0) {
    throw "Could not inspect the prepared release-only repository."
}
if ($preparedFiles.Count -ne 1 -or $preparedFiles[0] -ne "README.md") {
    throw "Prepared public history must contain only README.md. Found: $($preparedFiles -join ', ')"
}
if (-not [string]::IsNullOrWhiteSpace((& git -C $PreparedRepositoryPath status --porcelain))) {
    throw "Prepared public repository has uncommitted changes."
}

$secretRows = Invoke-GitHubJson -Arguments @("secret", "list", "--repo", $PrivateRepository, "--json", "name")
$variableRows = Invoke-GitHubJson -Arguments @("variable", "list", "--repo", $PrivateRepository, "--json", "name,value")
$secretNames = @($secretRows | ForEach-Object { $_.name })
$variables = @{}
foreach ($row in @($variableRows)) {
    $variables[$row.name] = $row.value
}

$requiredSecrets = @(
    "RELEASE_REPO_TOKEN",
    "DISCORD_ANNOUNCEMENT_WEBHOOK_URL",
    "CLOUDFLARE_API_TOKEN"
)
$missingSecrets = @($requiredSecrets | Where-Object { $_ -notin $secretNames })
$expectedVariables = @{
    PUBLIC_RELEASE_REPOSITORY = $PublicRepository
    CLOUDFLARE_ACCOUNT_ID = "614904a740cbe8f2de8cacd87f70ba9c"
    CLOUDFLARE_PAGES_DEPLOY_ENABLED = "true"
}
$invalidVariables = @(
    foreach ($entry in $expectedVariables.GetEnumerator()) {
        if ($variables[$entry.Key] -ne $entry.Value) {
            "$($entry.Key)=$($variables[$entry.Key]) (expected $($entry.Value))"
        }
    }
)

if (-not $Apply) {
    [pscustomobject]@{
        PublicRepository = $PublicRepository
        PublicStars = $publicState.stargazers_count
        PrivateRepository = $PrivateRepository
        PreparedCommit = (& git -C $PreparedRepositoryPath rev-parse HEAD).Trim()
        MissingSecrets = $missingSecrets -join ", "
        InvalidVariables = $invalidVariables -join ", "
        ReadyToApply = $missingSecrets.Count -eq 0 -and $invalidVariables.Count -eq 0
    }
    return
}

if ($missingSecrets.Count -gt 0) {
    throw "Missing private repository secrets: $($missingSecrets -join ', ')"
}
if ($invalidVariables.Count -gt 0) {
    throw "Repository variables are not ready: $($invalidVariables -join '; ')"
}
if ($Confirmation -ne "rewrite $PublicRepository") {
    throw "Refusing public history rewrite. Pass -Confirmation 'rewrite $PublicRepository'."
}

$siteConfig = (Invoke-WebRequest -Uri "https://tbot-ultra.pages.dev/config.js" -UseBasicParsing).Content
if ($siteConfig -notmatch 'checkoutMode:\s*"sandbox"') {
    throw "The deployed website is not in sandbox mode."
}
if ($siteConfig -notmatch 'github\.com/fetmamm/TravianBot-Ultra/releases') {
    throw "The deployed website does not point to $PublicRepository releases."
}

$publicUrl = "https://github.com/$PublicRepository.git"
$existingRemote = & git -C $PreparedRepositoryPath remote get-url public 2>$null
if ($LASTEXITCODE -eq 0) {
    & git -C $PreparedRepositoryPath remote set-url public $publicUrl
} else {
    & git -C $PreparedRepositoryPath remote add public $publicUrl
}
if ($LASTEXITCODE -ne 0) {
    throw "Could not configure the prepared repository's public remote."
}

$remoteHeads = @(& git ls-remote --heads $publicUrl)
if ($LASTEXITCODE -ne 0) {
    throw "Could not read public branches."
}
$sourceBranches = @(
    $remoteHeads |
        ForEach-Object { ($_ -split "\s+")[1] -replace '^refs/heads/', '' } |
        Where-Object { $_ -and $_ -ne "main" }
)
$remoteTags = @(& git ls-remote --tags --refs $publicUrl)
if ($LASTEXITCODE -ne 0) {
    throw "Could not read public tags."
}
$tagNames = @(
    $remoteTags |
        ForEach-Object { ($_ -split "\s+")[1] -replace '^refs/tags/', '' } |
        Where-Object { $_ }
)

& git -C $PreparedRepositoryPath push public HEAD:refs/heads/main --force
if ($LASTEXITCODE -ne 0) {
    throw "Failed to replace the public main branch."
}
if ($sourceBranches.Count -gt 0) {
    & git -C $PreparedRepositoryPath push public --delete @sourceBranches
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to delete public source branches."
    }
}
foreach ($tagName in $tagNames) {
    & git -C $PreparedRepositoryPath tag --force $tagName HEAD | Out-Null
}
if ($tagNames.Count -gt 0) {
    & git -C $PreparedRepositoryPath push public --tags --force
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to move public tags to release-only history."
    }
}

$finalState = Invoke-GitHubJson -Arguments @("api", "repos/$PublicRepository")
$finalHeads = @(& git ls-remote --heads $publicUrl)
if ($finalHeads.Count -ne 1 -or $finalHeads[0] -notmatch 'refs/heads/main$') {
    throw "Public branch verification failed after cutover."
}
if ($finalState.stargazers_count -lt 12) {
    throw "Public stars unexpectedly changed after cutover."
}

Write-Host "Public release-only history is active at $PublicRepository."
Write-Host "Stars preserved: $($finalState.stargazers_count)"
Write-Host "Moved tags: $($tagNames.Count)"
