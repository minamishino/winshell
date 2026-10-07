using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using WinShell.Core.Credentials;
using WinShell.Core.Profiles;

namespace WinShell.App.Features.Connections;

// UI-affine: callers use the dispatcher context; awaits deliberately preserve that context.
public sealed class ConnectionProfilesViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IConnectionProfileRepository _repository;
    private readonly ICredentialStore _credentials;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ProfileCollection _visibleProfiles = new();
    private readonly List<ConnectionProfile> _allProfiles = [];
    private Task _activeOperation = Task.CompletedTask;
    private Task? _disposal;
    private bool _disposed, _isBusy;
    private string _searchText = "";
    private string? _errorMessage, _statusMessage;
    private ConnectionProfile? _selectedProfile;

    public ConnectionProfilesViewModel(IConnectionProfileRepository repository, ICredentialStore credentials)
    {
        _repository = repository;
        _credentials = credentials;
        Profiles = new(_visibleProfiles);
    }

    public ReadOnlyObservableCollection<ConnectionProfile> Profiles { get; }
    public event EventHandler<Guid>? ProfileDeleted;
    // The window binds this to the actual terminal page; the lease spans every repository/vault write.
    public Func<Guid, CancellationToken, Task<IDisposable>>? BeginTerminalProfileMutationAsync { get; set; }
    public ConnectionProfile? SelectedProfile { get => _selectedProfile; set => SetProperty(ref _selectedProfile, value); }
    public string SearchText
    {
        get => _searchText;
        set { if (!_disposed && SetProperty(ref _searchText, value)) ApplySearch(); }
    }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(CanEdit)); } }
    public bool CanEdit => !IsBusy && !_disposed;
    public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public void ReportValidationError(string message) { if (!_disposed) { ErrorMessage = message; StatusMessage = null; } }

    public Task LoadAsync(CancellationToken cancellationToken) => RunAsync(async token =>
    {
        var result = await _repository.ReadAsync(token);
        if (_disposed) return;
        _allProfiles.Clear();
        _allProfiles.AddRange(result.Profiles);
        ApplySearch();
        StatusMessage = result.RecoveredFromBackup
            ? "主配置无法读取，已从备份恢复显示。原文件未被静默覆盖；下次明确保存时写入恢复后的配置。"
            : $"已加载 {_allProfiles.Count} 个连接配置。";
    }, cancellationToken);

    public Task SaveProfileAsync(ConnectionProfile profile, CancellationToken cancellationToken) => RunAsync(async token =>
    {
        var errors = ConnectionProfileValidator.Validate(profile);
        if (errors.Count != 0) throw new ArgumentException(string.Join(Environment.NewLine, errors.Select(error => error.Message)));
        using var mutation = await BeginMutationAsync(profile.Id, token);
        await _repository.UpsertAsync(profile, token);
        if (_disposed) return;
        var index = _allProfiles.FindIndex(item => item.Id == profile.Id);
        if (index < 0) _allProfiles.Add(profile); else _allProfiles[index] = profile;
        ApplySearch();
        SelectedProfile = profile;
        StatusMessage = "连接配置已保存。凭据需要单独保存。";
    }, cancellationToken);

    public Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken) => RunAsync(async token =>
    {
        RequireSavedProfile(profileId);
        using var mutation = await BeginMutationAsync(profileId, token);
        await _credentials.DeleteAsync(profileId, token);
        try
        {
            token.ThrowIfCancellationRequested();
            await _repository.DeleteAsync(profileId, token);
        }
        catch (Exception exception)
        {
            // The credential side effect has committed, including when cancellation follows it.
            if (!_disposed) ErrorMessage = $"凭据已删除；磁盘上的配置是否删除尚未确认，请重新加载确认。当前页面的列表及编辑快照已保留。{DescribeFailure(exception)}";
            return;
        }
        if (_disposed) return;
        _allProfiles.RemoveAll(profile => profile.Id == profileId);
        if (SelectedProfile?.Id == profileId) SelectedProfile = null;
        ApplySearch();
        StatusMessage = "连接配置及其凭据已删除。";
        ProfileDeleted?.Invoke(this, profileId);
    }, cancellationToken);

    public Task SaveCredentialAsync(Guid profileId, ReadOnlyMemory<byte> secretUtf8, CancellationToken cancellationToken)
    {
        // Snapshot before any await; never hold the caller's mutable memory across asynchronous work.
        if (secretUtf8.Length is 0 or > 2560)
            return RunAsync(_ => throw new ArgumentException("请输入凭据（UTF-8 最多 2560 字节）；空输入不会删除已有凭据。"), cancellationToken);
        if (_disposed) return Task.FromException(new ObjectDisposedException(nameof(ConnectionProfilesViewModel)));
        if (IsBusy) return Task.CompletedTask;
        var ownedSecret = secretUtf8.ToArray();
        return RunAsync(async token =>
        {
            try
            {
                RequireSavedProfile(profileId);
                using var mutation = await BeginMutationAsync(profileId, token);
                await _credentials.WriteAsync(profileId, ownedSecret, token);
                if (!_disposed) StatusMessage = "凭据已保存到 Windows 凭据管理器。";
            }
            finally { CryptographicOperations.ZeroMemory(ownedSecret); }
        }, cancellationToken, () => CryptographicOperations.ZeroMemory(ownedSecret));
    }

    public Task DeleteCredentialAsync(Guid profileId, CancellationToken cancellationToken) => RunAsync(async token =>
    {
        RequireSavedProfile(profileId);
        using var mutation = await BeginMutationAsync(profileId, token);
        await _credentials.DeleteAsync(profileId, token);
        if (!_disposed) StatusMessage = "凭据已删除；连接配置保持不变。";
    }, cancellationToken);

    public Task CheckCredentialAsync(Guid profileId, CancellationToken cancellationToken) => RunAsync(async token =>
    {
        RequireSavedProfile(profileId);
        using var secret = await _credentials.ReadAsync(profileId, token);
        if (!_disposed) StatusMessage = secret is null ? "此连接未保存凭据。" : "此连接已保存凭据（不显示内容）。";
    }, cancellationToken);

    private void RequireSavedProfile(Guid profileId)
    {
        if (!_allProfiles.Any(profile => profile.Id == profileId))
            throw new ArgumentException("请先保存连接配置，再管理其凭据。");
    }
    private async Task<IDisposable?> BeginMutationAsync(Guid profileId, CancellationToken token)
    {
        if (BeginTerminalProfileMutationAsync is not { } begin) return null;
        StatusMessage = "正在关闭此连接的终端，再更新配置或凭据；保存后请重新打开终端。";
        try { return await begin(profileId, token); }
        catch (AggregateException aggregate)
        {
            throw new InvalidOperationException($"终端关闭失败，未开始修改配置或凭据：{aggregate.Flatten().InnerExceptions[0].Message}", aggregate);
        }
    }

    private Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken, Action? cleanup = null)
    {
        if (_disposed) { cleanup?.Invoke(); return Task.FromException(new ObjectDisposedException(nameof(ConnectionProfilesViewModel))); }
        if (IsBusy) { cleanup?.Invoke(); return Task.CompletedTask; }
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        _activeOperation = ExecuteAsync();
        return _activeOperation;

        async Task ExecuteAsync()
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            try
            {
                linkedCancellation.Token.ThrowIfCancellationRequested();
                await operation(linkedCancellation.Token);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested) { }
            catch (Exception exception) { if (!_disposed) ErrorMessage = DescribeFailure(exception); }
            finally { cleanup?.Invoke(); IsBusy = false; }
        }
    }

    private static string DescribeFailure(Exception exception) => exception switch
    {
        OperationCanceledException => "操作已取消。",
        CredentialOperationUnconfirmedException => "Windows 凭据写入或删除结果尚未确认，请检查凭据状态后再操作。",
        UnauthorizedAccessException => "没有访问配置或凭据的权限。",
        System.ComponentModel.Win32Exception => "Windows 凭据管理器操作失败，请检查当前用户权限。",
        InvalidDataException => "配置文件无效或版本不受支持，未覆盖原文件。",
        NotSupportedException => "当前应用不支持此配置版本或格式，未覆盖原文件。请使用兼容的应用版本。",
        IOException => "配置文件读写失败，请检查磁盘空间、文件权限或占用。",
        TimeoutException => "等待配置文件锁超时，请稍后重试。",
        ArgumentException argument => argument.Message,
        InvalidOperationException operation => operation.Message,
        _ => "操作失败；配置或凭据未能完成更新。"
    };

    private void ApplySearch()
    {
        var query = SearchText.Trim();
        var selectedId = SelectedProfile?.Id;
        _visibleProfiles.Replace(_allProfiles.Where(profile =>
            profile.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            profile.GroupName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            profile.Host.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray());
        SelectedProfile = _visibleProfiles.FirstOrDefault(profile => profile.Id == selectedId);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposal is not null) return new(_disposal);
        _disposed = true;
        OnPropertyChanged(nameof(CanEdit));
        _disposal = SettleAsync();
        return new(_disposal);
    }
    private async Task SettleAsync()
    {
        List<Exception> failures = [];
        try { _lifetime.Cancel(); }
        catch (Exception exception) { failures.Add(exception); }
        try { await _activeOperation; }
        catch (Exception exception) { failures.Add(exception); }
        finally { _lifetime.Dispose(); }
        if (failures.Count != 0) throw new AggregateException("配置操作关闭时发生错误，已等待操作结束。", failures);
    }

    private sealed class ProfileCollection : ObservableCollection<ConnectionProfile>
    {
        public void Replace(IReadOnlyList<ConnectionProfile> profiles)
        {
            Items.Clear();
            foreach (var profile in profiles) Items.Add(profile);
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
