namespace WinShell.Core.Tunnels;
public enum TunnelKind { Local, Remote, Dynamic }
public sealed record TunnelDefinition(Guid Id, string Name, Guid ProfileId, TunnelKind Kind, ushort BoundPort, string TargetHost, ushort TargetPort);
