using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentFTP;
using FluentFTP.Helpers;
using WinShell.Core.Connections;
using WinShell.Core.Credentials;
using WinShell.Core.Files;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
namespace WinShell.Infrastructure.Ftp;

internal sealed class FtpFileSession(ConnectionProfile profile, ICredentialStore credentials, ConnectionAdmissionBudget budget, IConnectionProfileRepository profiles, TimeSpan timeout, X509ChainPolicy? chainPolicy) : IRemoteFileSession
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _operation, _close;
    public bool IsReady
    {
        get
        {
            lock (_gate)
                return _close is null;
        }
    }
    internal Task ValidateAsync(CancellationToken token) => RunAsync((_, _) => Task.FromResult(true), false, token);
    public Task<RemoteDirectoryPage> ListDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        FtpPathPolicy.RemotePath(path);
        return RunAsync((client, token) => ListCoreAsync(client, path, token), true, cancellationToken);
    }
    private async Task<RemoteDirectoryPage> ListCoreAsync(BoundedFtpClient client, string path, CancellationToken token)
    {
        var machine = client.HasFeature(FtpCapability.MLST);
        var parser = new FtpListParser(client);
        parser.Init(client.ServerOS, machine ? FtpParser.Machine : FtpParser.Auto);
        var stream = await client.OpenOwnedDataAsync((machine ? "MLSD " : "LIST ") + path, token).ConfigureAwait(false);
        List<RemoteFileEntry> entries = [];
        var buffer = new byte[4096];
        var line = new byte[16384];
        var lineLength = 0;
        var total = 0;
        using var lineDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        lineDeadline.CancelAfter(timeout);
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, 1048576 - total)), lineDeadline.Token).ConfigureAwait(false);
            if (count == 0)
            {
                if (lineLength != 0)
                    throw new RemoteFileException(RemoteFileFailureKind.Protocol, "FTP 目录行不完整。");
                await client.ConfirmCompletionAsync(stream, token).ConfigureAwait(false);
                return new RemoteDirectoryPage(path, entries.AsReadOnly(), false);
            }
            total += count;
            for (var index = 0; index < count; index++)
            {
                var value = buffer[index];
                if (value == 10)
                {
                    var length = lineLength > 0 && line[lineLength - 1] == 13 ? lineLength - 1 : lineLength;
                    var text = new UTF8Encoding(false, true).GetString(line, 0, length);
                    lineLength = 0;
                    lineDeadline.CancelAfter(timeout);
                    if (text.Length == 0)
                        continue;
                    var item = parser.ParseSingleLine(path, text, client.Capabilities, machine);
                    if (item is null)
                        throw new RemoteFileException(RemoteFileFailureKind.Protocol, "无法解析 FTP 目录记录。");
                    if (item.Name is "." or "..")
                        continue;
                    if (item is { Size: < 0, Type: FtpObjectType.File })
                        throw new RemoteFileException(RemoteFileFailureKind.Protocol, "FTP 文件长度无效。");
                    entries.Add(new RemoteFileEntry(item.Name, FtpPathPolicy.Child(path, item.Name), item.Type == FtpObjectType.Directory, item.Type == FtpObjectType.File, Math.Max(0, item.Size)));
                    if (entries.Count == 1000)
                        return new RemoteDirectoryPage(path, entries.AsReadOnly(), true);
                }
                else
                {
                    if (lineLength == line.Length)
                        return new RemoteDirectoryPage(path, entries.AsReadOnly(), true);
                    line[lineLength++] = value;
                }
            }
            if (total == 1048576)
                return new RemoteDirectoryPage(path, entries.AsReadOnly(), true);
        }
    }
    private async Task EnsureAbsentAsync(BoundedFtpClient client, string directory, string target, string? temporary, CancellationToken token)
    {
        var page = await ListCoreAsync(client, directory, token).ConfigureAwait(false);
        if (page.Entries.Any(entry => string.Equals(entry.FullPath, target, StringComparison.OrdinalIgnoreCase) || string.Equals(entry.FullPath, temporary, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("上传目标或临时路径已存在；不覆盖文件。");
        if (page.IsLimited)
            throw new IOException("目录快照达到上限，无法确认目标不存在；上传已拒绝。");
    }
    public Task<FileTransferResult> DownloadAsync(string remotePath, string localPath, IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken)
    {
        FtpPathPolicy.RemotePath(remotePath);
        localPath = SftpPathPolicy.LocalFile(localPath);
        if (File.Exists(localPath) || Directory.Exists(localPath))
            throw new IOException("下载目标已存在；不覆盖文件。");
        return RunAsync(async (client, token) =>
        {
            var temporary = Path.Combine(Path.GetDirectoryName(localPath)!, ".winshell-" + Guid.NewGuid().ToString("N") + ".tmp");
            var owned = false;
            Exception? failure = null;
            try
            {
                long size;
                using (var deadline = Deadline(token))
                    size = await client.GetFileSize(remotePath, -1, deadline.Token).ConfigureAwait(false);
                if (size < 0)
                    throw new IOException("服务器未提供有效的文件长度，拒绝下载。");
                FileTransferResult result;
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    owned = true;
                    var stream = await client.OpenOwnedDataAsync("RETR " + remotePath, token).ConfigureAwait(false);
                    result = await CopyAsync(stream, output, size, progress, token).ConfigureAwait(false);
                    await client.ConfirmCompletionAsync(stream, token).ConfigureAwait(false);
                    await output.FlushAsync(token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                SftpPathPolicy.LocalFile(localPath);
                File.Move(temporary, localPath, overwrite: false);
                owned = false;
                return result;
            }
            catch (Exception error) { failure = error; throw; }
            finally { if (owned) try { File.Delete(temporary); } catch (Exception cleanup) when (failure is not null) { failure.Data["LocalTemporaryFile"] = temporary; failure.Data["FtpCleanupFailure"] = cleanup; } }
        }, true, cancellationToken);
    }
    public Task<FileTransferResult> UploadAsync(string localPath, string remoteDirectory, string fileName, IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken)
    {
        localPath = SftpPathPolicy.LocalFile(localPath);
        var target = FtpPathPolicy.Child(remoteDirectory, fileName);
        var temporary = FtpPathPolicy.Child(remoteDirectory, ".winshell-" + Guid.NewGuid().ToString("N") + ".tmp");
        return RunAsync(async (client, token) =>
        {

            try
            {
                await EnsureAbsentAsync(client, remoteDirectory, target, temporary, token).ConfigureAwait(false);
                await using var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var stream = await client.OpenOwnedDataAsync("STOR " + temporary, token).ConfigureAwait(false);
                var result = await CopyAsync(input, stream, input.Length, progress, token).ConfigureAwait(false);
                await client.ConfirmCompletionAsync(stream, token).ConfigureAwait(false);
                using var deadline = Deadline(token);
                await EnsureAbsentAsync(client, remoteDirectory, target, null, deadline.Token).ConfigureAwait(false);
                await client.Rename(temporary, target, deadline.Token).ConfigureAwait(false);
                return result;
            }
            catch (Exception error)
            {
                if (client.StoreAttempted)
                {
                    error.Data["RemoteMutationPhase"] = client.RenameAttempted ? "RenameTemporary" : "StoreTemporary";
                    error.Data["RemoteTemporaryOwnershipConfirmed"] = false;
                    error.Data["RemoteTemporaryFile"] = temporary;
                }
                if (client.RenameAttempted && error is not FluentFTP.Exceptions.FtpCommandException)
                    error.Data["RemoteDestinationFile"] = target;
                throw;
            }
        }, true, cancellationToken);
    }
    private CancellationTokenSource Deadline(CancellationToken token)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        return deadline;
    }
    private async Task<FileTransferResult> CopyAsync(Stream input, Stream output, long size, IProgress<FileTransferProgress>? progress, CancellationToken token)
    {
        var buffer = new byte[65536];
        long copied = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var last = Environment.TickCount64;
        while (true)
        {
            using var deadline = Deadline(token);
            var count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
            if (count == 0)
                break;
            if (count > size - copied)
                throw new IOException("文件长度在传输期间发生变化。");
            await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
            hash.AppendData(buffer.AsSpan(0, count));
            copied += count;
            if (Environment.TickCount64 - last < 100) continue;
            progress?.Report(new FileTransferProgress(copied, size));
            last = Environment.TickCount64;
        }
        if (copied != size)
            throw new IOException("文件长度在传输期间发生变化。");
        await output.FlushAsync(token).ConfigureAwait(false);
        progress?.Report(new FileTransferProgress(copied, size));
        return new FileTransferResult(copied, Convert.ToHexString(hash.GetHashAndReset()));
    }
    private Task<T> RunAsync<T>(Func<BoundedFtpClient, CancellationToken, Task<T>> operation, bool data, CancellationToken caller)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_close is not null, this);
            if (_operation is not null)
                throw new InvalidOperationException("此 FTP 会话已有操作正在运行。");
            caller.ThrowIfCancellationRequested();
            var dataLease = data ? budget.AdmitTransfer() : null;
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _operation = completion.Task;
            _ = Task.Run(async () =>
            {
                Exception? failure = null;
                BoundedFtpClient? client = null;
                IDisposable? connection = null, handshake = null;
                T result = default!;
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _stop.Token);
                    var saved = (await profiles.ReadAsync(linked.Token).ConfigureAwait(false)).Profiles.FirstOrDefault(item => item.Id == profile.Id);
                    if (saved is null || saved with
                    {
                        DisplayName = profile.DisplayName,
                        GroupName = profile.GroupName
                    } != profile)
                        throw new InvalidOperationException("配置已更改或删除；请重新选择文件连接。");
                    connection = budget.AdmitConnection();
                    client = new BoundedFtpClient(profile, credentials, timeout, chainPolicy, budget);
                    using (var deadline = Deadline(linked.Token))
                    {
                        handshake = await budget.EnterHandshakeAsync(deadline.Token).ConfigureAwait(false);
                        await client.Connect(deadline.Token).ConfigureAwait(false);
                        handshake.Dispose();
                        handshake = null;
                    }
                    result = await operation(client, linked.Token).ConfigureAwait(false);
                }
                catch (Exception error) { failure = error; }
                finally { try { if (client is not null) await client.CloseOwnedAsync().ConfigureAwait(false); } catch (Exception cleanup) { if (failure is not null) failure.Data["FtpCleanupFailure"] = cleanup; else failure = cleanup; } finally { handshake?.Dispose(); connection?.Dispose(); dataLease?.Dispose(); lock (_gate) _operation = null; } }
                if (failure is null)
                    completion.SetResult(result);
                else
                    completion.SetException(Classify(failure, caller.IsCancellationRequested || _stop.IsCancellationRequested));
            }, CancellationToken.None);
            return completion.Task;
        }
    }
    private static Exception Classify(Exception error, bool cancelled)
    {
        if (!cancelled && error is ArgumentException or InvalidOperationException or SshConnectionException or RemoteFileException)
            return error;
        Exception classified = error switch
        {
            _ when cancelled => new OperationCanceledException("FTP 操作已取消。", error),
            FluentFTP.Exceptions.FtpAuthenticationException => new RemoteFileException(RemoteFileFailureKind.Authentication, "FTP 用户名或密码认证失败。", error),
            _ when ContainsAuthenticationError(error) => new RemoteFileException(RemoteFileFailureKind.Certificate, "FTPS 证书、主机名或 TLS 验证失败。", error),
            OperationCanceledException or TimeoutException => new RemoteFileException(RemoteFileFailureKind.Timeout, "FTP 网络操作超时。", error),
            UnauthorizedAccessException => new RemoteFileException(RemoteFileFailureKind.Permission, "FTP 文件或目录访问被拒绝。", error),
            FluentFTP.Exceptions.FtpCommandException command when command.CompletionCode.StartsWith("55", StringComparison.Ordinal) =>
                new RemoteFileException(RemoteFileFailureKind.Permission, "FTP 文件或目录访问被拒绝。", error),
            FluentFTP.Exceptions.FtpException => new RemoteFileException(RemoteFileFailureKind.Protocol, "FTP 协议操作失败。", error),
            IOException => new RemoteFileException(RemoteFileFailureKind.Network, error.Message, error),
            _ => new RemoteFileException(RemoteFileFailureKind.Network, "FTP 网络连接失败。", error)
        };
        foreach (System.Collections.DictionaryEntry entry in error.Data)
            classified.Data[entry.Key] = entry.Value;
        return classified;
    }
    private static bool ContainsAuthenticationError(Exception error) => error is System.Security.Authentication.AuthenticationException || error.InnerException is { } inner && ContainsAuthenticationError(inner);
    public ValueTask DisposeAsync()
    {
        lock (_gate)
            return new ValueTask(_close ??= Task.Run(async () =>
            {
                Exception? cancellationFailure = null;
                try { await _stop.CancelAsync().ConfigureAwait(false); }
                catch (Exception error) { cancellationFailure = error; }
                // Cancellation callbacks may fail, but admitted work still owns its resources until joined.
                Task? operation;
                lock (_gate) operation = _operation;
                if (operation is not null)
                {
                    try { await operation.ConfigureAwait(false); }
                    catch (Exception) { /* Caller owns operation error. */ }
                }
                _stop.Dispose();
                if (cancellationFailure is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cancellationFailure).Throw();
            }));
    }
}
