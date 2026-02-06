namespace FileMonitorService.Configuration;

/// <summary>
/// Configuration for the file monitoring service, bound from appsettings.json.
/// </summary>
public sealed class MonitorSettings
{
    public const string SectionName = "MonitorSettings";

    /// <summary>
    /// Local paths to monitor (e.g., "C:\Users", "D:\SharedDocs").
    /// </summary>
    public List<string> LocalPaths { get; set; } = new();

    /// <summary>
    /// UNC paths for file server shares to monitor (e.g., "\\fileserver\share").
    /// </summary>
    public List<string> FileServerPaths { get; set; } = new();

    /// <summary>
    /// File extensions to monitor. Empty list means all files.
    /// Example: [".docx", ".xlsx", ".pdf", ".txt"]
    /// </summary>
    public List<string> FileExtensionFilter { get; set; } = new();

    /// <summary>
    /// Minimum file size in bytes to log. Files smaller than this are ignored.
    /// Default is 0 (log all sizes).
    /// </summary>
    public long MinimumFileSizeBytes { get; set; } = 0;

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
