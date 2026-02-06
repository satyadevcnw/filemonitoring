using FileMonitorService.Configuration;
using FileMonitorService.Models;
using Microsoft.Extensions.Options;

namespace FileMonitorService.Services;

/// <summary>
/// Writes file transfer events to daily-rotating CSV log files.
/// </summary>
public sealed class CsvLogService
{
    private readonly ILogger<CsvLogService> _logger;
    private readonly MonitorSettings _settings;
    private readonly object _writeLock = new();

    private const string CsvHeader =
        "\"Timestamp\",\"UserName\",\"EventType\",\"SourcePath\",\"DestinationPath\",\"FileName\",\"FileSizeBytes\",\"MachineName\"";

    public CsvLogService(ILogger<CsvLogService> logger, IOptions<MonitorSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
    }

    /// <summary>
    /// Ensures the CSV log directory exists.
    /// </summary>
    public void EnsureLogDirectory()
    {
        try
        {
            Directory.CreateDirectory(_settings.CsvLogPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create CSV log directory at {Path}", _settings.CsvLogPath);
        }
    }

    /// <summary>
    /// Appends a file transfer event to today's CSV log file.
    /// </summary>
    public void WriteEvent(FileTransferEvent transferEvent)
    {
        var fileName = $"file-transfers-{DateTime.Now:yyyy-MM-dd}.csv";
        var filePath = Path.Combine(_settings.CsvLogPath, fileName);

        lock (_writeLock)
        {
            try
            {
                var fileExists = File.Exists(filePath);
                using var writer = new StreamWriter(filePath, append: true);

                if (!fileExists)
                {
                    writer.WriteLine(CsvHeader);
                }

                writer.WriteLine(transferEvent.ToCsvLine());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write to CSV log at {Path}", filePath);
            }
        }
    }
}
