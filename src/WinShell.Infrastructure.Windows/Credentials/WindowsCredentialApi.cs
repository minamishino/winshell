using System.ComponentModel;
using System.Runtime.InteropServices;
using WinShell.Core.Credentials;

namespace WinShell.Infrastructure.Windows.Credentials;

internal sealed class WindowsCredentialApi : IWindowsCredentialApi
{
    public void Delete(string targetName)
    {
        if (!CredentialNativeMethods.Delete(targetName, CredentialNativeMethods.GenericCredentialType, 0))
        {
            var errorCode = Marshal.GetLastPInvokeError();
            if (errorCode != CredentialNativeMethods.ErrorNotFound)
                throw new Win32Exception(errorCode, "Windows Credential Manager delete failed.");
        }
    }
    public unsafe CredentialSecret? Read(string targetName)
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

    public unsafe void Write(string targetName, byte[] ownedSecret)
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
