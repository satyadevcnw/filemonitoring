# File Monitor Service

A Windows Service that monitors file copy events between local user computers and shared file servers in an Active Directory environment. It logs the AD username, transfer direction, destination path, and timestamp for each detected file transfer.

## Event Log Fields

Each captured event includes:

| Field             | Description                                          | Example                          |
|-------------------|------------------------------------------------------|----------------------------------|
| **Time**          | Timestamp of the event                               | `2026-02-06 14:32:15`            |
| **User**          | AD domain\username of the logged-in user             | `CONTOSO\jsmith`                 |
| **EventType**     | Direction of transfer                                | `Local to File Server` or `File Server to Local` |
| **DestinationPath** | Full path where the file was written              | `\\fileserver\shared\report.xlsx`|
| **FileName**      | Name of the file transferred                         | `report.xlsx`                    |
| **FileSizeBytes** | Size of the file in bytes                            | `245760`                         |
| **MachineName**   | Computer name where the service is running           | `WORKSTATION01`                  |

## Architecture

```
┌──────────────────────────────────────────────────────┐
│               FileMonitorWorker                       │
│         (BackgroundService / Windows Service)         │
│                                                       │
│  ┌─────────────────┐    ┌──────────────────────┐     │
│  │ FileSystemWatcher│    │  FileSystemWatcher    │     │
│  │  (Local Paths)  │    │  (File Server Paths) │     │
│  └────────┬────────┘    └──────────┬───────────┘     │
│           │                        │                  │
│           └────────┬───────────────┘                  │
│                    ▼                                  │
│           PathClassifier                              │
│      (Determine transfer direction)                   │
│                    │                                  │
│                    ▼                                  │
│         UserIdentityService                           │
│    (Resolve AD/Windows username)                      │
│                    │                                  │
│           ┌───────┴────────┐                         │
│           ▼                ▼                          │
│    EventLogService    CsvLogService                   │
│  (Windows Event Log)  (CSV files)                     │
└──────────────────────────────────────────────────────┘
```

**How detection works:**
- A `FileSystemWatcher` on a **file server path** detects new files → classified as **Local to File Server**
- A `FileSystemWatcher` on a **local path** detects new files → classified as **File Server to Local**
- The user is resolved from the file's ACL owner or the current Windows/AD identity

## Prerequisites

- Windows 10/11 or Windows Server 2016+
- .NET 8 SDK (for building) or self-contained deployment
- Active Directory domain environment
- Administrator privileges (for service installation)

## Quick Start

### 1. Configure Monitored Paths

Edit `src/FileMonitorService/appsettings.json`:

```json
{
  "MonitorSettings": {
    "LocalPaths": [
      "C:\\Users"
    ],
    "FileServerPaths": [
      "\\\\fileserver\\shared",
      "\\\\fileserver\\departments"
    ],
    "FileExtensionFilter": [],
    "MinimumFileSizeBytes": 0,
    "IncludeSubdirectories": true,
    "DebounceIntervalMs": 2000
  }
}
```

### 2. Install as Windows Service

Run PowerShell as Administrator:

```powershell
.\scripts\Install-Service.ps1
```

To run under a specific AD service account:

```powershell
.\scripts\Install-Service.ps1 -ServiceAccount "CONTOSO\svc_filemonitor"
```

### 3. Verify

Check the service is running:

```powershell
Get-Service FileMonitorService
```

View events in Windows Event Viewer under **Applications and Services Logs > FileMonitorService**, or check CSV logs at:

```
C:\ProgramData\FileMonitorService\Logs\file-transfers-2026-02-06.csv
```

## Configuration Reference

| Setting                 | Type       | Default                                    | Description                              |
|-------------------------|------------|--------------------------------------------|------------------------------------------|
| `LocalPaths`            | `string[]` | `["C:\\Users"]`                            | Local directories to monitor             |
| `FileServerPaths`       | `string[]` | `["\\\\fileserver\\shared"]`               | UNC file server shares to monitor        |
| `FileExtensionFilter`   | `string[]` | `[]` (all files)                           | Restrict to specific extensions           |
| `MinimumFileSizeBytes`  | `long`     | `0`                                        | Ignore files smaller than this            |
| `CsvLogPath`            | `string`   | `C:\ProgramData\FileMonitorService\Logs`   | Directory for CSV log files               |
| `WriteToWindowsEventLog`| `bool`     | `true`                                     | Write events to Windows Event Log         |
| `EventLogSource`        | `string`   | `FileMonitorService`                       | Event Log source name                     |
| `EventLogName`          | `string`   | `FileMonitorService`                       | Event Log name                            |
| `IncludeSubdirectories` | `bool`     | `true`                                     | Monitor subdirectories recursively        |
| `DebounceIntervalMs`    | `int`      | `2000`                                     | Interval to deduplicate events (ms)       |

## Development

### Build

```bash
cd src/FileMonitorService
dotnet build
```

### Run in Console Mode (for testing)

```bash
dotnet run --project src/FileMonitorService
```

The service runs as a console app when not installed as a Windows Service, writing logs to the terminal.

### Publish Self-Contained

```bash
dotnet publish src/FileMonitorService -c Release -o publish --self-contained -r win-x64
```

## Uninstall

```powershell
.\scripts\Uninstall-Service.ps1

# Also remove logs:
.\scripts\Uninstall-Service.ps1 -RemoveLogs
```

## Deployment Considerations

- **Service Account**: For monitoring network shares, the service account needs read access to the UNC paths. Use a dedicated AD service account rather than LocalSystem.
- **Multiple Machines**: Deploy the service to each workstation that needs monitoring. Each instance reports its own `MachineName`.
- **Performance**: `FileSystemWatcher` has an internal buffer (default 8KB). For very high-volume directories, consider increasing the buffer or narrowing the monitored paths.
- **Mapped Drives**: The service cannot monitor mapped drive letters (e.g., `Z:\`) because mapped drives are per-user session. Use UNC paths instead (e.g., `\\server\share`).

## CSV Log Format

Daily rotating CSV files with the following columns:

```csv
"Timestamp","UserName","EventType","SourcePath","DestinationPath","FileName","FileSizeBytes","MachineName"
"2026-02-06 14:32:15","CONTOSO\jsmith","Local to File Server","Local Machine","\\fileserver\shared\report.xlsx","report.xlsx",245760,"WORKSTATION01"
```
