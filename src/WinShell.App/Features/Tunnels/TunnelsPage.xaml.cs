using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
using WinShell.Core.Tunnels;
namespace WinShell.App.Features.Tunnels;
public sealed partial class TunnelsPage : Page
{
    private readonly IConnectionProfileRepository _profiles;
    private readonly NativeDialogService _dialogs;
    private readonly DispatcherQueueTimer _timer;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<Task> _actions=[];
    private bool _closing,_acting; private Task? _shutdown;
    public TunnelsViewModel ViewModel {get;}
    public TunnelsPage(ITunnelFactory factory,IKnownHostRepository knownHosts,ITunnelDefinitionRepository definitions,IConnectionProfileRepository profiles,NativeDialogService dialogs)
    {
        _profiles=profiles; _dialogs=dialogs; ViewModel=new(factory,knownHosts,definitions,ConfirmAsync);
        InitializeComponent(); DataContext=ViewModel;
        _timer=DispatcherQueue.CreateTimer(); _timer.Interval=TimeSpan.FromMilliseconds(100); _timer.Tick+=Tick; _timer.Start();
    }
    private void Tick(DispatcherQueueTimer sender,object args)=>ViewModel.Publish();
    private Task<bool> ConfirmAsync(TunnelStartResult.UnknownHostKey challenge,CancellationToken token)=>_dialogs.ConfirmAsync(XamlRoot,"确认隧道主机公钥",$"主机：{challenge.Endpoint.Host}:{challenge.Endpoint.Port}\n公钥类型：{challenge.Key.Algorithm}\n完整指纹：{challenge.Key.Fingerprint}\n\n尚未认证。请核实指纹后信任并重新连接。","信任并连接",token);
    public Task LoadAsync()=>RunAsync(async()=>{
        await ViewModel.LoadAsync(_stop.Token); var saved=await _profiles.ReadAsync(_stop.Token); _stop.Token.ThrowIfCancellationRequested();
        Profiles.ItemsSource=saved.Profiles.Where(profile=>profile.Protocol==ConnectionProtocol.Ssh).ToArray(); Profiles.SelectedIndex=Profiles.Items.Count==0?-1:0;
    });
    private void Kind_SelectionChanged(object sender,SelectionChangedEventArgs args) { if(TargetFields is not null) TargetFields.Visibility=Kind.SelectedIndex==2?Visibility.Collapsed:Visibility.Visible; }
    private void Definitions_SelectionChanged(object sender,SelectionChangedEventArgs args)
    {
        if(Definitions.SelectedItem is not TunnelRowViewModel row)return;
        var definition=row.Definition; TunnelName.Text=definition.Name; Kind.SelectedIndex=(int)definition.Kind; ListenPort.Value=definition.BoundPort;
        TargetHost.Text=definition.TargetHost; TargetPort.Value=definition.TargetPort==0?22:definition.TargetPort;
        Profiles.SelectedItem=Profiles.Items.OfType<ConnectionProfile>().FirstOrDefault(profile=>profile.Id==definition.ProfileId);
    }
    private void New_Click(object sender,RoutedEventArgs args) { Definitions.SelectedItem=null; TunnelName.Text=""; TunnelName.Focus(FocusState.Programmatic); }
    private async void Reload_Click(object sender,RoutedEventArgs args)=>await LoadAsync();
    private async void Save_Click(object sender,RoutedEventArgs args)=>await RunAsync(async()=>{
        if(Profiles.SelectedItem is not ConnectionProfile profile)throw new ArgumentException("请选择已保存的 SSH 配置。");
        var kind=(TunnelKind)Kind.SelectedIndex;
        static ushort Port(double value) { if(!double.IsFinite(value)||value!=Math.Truncate(value)||value<0||value>65535)throw new ArgumentException("请输入有效整数端口。"); return (ushort)value; }
        var definition=new TunnelDefinition((Definitions.SelectedItem as TunnelRowViewModel)?.Definition.Id??Guid.NewGuid(),TunnelName.Text,profile.Id,kind,Port(ListenPort.Value),kind==TunnelKind.Dynamic?"":TargetHost.Text,kind==TunnelKind.Dynamic?(ushort)0:Port(TargetPort.Value));
        await ViewModel.SaveAsync(definition,_stop.Token); Definitions.SelectedItem=ViewModel.Rows.First(row=>row.Definition.Id==definition.Id); Feedback.Text="定义已保存，尚未自动启动。";
    });
    private async void Start_Click(object sender,RoutedEventArgs args)=>await RunAsync(async()=>{
        if(Definitions.SelectedItem is not TunnelRowViewModel row)throw new ArgumentException("请选择隧道定义。");
        var saved=await _profiles.ReadAsync(_stop.Token); var profile=saved.Profiles.FirstOrDefault(value=>value.Id==row.Definition.ProfileId)??throw new ArgumentException("引用的 SSH 配置不存在。");
        await ViewModel.StartAsync(row,profile);
    });
    private async void Stop_Click(object sender,RoutedEventArgs args) { if(Definitions.SelectedItem is TunnelRowViewModel row)await TrackAsync(()=>ViewModel.StopAsync(row)); }
    private async void Delete_Click(object sender,RoutedEventArgs args)=>await RunAsync(async()=>{
        if(Definitions.SelectedItem is not TunnelRowViewModel row)return;
        if(await _dialogs.ConfirmAsync(XamlRoot,"删除隧道定义",$"停止并删除“{row.Name}”？","删除",_stop.Token))await ViewModel.DeleteAsync(row,_stop.Token);
    });
    public Task CloseProfileAsync(Guid profileId)=>TrackAsync(()=>ViewModel.DeleteProfileAsync(profileId));
    private async Task RunAsync(Func<Task> action)
    {
        if(_closing||_acting)return; _acting=true;
        try { await TrackAsync(action); } finally {_acting=false;}
    }
    private Task TrackAsync(Func<Task> action)
    {
        if(_closing||_actions.Count>=16)return Task.CompletedTask;
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _actions.Add(completion.Task); _=CompleteAsync(); return completion.Task;
        async Task CompleteAsync() {
            try { await action(); }
            catch(OperationCanceledException) { Feedback.Text="操作已取消。"; }
            catch(Exception error) { Feedback.Text=error is ArgumentException or InvalidOperationException ? error.Message : "隧道操作失败，请检查配置与文件权限。"; }
            finally {_actions.Remove(completion.Task);completion.SetResult();}
        }
    }
    public Task BeginShutdownAsync()
    {
        if(_shutdown is not null)return _shutdown; _closing=true; IsEnabled=false; _timer.Stop(); _timer.Tick-=Tick; _stop.Cancel();
        _shutdown=CloseAsync();return _shutdown;
    }
    private async Task CloseAsync() { try { await Task.WhenAll(_actions.Append(ViewModel.DisposeAsync().AsTask()).ToArray()); } finally {_stop.Dispose();} }
}
