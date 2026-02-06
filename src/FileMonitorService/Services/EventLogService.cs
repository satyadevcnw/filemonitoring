using System.Diagnostics;
using FileMonitorService.Configuration;
using FileMonitorService.Models;
using Microsoft.Extensions.Options;

namespace FileMonitorService.Services;

/// <summary>
/// Writes file transfer events to the Windows Event Log.
/// </summary>
public sealed class EventLogService
{
    private readonly ILogger<EventLogService> _logger;
    private readonly MonitorSettings _settings;
    private bool _initialized;

    public EventLogService(ILogger<EventLogService> logger, IOptions<MonitorSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
    }

    /// <summary>
    /// Ensures the Windows Event Log source is registered. Must run as Administrator.
    /// </summary>
    public void EnsureEventLogSource()
    {
        if (!_settings.WriteToWindowsEventLog)
            return;

        try
        {
            if (!EventLog.SourceExists(_settings.EventLogSource))
            {
                EventLog.CreateEventSource(_settings.EventLogSource, _settings.EventLogName);
                _logger.LogInformation(
                    "Created Windows Event Log source '{Source}' in log '{LogName}'",
                    _settings.EventLogSource, _settings.EventLogName);
            }
            _initialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create Event Log source. Run the installer as Administrator first, " +
                "or disable WriteToWindowsEventLog in settings");
            _initialized = false;
        }
    }

    /// <summary>
    /// Writes a file transfer event to the Windows Event Log.
    /// </summary>
    public void WriteEvent(FileTransferEvent transferEvent)
    {
        if (!_settings.WriteToWindowsEventLog || !_initialized)
            return;

        try
        {
            var message = FormatEventMessage(transferEvent);
            EventLog.WriteEntry(
                _settings.EventLogSource,
                message,
                EventLogEntryType.Information,
                eventID: 1000);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write to Windows Event Log");
        }
    }

    private static string FormatEventMessage(FileTransferEvent e)
    {
        return $"""
            File Transfer Detected
            =======================
            Time:            {e.Timestamp:yyyy-MM-dd HH:mm:ss}
            User:            {e.UserName}
            Event Type:      {e.EventTypeDisplay}
            File Name:       {e.FileName}
            Source Path:      {e.SourcePath}
            Destination Path: {e.DestinationPath}
            File Size:       {e.FileSizeBytes:N0} bytes
            Machine:         {e.MachineName}
            """;
    }
}
