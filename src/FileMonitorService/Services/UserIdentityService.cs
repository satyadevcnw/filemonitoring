using System.Security.Principal;

namespace FileMonitorService.Services;

/// <summary>
/// Resolves the current Active Directory / Windows user identity.
/// </summary>
public sealed class UserIdentityService
{
    private readonly ILogger<UserIdentityService> _logger;

    public UserIdentityService(ILogger<UserIdentityService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the currently logged-in Windows/AD username in DOMAIN\Username format.
    /// </summary>
    public string GetCurrentUserName()
    {
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            return identity.Name; // Returns DOMAIN\Username
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve Windows identity, falling back to Environment.UserName");
            return Environment.UserName;
        }
    }

    /// <summary>
    /// Attempts to resolve the owner of a file to determine who performed the copy.
    /// Uses the file's ACL to find the owner SID and resolves it to an AD account.
    /// </summary>
    public string GetFileOwner(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var security = fileInfo.GetAccessControl();
            var owner = security.GetOwner(typeof(NTAccount));
            return owner?.Value ?? GetCurrentUserName();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve file owner for {FilePath}, using current user", filePath);
            return GetCurrentUserName();
        }
    }
}
