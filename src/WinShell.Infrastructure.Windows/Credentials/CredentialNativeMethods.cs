using System.Runtime.InteropServices;

namespace WinShell.Infrastructure.Windows.Credentials;

internal static partial class CredentialNativeMethods
{
    internal const uint GenericCredentialType = 1;
    internal const uint PersistLocalMachine = 2;
    internal const uint MaximumBlobBytes = 2560;
    internal const int ErrorNotFound = 1168;

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Read(string targetName, uint credentialType, uint flags, out SafeCredentialHandle credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Write(in NativeCredential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Delete(string targetName, uint credentialType, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    internal static partial void Free(nint credential);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeCredential
    {
        internal uint Flags;
        internal uint Type;
        internal nint TargetName;
        internal nint Comment;
        // FILETIME is two DWORDs; primitive fields keep this boundary fully blittable.
        internal uint LastWrittenLowDateTime;
        internal uint LastWrittenHighDateTime;
        internal uint CredentialBlobSize;
        internal nint CredentialBlob;
        internal uint Persist;
        internal uint AttributeCount;
        internal nint Attributes;
        internal nint TargetAlias;
        internal nint UserName;
    }
}
