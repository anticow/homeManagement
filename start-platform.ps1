<#
.SYNOPSIS
    Builds and launches the HomeManagement platform stack via Docker Compose.

.DESCRIPTION
    1. Builds all Docker images
    2. Starts the full platform stack (SQL Server, Seq, Broker, Auth, Web, Gateway, AgentGW)
    3. Waits for all services to become healthy
    4. Opens the Web UI in the default browser (unless -NoBrowser)

    Press Ctrl+C to shut down the stack gracefully.

.EXAMPLE
    .\start-platform.ps1
    .\start-platform.ps1 -NoBrowser
    .\start-platform.ps1 -Detach
#>
param(
    [switch]$NoBrowser,
    [switch]$Detach
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$composeFile = Join-Path $root 'deploy' 'docker' 'docker-compose.yaml'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Host 'ERROR: docker is not installed or not on PATH.' -ForegroundColor Red
    exit 1
}

Write-Host '========================================' -ForegroundColor Cyan
Write-Host '  HomeManagement — Platform Stack' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

# ── Build ──
Write-Host '[1/3] Building Docker images...' -ForegroundColor Yellow
docker compose -f $composeFile build
if ($LASTEXITCODE -ne 0) {
    Write-Host 'BUILD FAILED — aborting.' -ForegroundColor Red
    exit 1
}
Write-Host '       Images built successfully' -ForegroundColor Green
Write-Host ''

# ── Start ──
Write-Host '[2/3] Starting platform services...' -ForegroundColor Yellow
if ($Detach) {
    docker compose -f $composeFile up -d
} else {
    docker compose -f $composeFile up -d
}
if ($LASTEXITCODE -ne 0) {
    Write-Host 'STARTUP FAILED — aborting.' -ForegroundColor Red
    exit 1
}
Write-Host ''

# ── Wait for health ──
Write-Host '[3/3] Waiting for services to become healthy...' -ForegroundColor Yellow

$services = @(
    @{ Name = 'SQL Server';     Container = 'docker-sqlserver-1';  Url = $null }
    @{ Name = 'Seq';            Container = 'docker-seq-1';        Url = 'http://localhost:5380' }
    @{ Name = 'Broker';         Container = 'docker-broker-1';     Url = 'http://localhost:8082/healthz' }
    @{ Name = 'Auth';           Container = 'docker-auth-1';       Url = 'http://localhost:8083/healthz' }
    @{ Name = 'Gateway';        Container = 'docker-gateway-1';    Url = 'http://localhost:8081/healthz' }
    @{ Name = 'Web';            Container = 'docker-web-1';        Url = 'http://localhost:8080/healthz' }
    @{ Name = 'Agent Gateway';  Container = 'docker-agent-gw-1';   Url = 'http://localhost:9445/healthz' }
)

$timeout = 120
$start = Get-Date

foreach ($svc in $services) {
    if (-not $svc.Url) { continue }

    $ready = $false
    while (-not $ready) {
        $elapsed = ((Get-Date) - $start).TotalSeconds
        if ($elapsed -gt $timeout) {
            Write-Host "TIMEOUT: $($svc.Name) did not become healthy within ${timeout}s" -ForegroundColor Red
            Write-Host 'Run "docker compose -f deploy/docker/docker-compose.yaml logs" to inspect.' -ForegroundColor Yellow
            exit 1
        }
        try {
            $response = Invoke-WebRequest -Uri $svc.Url -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop
            if ($response.StatusCode -eq 200) {
                $ready = $true
                Write-Host "       $($svc.Name) — healthy" -ForegroundColor Green
            }
        } catch {
            Start-Sleep -Seconds 3
        }
    }
}

Write-Host ''
Write-Host '========================================' -ForegroundColor Green
Write-Host '  Platform is running!' -ForegroundColor Green
Write-Host '========================================' -ForegroundColor Green
Write-Host ''
Write-Host "  Web UI:         http://localhost:8080" -ForegroundColor White
Write-Host "  Gateway API:    http://localhost:8081" -ForegroundColor White
Write-Host "  Broker API:     http://localhost:8082" -ForegroundColor White
Write-Host "  Auth API:       http://localhost:8083" -ForegroundColor White
Write-Host "  Agent Gateway:  grpc://localhost:9444 (gRPC), http://localhost:9445 (API)" -ForegroundColor White
Write-Host "  Seq Logs:       http://localhost:5380" -ForegroundColor White
Write-Host "  SQL Server:     localhost:1433" -ForegroundColor White
Write-Host ''

if (-not $NoBrowser) {
    Start-Process 'http://localhost:8080'
}

if (-not $Detach) {
    Write-Host 'Press Ctrl+C to stop the stack...' -ForegroundColor DarkGray
    try {
        docker compose -f $composeFile logs -f
    }
    finally {
        Write-Host ''
        Write-Host 'Shutting down platform stack...' -ForegroundColor Yellow
        docker compose -f $composeFile down
        Write-Host 'Done.' -ForegroundColor Green
    }
}
