using FileMonitorService.Configuration;
using FileMonitorService.Models;
using Microsoft.Extensions.Options;

namespace FileMonitorService.Services;

/// <summary>
/// Determines whether a path belongs to a local drive or a file server (UNC/network share),
/// classifies transfer direction, and filters out excluded paths/patterns.
/// </summary>
public sealed class PathClassifier
{
    private readonly MonitorSettings _settings;

    // Hard-coded exclusion directories — these NEVER represent user file copies
    private static readonly string[] AlwaysExcludedSubpaths = {
        @"\AppData\",
        @"\Application Data\",
        @"\Local Settings\",
        @"\.git\",
        @"\.vs\",
        @"\.vscode\",
        @"\node_modules\",
        @"\$Recycle.Bin\",
        @"\System Volume Information\",
        @"\Windows\Temp\",
        @"\ProgramData\",
    };

    // File patterns that are never user-initiated copies
    private static readonly string[] AlwaysExcludedPatterns = {
        ".tmp",
        ".TMP",
        "~$",          // Office lock files like ~$document.docx
        ".crdownload", // Chrome partial download
        ".partial",    // Firefox partial download
        ".lock",
        ".log",
        "thumbs.db",
        "desktop.ini",
        ".db-journal",
        ".db-wal",
        "-journal",
    };

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

        if (path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
            return true;

        return _settings.FileServerPaths.Any(serverPath =>
            path.StartsWith(serverPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns true if the given path is a local path.
    /// </summary>
    public bool IsLocalPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
            return !IsFileServerPath(path);

        return _settings.LocalPaths.Any(localPath =>
            path.StartsWith(localPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Determines the transfer direction based on which watcher detected the event.
    /// </summary>
    public TransferDirection? ClassifyDirection(string watchedPath, string filePath)
    {
        if (IsFileServerPath(watchedPath))
            return TransferDirection.LocalToFileServer;

        if (IsLocalPath(watchedPath))
            return TransferDirection.FileServerToLocal;

        return null;
    }

    /// <summary>
    /// Checks if a file matches the configured extension filter.
    /// </summary>
    public bool MatchesExtensionFilter(string filePath)
    {
        if (_settings.FileExtensionFilter.Count == 0)
            return true;

        var extension = Path.GetExtension(filePath);
        return _settings.FileExtensionFilter.Any(ext =>
            ext.Equals(extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns true if the file should be excluded from logging based on:
    /// 1. Hard-coded exclusion subpaths (AppData, browser caches, system dirs)
    /// 2. Configured exclusion paths from appsettings.json
    /// 3. Hard-coded excluded file patterns (.tmp, ~$, lock files)
    /// 4. Configured excluded file patterns from appsettings.json
    /// </summary>
    public bool ShouldExclude(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return true;

        // Check hard-coded excluded subpaths
        foreach (var subpath in AlwaysExcludedSubpaths)
        {
            if (filePath.Contains(subpath, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Check configured excluded paths
        foreach (var excluded in _settings.ExcludedPaths)
        {
            if (filePath.StartsWith(excluded, StringComparison.OrdinalIgnoreCase)
                || filePath.Contains(excluded, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var fileName = Path.GetFileName(filePath);

        // Check hard-coded excluded file patterns
        foreach (var pattern in AlwaysExcludedPatterns)
        {
            if (fileName.EndsWith(pattern, StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Check configured excluded file patterns
        foreach (var pattern in _settings.ExcludedFilePatterns)
        {
            if (pattern.StartsWith("*"))
            {
                // Suffix match: *.bak → check if filename ends with .bak
                var suffix = pattern[1..];
                if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (pattern.EndsWith("*"))
            {
                // Prefix match: ~$* → check if filename starts with ~$
                var prefix = pattern[..^1];
                if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else
            {
                // Exact match
                if (fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
