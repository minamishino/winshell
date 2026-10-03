using System.Security.AccessControl;
using System.Security.Principal;

namespace WinShell.Infrastructure.Windows.Profiles;

public static class ProfileStorageAccess
{
    private static readonly string[] ConfigurationFileNames = ["profiles.json", "profiles.backup.json", "profiles.lock"];

    public static void EnsureCurrentUserAccess(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var directory = new DirectoryInfo(directoryPath);
        if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("配置目录不能是重解析点。");
        }

        // Check every known path before changing the directory ACL, which may propagate to children.
        foreach (var fileName in ConfigurationFileNames)
        {
            IsRegularFileOrMissing(Path.Combine(directory.FullName, fileName));
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

    internal static void EnsureConfigurationFileAccess(string directoryPath)
    {
        foreach (var fileName in ConfigurationFileNames)
        {
            EnsureCurrentUserFileAccess(Path.Combine(directoryPath, fileName));
        }
    }

    internal static void EnsureCurrentUserFileAccess(string filePath)
    {
        if (!IsRegularFileOrMissing(filePath))
        {
            return;
        }
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User ?? throw new UnauthorizedAccessException("无法确定当前用户。");
        // Build a replacement DACL; adding a user rule to an existing ACL would retain foreign grants.
        var security = new FileSecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(filePath).SetAccessControl(security);
    }

    internal static bool IsRegularFileOrMissing(string filePath)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(filePath);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new IOException("配置文件不能是重解析点或目录。");
        }
        return true;
    }
}
