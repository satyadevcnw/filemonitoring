namespace FileMonitorService.Configuration;

/// <summary>
/// Configuration for the file monitoring service, bound from appsettings.json.
/// </summary>
public sealed class MonitorSettings
{
    public const string SectionName = "MonitorSettings";

    /// <summary>
    /// Local paths to monitor for incoming files FROM the file server.
    /// Should be user-visible directories only (Desktop, Documents, Downloads).
    /// Do NOT use broad paths like "C:\Users" — that catches all app activity.
    /// </summary>
    public List<string> LocalPaths { get; set; } = new();

    /// <summary>
    /// UNC paths for file server shares to monitor (e.g., "\\fileserver\share").
    /// Files appearing here are classified as "Local to File Server".
    /// </summary>
    public List<string> FileServerPaths { get; set; } = new();

    /// <summary>
    /// Domain\Username or Username to authenticate to file server shares.
    /// Required when the service runs as LocalSystem (default for Windows Services).
    /// Example: "CONTOSO\svc_filemonitor" or "administrator"
    /// If empty, the service relies on the service account's own credentials.
    /// </summary>
    public string FileServerUsername { get; set; } = string.Empty;

    /// <summary>
    /// Password for the file server account. Required if FileServerUsername is set.
    /// For production, consider using Windows Credential Manager or DPAPI instead.
    /// </summary>
    public string FileServerPassword { get; set; } = string.Empty;

    /// <summary>
    /// File extensions to monitor. Empty list means all files.
    /// Example: [".docx", ".xlsx", ".pdf", ".txt"]
    /// </summary>
    public List<string> FileExtensionFilter { get; set; } = new();

    /// <summary>
    /// Minimum file size in bytes to log. Files smaller than this are ignored.
    /// Default is 1 (ignore zero-byte files).
    /// </summary>
    public long MinimumFileSizeBytes { get; set; } = 1;

    /// <summary>
    /// Paths that should be excluded from monitoring. Any file created under these
    /// directories will be silently ignored. Supports partial matching.
    /// </summary>
    public List<string> ExcludedPaths { get; set; } = new();

    /// <summary>
    /// File name patterns to exclude (e.g., "~$*", "*.tmp", "*.crdownload").
    /// Uses simple suffix/prefix matching.
    /// </summary>
    public List<string> ExcludedFilePatterns { get; set; } = new();

    /// <summary>
    /// If true, uses the Windows Restart Manager API to check which process created
    /// the file and only allows known file managers (explorer.exe, robocopy, etc.).
    /// Recommended: false. The FileServerVerifier provides better filtering without
    /// the issues caused by the Restart Manager (e.g., blocking svchost/SMB copies).
    /// </summary>
    public bool OnlyUserInitiatedCopies { get; set; } = false;

    /// <summary>
    /// Path to write CSV log files.
    /// </summary>
    public string CsvLogPath { get; set; } = @"C:\ProgramData\FileMonitorService\Logs";

    /// <summary>
    /// Whether to write events to the Windows Event Log.
    /// </summary>
    public bool WriteToWindowsEventLog { get; set; } = true;

    /// <summary>
    /// Windows Event Log source name.
    /// </summary>
    public string EventLogSource { get; set; } = "FileMonitorService";

    /// <summary>
    /// Windows Event Log name.
    /// </summary>
    public string EventLogName { get; set; } = "FileMonitorService";

    /// <summary>
    /// Include subdirectories when monitoring paths.
    /// </summary>
    public bool IncludeSubdirectories { get; set; } = true;

    /// <summary>
    /// Debounce interval in milliseconds to avoid duplicate events from the same file operation.
    /// </summary>
    public int DebounceIntervalMs { get; set; } = 2000;
}
