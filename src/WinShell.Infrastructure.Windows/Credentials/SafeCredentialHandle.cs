using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace WinShell.Infrastructure.Windows.Credentials;

internal sealed class SafeCredentialHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeCredentialHandle() : base(ownsHandle: true) { }

    protected override unsafe bool ReleaseHandle()
    {
        // CredRead owns this allocation; clear its writable blob before returning it to Windows.
        var credential = (CredentialNativeMethods.NativeCredential*)handle;
        if (credential->CredentialBlob != 0)
        {
            var boundedSize = (int)Math.Min(credential->CredentialBlobSize, CredentialNativeMethods.MaximumBlobBytes);
            CryptographicOperations.ZeroMemory(new Span<byte>((void*)credential->CredentialBlob, boundedSize));
        }
        CredentialNativeMethods.Free(handle);
        return true;
    }
}
