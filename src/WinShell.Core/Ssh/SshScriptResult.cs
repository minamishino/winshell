namespace WinShell.Core.Ssh;

/// <summary>Complete bounded UTF-8 output and actual peer metadata. Missing status is never inferred as zero.</summary>
public sealed record SshScriptResult(string StandardOutput, string StandardError, int? ExitStatus,
    string? ExitSignal, bool RemoteEofReceived);
