using System.Security.AccessControl;
using System.Security.Principal;

namespace WinShell.Infrastructure.Windows.Profiles;

public static class ProfileStorageAccess
{
    public static void EnsureCurrentUserAccess(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var directory = new DirectoryInfo(directoryPath);
        if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("配置目录不能是重解析点。");
        }

        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User ?? throw new UnauthorizedAccessException("无法确定当前用户。");
        var security = new DirectorySecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        directory.Create(security);
        directory.SetAccessControl(security);
    }
}
