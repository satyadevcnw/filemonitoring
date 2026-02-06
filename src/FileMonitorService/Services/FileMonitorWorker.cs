using System.Collections.Concurrent;
using FileMonitorService.Configuration;
using FileMonitorService.Models;
using Microsoft.Extensions.Options;

namespace FileMonitorService.Services;

/// <summary>
/// Background worker that sets up FileSystemWatchers on configured paths
/// and processes file creation/change events to detect file transfers.
/// </summary>
public sealed class FileMonitorWorker : BackgroundService
{
    private readonly ILogger<FileMonitorWorker> _logger;
    private readonly MonitorSettings _settings;
    private readonly PathClassifier _pathClassifier;
    private readonly UserIdentityService _userIdentityService;
    private readonly EventLogService _eventLogService;
    private readonly CsvLogService _csvLogService;
    private readonly List<FileSystemWatcher> _watchers = new();

    // Debounce dictionary to prevent duplicate events for the same file
    private readonly ConcurrentDictionary<string, DateTime> _recentEvents = new();

    public FileMonitorWorker(
        ILogger<FileMonitorWorker> logger,
        IOptions<MonitorSettings> settings,
        PathClassifier pathClassifier,
        UserIdentityService userIdentityService,
        EventLogService eventLogService,
        CsvLogService csvLogService)
    {
        _logger = logger;
        _settings = settings.Value;
        _pathClassifier = pathClassifier;
        _userIdentityService = userIdentityService;
        _eventLogService = eventLogService;
        _csvLogService = csvLogService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("File Monitor Service starting...");

        _eventLogService.EnsureEventLogSource();
        _csvLogService.EnsureLogDirectory();

        SetupWatchers();

        _logger.LogInformation(
            "Monitoring {LocalCount} local path(s) and {ServerCount} file server path(s)",
            _settings.LocalPaths.Count, _settings.FileServerPaths.Count);

        // Keep the service running and periodically clean up the debounce dictionary
        while (!stoppingToken.IsCancellationRequested)
        {
            CleanupRecentEvents();
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private void SetupWatchers()
    {
        // Monitor local paths (detects: File Server → Local copies)
        foreach (var localPath in _settings.LocalPaths)
        {
            CreateWatcher(localPath, "Local");
        }

        // Monitor file server paths (detects: Local → File Server copies)
        foreach (var serverPath in _settings.FileServerPaths)
        {
            CreateWatcher(serverPath, "FileServer");
        }
    }

    private void CreateWatcher(string path, string label)
    {
        if (!Directory.Exists(path))
        {
            _logger.LogWarning("Path does not exist or is not accessible: {Path} ({Label})", path, label);
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = _settings.IncludeSubdirectories,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.CreationTime
                             | NotifyFilters.Size
            };

            watcher.Created += (sender, e) => OnFileEvent(path, e.FullPath, e.Name, WatcherChangeTypes.Created);
            watcher.Changed += (sender, e) => OnFileEvent(path, e.FullPath, e.Name, WatcherChangeTypes.Changed);
            watcher.Renamed += (sender, e) => OnFileEvent(path, e.FullPath, e.Name, WatcherChangeTypes.Renamed);
            watcher.Error += OnWatcherError;

            _watchers.Add(watcher);
            _logger.LogInformation("Watcher created for [{Label}] {Path}", label, path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create watcher for {Path} ({Label})", path, label);
        }
    }

    private void OnFileEvent(string watchedPath, string fullPath, string? fileName, WatcherChangeTypes changeType)
    {
        try
        {
            // Only process Created events to avoid duplicates for the same copy operation.
            // Changed events are included for large files that trigger multiple write notifications.
            if (changeType != WatcherChangeTypes.Created && changeType != WatcherChangeTypes.Renamed)
                return;

            // Debounce: skip if we already processed this file recently
            var debounceKey = fullPath.ToLowerInvariant();
            var now = DateTime.Now;
            if (_recentEvents.TryGetValue(debounceKey, out var lastSeen)
                && (now - lastSeen).TotalMilliseconds < _settings.DebounceIntervalMs)
            {
                return;
            }
            _recentEvents[debounceKey] = now;

            // Check extension filter
            if (!_pathClassifier.MatchesExtensionFilter(fullPath))
                return;

            // Skip directories
            if (Directory.Exists(fullPath) && !File.Exists(fullPath))
                return;

            // Check minimum file size
            long fileSize = 0;
            try
            {
                var fileInfo = new FileInfo(fullPath);
                if (fileInfo.Exists)
                    fileSize = fileInfo.Length;
            }
            catch
            {
                // File may still be locked/copying
            }

            if (fileSize < _settings.MinimumFileSizeBytes)
                return;

            // Classify the direction
            var direction = _pathClassifier.ClassifyDirection(watchedPath, fullPath);
            if (direction == null)
            {
                _logger.LogDebug("Could not classify direction for {FilePath} (watched: {WatchedPath})", fullPath, watchedPath);
                return;
            }

            // Resolve user
            var userName = _userIdentityService.GetFileOwner(fullPath);

            var transferEvent = new FileTransferEvent
            {
                Timestamp = DateTime.Now,
                UserName = userName,
                EventType = direction.Value,
                SourcePath = direction.Value == TransferDirection.LocalToFileServer
                    ? "Local Machine" : watchedPath,
                DestinationPath = fullPath,
                FileName = fileName ?? Path.GetFileName(fullPath),
                FileSizeBytes = fileSize,
                MachineName = Environment.MachineName
            };

            // Log the event
            _logger.LogInformation("{Event}", transferEvent);
            _eventLogService.WriteEvent(transferEvent);
            _csvLogService.WriteEvent(transferEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing file event for {FilePath}", fullPath);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        _logger.LogError(ex, "FileSystemWatcher error occurred");

        // Attempt to re-enable the watcher
        if (sender is FileSystemWatcher watcher)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.EnableRaisingEvents = true;
                _logger.LogInformation("FileSystemWatcher re-enabled for {Path}", watcher.Path);
            }
            catch (Exception restartEx)
            {
                _logger.LogError(restartEx, "Failed to re-enable FileSystemWatcher for {Path}", watcher.Path);
            }
        }
    }

    private void CleanupRecentEvents()
    {
        var cutoff = DateTime.Now.AddMilliseconds(-_settings.DebounceIntervalMs * 2);
        foreach (var kvp in _recentEvents)
        {
            if (kvp.Value < cutoff)
                _recentEvents.TryRemove(kvp.Key, out _);
        }
    }

    public override void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
        base.Dispose();
    }
}
