#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the File Monitor Service as a Windows Service.

.DESCRIPTION
    This script publishes the .NET application and registers it as a Windows Service.
    It also creates the required Windows Event Log source and log directory.

.PARAMETER ServiceAccount
    The domain account to run the service under (e.g., "DOMAIN\ServiceAccount").
    If not specified, the service runs as LocalSystem.

.PARAMETER InstallPath
    The installation directory for the service binaries.
    Default: C:\Program Files\FileMonitorService

.EXAMPLE
    .\Install-Service.ps1
    .\Install-Service.ps1 -ServiceAccount "CONTOSO\svc_filemonitor"
#>

param(
    [string]$ServiceAccount,
    [string]$InstallPath = "C:\Program Files\FileMonitorService"
)

$ErrorActionPreference = "Stop"
$ServiceName = "FileMonitorService"
$DisplayName = "File Monitor Service"
$Description = "Monitors file copy events between local machines and file servers for auditing."
$ProjectPath = Join-Path $PSScriptRoot "..\src\FileMonitorService\FileMonitorService.csproj"
$LogPath = "C:\ProgramData\FileMonitorService\Logs"

Write-Host "=== File Monitor Service Installer ===" -ForegroundColor Cyan

# Step 1: Create log directory
Write-Host "`n[1/5] Creating log directory..." -ForegroundColor Yellow
if (-not (Test-Path $LogPath)) {
    New-Item -ItemType Directory -Path $LogPath -Force | Out-Null
    Write-Host "  Created: $LogPath" -ForegroundColor Green
} else {
    Write-Host "  Already exists: $LogPath" -ForegroundColor Green
}

# Step 2: Create Windows Event Log source
Write-Host "`n[2/5] Registering Windows Event Log source..." -ForegroundColor Yellow
try {
    if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
        [System.Diagnostics.EventLog]::CreateEventSource($ServiceName, $ServiceName)
        Write-Host "  Created Event Log source: $ServiceName" -ForegroundColor Green
    } else {
        Write-Host "  Event Log source already exists: $ServiceName" -ForegroundColor Green
    }
} catch {
    Write-Warning "  Could not create Event Log source: $_"
}

# Step 3: Publish the application
Write-Host "`n[3/5] Publishing application..." -ForegroundColor Yellow
dotnet publish $ProjectPath -c Release -o $InstallPath --self-contained -r win-x64
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}
Write-Host "  Published to: $InstallPath" -ForegroundColor Green

# Step 4: Copy configuration
Write-Host "`n[4/5] Checking configuration..." -ForegroundColor Yellow
$configSource = Join-Path $PSScriptRoot "..\src\FileMonitorService\appsettings.json"
$configDest = Join-Path $InstallPath "appsettings.json"
if (-not (Test-Path $configDest)) {
    Copy-Item $configSource $configDest
    Write-Host "  Copied default appsettings.json" -ForegroundColor Green
} else {
    Write-Host "  appsettings.json already exists (keeping existing config)" -ForegroundColor Green
}

# Step 5: Install Windows Service
Write-Host "`n[5/5] Installing Windows Service..." -ForegroundColor Yellow
$exePath = Join-Path $InstallPath "FileMonitorService.exe"

# Stop existing service if running
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "  Stopping existing service..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# Create the service
if ($ServiceAccount) {
    $securePassword = Read-Host "Enter password for $ServiceAccount" -AsSecureString
    $credential = New-Object System.Management.Automation.PSCredential($ServiceAccount, $securePassword)
    New-Service -Name $ServiceName `
                -BinaryPathName $exePath `
                -DisplayName $DisplayName `
                -Description $Description `
                -StartupType Automatic `
                -Credential $credential
} else {
    New-Service -Name $ServiceName `
                -BinaryPathName $exePath `
                -DisplayName $DisplayName `
                -Description $Description `
                -StartupType Automatic
}

# Configure service recovery (restart on failure)
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

# Start the service
Start-Service -Name $ServiceName
Write-Host "  Service installed and started!" -ForegroundColor Green

Write-Host "`n=== Installation Complete ===" -ForegroundColor Cyan
Write-Host "Service Name:   $ServiceName"
Write-Host "Install Path:   $InstallPath"
Write-Host "Log Path:       $LogPath"
Write-Host "Config:         $configDest"
Write-Host "`nEdit $configDest to configure monitored paths, then restart the service."
