using System.Collections.Concurrent;
using FileMonitorService.Configuration;
using Microsoft.Extensions.Options;

namespace FileMonitorService.Services;

/// <summary>
/// Verifies whether a file that appeared on a local drive actually came from
/// the file server by checking if a file with the same name exists on any
/// configured file server path.
///
/// This eliminates false positives from:
/// - Build outputs copying files between local folders
/// - Applications saving/generating files locally
/// - Local-to-local file copies
///
/// Maintains a background-refreshed index of file server filenames for fast lookups.
/// </summary>
public sealed class FileServerVerifier
{
    private readonly ILogger<FileServerVerifier> _logger;
    private readonly MonitorSettings _settings;

    // Indexed set of filenames (lowercase) that exist on the file server.
    // Refreshed periodically in the background.
    private volatile HashSet<string> _serverFileIndex = new(StringComparer.OrdinalIgnoreCase);

    // Track when the index was last refreshed
    private DateTime _lastIndexRefresh = DateTime.MinValue;

    // Lock for index rebuilding
    private readonly object _indexLock = new();
    private volatile bool _indexing = false;

    /// <summary>
    /// How deep to scan the file server for building the index.
    /// Deeper = more accurate but slower to build.
    /// </summary>
    private const int MaxScanDepth = 5;

    public FileServerVerifier(ILogger<FileServerVerifier> logger, IOptions<MonitorSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
    }

    /// <summary>
    /// Builds the initial file server index. Call this at service startup.
    /// </summary>
    public void BuildInitialIndex()
    {
        RebuildIndex();
    }

    /// <summary>
    /// Starts a background task that periodically refreshes the file server index.
    /// </summary>
    public async Task StartPeriodicRefreshAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Refresh every 5 minutes
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

            try
            {
                RebuildIndex();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh file server index");
            }
        }
    }

    /// <summary>
    /// Call when the file server watcher detects a new file — add it to the index
    /// immediately so it's available for local-side verification.
    /// </summary>
    public void AddToIndex(string fileName)
    {
        // Create a new set with the added file (immutable swap for thread safety)
        var newIndex = new HashSet<string>(_serverFileIndex, StringComparer.OrdinalIgnoreCase);
        newIndex.Add(fileName.ToLowerInvariant());
        _serverFileIndex = newIndex;
    }

    /// <summary>
    /// Call when the file server watcher detects a file was deleted.
    /// </summary>
    public void RemoveFromIndex(string fileName)
    {
        var newIndex = new HashSet<string>(_serverFileIndex, StringComparer.OrdinalIgnoreCase);
        newIndex.Remove(fileName.ToLowerInvariant());
        _serverFileIndex = newIndex;
    }

    /// <summary>
    /// Checks if a file with the given name exists on any configured file server path.
    /// Uses the cached index for fast O(1) lookup, with a fallback to live check.
    /// </summary>
    public bool ExistsOnFileServer(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return false;

        // Fast path: check the cached index
        if (_serverFileIndex.Contains(fileName))
            return true;

        // Slow path fallback: do a live check directly on the server
        // This catches files added after the last index refresh
        return LiveCheckOnServer(fileName);
    }

    /// <summary>
    /// Does a quick live check if the file exists on any configured server path.
    /// Checks root and one level of subdirectories only (fast).
    /// </summary>
    private bool LiveCheckOnServer(string fileName)
    {
        foreach (var serverPath in _settings.FileServerPaths)
        {
            try
            {
                // Check root
                var rootCheck = Path.Combine(serverPath, fileName);
                if (File.Exists(rootCheck))
                    return true;

                // Check one level of subdirectories
                foreach (var subDir in Directory.GetDirectories(serverPath))
                {
                    var subCheck = Path.Combine(subDir, fileName);
                    if (File.Exists(subCheck))
                        return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Live check failed for {File} on {Server}", fileName, serverPath);
            }
        }

        return false;
    }

    /// <summary>
    /// Rebuilds the complete file server index by scanning all configured
    /// file server paths up to MaxScanDepth levels deep.
    /// </summary>
    private void RebuildIndex()
    {
        if (_indexing)
            return;

        lock (_indexLock)
        {
            if (_indexing)
                return;

            _indexing = true;
        }

        try
        {
            var newIndex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int totalFiles = 0;

            foreach (var serverPath in _settings.FileServerPaths)
            {
                if (!Directory.Exists(serverPath))
                {
                    _logger.LogWarning("File server path not accessible for indexing: {Path}", serverPath);
                    continue;
                }

                try
                {
                    ScanDirectory(serverPath, newIndex, 0, ref totalFiles);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error scanning file server {Path}", serverPath);
                }
            }

            _serverFileIndex = newIndex;
            _lastIndexRefresh = DateTime.Now;

            _logger.LogInformation(
                "File server index rebuilt: {FileCount} filenames indexed from {ServerCount} server path(s)",
                totalFiles, _settings.FileServerPaths.Count);
        }
        finally
        {
            _indexing = false;
        }
    }

    private void ScanDirectory(string path, HashSet<string> index, int depth, ref int totalFiles)
    {
        if (depth > MaxScanDepth)
            return;

        try
        {
            // Index filenames in this directory
            foreach (var file in Directory.GetFiles(path))
            {
                var fileName = Path.GetFileName(file);
                index.Add(fileName);
                totalFiles++;
            }

            // Recurse into subdirectories
            foreach (var subDir in Directory.GetDirectories(path))
            {
                ScanDirectory(subDir, index, depth + 1, ref totalFiles);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error scanning {Path}", path);
        }
    }
}
