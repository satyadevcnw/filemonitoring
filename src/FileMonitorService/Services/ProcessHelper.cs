using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FileMonitorService.Services;

/// <summary>
/// Uses Windows Restart Manager API to find which process has a file locked/open,
/// which helps determine if a file operation was user-initiated (explorer.exe)
/// or system/application-generated.
/// </summary>
public sealed class ProcessHelper
{
    private readonly ILogger<ProcessHelper> _logger;

    // Known processes that represent user-initiated file copy operations
    private static readonly HashSet<string> UserCopyProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",      // Windows Explorer (drag-drop, copy-paste)
        "robocopy",      // Command-line copy tool
        "xcopy",         // Command-line copy
        "cmd",           // Command prompt (copy command)
        "powershell",    // PowerShell (Copy-Item)
        "pwsh",          // PowerShell Core
        "totalcmd",      // Total Commander
        "7zfm",          // 7-Zip File Manager
        "winrar",        // WinRAR
        "teracopy",      // TeraCopy
        "fastcopy",      // FastCopy
    };

    // Processes that generate file activity that should be IGNORED
    private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome",
        "msedge",
        "firefox",
        "code",           // VS Code
        "devenv",         // Visual Studio
        "searchindexer",
        "searchprotocolhost",
        "svchost",
        "system",
        "antimalware service executable",
        "msmpeng",        // Windows Defender
        "onedrive",
        "dropbox",
        "googledrivesync",
        "teams",
        "outlook",
        "winword",        // Word (autosave, not user copy)
        "excel",          // Excel (autosave)
    };

    public ProcessHelper(ILogger<ProcessHelper> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Checks if the process that created/wrote a file is a user-initiated file copy operation.
    /// Returns the process name if found, or null if it cannot be determined.
    /// </summary>
    public (bool isUserInitiated, string? processName) IsUserInitiatedCopy(string filePath)
    {
        try
        {
            var lockingProcesses = GetLockingProcesses(filePath);
            if (lockingProcesses.Count == 0)
            {
                // No process has the file locked — could be a completed copy.
                // For completed copies, we can't determine the process, so we
                // allow it through and rely on path-based filtering.
                return (true, null);
            }

            foreach (var proc in lockingProcesses)
            {
                try
                {
                    var procName = proc.ProcessName;
                    if (UserCopyProcesses.Contains(procName))
                        return (true, procName);
                    if (SystemProcesses.Contains(procName))
                        return (false, procName);
                }
                catch (Exception)
                {
                    // Process may have exited
                }
            }

            // Unknown process — allow it but log the process name
            var firstProc = lockingProcesses.FirstOrDefault();
            var name = firstProc?.ProcessName ?? "unknown";
            _logger.LogDebug("File {File} held by unclassified process: {Process}", filePath, name);
            return (true, name);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not determine locking process for {File}", filePath);
            return (true, null); // Allow through if we can't determine
        }
    }

    /// <summary>
    /// Uses the Windows Restart Manager API to find processes that have a file open.
    /// </summary>
    private static List<Process> GetLockingProcesses(string filePath)
    {
        var processes = new List<Process>();

        int res = RmStartSession(out uint sessionHandle, 0, Guid.NewGuid().ToString());
        if (res != 0)
            return processes;

        try
        {
            string[] resources = { filePath };
            res = RmRegisterResources(sessionHandle, (uint)resources.Length, resources, 0, null, 0, null);
            if (res != 0)
                return processes;

            uint pnProcInfoNeeded = 0;
            uint pnProcInfo = 0;
            uint lpdwRebootReasons = 0;

            // First call to get the number of processes
            res = RmGetList(sessionHandle, out pnProcInfoNeeded, ref pnProcInfo, null, ref lpdwRebootReasons);

            if (res == ERROR_MORE_DATA && pnProcInfoNeeded > 0)
            {
                var processInfo = new RM_PROCESS_INFO[pnProcInfoNeeded];
                pnProcInfo = pnProcInfoNeeded;

                res = RmGetList(sessionHandle, out pnProcInfoNeeded, ref pnProcInfo, processInfo, ref lpdwRebootReasons);
                if (res == 0)
                {
                    for (int i = 0; i < pnProcInfo; i++)
                    {
                        try
                        {
                            processes.Add(Process.GetProcessById(processInfo[i].Process.dwProcessId));
                        }
                        catch (ArgumentException)
                        {
                            // Process has already exited
                        }
                    }
                }
            }
        }
        finally
        {
            RmEndSession(sessionHandle);
        }

        return processes;
    }

    #region Windows Restart Manager P/Invoke

    private const int ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle,
        uint nFiles, string[]? rgsFileNames,
        uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle,
        out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    #endregion
}
