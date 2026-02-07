using System.Collections.Concurrent;
using System.Threading.Channels;
using FileMonitorService.Configuration;
using FileMonitorService.Models;
using Microsoft.Extensions.Options;

namespace FileMonitorService.Services;

/// <summary>
/// Background worker that sets up FileSystemWatchers on configured paths
/// and processes file creation events to detect genuine user-initiated file transfers
/// between local machines and file servers.
///
/// For drive root paths (e.g., C:\), the service automatically expands them into
/// non-system subdirectories to avoid FileSystemWatcher buffer overflow from
/// the massive volume of system file activity.
///
/// Events are queued via a Channel and processed on a background thread so the
/// FileSystemWatcher callback returns immediately and never backs up.
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

    // Async queue: watcher callbacks just enqueue, processing happens on background thread
    private readonly Channel<(string watchedPath, string fullPath, string? fileName)> _eventChannel =
        Channel.CreateBounded<(string, string, string?)>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    // Debounce dictionary to prevent duplicate events for the same file
    private readonly ConcurrentDictionary<string, DateTime> _recentEvents = new();

    // Windows system directories that should NEVER be watched — they generate
    // thousands of events per second and will overflow FileSystemWatcher buffers
    private static readonly HashSet<string> SystemDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows",
        "Program Files",
        "Program Files (x86)",
        "ProgramData",
        "$Recycle.Bin",
        "System Volume Information",
        "Recovery",
        "PerfLogs",
        "Boot",
        "MSOCache",
        "Intel",
        "AMD",
        "NVIDIA",
        "Config.Msi",
        "Documents and Settings",   // Junction to C:\Users
        "msys64",
        "MinGW",
        "Cygwin",
        "Cygwin64",
    };

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

        _logger.LogInformation("Active watchers: {Count}", _watchers.Count);

        // Process queued events on the background thread
        await ProcessEventQueueAsync(stoppingToken);
    }

    private void SetupWatchers()
    {
        // Monitor local paths (detects: File Server → Local copies)
        foreach (var localPath in _settings.LocalPaths)
        {
            if (IsDriveRoot(localPath))
            {
                // Expand drive root into non-system subdirectories
                ExpandDriveRoot(localPath, "Local");
            }
            else
            {
                CreateWatcher(localPath, "Local");
            }
        }

        // Monitor file server paths (detects: Local → File Server copies)
        foreach (var serverPath in _settings.FileServerPaths)
        {
            CreateWatcher(serverPath, "FileServer");
        }
    }

    /// <summary>
    /// Checks if a path is a drive root like "C:\" or "D:\".
    /// </summary>
    private static bool IsDriveRoot(string path)
    {
        return path.Length <= 3
            && path.Length >= 2
            && char.IsLetter(path[0])
            && path[1] == ':'
            && (path.Length == 2 || path[2] == '\\');
    }

    /// <summary>
    /// Instead of watching a full drive root, enumerate its top-level directories
    /// and create watchers only for non-system folders.
    /// This prevents FileSystemWatcher buffer overflow from system file activity.
    /// </summary>
    private void ExpandDriveRoot(string drivePath, string label)
    {
        var normalizedDrive = drivePath.Length == 2 ? drivePath + @"\" : drivePath;

        if (!Directory.Exists(normalizedDrive))
        {
            _logger.LogWarning("Drive does not exist: {Drive}", normalizedDrive);
            return;
        }

        _logger.LogInformation(
            "Expanding drive root {Drive} into non-system subdirectories...",
            normalizedDrive);

        int created = 0;
        int skipped = 0;

        try
        {
            foreach (var dir in Directory.GetDirectories(normalizedDrive))
            {
                var dirName = Path.GetFileName(dir);

                if (SystemDirectories.Contains(dirName))
                {
                    _logger.LogInformation("  Skipping system directory: {Dir}", dir);
                    skipped++;
                    continue;
                }

                // Skip hidden/system directories
                try
                {
                    var attrs = File.GetAttributes(dir);
                    if (attrs.HasFlag(FileAttributes.System) && attrs.HasFlag(FileAttributes.Hidden))
                    {
                        _logger.LogInformation("  Skipping hidden system directory: {Dir}", dir);
                        skipped++;
                        continue;
                    }
                }
                catch
                {
                    // Can't read attributes — skip it
                    skipped++;
                    continue;
                }

                CreateWatcher(dir, label);
                created++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate directories in {Drive}", normalizedDrive);
        }

        _logger.LogInformation(
            "Drive {Drive}: created {Created} watchers, skipped {Skipped} system directories",
            normalizedDrive, created, skipped);
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
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                // 64KB buffer to handle bursts of events without dropping them
                InternalBufferSize = 65536
            };

            // Callback is lightweight — just enqueue the event
            watcher.Created += (sender, e) => EnqueueEvent(path, e.FullPath, e.Name);
            watcher.Error += OnWatcherError;

            _watchers.Add(watcher);
            _logger.LogInformation("  Watcher [{Label}] {Path}", label, path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create watcher for {Path} ({Label})", path, label);
        }
    }

    /// <summary>
    /// Lightweight callback — just pushes to the channel, no filtering here.
    /// </summary>
    private void EnqueueEvent(string watchedPath, string fullPath, string? fileName)
    {
        // Apply only the cheapest filter (path exclusion) before enqueuing
        if (_pathClassifier.ShouldExclude(fullPath))
            return;

        _eventChannel.Writer.TryWrite((watchedPath, fullPath, fileName));
    }

    /// <summary>
    /// Background loop that reads from the event channel and processes events.
    /// All expensive operations (file size check, process check, user resolution) happen here.
    /// </summary>
    private async Task ProcessEventQueueAsync(CancellationToken stoppingToken)
    {
        var reader = _eventChannel.Reader;

        // Start a periodic cleanup task
        _ = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                CleanupRecentEvents();
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }, stoppingToken);

        await foreach (var (watchedPath, fullPath, fileName) in reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                ProcessEvent(watchedPath, fullPath, fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing queued event for {FilePath}", fullPath);
            }
        }
    }

    private void ProcessEvent(string watchedPath, string fullPath, string? fileName)
    {
        // Debounce — skip if we already processed this file recently
        var debounceKey = fullPath.ToLowerInvariant();
        var now = DateTime.Now;
        if (_recentEvents.TryGetValue(debounceKey, out var lastSeen)
            && (now - lastSeen).TotalMilliseconds < _settings.DebounceIntervalMs)
        {
            return;
        }
        _recentEvents[debounceKey] = now;

        // Extension filter
        if (!_pathClassifier.MatchesExtensionFilter(fullPath))
            return;

        // Skip directories
        if (Directory.Exists(fullPath) && !File.Exists(fullPath))
            return;

        // File size check
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

        // Process check — is this a user-initiated copy?
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

        // Classify the transfer direction
        var direction = _pathClassifier.ClassifyDirection(watchedPath, fullPath);
        if (direction == null)
        {
            _logger.LogDebug("Could not classify direction for {FilePath}", fullPath);
            return;
        }

        // Resolve the user
        var userName = _userIdentityService.GetFileOwner(fullPath);

        // Skip SYSTEM / service account events
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

        _logger.LogInformation("{Event}", transferEvent);
        _eventLogService.WriteEvent(transferEvent);
        _csvLogService.WriteEvent(transferEvent);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();

        if (ex is InternalBufferOverflowException)
        {
            _logger.LogWarning("FileSystemWatcher buffer overflow — some events may have been lost. " +
                "Consider narrowing monitored paths.");
        }
        else
        {
            _logger.LogError(ex, "FileSystemWatcher error occurred");
        }

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
        _eventChannel.Writer.Complete();
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
        base.Dispose();
    }
}
