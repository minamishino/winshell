using CommunityToolkit.Mvvm.ComponentModel;
using WinShell.Core.Settings;

namespace WinShell.App.Features.Settings;

// Dispatcher-affine, with a single owned operation. Appearance writes never modify grants.
public sealed class SettingsViewModel(IAppearanceSettingsStore store) : ObservableObject, IAsyncDisposable
{
    private readonly CancellationTokenSource _loadCancellation = new();
    private AppearanceTheme _theme;
    private string _status = "";
    private bool _busy, _closing;
    private Task _pending = Task.CompletedTask;
    private Task? _close;
    public AppearanceTheme Theme { get => _theme; private set => SetProperty(ref _theme, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool CanEdit => !_busy && !_closing;

    public Task LoadAsync() => RunAsync(async () =>
    {
        var saved = await store.ReadAsync(_loadCancellation.Token);
        if (!_closing) { Theme = saved; Status = ""; }
    }, saving: false);

    public Task SetThemeAsync(AppearanceTheme theme)
    {
        if (!Enum.IsDefined(theme)) throw new ArgumentOutOfRangeException(nameof(theme));
        if (!CanEdit) return Task.CompletedTask;
        Theme = theme;
        return SaveAsync();
    }

    public Task SaveAsync() => RunAsync(async () =>
    {
        // Close joins an admitted save so the last explicit choice survives immediate exit.
        await store.SaveAsync(Theme, CancellationToken.None);
        Status = "外观已保存。";
    }, saving: true);

    private Task RunAsync(Func<Task> operation, bool saving)
    {
        if (!CanEdit) return Task.CompletedTask;
        _busy = true; OnPropertyChanged(nameof(CanEdit));
        return _pending = ExecuteAsync();
        async Task ExecuteAsync()
        {
            await Task.Yield();
            try { await operation(); }
            catch (OperationCanceledException) when (_closing && !saving) { }
            catch (Exception error)
            {
                var reason = error switch
                {
                    UnauthorizedAccessException => "没有访问设置的权限。",
                    TimeoutException => "设置正在被其他窗口使用，请稍后重试。",
                    InvalidDataException => "已有设置损坏或版本不受支持，原文件已保留。",
                    _ => "无法访问外观设置，请重试。"
                };
                Status = (saving ? "外观未保存。" : "未能读取外观设置。") + reason;
            }
            finally { _busy = false; OnPropertyChanged(nameof(CanEdit)); }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_close is not null) return new(_close);
        _closing = true; OnPropertyChanged(nameof(CanEdit));
        _loadCancellation.Cancel();
        return new(_close = CloseAsync());
    }
    private async Task CloseAsync()
    {
        try { await _pending; }
        finally { _loadCancellation.Dispose(); }
    }
}
