<#
.SYNOPSIS
    Generates mTLS certificates for the HomeManagement Agent infrastructure.

.DESCRIPTION
    Creates a private CA (if not already present) and issues:
    - A server certificate for the AgentGateway (server.pfx)
    - An agent certificate for each agent identity (agent.pfx)

    All certificates are written to the certs/ directory at the repository root.
    Existing CA files are reused; existing server/agent certs are regenerated
    unless -NoClobber is specified.

.EXAMPLE
    .\New-AgentCert.ps1
    .\New-AgentCert.ps1 -AgentId "agent-worker1" -ServerSAN "agentgw.cowgomu.net"
    .\New-AgentCert.ps1 -NoClobber
    .\New-AgentCert.ps1 -CertValidityDays 730

.PARAMETER AgentId
    The agent identity embedded in the agent certificate CN. Default: "hm-agent".

.PARAMETER ServerSAN
    Subject Alternative Name for the server cert. Default: "localhost".
    Pass the production hostname (e.g., "agentgw.cowgomu.net") for K8s deployments.

.PARAMETER CertValidityDays
    Validity period for issued certificates in days. Default: 365.

.PARAMETER CaValidityDays
    Validity period for the CA certificate in days. Default: 3650 (10 years).

.PARAMETER Password
    PFX export password. Default: empty string (no password).

.PARAMETER NoClobber
    Skip regeneration if the target certificate already exists.
#>
param(
    [string]$AgentId = 'hm-agent',
    [string]$ServerSAN = 'localhost',
    [int]$CertValidityDays = 365,
    [int]$CaValidityDays = 3650,
    [string]$Password = '',
    [switch]$NoClobber
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$certsDir = Join-Path $PSScriptRoot '..' 'certs'
if (-not (Test-Path $certsDir)) {
    New-Item -ItemType Directory -Path $certsDir -Force | Out-Null
}
$certsDir = (Resolve-Path $certsDir).Path

$caKey    = Join-Path $certsDir 'ca.key'
$caCert   = Join-Path $certsDir 'ca.crt'
$srvKey   = Join-Path $certsDir 'server.key'
$srvCert  = Join-Path $certsDir 'server.crt'
$srvPfx   = Join-Path $certsDir 'server.pfx'
$agentKey  = Join-Path $certsDir 'agent.key'
$agentCert = Join-Path $certsDir 'agent.crt'
$agentPfx  = Join-Path $certsDir 'agent.pfx'

# Verify openssl is available
if (-not (Get-Command openssl -ErrorAction SilentlyContinue)) {
    Write-Host 'ERROR: openssl is required but not found on PATH.' -ForegroundColor Red
    Write-Host 'Install OpenSSL or use Git Bash (openssl ships with Git for Windows).' -ForegroundColor Yellow
    exit 1
}

Write-Host '========================================' -ForegroundColor Cyan
Write-Host '  HomeManagement — Agent mTLS Certs' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

# ── Step 1: CA ──
if ((Test-Path $caKey) -and (Test-Path $caCert)) {
    Write-Host '[CA] Reusing existing CA certificate' -ForegroundColor Green
} else {
    Write-Host '[CA] Generating private CA...' -ForegroundColor Yellow
    openssl req -x509 -newkey rsa:4096 -sha256 -days $CaValidityDays `
        -nodes -keyout $caKey -out $caCert `
        -subj "/CN=HomeManagement Agent CA/O=HomeManagement"
    if ($LASTEXITCODE -ne 0) { throw 'CA generation failed' }
    Write-Host "[CA] Created: $caCert (valid $CaValidityDays days)" -ForegroundColor Green
}
Write-Host ''

# ── Step 2: Server certificate ──
if ($NoClobber -and (Test-Path $srvPfx)) {
    Write-Host '[Server] Skipping — server.pfx already exists (-NoClobber)' -ForegroundColor DarkGray
} else {
    Write-Host "[Server] Generating server certificate for SAN=$ServerSAN..." -ForegroundColor Yellow

    $srvExt = Join-Path $certsDir 'server.ext'
    @"
authorityKeyIdentifier=keyid,issuer
basicConstraints=CA:FALSE
keyUsage=digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=@alt_names

[alt_names]
DNS.1=$ServerSAN
DNS.2=localhost
IP.1=127.0.0.1
"@ | Set-Content -Path $srvExt -Encoding UTF8

    openssl req -newkey rsa:2048 -nodes -keyout $srvKey `
        -subj "/CN=$ServerSAN/O=HomeManagement" -out (Join-Path $certsDir 'server.csr')
    if ($LASTEXITCODE -ne 0) { throw 'Server CSR generation failed' }

    openssl x509 -req -in (Join-Path $certsDir 'server.csr') `
        -CA $caCert -CAkey $caKey -CAcreateserial `
        -days $CertValidityDays -sha256 -extfile $srvExt -out $srvCert
    if ($LASTEXITCODE -ne 0) { throw 'Server cert signing failed' }

    openssl pkcs12 -export -out $srvPfx `
        -inkey $srvKey -in $srvCert -certfile $caCert `
        -passout "pass:$Password"
    if ($LASTEXITCODE -ne 0) { throw 'Server PFX export failed' }

    # Clean up intermediate files
    Remove-Item -Path (Join-Path $certsDir 'server.csr'), $srvExt -ErrorAction SilentlyContinue

    Write-Host "[Server] Created: $srvPfx (valid $CertValidityDays days)" -ForegroundColor Green
}
Write-Host ''

# ── Step 3: Agent certificate ──
if ($NoClobber -and (Test-Path $agentPfx)) {
    Write-Host '[Agent] Skipping — agent.pfx already exists (-NoClobber)' -ForegroundColor DarkGray
} else {
    Write-Host "[Agent] Generating agent certificate for ID=$AgentId..." -ForegroundColor Yellow

    $agentExt = Join-Path $certsDir 'agent.ext'
    @"
authorityKeyIdentifier=keyid,issuer
basicConstraints=CA:FALSE
keyUsage=digitalSignature,keyEncipherment
extendedKeyUsage=clientAuth
"@ | Set-Content -Path $agentExt -Encoding UTF8

    openssl req -newkey rsa:2048 -nodes -keyout $agentKey `
        -subj "/CN=$AgentId/O=HomeManagement" -out (Join-Path $certsDir 'agent.csr')
    if ($LASTEXITCODE -ne 0) { throw 'Agent CSR generation failed' }

    openssl x509 -req -in (Join-Path $certsDir 'agent.csr') `
        -CA $caCert -CAkey $caKey -CAcreateserial `
        -days $CertValidityDays -sha256 -extfile $agentExt -out $agentCert
    if ($LASTEXITCODE -ne 0) { throw 'Agent cert signing failed' }

    openssl pkcs12 -export -out $agentPfx `
        -inkey $agentKey -in $agentCert -certfile $caCert `
        -passout "pass:$Password"
    if ($LASTEXITCODE -ne 0) { throw 'Agent PFX export failed' }

    # Clean up intermediate files
    Remove-Item -Path (Join-Path $certsDir 'agent.csr'), $agentExt -ErrorAction SilentlyContinue

    Write-Host "[Agent] Created: $agentPfx (valid $CertValidityDays days)" -ForegroundColor Green
}

Write-Host ''
Write-Host '========================================' -ForegroundColor Green
Write-Host '  Certificate generation complete' -ForegroundColor Green
Write-Host '========================================' -ForegroundColor Green
Write-Host ''
Write-Host "  CA cert:       $caCert" -ForegroundColor White
Write-Host "  Server PFX:    $srvPfx  (SAN: $ServerSAN)" -ForegroundColor White
Write-Host "  Agent PFX:     $agentPfx  (CN: $AgentId)" -ForegroundColor White
Write-Host ''
Write-Host '  Certificate structure:' -ForegroundColor DarkGray
Write-Host '    CA (ca.crt/ca.key)' -ForegroundColor DarkGray
Write-Host '     ├── Server (server.pfx) — loaded by AgentGateway.Host' -ForegroundColor DarkGray
Write-Host '     └── Agent  (agent.pfx)  — loaded by each HomeManagement.Agent' -ForegroundColor DarkGray
Write-Host ''
