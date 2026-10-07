using System.Text;
using WinShell.Core.Files;
namespace WinShell.Infrastructure.Ftp;

internal static class FtpPathPolicy
{
    internal static string CommandValue(string value, int maximum = 4096, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Any(char.IsControl) || new UTF8Encoding(false, true).GetByteCount(value) > maximum)
            throw new ArgumentException("FTP 字段包含控制字符、无效编码或超出长度限制。");
        return value;
    }
    internal static string RemotePath(string path) => CommandValue(SftpPathPolicy.RemotePath(path));
    internal static string Child(string directory, string name) => RemotePath(SftpPathPolicy.RemoteChild(RemotePath(directory), CommandValue(name)));
}
