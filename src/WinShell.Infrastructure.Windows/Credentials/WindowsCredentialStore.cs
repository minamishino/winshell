using System.Security.Cryptography;
using WinShell.Core.Credentials;

namespace WinShell.Infrastructure.Windows.Credentials;

public sealed class WindowsCredentialStore : ICredentialStore
{
    // A process-wide budget for the shared Windows store, not a claim about the
    // underlying native anomaly. Admission includes the active operation and is
    // bounded before copying secrets (at most 64 * 2560 owned payload bytes).
    private const int MaximumAdmittedOperations = 64;
    private static readonly SemaphoreSlim NativeGate = new(1, 1);
    private static int _admittedOperations;
    private readonly IWindowsCredentialApi _nativeApi;

    public WindowsCredentialStore() : this(new WindowsCredentialApi()) { }
    internal WindowsCredentialStore(IWindowsCredentialApi nativeApi) => _nativeApi = nativeApi;

    public Task<CredentialSecret?> ReadAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var targetName = GetTargetName(profileId);
        Admit(cancellationToken);
        return ExecuteAsync(() => _nativeApi.Read(targetName), cancellationToken);
    }

    public Task WriteAsync(Guid profileId, ReadOnlyMemory<byte> secretUtf8, CancellationToken cancellationToken)
    {
        var targetName = GetTargetName(profileId);
        if (secretUtf8.Length > CredentialNativeMethods.MaximumBlobBytes)
            throw new ArgumentOutOfRangeException(nameof(secretUtf8), "Credential exceeds the 2560-byte limit.");
        Admit(cancellationToken);
        byte[] ownedSecret;
        try { ownedSecret = secretUtf8.ToArray(); }
        catch { Interlocked.Decrement(ref _admittedOperations); throw; }

        return ExecuteAsync(() =>
        {
            _nativeApi.Write(targetName, ownedSecret);
            using var confirmation = ReadForConfirmation(targetName);
            if (confirmation is null || !confirmation.Bytes.Span.SequenceEqual(ownedSecret))
                throw new CredentialOperationUnconfirmedException();
            return true;
        }, cancellationToken, ownedSecret);
    }

    public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var targetName = GetTargetName(profileId);
        Admit(cancellationToken);
        return ExecuteAsync(() =>
        {
            _nativeApi.Delete(targetName);
            using var confirmation = ReadForConfirmation(targetName);
            return confirmation is null ? true : throw new CredentialOperationUnconfirmedException();
        }, cancellationToken);
    }

    private CredentialSecret? ReadForConfirmation(string targetName)
    {
        try { return _nativeApi.Read(targetName); }
        catch (Exception exception) { throw new CredentialOperationUnconfirmedException(exception); }
    }

    private static void Admit(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _admittedOperations) <= MaximumAdmittedOperations) return;
        Interlocked.Decrement(ref _admittedOperations);
        throw new InvalidOperationException("Windows credential operation queue is full.");
    }

    private static async Task<T> ExecuteAsync<T>(Func<T> operation, CancellationToken cancellationToken, byte[]? ownedSecret = null)
    {
        var entered = false;
        try
        {
            await NativeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            cancellationToken.ThrowIfCancellationRequested();
            // Once native work begins, settle it and its single confirmation even
            // if cancellation arrives. Never abandon a mutation with unknown state.
            return await Task.Run(operation, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (ownedSecret is not null) CryptographicOperations.ZeroMemory(ownedSecret);
            if (entered) NativeGate.Release();
            Interlocked.Decrement(ref _admittedOperations);
        }
    }

    private static string GetTargetName(Guid profileId)
    {
        return profileId != Guid.Empty
            ? $"WinShell/connection/{profileId:D}"
            : throw new ArgumentException("A nonempty connection profile ID is required.", nameof(profileId));
    }
}
