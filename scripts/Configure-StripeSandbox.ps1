[CmdletBinding()]
param(
    [string]$ProjectRef = "sfgyrrduvbyxywahsjil",
    [string]$WebhookUrl = "https://sfgyrrduvbyxywahsjil.supabase.co/functions/v1/stripe-webhook"
)

$ErrorActionPreference = "Stop"
$secretFile = $null
$stripeSecretKey = $null
$webhookSecret = $null

try {
    $secureKey = Read-Host "Paste the Stripe sandbox secret key (sk_test_...)" -AsSecureString
    $keyPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
    try {
        $stripeSecretKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($keyPointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($keyPointer)
    }

    if ($stripeSecretKey -notmatch '^sk_test_[A-Za-z0-9_]+$') {
        throw "Expected a Stripe sandbox key beginning with sk_test_."
    }

    $headers = @{ Authorization = "Bearer $stripeSecretKey" }
    $existing = Invoke-RestMethod `
        -Uri "https://api.stripe.com/v1/webhook_endpoints?limit=100" `
        -Headers $headers `
        -Method Get
    $matchingEndpoints = @($existing.data | Where-Object { $_.url -eq $WebhookUrl })
    if ($matchingEndpoints.Count -gt 0) {
        Write-Host "A sandbox webhook already uses the Tbot endpoint."
        $confirmation = Read-Host "Recreate it so a new signing secret can be stored? Type YES"
        if ($confirmation -cne "YES") {
            throw "Webhook setup canceled without changing the existing endpoint."
        }
        foreach ($endpoint in $matchingEndpoints) {
            Invoke-RestMethod `
                -Uri "https://api.stripe.com/v1/webhook_endpoints/$($endpoint.id)" `
                -Headers $headers `
                -Method Delete | Out-Null
        }
    }

    $endpointBody = @{
        url = $WebhookUrl
        description = "Tbot Ultra sandbox license fulfillment"
        "enabled_events[0]" = "checkout.session.completed"
        "enabled_events[1]" = "checkout.session.async_payment_succeeded"
        "enabled_events[2]" = "charge.refunded"
        "enabled_events[3]" = "refund.created"
        "enabled_events[4]" = "charge.dispute.created"
    }
    $endpoint = Invoke-RestMethod `
        -Uri "https://api.stripe.com/v1/webhook_endpoints" `
        -Headers $headers `
        -Method Post `
        -ContentType "application/x-www-form-urlencoded" `
        -Body $endpointBody

    if ($endpoint.livemode -ne $false -or $endpoint.secret -notmatch '^whsec_') {
        throw "Stripe did not return a sandbox webhook signing secret."
    }
    $webhookSecret = $endpoint.secret

    $secretFile = [IO.Path]::GetTempFileName()
    $encoding = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllLines($secretFile, @(
        "STRIPE_SECRET_KEY=$stripeSecretKey",
        "STRIPE_WEBHOOK_SECRET=$webhookSecret"
    ), $encoding)

    & npx --yes supabase secrets set --env-file $secretFile --project-ref $ProjectRef
    if ($LASTEXITCODE -ne 0) {
        throw "Supabase rejected the Stripe secrets."
    }

    Write-Host "Stripe sandbox webhook configured: $($endpoint.id)"
    Write-Host "Stripe secrets stored in Supabase."
}
finally {
    if ($secretFile -and (Test-Path -LiteralPath $secretFile)) {
        Remove-Item -LiteralPath $secretFile -Force
    }
    $stripeSecretKey = $null
    $webhookSecret = $null
}
