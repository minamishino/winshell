namespace WinShell.Core.Profiles;

public sealed record ProfileReadResult(IReadOnlyList<ConnectionProfile> Profiles, bool RecoveredFromBackup);
