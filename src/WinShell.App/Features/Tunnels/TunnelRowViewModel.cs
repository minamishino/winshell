using CommunityToolkit.Mvvm.ComponentModel;
using WinShell.Core.Tunnels;
namespace WinShell.App.Features.Tunnels;
public sealed class TunnelRowViewModel(TunnelDefinition definition) : ObservableObject
{
    public TunnelDefinition Definition { get; } = definition;
    public string Name => Definition.Name;
    public string Kind => Definition.Kind switch { TunnelKind.Local => "本地转发", TunnelKind.Remote => "远程转发", _ => "动态 SOCKS" };
    private string _status = "已停止", _error = "";
    private ushort _boundPort;
    private int _activeConnections;
    public string Status { get => _status; internal set => SetProperty(ref _status,value); }
    public string Error { get => _error; internal set => SetProperty(ref _error,value); }
    public ushort BoundPort { get => _boundPort; private set => SetProperty(ref _boundPort,value); }
    public int ActiveConnections { get => _activeConnections; private set => SetProperty(ref _activeConnections,value); }
    internal ITunnelSession? Session;
    internal CancellationTokenSource? Cancellation;
    internal Task Operation = Task.CompletedTask;
    internal Task? Closing;
    internal bool Busy => Cancellation is not null || Session is not null || Closing is not null;
    internal void Publish()
    {
        if(Session is not { } session) { ActiveConnections=0; return; }
        var snapshot=session.Snapshot; BoundPort=session.BoundPort; ActiveConnections=snapshot.ActiveConnections;
        Status=snapshot.State switch { TunnelState.Running=>"运行中",TunnelState.Closing=>"正在关闭",TunnelState.Closed=>"已断开，请停止后重新启动",TunnelState.Failed=>"连接失败",_=>"正在启动" };
        if(snapshot.Failure is { } failure) Error=$"最近转发错误：{failure}";
    }
}
