<#
.SYNOPSIS
    Stops all running HomeManagement processes (Agent + GUI).

.DESCRIPTION
    Reads the saved Agent PID from .agent.pid and terminates it.
    Also finds and stops any orphaned HomeManagement.Gui or HomeManagement.Agent processes.

.EXAMPLE
    .\stop.ps1
#>

$ErrorActionPreference = 'SilentlyContinue'
$root = $PSScriptRoot
$pidFile = Join-Path $root '.agent.pid'

Write-Host '========================================' -ForegroundColor Cyan
Write-Host '  HomeManagement — Stop' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

$stopped = 0

# ── Stop Agent by saved PID ──
if (Test-Path $pidFile) {
    $savedPid = [int](Get-Content $pidFile -Raw).Trim()
    $proc = Get-Process -Id $savedPid -ErrorAction SilentlyContinue
    if ($proc -and -not $proc.HasExited) {
        Write-Host "Stopping Agent (PID: $savedPid)..." -ForegroundColor Yellow
        Stop-Process -Id $savedPid -Force
        $stopped++
    }
    Remove-Item $pidFile -Force
}

# ── Clean up any orphaned processes ──
$orphans = Get-Process | Where-Object {
    $_.ProcessName -match 'HomeManagement\.(Gui|Agent)' -or
    ($_.ProcessName -eq 'dotnet' -and (
        $_.CommandLine -match 'HomeManagement\.Gui' -or
        $_.CommandLine -match 'HomeManagement\.Agent'
    ))
} -ErrorAction SilentlyContinue

foreach ($orphan in $orphans) {
    if (-not $orphan.HasExited) {
        Write-Host "Stopping orphaned process: $($orphan.ProcessName) (PID: $($orphan.Id))..." -ForegroundColor Yellow
        Stop-Process -Id $orphan.Id -Force
        $stopped++
    }
}

if ($stopped -eq 0) {
    Write-Host 'No HomeManagement processes found.' -ForegroundColor DarkGray
} else {
    Write-Host ''
    Write-Host "Stopped $stopped process(es)." -ForegroundColor Green
}
