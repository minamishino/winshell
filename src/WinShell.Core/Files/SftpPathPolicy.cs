using System.Text;
namespace WinShell.Core.Files;

public static class SftpPathPolicy
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static string RemotePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\0') || StrictUtf8.GetByteCount(path) > 4096) throw new ArgumentException("远端路径不能包含 NUL 或超过 4096 UTF-8 字节。");
        return path;
    }
    public static string RemoteChild(string directory, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0) throw new ArgumentException("远端文件名必须是单个名称。");
        return RemotePath(RemotePath(directory).TrimEnd('/') + "/" + name);
    }
    public static string LocalFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.Contains('\0'))
            throw new ArgumentException("请选择本机绝对文件路径。");
        var root = Path.GetPathRoot(path)!;
        foreach (var component in path[root.Length..].Split(['/', '\\']))
        {
            if (component.Length == 0 || component is "." or ".." || component.EndsWith('.') || component.EndsWith(' ') || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("本地路径包含不安全的文件名、流或相对路径。");
            var stem = component.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]))
                throw new ArgumentException("本地路径不能使用设备名称。");
        }
        var fullPath = Path.GetFullPath(path);
        for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("本地路径不能经过重解析点。"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return fullPath;
    }
}
