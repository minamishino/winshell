using WinShell.Core.Credentials;

namespace WinShell.Infrastructure.Windows.Credentials;

// Synchronous Windows boundary: buffers are borrowed only for the native call.
internal interface IWindowsCredentialApi
{
    CredentialSecret? Read(string targetName);
    void Write(string targetName, byte[] ownedSecret);
    void Delete(string targetName);
}
