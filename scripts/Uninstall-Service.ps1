#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Uninstalls the File Monitor Service.

.PARAMETER RemoveLogs
    If specified, also removes the log directory.

.EXAMPLE
    .\Uninstall-Service.ps1
    .\Uninstall-Service.ps1 -RemoveLogs
#>

param(
    [switch]$RemoveLogs
)

$ErrorActionPreference = "Stop"
$ServiceName = "FileMonitorService"
$InstallPath = "C:\Program Files\FileMonitorService"
$LogPath = "C:\ProgramData\FileMonitorService"

Write-Host "=== File Monitor Service Uninstaller ===" -ForegroundColor Cyan

# Stop and remove service
Write-Host "`n[1/3] Stopping and removing service..." -ForegroundColor Yellow
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Write-Host "  Service removed" -ForegroundColor Green
} else {
    Write-Host "  Service not found (already removed)" -ForegroundColor Green
}

# Remove Event Log source
Write-Host "`n[2/3] Removing Event Log source..." -ForegroundColor Yellow
try {
    if ([System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
        [System.Diagnostics.EventLog]::DeleteEventSource($ServiceName)
        Write-Host "  Event Log source removed" -ForegroundColor Green
    } else {
        Write-Host "  Event Log source not found" -ForegroundColor Green
    }
} catch {
    Write-Warning "  Could not remove Event Log source: $_"
}

# Remove files
Write-Host "`n[3/3] Removing files..." -ForegroundColor Yellow
if (Test-Path $InstallPath) {
    Remove-Item -Path $InstallPath -Recurse -Force
    Write-Host "  Removed: $InstallPath" -ForegroundColor Green
}

if ($RemoveLogs -and (Test-Path $LogPath)) {
    Remove-Item -Path $LogPath -Recurse -Force
    Write-Host "  Removed: $LogPath" -ForegroundColor Green
} elseif (Test-Path $LogPath) {
    Write-Host "  Logs preserved at: $LogPath (use -RemoveLogs to remove)" -ForegroundColor Yellow
}

Write-Host "`n=== Uninstall Complete ===" -ForegroundColor Cyan
