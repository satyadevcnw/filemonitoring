using System.Collections.Concurrent;
using FileMonitorService.Configuration;
using FileMonitorService.Models;
using Microsoft.Extensions.Options;

namespace FileMonitorService.Services;

/// <summary>
/// Background worker that sets up FileSystemWatchers on configured paths
/// and processes file creation events to detect genuine user-initiated file transfers
/// between local machines and file servers.
///
/// Filtering pipeline (applied in order):
/// 1. Path exclusion  — AppData, browser cache, temp dirs, system dirs
/// 2. Debounce        — skip duplicate events for the same file
/// 3. Extension filter — optional whitelist of file extensions
/// 4. Directory check  — skip directory creation events
/// 5. File size        — skip zero-byte / tiny files
/// 6. Process check    — only allow explorer.exe and known copy tools
/// 7. User check       — skip NT AUTHORITY\SYSTEM and service accounts
/// </summary>
public sealed class FileMonitorWorker : BackgroundService
{
    private readonly ILogger<FileMonitorWorker> _logger;
    private readonly MonitorSettings _settings;
    private readonly PathClassifier _pathClassifier;
    private readonly UserIdentityService _userIdentityService;
    private readonly EventLogService _eventLogService;
    private readonly CsvLogService _csvLogService;
    private readonly ProcessHelper _processHelper;
    private readonly List<FileSystemWatcher> _watchers = new();

    // Debounce dictionary to prevent duplicate events for the same file
    private readonly ConcurrentDictionary<string, DateTime> _recentEvents = new();

    public FileMonitorWorker(
        ILogger<FileMonitorWorker> logger,
        IOptions<MonitorSettings> settings,
        PathClassifier pathClassifier,
        UserIdentityService userIdentityService,
        EventLogService eventLogService,
        CsvLogService csvLogService,
        ProcessHelper processHelper)
    {
        _logger = logger;
        _settings = settings.Value;
        _pathClassifier = pathClassifier;
        _userIdentityService = userIdentityService;
        _eventLogService = eventLogService;
        _csvLogService = csvLogService;
        _processHelper = processHelper;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("File Monitor Service starting...");

        _eventLogService.EnsureEventLogSource();
        _csvLogService.EnsureLogDirectory();

        SetupWatchers();

        _logger.LogInformation(
            "Monitoring {LocalCount} local path(s) and {ServerCount} file server path(s). " +
            "Process filtering: {ProcessFilter}",
            _settings.LocalPaths.Count, _settings.FileServerPaths.Count,
            _settings.OnlyUserInitiatedCopies ? "ON (explorer.exe only)" : "OFF");

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
                // Only watch for new files — not modifications to existing files
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime
            };

            // Only listen for Created events — the only reliable indicator of a copy
            watcher.Created += (sender, e) => OnFileEvent(path, e.FullPath, e.Name);
            watcher.Error += OnWatcherError;

            _watchers.Add(watcher);
            _logger.LogInformation("Watcher created for [{Label}] {Path}", label, path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create watcher for {Path} ({Label})", path, label);
        }
    }

    private void OnFileEvent(string watchedPath, string fullPath, string? fileName)
    {
        try
        {
            // === FILTER 1: Path exclusion (AppData, browser cache, temp dirs, etc.) ===
            if (_pathClassifier.ShouldExclude(fullPath))
                return;

            // === FILTER 2: Debounce — skip if we already processed this file recently ===
            var debounceKey = fullPath.ToLowerInvariant();
            var now = DateTime.Now;
            if (_recentEvents.TryGetValue(debounceKey, out var lastSeen)
                && (now - lastSeen).TotalMilliseconds < _settings.DebounceIntervalMs)
            {
                return;
            }
            _recentEvents[debounceKey] = now;

            // === FILTER 3: Extension filter ===
            if (!_pathClassifier.MatchesExtensionFilter(fullPath))
                return;

            // === FILTER 4: Skip directories ===
            if (Directory.Exists(fullPath) && !File.Exists(fullPath))
                return;

            // === FILTER 5: Minimum file size ===
            long fileSize = 0;
            try
            {
                var fileInfo = new FileInfo(fullPath);
                if (fileInfo.Exists)
                    fileSize = fileInfo.Length;
            }
            catch
            {
                // File may still be locked/being written
            }

            if (fileSize < _settings.MinimumFileSizeBytes)
                return;

            // === FILTER 6: Process check — is this a user-initiated copy? ===
            if (_settings.OnlyUserInitiatedCopies)
            {
                var (isUserInitiated, procName) = _processHelper.IsUserInitiatedCopy(fullPath);
                if (!isUserInitiated)
                {
                    _logger.LogDebug(
                        "Skipping non-user file event: {File} (process: {Process})",
                        fullPath, procName ?? "unknown");
                    return;
                }
            }

            // === CLASSIFY the transfer direction ===
            var direction = _pathClassifier.ClassifyDirection(watchedPath, fullPath);
            if (direction == null)
            {
                _logger.LogDebug("Could not classify direction for {FilePath}", fullPath);
                return;
            }

            // === RESOLVE the user ===
            var userName = _userIdentityService.GetFileOwner(fullPath);

            // === FILTER 7: Skip SYSTEM / service account events — not real user copies ===
            if (userName.StartsWith("NT AUTHORITY\\", StringComparison.OrdinalIgnoreCase)
                || userName.StartsWith("NT SERVICE\\", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping system account event: {User} -> {File}", userName, fullPath);
                return;
            }

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
