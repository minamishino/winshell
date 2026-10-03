using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
namespace WinShell.Core.Files;

public sealed record RemoteFileEntry(string Name, string FullPath, bool IsDirectory, bool IsRegularFile, long Length);
public sealed record RemoteDirectoryPage(string Path, IReadOnlyList<RemoteFileEntry> Entries, bool IsLimited);
public sealed record FileTransferProgress(long BytesTransferred, long TotalBytes);
public sealed record FileTransferResult(long BytesTransferred, string Sha256);
public interface ISftpSession : IAsyncDisposable
{
    bool IsConnected { get; }
    Task<RemoteDirectoryPage> ListDirectoryAsync(string path, CancellationToken cancellationToken);
    Task<FileTransferResult> DownloadAsync(string remotePath, string localPath, IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken);
    Task<FileTransferResult> UploadAsync(string localPath, string remoteDirectory, string fileName, IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken);
}
public interface ISftpSessionFactory
{
    Task<SftpConnectResult> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken);
}
public abstract record SftpConnectResult
{
    private SftpConnectResult() { }
    public sealed record Connected(ISftpSession Session, SshHostKey Key) : SftpConnectResult;
    public sealed record UnknownHostKey(SshEndpointIdentity Endpoint, SshHostKey Key) : SftpConnectResult;
}
