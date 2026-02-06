namespace FileMonitorService.Models;

/// <summary>
/// Represents the direction of a file transfer.
/// </summary>
public enum TransferDirection
{
    LocalToFileServer,
    FileServerToLocal
}

/// <summary>
/// Represents a single file copy/move event captured by the monitor.
/// </summary>
public sealed class FileTransferEvent
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string UserName { get; init; } = string.Empty;
    public TransferDirection EventType { get; init; }
    public string SourcePath { get; init; } = string.Empty;
    public string DestinationPath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public string MachineName { get; init; } = Environment.MachineName;

    public string EventTypeDisplay => EventType switch
    {
        TransferDirection.LocalToFileServer => "Local to File Server",
        TransferDirection.FileServerToLocal => "File Server to Local",
        _ => "Unknown"
    };

    public string ToCsvLine()
    {
        return $"\"{Timestamp:yyyy-MM-dd HH:mm:ss}\",\"{UserName}\",\"{EventTypeDisplay}\",\"{SourcePath}\",\"{DestinationPath}\",\"{FileName}\",{FileSizeBytes},\"{MachineName}\"";
    }

    public override string ToString()
    {
        return $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] User={UserName} | {EventTypeDisplay} | File={FileName} | Destination={DestinationPath}";
    }
}
