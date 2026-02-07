#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the File Monitor Service as a tamper-protected Windows Service.

.DESCRIPTION
    This script publishes the .NET application and registers it as a Windows Service.
    It also:
    - Creates the required Windows Event Log source and log directory
    - Locks down the service so regular users CANNOT stop, pause, or modify it
    - Configures automatic restart on failure
    - Sets file permissions so regular users cannot tamper with the binaries or config

.PARAMETER ServiceAccount
    The domain account to run the service under (e.g., "DOMAIN\ServiceAccount").
    If not specified, the service runs as LocalSystem.
    Recommended: Use a domain account that has read access to the file server share.

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
$Description = "Monitors file copy events between local machines and file servers for auditing. Protected — only Administrators can manage this service."
$ProjectPath = Join-Path $PSScriptRoot "..\src\FileMonitorService\FileMonitorService.csproj"
$LogPath = "C:\ProgramData\FileMonitorService\Logs"

Write-Host "=== File Monitor Service Installer ===" -ForegroundColor Cyan

# Step 1: Create log directory
Write-Host "`n[1/7] Creating log directory..." -ForegroundColor Yellow
if (-not (Test-Path $LogPath)) {
    New-Item -ItemType Directory -Path $LogPath -Force | Out-Null
    Write-Host "  Created: $LogPath" -ForegroundColor Green
} else {
    Write-Host "  Already exists: $LogPath" -ForegroundColor Green
}

# Step 2: Create Windows Event Log source
Write-Host "`n[2/7] Registering Windows Event Log source..." -ForegroundColor Yellow
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
Write-Host "`n[3/7] Publishing application..." -ForegroundColor Yellow
dotnet publish $ProjectPath -c Release -o $InstallPath --self-contained -r win-x64
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}
Write-Host "  Published to: $InstallPath" -ForegroundColor Green

# Step 4: Copy configuration
Write-Host "`n[4/7] Checking configuration..." -ForegroundColor Yellow
$configSource = Join-Path $PSScriptRoot "..\src\FileMonitorService\appsettings.json"
$configDest = Join-Path $InstallPath "appsettings.json"
if (-not (Test-Path $configDest)) {
    Copy-Item $configSource $configDest
    Write-Host "  Copied default appsettings.json" -ForegroundColor Green
    Write-Host "  IMPORTANT: Edit $configDest to set FileServerUsername and FileServerPassword" -ForegroundColor Red
} else {
    Write-Host "  appsettings.json already exists (keeping existing config)" -ForegroundColor Green
}

# Step 5: Install Windows Service
Write-Host "`n[5/7] Installing Windows Service..." -ForegroundColor Yellow
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
# Reset failure count after 1 day, restart after 30s / 60s / 120s
sc.exe failure $ServiceName reset= 86400 actions= restart/30000/restart/60000/restart/120000 | Out-Null

# Step 6: Lock down the service — prevent non-admin users from stopping it
Write-Host "`n[6/7] Applying service security (tamper protection)..." -ForegroundColor Yellow

# SDDL breakdown:
#   D:                          — DACL (Discretionary Access Control List)
#   (A;;CCLCSWRPWPDTLOCRRC;;;SY) — SYSTEM: full control
#   (A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA) — Administrators: full control
#   (A;;CCLCSWLOCRRC;;;IU)      — Interactive Users: query status only (NO stop/start/pause)
#   (A;;CCLCSWLOCRRC;;;SU)      — Service Users: query status only
#
# What regular users CAN do:   View service status in services.msc
# What regular users CANNOT do: Stop, Start, Pause, Modify, Delete the service
$sddl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)"
$result = sc.exe sdset $ServiceName $sddl
if ($LASTEXITCODE -eq 0) {
    Write-Host "  Service security applied — only Administrators can stop/modify" -ForegroundColor Green
} else {
    Write-Warning "  Failed to set service security: $result"
}

# Prevent service from being killed via Task Manager by non-admins
# (This is automatic when running as SYSTEM, but let's also set the
#  PreShutdownInfo to give the service time to clean up)
sc.exe preshutdown $ServiceName 10000 2>$null | Out-Null

# Step 7: Lock down file permissions — prevent users from modifying binaries or config
Write-Host "`n[7/7] Securing installation files..." -ForegroundColor Yellow
try {
    $acl = Get-Acl $InstallPath

    # Remove inherited permissions
    $acl.SetAccessRuleProtection($true, $false)

    # SYSTEM: Full Control
    $systemRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        "NT AUTHORITY\SYSTEM", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.AddAccessRule($systemRule)

    # Administrators: Full Control
    $adminRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        "BUILTIN\Administrators", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.AddAccessRule($adminRule)

    # Regular Users: Read & Execute only (can't modify config or replace binaries)
    $usersRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        "BUILTIN\Users", "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.AddAccessRule($usersRule)

    Set-Acl $InstallPath $acl
    Write-Host "  File permissions secured — users have read-only access" -ForegroundColor Green
} catch {
    Write-Warning "  Could not set file permissions: $_"
}

# Also secure the log directory
try {
    $logAcl = Get-Acl $LogPath

    $logAcl.SetAccessRuleProtection($true, $false)

    $logSystemRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        "NT AUTHORITY\SYSTEM", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
    $logAcl.AddAccessRule($logSystemRule)

    $logAdminRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        "BUILTIN\Administrators", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
    $logAcl.AddAccessRule($logAdminRule)

    # Users can read logs but not delete or modify them
    $logUsersRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        "BUILTIN\Users", "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")
    $logAcl.AddAccessRule($logUsersRule)

    Set-Acl $LogPath $logAcl
    Write-Host "  Log directory permissions secured" -ForegroundColor Green
} catch {
    Write-Warning "  Could not set log directory permissions: $_"
}

# Start the service
Start-Service -Name $ServiceName
Write-Host "`n  Service installed and started!" -ForegroundColor Green

Write-Host "`n=== Installation Complete ===" -ForegroundColor Cyan
Write-Host "Service Name:   $ServiceName"
Write-Host "Install Path:   $InstallPath"
Write-Host "Log Path:       $LogPath"
Write-Host "Config:         $configDest"
Write-Host ""
Write-Host "Security:" -ForegroundColor Cyan
Write-Host "  - Regular users CANNOT stop, pause, or modify the service"
Write-Host "  - Regular users CANNOT edit config or replace binaries"
Write-Host "  - Regular users CAN view service status"
Write-Host "  - Service auto-restarts on failure (30s / 60s / 120s)"
Write-Host "  - Only Administrators can manage the service"
Write-Host ""
Write-Host "Edit $configDest to configure monitored paths and file server credentials."
Write-Host "Then restart: Restart-Service $ServiceName"
