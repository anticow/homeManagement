[CmdletBinding()]
param(
    [ValidateRange(1, 3300)]
    [int]$TimeoutSeconds = 3300,
    [ValidateRange(1, 60)]
    [int]$PollSeconds = 15
)

$ErrorActionPreference = 'Stop'
# Tokens are read only from the existing App action's environment, never process args.
if ([string]::IsNullOrWhiteSpace($env:DISPATCH_TOKEN)) {
    throw 'The repository-scoped Ansible dispatcher GitHub App token is required.'
}
if ($env:RELEASE_VERSION -cnotmatch '^(0|[1-9][0-9]{0,9})\.(0|[1-9][0-9]{0,9})\.(0|[1-9][0-9]{0,9})\z' -or
    $env:GITHUB_RUN_ID -cnotmatch '^[0-9]{1,20}\z' -or
    $env:GITHUB_RUN_ATTEMPT -cnotmatch '^[0-9]{1,6}\z') {
    throw 'A stable release version and valid GitHub run correlation are required.'
}

$deliveryId = "hm-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"
$title = "HomeManagement release $deliveryId"
$base = 'https://api.github.com/repos/anticow/ansible/actions'
$headers = @{
    Accept = 'application/vnd.github+json'
    Authorization = "Bearer $env:DISPATCH_TOKEN"
    'X-GitHub-Api-Version' = '2022-11-28'
}
$started = [DateTime]::UtcNow
$deadline = $started.AddSeconds($TimeoutSeconds)
$created = [Uri]::EscapeDataString('>=' + $started.AddSeconds(-5).ToString('yyyy-MM-ddTHH:mm:ssZ'))
$body = @{
    ref = 'main'
    inputs = @{
        operation = 'homemanagement-release'
        environment = 'cowgo'
        check = 'false'
        delivery_id = $deliveryId
        release_version = $env:RELEASE_VERSION
    }
} | ConvertTo-Json -Depth 3 -Compress

# Verify existing Actions read permission BEFORE dispatch. Do not silently broaden
# App permissions or report an accepted dispatch as completed delivery.
try {
    $null = Invoke-RestMethod -Method Get -Uri "$base/workflows/platform-configure.yml" `
        -Headers $headers -TimeoutSec 30
} catch {
    throw 'Cannot read Platform Configure with the existing App installation. Verify ansible-scoped Actions read/write permission; nothing was dispatched.'
}
try {
    $null = Invoke-RestMethod -Method Post -Uri "$base/workflows/platform-configure.yml/dispatches" `
        -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 30
} catch {
    throw 'Platform Configure dispatch failed or was ambiguous. No automatic retry; inspect ansible runs before any manual retry.'
}

$runId = $null
while ([DateTime]::UtcNow -lt $deadline) {
    try {
        if ($null -eq $runId) {
            $page = Invoke-RestMethod -Method Get `
                -Uri "$base/workflows/platform-configure.yml/runs?event=workflow_dispatch&branch=main&created=$created&per_page=100" `
                -Headers $headers -TimeoutSec 30
            $matches = @($page.workflow_runs | Where-Object {
                $_.display_title -ceq $title -and $_.event -ceq 'workflow_dispatch' -and $_.head_branch -ceq 'main'
            })
            if ($matches.Count -gt 1) {
                throw 'Duplicate correlated receiver runs.'
            }
            if ($matches.Count -eq 1) {
                $runId = $matches[0].id
                Write-Host "Tracking Ansible run $runId for $deliveryId."
            }
        }
        if ($null -ne $runId) {
            $run = Invoke-RestMethod -Method Get -Uri "$base/runs/$runId" -Headers $headers -TimeoutSec 30
            if ($run.status -eq 'completed') {
                if ($run.conclusion -ne 'success') {
                    throw 'Receiver did not succeed.'
                }
                Write-Host "HomeManagement delivery completed successfully (Ansible run $runId)."
                return
            }
        }
    } catch {
        # Never emit REST exceptions, headers, or bodies: they may contain credentials.
        throw "Cannot confirm successful Ansible delivery ($deliveryId, run $runId). Inspect receiver status; no automatic redispatch."
    }
    Start-Sleep -Seconds $PollSeconds
}
throw "Timed out waiting for Ansible delivery ($deliveryId, run $runId). The receiver may still be running; inspect it before retrying. No redispatch or cancellation was attempted."
