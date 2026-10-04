using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using Renci.SshNet.Common;
using WinShell.Core.Connections;
using WinShell.Core.Files;
using WinShell.Core.Ssh;
using SshConnectionException = WinShell.Core.Ssh.SshConnectionException;
namespace WinShell.Infrastructure.Ssh;

// One operation per client; close cancels and joins its core before disconnecting the subsystem.
internal sealed class SftpSession : ISftpSession
{
    private readonly SftpClient _client;
    private readonly SshHostKeyPolicy _policy;
    private readonly ConnectionAdmissionBudget _budget;
    private readonly IDisposable _admission;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _operation, _close, _disconnect;
    private readonly Task _watch;
    internal SftpSession(SftpClient client, SshHostKeyPolicy policy, ConnectionAdmissionBudget budget, IDisposable admission)
    {
        _client = client; _policy = policy; _budget = budget; _admission = admission;
        _watch = WatchAsync();
    }
    public bool IsConnected { get { lock (_gate) return _close is null && _client.IsConnected; } }
    public Task<RemoteDirectoryPage> ListDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        SftpPathPolicy.RemotePath(path);
        return RunAsync(async token =>
        {
            var entries = new List<RemoteFileEntry>(); var bytes = 0; var limited = false;
            await foreach (var file in _client.ListDirectoryAsync(path, token).ConfigureAwait(false))
            {
                if (file.Name is "." or "..") continue;
                var nextBytes = Encoding.UTF8.GetByteCount(file.Name);
                if (bytes + nextBytes > 1024 * 1024) { limited = true; break; }
                var child = SftpPathPolicy.RemoteChild(path, file.Name);
                entries.Add(new RemoteFileEntry(file.Name, child, file.IsDirectory, file.IsRegularFile, file.Length));
                bytes += nextBytes;
                if (entries.Count == 1000) { limited = true; break; }
            }
            return new RemoteDirectoryPage(path, entries.AsReadOnly(), limited);
        }, false, cancellationToken);
    }
    public Task<FileTransferResult> DownloadAsync(string remotePath, string localPath, IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken)
    {
        SftpPathPolicy.RemotePath(remotePath); localPath = SftpPathPolicy.LocalFile(localPath);
        return RunAsync(token => DownloadCoreAsync(remotePath, localPath, progress, token), true, cancellationToken);
    }
    private async Task<FileTransferResult> DownloadCoreAsync(string remotePath, string localPath, IProgress<FileTransferProgress>? progress, CancellationToken token)
    {
        if (File.Exists(localPath) || Directory.Exists(localPath)) throw new IOException("下载目标已存在；首版不覆盖文件。");
        var temporary = Path.Combine(Path.GetDirectoryName(localPath)!, ".winshell-" + Guid.NewGuid().ToString("N") + ".tmp");
        var ownsTemporary = false;
        Exception? failure = null;
        try
        {
            var attributes = await _client.GetAttributesAsync(remotePath, token).ConfigureAwait(false);
            if (!attributes.IsRegularFile || attributes.Size < 0) throw new IOException("仅支持下载普通文件。");
            FileTransferResult result;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                ownsTemporary = true;
                var remote = await _client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, token).ConfigureAwait(false);
                result = await CopyRemoteAsync(remote, output, false, attributes.Size, progress, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            SftpPathPolicy.LocalFile(localPath); // Recheck before committing the owned temporary file.
            File.Move(temporary, localPath, overwrite: false); ownsTemporary = false;
            return result;
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            if (ownsTemporary)
                try { File.Delete(temporary); }
                catch (Exception cleanup) when (failure is not null) { failure.Data["LocalTemporaryFile"] = temporary; failure.Data["SftpCleanupFailure"] = cleanup; }
        }
    }
    public Task<FileTransferResult> UploadAsync(string localPath, string remoteDirectory, string fileName, IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken)
    {
        localPath = SftpPathPolicy.LocalFile(localPath);
        var target = SftpPathPolicy.RemoteChild(remoteDirectory, fileName);
        var temporary = SftpPathPolicy.RemoteChild(remoteDirectory, ".winshell-" + Guid.NewGuid().ToString("N") + ".tmp");
        return RunAsync(token => UploadCoreAsync(localPath, target, temporary, progress, token), true, cancellationToken);
    }
    private async Task<FileTransferResult> UploadCoreAsync(string localPath, string target, string temporary, IProgress<FileTransferProgress>? progress, CancellationToken token)
    {
        var ownsTemporary = false; var streamClosed = false;
        var createAttempted = false; var renameAttempted = false; var renameRejected = false;
        try
        {
            if (await _client.ExistsAsync(target, token).ConfigureAwait(false)) throw new IOException("上传目标已存在；首版不覆盖文件。");
            await using var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            // OpenAsync includes OPEN and FSTAT. Failure can occur after the server created the path,
            // even when this API never returns a stream/handle proving ownership to this caller.
            createAttempted = true;
            var remote = await _client.OpenAsync(temporary, FileMode.CreateNew, FileAccess.Write, token).ConfigureAwait(false);
            ownsTemporary = true;
            var result = await CopyRemoteAsync(remote, input, true, input.Length, progress, token).ConfigureAwait(false);
            streamClosed = true;
            token.ThrowIfCancellationRequested();
            if (await _client.ExistsAsync(target, token).ConfigureAwait(false)) throw new IOException("上传目标已存在，临时文件未提交。");
            // Standard SFTP v3 RENAME fails if destination exists; never use POSIX overwrite rename.
            renameAttempted = true;
            try { await _client.RenameFileAsync(temporary, target, token).ConfigureAwait(false); }
            catch (SftpException) { renameRejected = true; throw; } // A received failure STATUS confirms rejection.
            ownsTemporary = false;
            return result;
        }
        catch (Exception error)
        {
            var renameUnconfirmed = renameAttempted && !renameRejected;
            if (createAttempted)
            {
                error.Data["RemoteMutationPhase"] = renameAttempted ? "RenameTemporary" : ownsTemporary ? "WriteTemporary" : "CreateTemporary";
                error.Data["RemoteTemporaryOwnershipConfirmed"] = ownsTemporary;
                if (!ownsTemporary) error.Data["RemoteTemporaryFile"] = temporary;
            }
            if (renameUnconfirmed) error.Data["RemoteDestinationFile"] = target;
            if (ownsTemporary)
            {
                // After a failed write, disconnect precedes stream disposal to prevent a cancelled tail flush.
                // A successfully closed stream permits one bounded best-effort unlink of our exact temporary path.
                // An unacknowledged rename may already have moved the owned file. Neither path is
                // safe to delete or retry now; retain both exact paths and the original failure.
                if (streamClosed && !renameUnconfirmed && _client.IsConnected)
                {
                    try { using var cleanup = new CancellationTokenSource(_client.OperationTimeout); await _client.DeleteFileAsync(temporary, cleanup.Token).ConfigureAwait(false); ownsTemporary = false; }
                    catch (Exception cleanupError) { error.Data["SftpCleanupFailure"] = cleanupError; }
                }
                if (ownsTemporary) error.Data["RemoteTemporaryFile"] = temporary;
            }
            throw;
        }
    }
    private async Task<FileTransferResult> CopyRemoteAsync(SftpFileStream remote, FileStream local, bool upload, long expected, IProgress<FileTransferProgress>? progress, CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            var buffer = new byte[65536]; long copied = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Stream input = upload ? local : remote, output = upload ? remote : local;
            int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if (count > expected - copied) throw new IOException("文件长度在传输期间发生变化。");
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                hash.AppendData(buffer.AsSpan(0, count)); copied += count;
                progress?.Report(new FileTransferProgress(copied, expected));
            }
            if (copied != expected) throw new IOException("文件长度在传输期间发生变化。");
            if (upload) await remote.FlushAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new FileTransferResult(copied, Convert.ToHexString(hash.GetHashAndReset()));
        }
        catch (Exception error)
        {
            failure = error;
            try { await DisconnectTransportAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { error.Data["SftpDisconnectFailure"] = cleanup; }
            throw;
        }
        finally
        {
            try { await remote.DisposeAsync().ConfigureAwait(false); } // CLOSE uses finite OperationTimeout, and must succeed before commit.
            catch (Exception cleanup) when (failure is not null) { failure.Data["SftpStreamCleanupFailure"] = cleanup; }
        }
    }
    private Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, bool transfer, CancellationToken callerToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_close is not null, this);
            if (_operation is not null) throw new InvalidOperationException("此 SFTP 会话已有操作正在运行。");
            callerToken.ThrowIfCancellationRequested();
            var transferLease = transfer ? _budget.AdmitTransfer() : null;
            var core = Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _stop.Token);
                return await operation(linked.Token).ConfigureAwait(false);
            });
            _operation = core;
            return CompleteAsync(core, transferLease);
        }
    }
    private async Task<T> CompleteAsync<T>(Task<T> core, IDisposable? transferLease)
    {
        try { return await core.ConfigureAwait(false); }
        catch (Exception error)
        {
            try { await BeginClose().ConfigureAwait(false); }
            catch (Exception cleanup) { error.Data["SftpSessionCleanupFailure"] = cleanup; }
            if (_policy.Failure is not null)
            {
                foreach (System.Collections.DictionaryEntry entry in error.Data) _policy.Failure.Data[entry.Key] = entry.Value;
                throw _policy.Failure;
            }
            if (error is OperationCanceledException or ArgumentException or IOException) throw;
            var classified = error is SftpPermissionDeniedException or UnauthorizedAccessException
                ? new SshConnectionException(SshFailureKind.Permission, "SFTP 操作被拒绝。", error) : SshSessionFactory.Classify(error);
            foreach (System.Collections.DictionaryEntry entry in error.Data) classified.Data[entry.Key] = entry.Value;
            throw classified;
        }
        finally { lock (_gate) _operation = null; transferLease?.Dispose(); }
    }
    private async Task WatchAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                if (_client.IsConnected && _policy.Failure is null) continue;
                _ = BeginClose(); return;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private Task DisconnectTransportAsync() { lock (_gate) return _disconnect ??= Task.Run(_client.Disconnect); }
    public ValueTask DisposeAsync() => new(BeginClose());
    private Task BeginClose()
    {
        lock (_gate) return _close ??= Task.Run(async () =>
        {
            try
            {
                await _stop.CancelAsync().ConfigureAwait(false);
                Task? operation; lock (_gate) operation = _operation;
                if (operation is not null) try { await operation.ConfigureAwait(false); } catch (Exception) { /* CompleteAsync observes and classifies the original operation failure. */ }
                await _watch.ConfigureAwait(false);
                await DisconnectTransportAsync().ConfigureAwait(false);
            }
            finally
            {
                try { _client.Dispose(); }
                finally { _client.HostKeyReceived -= _policy.OnReceived; _stop.Dispose(); _admission.Dispose(); }
            }
        });
    }
}
