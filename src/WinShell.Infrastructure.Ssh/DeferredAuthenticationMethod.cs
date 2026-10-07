using Renci.SshNet;
using WinShell.Core.Credentials;
using WinShell.Core.Profiles;

namespace WinShell.Infrastructure.Ssh;

// SSH.NET invokes this public hook only after successful host-key validation.
// The factory runs connection/authentication on its background worker; the outer
// cancellable adapter owns disconnect and joins it before the session can unwind.
internal sealed class DeferredAuthenticationMethod(ConnectionProfile profile, ICredentialStore credentials, CancellationToken token)
    : Renci.SshNet.AuthenticationMethod(profile.UserName)
{
    public override string Name => profile.AuthenticationMethod == Core.Profiles.AuthenticationMethod.Password ? "password" : "publickey";

    public override AuthenticationResult Authenticate(Session session)
    {
        token.ThrowIfCancellationRequested();
        using var scope = SshAuthenticationScope.CreateAsync(profile, credentials, token).GetAwaiter().GetResult();
        token.ThrowIfCancellationRequested();
        var result = scope.Method.Authenticate(session);
        AllowedAuthentications = scope.Method.AllowedAuthentications;
        return result;
    }
}
