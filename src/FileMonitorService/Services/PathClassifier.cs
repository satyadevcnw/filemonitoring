using FileMonitorService.Configuration;
using FileMonitorService.Models;
using Microsoft.Extensions.Options;

namespace FileMonitorService.Services;

/// <summary>
/// Determines whether a path belongs to a local drive or a file server (UNC/network share),
/// and classifies the transfer direction of file operations.
/// </summary>
public sealed class PathClassifier
{
    private readonly MonitorSettings _settings;

    public PathClassifier(IOptions<MonitorSettings> settings)
    {
        _settings = settings.Value;
    }

    /// <summary>
    /// Returns true if the given path is a UNC path (\\server\share) or matches
    /// a configured file server path.
    /// </summary>
    public bool IsFileServerPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        // UNC paths start with \\
        if (path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
            return true;

        // Check if the path is under any configured file server paths
        return _settings.FileServerPaths.Any(serverPath =>
            path.StartsWith(serverPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns true if the given path is a local path (drive letter or matches
    /// a configured local path).
    /// </summary>
    public bool IsLocalPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        // Drive letter paths like C:\, D:\ etc.
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
            return !IsFileServerPath(path); // Mapped drives could be server paths

        // Check configured local paths
        return _settings.LocalPaths.Any(localPath =>
            path.StartsWith(localPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Determines the transfer direction based on which watcher detected the event
    /// and the path location.
    /// </summary>
    public TransferDirection? ClassifyDirection(string watchedPath, string filePath)
    {
        // If the watcher is on a file server path and a new file appeared,
        // it was copied FROM local TO the file server
        if (IsFileServerPath(watchedPath))
            return TransferDirection.LocalToFileServer;

        // If the watcher is on a local path and a new file appeared,
        // it was copied FROM the file server TO local
        if (IsLocalPath(watchedPath))
            return TransferDirection.FileServerToLocal;

        return null;
    }

    /// <summary>
    /// Checks if a file matches the configured extension filter.
    /// Returns true if no filter is set or if the file extension matches.
    /// </summary>
    public bool MatchesExtensionFilter(string filePath)
    {
        if (_settings.FileExtensionFilter.Count == 0)
            return true;

        var extension = Path.GetExtension(filePath);
        return _settings.FileExtensionFilter.Any(ext =>
            ext.Equals(extension, StringComparison.OrdinalIgnoreCase));
    }
}
