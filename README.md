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
┌────────────────────────────────────────────────────────────┐
│                   FileMonitorWorker                         │
│            (BackgroundService / Windows Service)            │
│                                                            │
│  ┌──────────────────┐    ┌────────────────────────┐       │
│  │ FileSystemWatcher │    │  FileSystemWatcher      │       │
│  │ (Desktop/Docs/DL) │    │  (\\server\share UNC)  │       │
│  └────────┬─────────┘    └──────────┬─────────────┘       │
│           │                         │                      │
│           └─────────┬───────────────┘                      │
│                     ▼                                      │
│   ┌─────────────────────────────────┐                     │
│   │   7-Layer Filter Pipeline       │                     │
│   │  1. Path exclusion (AppData)    │                     │
│   │  2. Debounce (dedup)            │                     │
│   │  3. Extension filter            │                     │
│   │  4. Directory check             │                     │
│   │  5. File size filter            │                     │
│   │  6. Process check (explorer?)   │  ← KEY FILTER      │
│   │  7. User check (skip SYSTEM)    │                     │
│   └────────────────┬────────────────┘                     │
│                    ▼                                       │
│    PathClassifier + UserIdentityService                    │
│                    │                                       │
│           ┌───────┴────────┐                              │
│           ▼                ▼                               │
│    EventLogService    CsvLogService                        │
│  (Windows Event Log)  (CSV files)                          │
└────────────────────────────────────────────────────────────┘
```

**How detection works:**
- A `FileSystemWatcher` on a **file server UNC path** detects new files → classified as **Local to File Server**
- A `FileSystemWatcher` on **specific local folders** (Desktop, Documents, Downloads) detects new files → classified as **File Server to Local**
- **Process filtering** uses the Windows Restart Manager API to check if `explorer.exe` (or another file manager) created the file — this eliminates noise from browsers, editors, and system processes
- **Path exclusion** blocks `AppData`, browser caches, temp files, `.git`, and other non-user directories
- **System account filtering** skips events from `NT AUTHORITY\SYSTEM` and service accounts
- The user is resolved from the file's ACL owner (returns `DOMAIN\Username` for AD users)

**Important: Why NOT to monitor `C:\Users` broadly:**
Watching `C:\Users` captures **all** file activity — Chrome writing preferences, VS Code saving state, Edge caching data, Windows Defender scanning — none of which are file server transfers. Always use specific user-visible folders.

## Prerequisites

- Windows 10/11 or Windows Server 2016+
- .NET 8 SDK (for building) or self-contained deployment
- Active Directory domain environment
- Administrator privileges (for service installation)

## Quick Start

### 1. Configure Monitored Paths

Edit `src/FileMonitorService/appsettings.json` — replace `Dell` with the actual username:

```json
{
  "MonitorSettings": {
    "LocalPaths": [
      "C:\\Users\\Dell\\Desktop",
      "C:\\Users\\Dell\\Documents",
      "C:\\Users\\Dell\\Downloads"
    ],
    "FileServerPaths": [
      "\\\\fileserver\\shared",
      "\\\\fileserver\\departments"
    ],
    "OnlyUserInitiatedCopies": true,
    "ExcludedFilePatterns": ["*.tmp", "*.TMP", "~$*", "*.crdownload"],
    "MinimumFileSizeBytes": 1,
    "IncludeSubdirectories": true
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

## Filtering System

The service applies 7 layers of filtering to eliminate false positives:

| Layer | Filter | What it blocks |
|-------|--------|---------------|
| 1 | **Path exclusion** | `AppData\`, `Local Settings\`, `.git\`, `$Recycle.Bin\`, browser caches |
| 2 | **Debounce** | Duplicate events for the same file within 2 seconds |
| 3 | **Extension filter** | Optional whitelist (e.g., only `.docx`, `.xlsx`, `.pdf`) |
| 4 | **Directory check** | Folder creation events (not file copies) |
| 5 | **File size** | Zero-byte and temp placeholder files |
| 6 | **Process check** | Files created by Chrome, Edge, VS Code, system services. Only allows `explorer.exe`, `robocopy`, `xcopy`, `cmd`, `powershell` |
| 7 | **User check** | Events from `NT AUTHORITY\SYSTEM`, `NT SERVICE\*` accounts |

## Configuration Reference

| Setting                 | Type       | Default                                    | Description                              |
|-------------------------|------------|--------------------------------------------|------------------------------------------|
| `LocalPaths`            | `string[]` | `["Desktop", "Documents", "Downloads"]`    | Specific local user directories to monitor |
| `FileServerPaths`       | `string[]` | `["\\\\fileserver\\shared"]`               | UNC file server shares to monitor        |
| `FileExtensionFilter`   | `string[]` | `[]` (all files)                           | Restrict to specific extensions           |
| `MinimumFileSizeBytes`  | `long`     | `1`                                        | Ignore files smaller than this            |
| `ExcludedPaths`         | `string[]` | `["\\AppData\\", ...]`                     | Path substrings to exclude                |
| `ExcludedFilePatterns`  | `string[]` | `["*.tmp", "~$*", ...]`                    | File name patterns to exclude             |
| `OnlyUserInitiatedCopies`| `bool`    | `true`                                     | Only log explorer.exe/robocopy events     |
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

- **Deploy on user workstations**, not the file server. The service needs to see both local and network paths.
- **Service Account**: For monitoring network shares, the service account needs read access to the UNC paths.
- **Multiple Users**: For machines with multiple user profiles, add each user's Desktop/Documents/Downloads to `LocalPaths`.
- **Mapped Drives**: The service cannot monitor mapped drive letters (e.g., `Z:\`) because mapped drives are per-user session. Use UNC paths instead (e.g., `\\server\share`).
- **Group Policy Deployment**: Push the service to domain workstations via GPO startup script.

## CSV Log Format

Daily rotating CSV files with the following columns:

```csv
"Timestamp","UserName","EventType","SourcePath","DestinationPath","FileName","FileSizeBytes","MachineName"
"2026-02-06 14:32:15","CONTOSO\jsmith","Local to File Server","Local Machine","\\fileserver\shared\report.xlsx","report.xlsx",245760,"WORKSTATION01"
"2026-02-06 14:35:22","CONTOSO\jsmith","File Server to Local","C:\Users\jsmith\Desktop","C:\Users\jsmith\Desktop\budget.xlsx","budget.xlsx",89600,"WORKSTATION01"
```
