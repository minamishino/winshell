using WinShell.Core.Terminal;
namespace WinShell.Core.Ssh;

/// <summary>An authenticated connection may reserve exactly one deferred interactive terminal.</summary>
public interface ISshTerminalSession : ISshSession
{
    IShellSession CreateTerminalSession(ushort columns, ushort rows, string? startupCommand = null);
}
