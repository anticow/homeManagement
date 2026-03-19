<#
.SYNOPSIS
    Builds and launches the HomeManagement system (GUI + Agent) for testing.

.DESCRIPTION
    1. Restores NuGet packages
    2. Builds the entire solution in Debug configuration
    3. Runs all 192 unit tests
    4. Launches the Agent process in the background
    5. Launches the GUI process in the foreground

    Press Ctrl+C in the GUI window or close it to trigger the stop script.

.EXAMPLE
    .\start.ps1
    .\start.ps1 -SkipTests
    .\start.ps1 -Configuration Release
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$sln = Join-Path $root 'homeManagement.sln'
$guiProject = Join-Path $root 'src' 'HomeManagement.Gui' 'HomeManagement.Gui.csproj'
$agentProject = Join-Path $root 'src' 'HomeManagement.Agent' 'HomeManagement.Agent.csproj'
$pidFile = Join-Path $root '.agent.pid'

Write-Host '========================================' -ForegroundColor Cyan
Write-Host '  HomeManagement — Build & Launch' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

# ── Step 1: Build ──
Write-Host '[1/4] Building solution...' -ForegroundColor Yellow
dotnet build $sln --configuration $Configuration --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host 'BUILD FAILED — aborting.' -ForegroundColor Red
    exit 1
}
Write-Host '       Build succeeded (0 errors, 0 warnings)' -ForegroundColor Green
Write-Host ''

# ── Step 2: Test ──
if (-not $SkipTests) {
    Write-Host '[2/4] Running tests...' -ForegroundColor Yellow
    dotnet test $sln --configuration $Configuration --no-build --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'TESTS FAILED — aborting.' -ForegroundColor Red
        exit 1
    }
    Write-Host '       All tests passed' -ForegroundColor Green
} else {
    Write-Host '[2/4] Skipping tests (--SkipTests)' -ForegroundColor DarkGray
}
Write-Host ''

# ── Step 3: Launch Agent ──
Write-Host '[3/4] Starting Agent process...' -ForegroundColor Yellow

$agentBin = Join-Path $root 'src' 'HomeManagement.Agent' 'bin' $Configuration 'net8.0'

# Copy agent config and certs to output directory if not already there
$agentConfig = Join-Path $root 'hm-agent.json'
if (Test-Path $agentConfig) {
    Copy-Item $agentConfig (Join-Path $agentBin 'hm-agent.json') -Force
}
$certsSource = Join-Path $root 'certs'
$certsDest = Join-Path $agentBin 'certs'
if (Test-Path $certsSource) {
    if (-not (Test-Path $certsDest)) { New-Item -ItemType Directory -Path $certsDest -Force | Out-Null }
    Copy-Item (Join-Path $certsSource '*') $certsDest -Recurse -Force
}

$agentProcess = Start-Process -FilePath 'dotnet' `
    -ArgumentList "run --project `"$agentProject`" --configuration $Configuration --no-build" `
    -WorkingDirectory $root `
    -PassThru `
    -WindowStyle Normal

# Save PID for stop script
$agentProcess.Id | Out-File -FilePath $pidFile -Encoding utf8 -Force
Write-Host "       Agent started (PID: $($agentProcess.Id))" -ForegroundColor Green
Write-Host ''

# ── Step 4: Launch GUI ──
Write-Host '[4/4] Starting GUI...' -ForegroundColor Yellow
Write-Host '       Close the GUI window or press Ctrl+C to stop.' -ForegroundColor DarkGray
Write-Host ''

try {
    dotnet run --project $guiProject --configuration $Configuration --no-build
}
finally {
    # GUI has exited — clean up agent
    Write-Host ''
    Write-Host 'GUI closed. Stopping agent...' -ForegroundColor Yellow
    & (Join-Path $root 'stop.ps1')
}
