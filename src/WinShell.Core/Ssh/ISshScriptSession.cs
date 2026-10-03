namespace WinShell.Core.Ssh;

/// <summary>Optional, single-use Linux/POSIX script capability. Execution owns and closes the entire connection.</summary>
public interface ISshScriptSession : ISshSession
{
    /// <summary>Runs at most 8192 UTF-8 bytes without a PTY. Output limits are 32768 stdout / 16384 stderr bytes.
    /// Cancellation and completion join owned workers and close the connection before returning.</summary>
    Task<SshScriptResult> RunScriptAsync(string script, CancellationToken cancellationToken);
}
