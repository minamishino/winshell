using Renci.SshNet;

namespace WinShell.Infrastructure.Ssh;

/// <summary>Adapts SSH.NET's synchronous public authentication hook to owned cancellation.</summary>
internal sealed class CancellableAuthenticationMethod(AuthenticationMethod inner, CancellationToken cancellationToken)
    : AuthenticationMethod(inner.Username)
{
    public override string Name => inner.Name;

    public override AuthenticationResult Authenticate(Session session)
    {
        var disconnectTask = Task.CompletedTask;
        Exception? authenticationFailure = null;
        var registration = cancellationToken.Register(() => disconnectTask = Task.Run(session.Disconnect));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = inner.Authenticate(session);
            AllowedAuthentications = inner.AllowedAuthentications;
            return result;
        }
        catch (Exception error) { authenticationFailure = error; throw; }
        finally
        {
            // Dispose joins the tiny scheduling callback before reading its task. Authenticate is always
            // entered by the factory's background worker. Join here before SSH.NET may dispose Session
            // on unwinding; never leave Disconnect racing native session disposal or a reused handle.
            registration.Dispose();
            try { disconnectTask.GetAwaiter().GetResult(); }
            catch (Exception cleanupError) when (authenticationFailure is not null)
            {
                authenticationFailure.Data["SshDisconnectFailure"] = cleanupError;
            }
        }
    }
}
