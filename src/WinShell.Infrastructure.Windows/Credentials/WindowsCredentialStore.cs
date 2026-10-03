using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WinShell.Core.Credentials;

namespace WinShell.Infrastructure.Windows.Credentials;

public sealed class WindowsCredentialStore : ICredentialStore
{
    public Task<CredentialSecret?> ReadAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var targetName = GetTargetName(profileId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Read(targetName);
        }, cancellationToken);
    }

    public async Task WriteAsync(Guid profileId, ReadOnlyMemory<byte> secretUtf8, CancellationToken cancellationToken)
    {
        var targetName = GetTargetName(profileId);
        cancellationToken.ThrowIfCancellationRequested();
        if (secretUtf8.Length > CredentialNativeMethods.MaximumBlobBytes)
            throw new ArgumentOutOfRangeException(nameof(secretUtf8), "Credential exceeds the 2560-byte limit.");

        // Snapshot on the caller thread before returning; never clear or pin caller-owned storage.
        var ownedSecret = secretUtf8.ToArray();
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Write(targetName, ownedSecret);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ownedSecret);
        }
    }

    public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var targetName = GetTargetName(profileId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CredentialNativeMethods.Delete(targetName, CredentialNativeMethods.GenericCredentialType, 0))
            {
                var errorCode = Marshal.GetLastPInvokeError();
                if (errorCode != CredentialNativeMethods.ErrorNotFound)
                    throw new Win32Exception(errorCode, "Windows Credential Manager delete failed.");
            }
        }, cancellationToken);
    }

    private static string GetTargetName(Guid profileId)
    {
        if (profileId == Guid.Empty)
            throw new ArgumentException("A nonempty connection profile ID is required.", nameof(profileId));
        return $"WinShell/connection/{profileId:D}";
    }

    private static unsafe CredentialSecret? Read(string targetName)
    {
        var succeeded = CredentialNativeMethods.Read(targetName, CredentialNativeMethods.GenericCredentialType, 0, out var credentialHandle);
        var errorCode = succeeded ? 0 : Marshal.GetLastPInvokeError();
        using (credentialHandle)
        {
            if (!succeeded)
            {
                if (errorCode == CredentialNativeMethods.ErrorNotFound)
                    return null;
                throw new Win32Exception(errorCode, "Windows Credential Manager read failed.");
            }
            if (credentialHandle.IsInvalid)
                throw new IOException("Windows Credential Manager returned an invalid allocation.");

            // This local handle is owned exclusively and cannot be disposed until this copy completes.
            var credential = (CredentialNativeMethods.NativeCredential*)credentialHandle.DangerousGetHandle();
            if (credential->CredentialBlobSize > CredentialNativeMethods.MaximumBlobBytes ||
                (credential->CredentialBlobSize != 0 && credential->CredentialBlob == 0))
                throw new IOException("Windows Credential Manager returned an invalid blob size or pointer.");

            return new CredentialSecret(new ReadOnlySpan<byte>((void*)credential->CredentialBlob, (int)credential->CredentialBlobSize));
        }
    }

    private static unsafe void Write(string targetName, byte[] ownedSecret)
    {
        fixed (char* targetPointer = targetName)
        fixed (byte* secretPointer = ownedSecret)
        {
            var credential = new CredentialNativeMethods.NativeCredential
            {
                Type = CredentialNativeMethods.GenericCredentialType,
                TargetName = (nint)targetPointer,
                CredentialBlobSize = (uint)ownedSecret.Length,
                CredentialBlob = (nint)secretPointer,
                Persist = CredentialNativeMethods.PersistLocalMachine
            };
            if (!CredentialNativeMethods.Write(in credential, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows Credential Manager write failed.");
        }
    }
}
