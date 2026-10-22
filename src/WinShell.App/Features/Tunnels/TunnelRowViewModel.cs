using CommunityToolkit.Mvvm.ComponentModel;
using WinShell.Core.Tunnels;
namespace WinShell.App.Features.Tunnels;
public sealed partial class TunnelRowViewModel(TunnelDefinition definition) : ObservableObject
{
    public TunnelDefinition Definition { get; } = definition;
    public string Name => Definition.Name;
    // WinUI uses the item's string representation to name realized and virtualized list entries.
    public override string ToString() => Name;
    public string Kind => Definition.Kind switch { TunnelKind.Local => "本地转发", TunnelKind.Remote => "远程转发", _ => "动态 SOCKS" };
    [ObservableProperty]
    public partial string Status { get; internal set; } = "已停止";
    [ObservableProperty]
    public partial string Error { get; internal set; } = "";
    [ObservableProperty]
    public partial ushort BoundPort { get; private set; }
    [ObservableProperty]
    public partial int ActiveConnections { get; private set; }
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
