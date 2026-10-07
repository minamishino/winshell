using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
namespace WinShell.Core.Files;

public sealed record RemoteFileEntry(string Name, string FullPath, bool IsDirectory, bool IsRegularFile, long Length);
public sealed record RemoteDirectoryPage(string Path, IReadOnlyList<RemoteFileEntry> Entries, bool IsLimited);
public sealed record FileTransferProgress(long BytesTransferred, long TotalBytes);
public sealed record FileTransferResult(long BytesTransferred, string Sha256);
public interface IRemoteFileSession : IAsyncDisposable
{
    bool IsReady
    {
        get;
    }
    Task<RemoteDirectoryPage> ListDirectoryAsync(string path, CancellationToken cancellationToken);
    Task<FileTransferResult> DownloadAsync(string remotePath, string localPath, IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken);
    Task<FileTransferResult> UploadAsync(string localPath, string remoteDirectory, string fileName, IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken);
}
public interface IRemoteFileSessionFactory
{
    Task<RemoteFileConnectResult> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken, bool allowPlaintext = false);
}
public abstract record RemoteFileConnectResult
{
    private RemoteFileConnectResult()
    {
    }
    public sealed record Connected(IRemoteFileSession Session, SshHostKey? Key = null) : RemoteFileConnectResult;
    public sealed record UnknownHostKey(SshEndpointIdentity Endpoint, SshHostKey Key) : RemoteFileConnectResult;
}
