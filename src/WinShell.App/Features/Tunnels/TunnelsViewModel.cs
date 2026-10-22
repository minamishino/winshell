using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
using WinShell.Core.Tunnels;
namespace WinShell.App.Features.Tunnels;

// UI-affine: each row retains its start/close operation and session until true cleanup completion.
public sealed partial class TunnelsViewModel(ITunnelFactory factory, IKnownHostRepository knownHosts, ITunnelDefinitionRepository repository,
    Func<TunnelStartResult.UnknownHostKey,CancellationToken,Task<bool>> confirm) : ObservableObject,IAsyncDisposable
{
    public ObservableCollection<TunnelRowViewModel> Rows { get; }=[];
    private readonly HashSet<Guid> _deletedProfiles=[];
    private readonly CancellationTokenSource _stop=new();
    private readonly SemaphoreSlim _mutations=new(1,1);
    private bool _loaded,_disposed; private Task? _disposal;
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,_stop.Token);
        await _mutations.WaitAsync(linked.Token);
        try { if(_loaded) return; var definitions=await repository.ReadAsync(linked.Token); linked.Token.ThrowIfCancellationRequested(); foreach(var definition in definitions) Rows.Add(new TunnelRowViewModel(definition)); _loaded=true; }
        finally { _mutations.Release(); }
    }
    public async Task SaveAsync(TunnelDefinition definition,CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed,this); TunnelDefinitionValidator.Validate(definition);
        await LoadAsync(cancellationToken);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,_stop.Token);
        await _mutations.WaitAsync(linked.Token);
        try {
            if(_deletedProfiles.Contains(definition.ProfileId)) throw new InvalidOperationException("SSH 配置已删除。");
            var existing=Rows.FirstOrDefault(row=>row.Definition.Id==definition.Id);
            if(existing?.Busy==true) throw new InvalidOperationException("请先停止隧道再修改定义。");
            if(existing is null && Rows.Count>=64) throw new InvalidOperationException("最多保存 64 个隧道定义。");
            var definitions=Rows.Where(row=>row!=existing).Select(row=>row.Definition).Append(definition).ToArray();
            await repository.ReplaceAsync(definitions,linked.Token);
            if(existing is not null) Rows.Remove(existing); Rows.Add(new TunnelRowViewModel(definition));
        } finally {_mutations.Release();}
    }
    public Task StartAsync(TunnelRowViewModel row,ConnectionProfile profile)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(!Rows.Contains(row) || _deletedProfiles.Contains(profile.Id)) throw new InvalidOperationException("隧道或 SSH 配置已删除。");
        if(row.Busy) return row.Operation;
        row.Cancellation=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); var token=row.Cancellation.Token;
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); row.Operation=completion.Task;
        row.Error=""; row.Status="正在连接"; _=StartCoreAsync(); return completion.Task;
        async Task StartCoreAsync() {
            try {
                var result=await factory.StartAsync(profile,row.Definition,token);
                if(result is TunnelStartResult.UnknownHostKey challenge) {
                    token.ThrowIfCancellationRequested(); row.Status="等待确认主机公钥";
                    if(!await confirm(challenge,token)) { row.Status="未信任主机"; return; }
                    token.ThrowIfCancellationRequested();
                    if(!await knownHosts.TrustIfAbsentOrSameAsync(challenge.Endpoint,challenge.Key,token)) throw new SshConnectionException(SshFailureKind.HostKeyChanged,"可信主机记录已改变。");
                    token.ThrowIfCancellationRequested(); result=await factory.StartAsync(profile,row.Definition,token);
                }
                if(result is not TunnelStartResult.Started started) throw new SshConnectionException(SshFailureKind.HostKeyChanged,"重新连接未确认同一主机。");
                if(token.IsCancellationRequested || _disposed || _deletedProfiles.Contains(profile.Id)) { await started.Session.DisposeAsync(); token.ThrowIfCancellationRequested(); return; }
                row.Session=started.Session; row.Publish();
            } catch(OperationCanceledException) { row.Status="已取消"; }
            catch(Exception error) { row.Status="启动失败"; row.Error=Describe(error); }
            finally { row.Cancellation.Dispose(); row.Cancellation=null; completion.SetResult(); }
        }
    }
    public static Task StopAsync(TunnelRowViewModel row)
    {
        if(row.Closing is not null) return row.Closing;
        row.Cancellation?.Cancel(); row.Status="正在关闭";
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); row.Closing=completion.Task;
        _=CloseCoreAsync(); return completion.Task;
        async Task CloseCoreAsync() {
            try { await row.Operation; if(row.Session is { } session) await session.DisposeAsync(); row.Session=null; row.Status="已停止"; row.Publish(); completion.SetResult(); }
            catch(Exception error) { row.Error=Describe(error); completion.SetException(error); }
            finally { row.Closing=null; }
        }
    }
    public async Task DeleteAsync(TunnelRowViewModel row,CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed,this); await StopAsync(row);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,_stop.Token); await _mutations.WaitAsync(linked.Token);
        try { TunnelDefinition[] remaining = [.. Rows.Where(value=>value!=row).Select(value=>value.Definition)]; await repository.ReplaceAsync(remaining,linked.Token); Rows.Remove(row); }
        finally {_mutations.Release();}
    }
    public async Task DeleteProfileAsync(Guid profileId)
    {
        await LoadAsync(_stop.Token);
        var affected = Rows.Where(value=>value.Definition.ProfileId==profileId).ToArray();
        if(affected.Length==0)return;
        _deletedProfiles.Add(profileId); // Only currently referenced definitions; no permanent deletion history.
        try {
            await Task.WhenAll(affected.Select(StopAsync));
            await _mutations.WaitAsync(_stop.Token);
            try { var remaining=Rows.Where(value=>value.Definition.ProfileId!=profileId).ToArray(); TunnelDefinition[] definitions = [.. remaining.Select(row=>row.Definition)]; await repository.ReplaceAsync(definitions,_stop.Token); foreach(var row in Rows.Except(remaining).ToArray()) Rows.Remove(row); }
            finally {_mutations.Release();}
        } finally {_deletedProfiles.Remove(profileId);}
    }
    public void Publish() { foreach(var row in Rows) row.Publish(); }
    private static string Describe(Exception error)=>error switch { SshConnectionException ssh => $"SSH 错误：{ssh.Kind}", OperationCanceledException => "操作已取消。", _ => "隧道操作失败，请检查参数、网络与文件权限。" };
    public ValueTask DisposeAsync() { if(_disposal is not null)return new ValueTask(_disposal); _disposed=true; _stop.Cancel(); _disposal=CloseAllAsync(); return new ValueTask(_disposal); }
    private async Task CloseAllAsync() {
        try { await Task.WhenAll(Rows.Select(StopAsync)); await _mutations.WaitAsync(); _mutations.Release(); }
        finally {_stop.Dispose();}
    }
}
