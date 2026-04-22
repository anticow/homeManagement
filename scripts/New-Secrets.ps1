<#
.SYNOPSIS
    Generates all HomeManagement secrets and writes deploy/docker/.env.

.DESCRIPTION
    Cryptographically generates every secret required to run the HomeManagement
    platform, writes them to deploy/docker/.env, and prints GitHub Actions CLI
    commands to upload them as repository secrets.

    Run once on a new environment. Re-run with -Force to rotate all secrets
    (or use -RotateOnly to rotate a specific subset).

.EXAMPLE
    # First-time setup
    .\scripts\New-Secrets.ps1

.EXAMPLE
    # Rotate everything (overwrites .env)
    .\scripts\New-Secrets.ps1 -Force

.EXAMPLE
    # Rotate only the audit HMAC key (leave all others unchanged)
    .\scripts\New-Secrets.ps1 -RotateOnly HmacKey

.EXAMPLE
    # Generate but don't write .env — just print to console
    .\scripts\New-Secrets.ps1 -PrintOnly
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # Overwrite an existing .env file
    [switch]$Force,
    # Print generated values to console without writing .env
    [switch]$PrintOnly,
    # Rotate only one secret, preserving all others in .env
    [ValidateSet('SqlPassword', 'JwtKey', 'HmacKey', 'AgentGatewayKey', 'SeqPassword', 'BootstrapPassword')]
    [string]$RotateOnly,
    # Custom admin username (default: admin)
    [string]$AdminUsername = 'admin'
)

$ErrorActionPreference = 'Stop'
$repoRoot  = Split-Path -Parent $PSScriptRoot
$envFile   = Join-Path $repoRoot 'deploy\docker\.env'
$envExample = Join-Path $repoRoot 'deploy\docker\.env.example'

# ── Helpers ──────────────────────────────────────────────────────────────────

function New-RandomBase64 {
    param([int]$Bytes = 32)
    [Convert]::ToBase64String(
        [System.Security.Cryptography.RandomNumberGenerator]::GetBytes($Bytes)
    )
}

function New-SqlPassword {
    # SQL Server requires: uppercase + lowercase + digit + symbol, 8+ chars
    $upper   = [char[]]'ABCDEFGHJKLMNPQRSTUVWXYZ'
    $lower   = [char[]]'abcdefghjkmnpqrstuvwxyz'
    $digits  = [char[]]'23456789'
    $symbols = [char[]]'!@#%^&*-+'
    $all     = $upper + $lower + $digits + $symbols
    $rng     = [System.Security.Cryptography.RandomNumberGenerator]::Create()

    $password = @(
        $upper[(Get-RandomByte $rng) % $upper.Count]
        $lower[(Get-RandomByte $rng) % $lower.Count]
        $digits[(Get-RandomByte $rng) % $digits.Count]
        $symbols[(Get-RandomByte $rng) % $symbols.Count]
    )
    for ($i = 0; $i -lt 20; $i++) {
        $password += $all[(Get-RandomByte $rng) % $all.Count]
    }
    # Fisher-Yates shuffle
    for ($i = $password.Count - 1; $i -gt 0; $i--) {
        $j = (Get-RandomByte $rng) % ($i + 1)
        $tmp = $password[$i]; $password[$i] = $password[$j]; $password[$j] = $tmp
    }
    return -join $password
}

function Get-RandomByte {
    param($rng)
    $buf = [byte[]]::new(1)
    $rng.GetBytes($buf)
    return $buf[0]
}

function Read-ExistingEnv {
    param([string]$Path)
    $map = @{}
    if (Test-Path $Path) {
        Get-Content $Path | ForEach-Object {
            if ($_ -match '^([^#=]+)=(.*)$') {
                $map[$Matches[1].Trim()] = $Matches[2].Trim()
            }
        }
    }
    return $map
}

# ── Load existing values if rotating only one key ────────────────────────────

$existing = @{}
if ($RotateOnly -and (Test-Path $envFile)) {
    $existing = Read-ExistingEnv $envFile
    Write-Host "Loaded existing .env — rotating only: $RotateOnly" -ForegroundColor Cyan
} elseif ((Test-Path $envFile) -and -not $Force -and -not $PrintOnly -and -not $RotateOnly) {
    throw ".env already exists at '$envFile'. Use -Force to overwrite, -RotateOnly <key> to rotate one secret, or -PrintOnly to preview."
}

# ── Generate secrets ─────────────────────────────────────────────────────────

Write-Host ''
Write-Host 'Generating secrets...' -ForegroundColor Cyan

$secrets = [ordered]@{
    HM_SQL_SA_PASSWORD        = if ($RotateOnly -and $RotateOnly -ne 'SqlPassword')     { $existing['HM_SQL_SA_PASSWORD']        } else { New-SqlPassword }
    HM_JWT_SIGNING_KEY        = if ($RotateOnly -and $RotateOnly -ne 'JwtKey')          { $existing['HM_JWT_SIGNING_KEY']        } else { New-RandomBase64 -Bytes 64 }
    HM_AUDIT_HMAC_KEY         = if ($RotateOnly -and $RotateOnly -ne 'HmacKey')         { $existing['HM_AUDIT_HMAC_KEY']         } else { New-RandomBase64 -Bytes 32 }
    HM_AGENT_GATEWAY_API_KEY  = if ($RotateOnly -and $RotateOnly -ne 'AgentGatewayKey') { $existing['HM_AGENT_GATEWAY_API_KEY']  } else { New-RandomBase64 -Bytes 32 }
    HM_SEQ_ADMIN_PASSWORD     = if ($RotateOnly -and $RotateOnly -ne 'SeqPassword')     { $existing['HM_SEQ_ADMIN_PASSWORD']     } else { New-RandomBase64 -Bytes 24 }
    HM_BOOTSTRAP_ADMIN_ENABLED   = 'true'
    HM_BOOTSTRAP_ADMIN_USERNAME  = $AdminUsername
    HM_BOOTSTRAP_ADMIN_PASSWORD  = if ($RotateOnly -and $RotateOnly -ne 'BootstrapPassword') { $existing['HM_BOOTSTRAP_ADMIN_PASSWORD'] } else { New-RandomBase64 -Bytes 24 }
}

# ── Write .env ────────────────────────────────────────────────────────────────

$envContent = @"
# HomeManagement Docker Environment
# Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
# DO NOT COMMIT THIS FILE. See .env.example for the template.

HM_SQL_SA_PASSWORD=$($secrets['HM_SQL_SA_PASSWORD'])
HM_JWT_SIGNING_KEY=$($secrets['HM_JWT_SIGNING_KEY'])
HM_AUDIT_HMAC_KEY=$($secrets['HM_AUDIT_HMAC_KEY'])
HM_AGENT_GATEWAY_API_KEY=$($secrets['HM_AGENT_GATEWAY_API_KEY'])
HM_SEQ_ADMIN_PASSWORD=$($secrets['HM_SEQ_ADMIN_PASSWORD'])
HM_BOOTSTRAP_ADMIN_ENABLED=$($secrets['HM_BOOTSTRAP_ADMIN_ENABLED'])
HM_BOOTSTRAP_ADMIN_USERNAME=$($secrets['HM_BOOTSTRAP_ADMIN_USERNAME'])
HM_BOOTSTRAP_ADMIN_PASSWORD=$($secrets['HM_BOOTSTRAP_ADMIN_PASSWORD'])
"@

if ($PrintOnly) {
    Write-Host ''
    Write-Host '── Generated .env contents ─────────────────────────────────────────' -ForegroundColor Yellow
    Write-Host $envContent
} else {
    if ($PSCmdlet.ShouldProcess($envFile, 'Write .env')) {
        $envDir = Split-Path -Parent $envFile
        if (-not (Test-Path $envDir)) { New-Item -ItemType Directory -Path $envDir -Force | Out-Null }
        Set-Content -Path $envFile -Value $envContent -Encoding UTF8 -NoNewline:$false
        Write-Host "  ✓ Written: $envFile" -ForegroundColor Green
    }
}

# ── Print GitHub Actions CLI commands ─────────────────────────────────────────

Write-Host ''
Write-Host '── GitHub Actions Repository Secrets ───────────────────────────────' -ForegroundColor Yellow
Write-Host '  Run these commands to upload secrets (requires gh CLI + repo write access):'
Write-Host ''

$ghSecrets = @('HM_SQL_SA_PASSWORD','HM_JWT_SIGNING_KEY','HM_AUDIT_HMAC_KEY',
               'HM_AGENT_GATEWAY_API_KEY','HM_SEQ_ADMIN_PASSWORD','HM_BOOTSTRAP_ADMIN_PASSWORD')

foreach ($key in $ghSecrets) {
    $val = $secrets[$key]
    Write-Host "  gh secret set $key --body `"$val`"" -ForegroundColor DarkCyan
}

Write-Host ''
Write-Host '  Or upload all at once from your .env file:' -ForegroundColor DarkGray
Write-Host '  gh secret set --env-file deploy/docker/.env' -ForegroundColor DarkCyan

# ── Rotation warnings ─────────────────────────────────────────────────────────

if ($RotateOnly -eq 'HmacKey' -or (-not $RotateOnly -and -not $PrintOnly)) {
    Write-Host ''
    Write-Host '── ⚠  HMAC Key Rotation Warning ────────────────────────────────────' -ForegroundColor Red
    Write-Host '  HM_AUDIT_HMAC_KEY has been rotated.' -ForegroundColor Yellow
    Write-Host '  Existing audit chain records (ChainVersion=1) will not verify' -ForegroundColor Yellow
    Write-Host '  against the new key. Take a snapshot before deploying:' -ForegroundColor Yellow
    Write-Host '    POST /api/audit/verify-chain  (run against OLD deployment first)' -ForegroundColor Yellow
}

if ($RotateOnly -eq 'JwtKey' -or (-not $RotateOnly -and -not $PrintOnly)) {
    Write-Host ''
    Write-Host '── ⚠  JWT Key Rotation Warning ─────────────────────────────────────' -ForegroundColor Red
    Write-Host '  HM_JWT_SIGNING_KEY has been rotated.' -ForegroundColor Yellow
    Write-Host '  All active sessions will be immediately invalidated.' -ForegroundColor Yellow
    Write-Host '  Users will need to log in again after deployment.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '── Done ────────────────────────────────────────────────────────────' -ForegroundColor Green
if (-not $PrintOnly) {
    Write-Host "  .env written to: $envFile"
}
Write-Host '  Next steps:'
Write-Host '    1. Upload secrets to GitHub Actions (commands above)'
Write-Host '    2. Run: docker compose -f deploy/docker/docker-compose.yaml up -d'
Write-Host '    3. After first login, set HM_BOOTSTRAP_ADMIN_ENABLED=false in .env'
Write-Host '       then restart: docker compose restart auth'
Write-Host ''
