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
    /// If true, only log events where the creating process is a known file manager
    /// (explorer.exe, robocopy, xcopy, etc.). This eliminates noise from browsers,
    /// editors, and system processes. Recommended: true.
    /// </summary>
    public bool OnlyUserInitiatedCopies { get; set; } = true;

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
