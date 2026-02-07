using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using FileMonitorService.Configuration;
using Microsoft.Extensions.Options;

namespace FileMonitorService.Services;

/// <summary>
/// Establishes an authenticated SMB connection to file server UNC paths
/// so the Windows Service (running as LocalSystem) can access network shares.
///
/// When you browse \\192.168.0.143\akku-fileserver in Explorer, YOUR AD
/// credentials are used. But the service runs as LocalSystem which has no
/// network identity. This service calls WNetAddConnection2 to authenticate
/// the service process to the file server using configured credentials.
/// </summary>
public sealed class NetworkShareConnector : IDisposable
{
    private readonly ILogger<NetworkShareConnector> _logger;
    private readonly MonitorSettings _settings;
    private readonly List<string> _connectedShares = new();

    public NetworkShareConnector(ILogger<NetworkShareConnector> logger, IOptions<MonitorSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
    }

    /// <summary>
    /// Connects to all configured file server paths using the provided credentials.
    /// Call this before creating FileSystemWatchers on UNC paths.
    /// </summary>
    public void ConnectAll()
    {
        if (string.IsNullOrEmpty(_settings.FileServerUsername))
        {
            _logger.LogWarning(
                "No FileServerUsername configured. The service needs credentials to access " +
                "network shares. Set FileServerUsername and FileServerPassword in appsettings.json, " +
                "or run the service under a domain account that has access.");
            return;
        }

        foreach (var serverPath in _settings.FileServerPaths)
        {
            Connect(serverPath);
        }
    }

    private void Connect(string uncPath)
    {
        // Extract the share root (e.g., \\192.168.0.143\akku-fileserver)
        var shareRoot = GetShareRoot(uncPath);

        try
        {
            var netResource = new NETRESOURCE
            {
                dwType = RESOURCETYPE_DISK,
                lpRemoteName = shareRoot
            };

            // Disconnect any existing connection first (clean slate)
            WNetCancelConnection2(shareRoot, 0, true);

            var result = WNetAddConnection2(
                ref netResource,
                _settings.FileServerPassword,
                _settings.FileServerUsername,
                CONNECT_TEMPORARY);

            if (result == 0)
            {
                _connectedShares.Add(shareRoot);
                _logger.LogInformation("Connected to file server: {Share} as {User}", shareRoot, _settings.FileServerUsername);
            }
            else if (result == 1219) // ERROR_SESSION_CREDENTIAL_CONFLICT — already connected
            {
                _connectedShares.Add(shareRoot);
                _logger.LogInformation("Already connected to file server: {Share}", shareRoot);
            }
            else
            {
                var errorMessage = new Win32Exception(result).Message;
                _logger.LogError(
                    "Failed to connect to {Share} as {User}: error {Code} — {Message}",
                    shareRoot, _settings.FileServerUsername, result, errorMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception connecting to {Share}", shareRoot);
        }
    }

    /// <summary>
    /// Extracts the share root from a UNC path.
    /// \\192.168.0.143\akku-fileserver\IT\subfolder → \\192.168.0.143\akku-fileserver
    /// </summary>
    private static string GetShareRoot(string uncPath)
    {
        if (!uncPath.StartsWith(@"\\"))
            return uncPath;

        // Remove leading \\, split by \, take server + share
        var withoutPrefix = uncPath[2..];
        var parts = withoutPrefix.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 2)
            return $@"\\{parts[0]}\{parts[1]}";

        return uncPath;
    }

    public void Dispose()
    {
        foreach (var share in _connectedShares)
        {
            try
            {
                WNetCancelConnection2(share, 0, true);
                _logger.LogInformation("Disconnected from file server: {Share}", share);
            }
            catch { }
        }
        _connectedShares.Clear();
    }

    #region P/Invoke

    private const int RESOURCETYPE_DISK = 1;
    private const int CONNECT_TEMPORARY = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string? lpLocalName;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string? lpRemoteName;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string? lpComment;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(
        ref NETRESOURCE netResource,
        string? password,
        string? username,
        int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(
        string name,
        int flags,
        bool force);

    #endregion
}
