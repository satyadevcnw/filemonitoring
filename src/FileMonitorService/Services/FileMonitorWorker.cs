using System.Collections.Concurrent;
using System.Threading.Channels;
using FileMonitorService.Configuration;
using FileMonitorService.Models;
using Microsoft.Extensions.Options;

namespace FileMonitorService.Services;

/// <summary>
/// Background worker that monitors file transfers between local drives and file servers.
///
/// For drive root paths (C:\, D:\), the service:
/// 1. Scans at startup to discover existing non-system user folders
/// 2. Creates a "sentinel watcher" on each drive root to detect new/renamed/deleted folders
/// 3. Dynamically adds/removes content watchers as users create/rename/delete folders
///
/// This works for multiple AD users on the same machine — any user creating a folder
/// on C:\ or D:\ will trigger automatic watcher creation.
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

    // All content watchers, keyed by their watched path (for dynamic add/remove)
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _contentWatchers = new(StringComparer.OrdinalIgnoreCase);

    // Sentinel watchers that monitor drive roots for directory changes only
    private readonly List<FileSystemWatcher> _sentinelWatchers = new();

    // File server watchers (static, don't change at runtime)
    private readonly List<FileSystemWatcher> _serverWatchers = new();

    // Track which drive roots are being managed dynamically
    private readonly HashSet<string> _managedDriveRoots = new(StringComparer.OrdinalIgnoreCase);

    // Async queue for file transfer events
    private readonly Channel<(string watchedPath, string fullPath, string? fileName)> _eventChannel =
        Channel.CreateBounded<(string, string, string?)>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    // Debounce dictionary
    private readonly ConcurrentDictionary<string, DateTime> _recentEvents = new();

    // Windows system directories that should NEVER be watched
    private static readonly HashSet<string> SystemDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows",
        "Program Files",
        "Program Files (x86)",
        "ProgramData",
        "$Recycle.Bin",
        "$RECYCLE.BIN",
        "System Volume Information",
        "Recovery",
        "PerfLogs",
        "Boot",
        "EFI",
        "MSOCache",
        "Intel",
        "AMD",
        "NVIDIA",
        "Config.Msi",
        "Documents and Settings",
        "OneDriveTemp",
        "msys64",
        "MinGW",
        "Cygwin",
        "Cygwin64",
        "WCH.CN",
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

        _logger.LogInformation(
            "Active content watchers: {ContentCount}, Sentinel watchers: {SentinelCount}, Server watchers: {ServerCount}",
            _contentWatchers.Count, _sentinelWatchers.Count, _serverWatchers.Count);

        // Process queued file events on the background thread
        await ProcessEventQueueAsync(stoppingToken);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  SETUP
    // ──────────────────────────────────────────────────────────────────────

    private void SetupWatchers()
    {
        foreach (var localPath in _settings.LocalPaths)
        {
            if (IsDriveRoot(localPath))
            {
                var root = NormalizeDriveRoot(localPath);
                _managedDriveRoots.Add(root);

                // Step 1: Scan existing folders and create content watchers
                ScanAndWatchUserFolders(root);

                // Step 2: Create a sentinel watcher on the drive root to detect
                //         new/renamed/deleted folders in real time
                CreateSentinelWatcher(root);
            }
            else
            {
                // Non-root path — just watch it directly
                AddContentWatcher(localPath, "Local");
            }
        }

        // File server watchers (static)
        foreach (var serverPath in _settings.FileServerPaths)
        {
            CreateServerWatcher(serverPath);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  STARTUP SCAN — discover existing user folders
    // ──────────────────────────────────────────────────────────────────────

    private void ScanAndWatchUserFolders(string driveRoot)
    {
        if (!Directory.Exists(driveRoot))
        {
            _logger.LogWarning("Drive does not exist: {Drive}", driveRoot);
            return;
        }

        _logger.LogInformation("Scanning {Drive} for user folders...", driveRoot);

        int watched = 0;
        int skipped = 0;

        try
        {
            foreach (var dir in Directory.GetDirectories(driveRoot))
            {
                var dirName = Path.GetFileName(dir);

                if (IsSystemDirectory(dir, dirName))
                {
                    _logger.LogDebug("  Skip system: {Dir}", dir);
                    skipped++;
                    continue;
                }

                AddContentWatcher(dir, "Local");
                watched++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan {Drive}", driveRoot);
        }

        _logger.LogInformation(
            "Drive {Drive}: watching {Watched} user folders, skipped {Skipped} system folders",
            driveRoot, watched, skipped);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  SENTINEL WATCHER — monitors drive root for folder create/rename/delete
    // ──────────────────────────────────────────────────────────────────────

    private void CreateSentinelWatcher(string driveRoot)
    {
        try
        {
            var watcher = new FileSystemWatcher(driveRoot)
            {
                // ONLY watch the root level, NOT subdirectories
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
                // Only watch for directory name changes (create, rename, delete)
                NotifyFilter = NotifyFilters.DirectoryName,
                InternalBufferSize = 16384
            };

            watcher.Created += (s, e) => OnRootDirectoryCreated(driveRoot, e.FullPath, e.Name);
            watcher.Deleted += (s, e) => OnRootDirectoryDeleted(driveRoot, e.FullPath, e.Name);
            watcher.Renamed += (s, e) => OnRootDirectoryRenamed(driveRoot, e.OldFullPath, e.OldName, e.FullPath, e.Name);
            watcher.Error += OnWatcherError;

            _sentinelWatchers.Add(watcher);
            _logger.LogInformation("Sentinel watcher active on {Drive} (watching for new/renamed/deleted folders)", driveRoot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create sentinel watcher for {Drive}", driveRoot);
        }
    }

    /// <summary>
    /// A user (any of the 5+ users on this machine) created a new folder at the drive root.
    /// Automatically add a content watcher for it.
    /// </summary>
    private void OnRootDirectoryCreated(string driveRoot, string fullPath, string? name)
    {
        var dirName = name ?? Path.GetFileName(fullPath);

        if (IsSystemDirectory(fullPath, dirName))
        {
            _logger.LogDebug("New system directory created (ignoring): {Path}", fullPath);
            return;
        }

        _logger.LogInformation("New user folder detected: {Path} — adding watcher", fullPath);
        AddContentWatcher(fullPath, "Local-Dynamic");
    }

    /// <summary>
    /// A folder was deleted from the drive root. Remove its content watcher.
    /// </summary>
    private void OnRootDirectoryDeleted(string driveRoot, string fullPath, string? name)
    {
        _logger.LogInformation("Folder deleted: {Path} — removing watcher", fullPath);
        RemoveContentWatcher(fullPath);
    }

    /// <summary>
    /// A folder was renamed at the drive root. Remove the old watcher, add a new one.
    /// </summary>
    private void OnRootDirectoryRenamed(string driveRoot, string oldPath, string? oldName, string newPath, string? newName)
    {
        _logger.LogInformation("Folder renamed: {OldPath} → {NewPath} — updating watcher", oldPath, newPath);

        RemoveContentWatcher(oldPath);

        var dirName = newName ?? Path.GetFileName(newPath);
        if (!IsSystemDirectory(newPath, dirName))
        {
            AddContentWatcher(newPath, "Local-Renamed");
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  CONTENT WATCHERS — watch user folders for file copy events
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a content watcher for a user folder. Thread-safe — can be called from
    /// sentinel watcher callbacks at any time.
    /// </summary>
    private void AddContentWatcher(string path, string label)
    {
        // Normalize path for consistent dictionary keys
        var key = Path.GetFullPath(path).TrimEnd('\\');

        if (_contentWatchers.ContainsKey(key))
        {
            _logger.LogDebug("Watcher already exists for {Path}", path);
            return;
        }

        if (!Directory.Exists(path))
        {
            _logger.LogWarning("Cannot watch — path does not exist: {Path}", path);
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = _settings.IncludeSubdirectories,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                InternalBufferSize = 65536
            };

            watcher.Created += (sender, e) => EnqueueEvent(path, e.FullPath, e.Name);
            watcher.Error += OnWatcherError;

            if (_contentWatchers.TryAdd(key, watcher))
            {
                _logger.LogInformation("  + Watcher [{Label}] {Path}", label, path);
            }
            else
            {
                // Another thread beat us — dispose our watcher
                watcher.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create watcher for {Path}", path);
        }
    }

    /// <summary>
    /// Removes and disposes a content watcher. Thread-safe.
    /// </summary>
    private void RemoveContentWatcher(string path)
    {
        var key = Path.GetFullPath(path).TrimEnd('\\');

        if (_contentWatchers.TryRemove(key, out var watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            _logger.LogInformation("  - Watcher removed: {Path}", path);
        }
    }

    /// <summary>
    /// Creates a static watcher for file server UNC paths.
    /// </summary>
    private void CreateServerWatcher(string path)
    {
        if (!Directory.Exists(path))
        {
            _logger.LogWarning("File server path not accessible: {Path}", path);
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = _settings.IncludeSubdirectories,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                InternalBufferSize = 65536
            };

            watcher.Created += (sender, e) => EnqueueEvent(path, e.FullPath, e.Name);
            watcher.Error += OnWatcherError;

            _serverWatchers.Add(watcher);
            _logger.LogInformation("  Watcher [FileServer] {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create watcher for file server {Path}", path);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  HELPERS
    // ──────────────────────────────────────────────────────────────────────

    private static bool IsDriveRoot(string path)
    {
        return path.Length <= 3
            && path.Length >= 2
            && char.IsLetter(path[0])
            && path[1] == ':'
            && (path.Length == 2 || path[2] == '\\');
    }

    private static string NormalizeDriveRoot(string path)
    {
        return path.Length == 2 ? path + @"\" : path;
    }

    /// <summary>
    /// Determines if a directory is a Windows system directory that should not be watched.
    /// Checks both the hard-coded list and the Hidden+System file attributes.
    /// </summary>
    private bool IsSystemDirectory(string fullPath, string dirName)
    {
        if (SystemDirectories.Contains(dirName))
            return true;

        try
        {
            var attrs = File.GetAttributes(fullPath);
            if (attrs.HasFlag(FileAttributes.System) && attrs.HasFlag(FileAttributes.Hidden))
                return true;
        }
        catch
        {
            // Can't read attributes — treat as system (safe default)
            return true;
        }

        return false;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  EVENT PROCESSING PIPELINE
    // ──────────────────────────────────────────────────────────────────────

    private void EnqueueEvent(string watchedPath, string fullPath, string? fileName)
    {
        if (_pathClassifier.ShouldExclude(fullPath))
            return;

        _eventChannel.Writer.TryWrite((watchedPath, fullPath, fileName));
    }

    private async Task ProcessEventQueueAsync(CancellationToken stoppingToken)
    {
        var reader = _eventChannel.Reader;

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
                _logger.LogError(ex, "Error processing event for {FilePath}", fullPath);
            }
        }
    }

    private void ProcessEvent(string watchedPath, string fullPath, string? fileName)
    {
        // Debounce
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

        // File size
        long fileSize = 0;
        try
        {
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Exists)
                fileSize = fileInfo.Length;
        }
        catch { }

        if (fileSize < _settings.MinimumFileSizeBytes)
            return;

        // Process check
        if (_settings.OnlyUserInitiatedCopies)
        {
            var (isUserInitiated, procName) = _processHelper.IsUserInitiatedCopy(fullPath);
            if (!isUserInitiated)
            {
                _logger.LogDebug("Skipping non-user event: {File} (process: {Process})", fullPath, procName ?? "unknown");
                return;
            }
        }

        // Direction
        var direction = _pathClassifier.ClassifyDirection(watchedPath, fullPath);
        if (direction == null)
            return;

        // User
        var userName = _userIdentityService.GetFileOwner(fullPath);

        if (userName.StartsWith("NT AUTHORITY\\", StringComparison.OrdinalIgnoreCase)
            || userName.StartsWith("NT SERVICE\\", StringComparison.OrdinalIgnoreCase))
        {
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

    // ──────────────────────────────────────────────────────────────────────
    //  ERROR HANDLING & CLEANUP
    // ──────────────────────────────────────────────────────────────────────

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();

        if (ex is InternalBufferOverflowException)
        {
            _logger.LogWarning("FileSystemWatcher buffer overflow — some events may have been lost");
        }
        else
        {
            _logger.LogError(ex, "FileSystemWatcher error");
        }

        if (sender is FileSystemWatcher watcher)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception restartEx)
            {
                _logger.LogError(restartEx, "Failed to re-enable watcher for {Path}", watcher.Path);
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

        foreach (var watcher in _sentinelWatchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _sentinelWatchers.Clear();

        foreach (var watcher in _serverWatchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _serverWatchers.Clear();

        foreach (var kvp in _contentWatchers)
        {
            kvp.Value.EnableRaisingEvents = false;
            kvp.Value.Dispose();
        }
        _contentWatchers.Clear();

        base.Dispose();
    }
}
