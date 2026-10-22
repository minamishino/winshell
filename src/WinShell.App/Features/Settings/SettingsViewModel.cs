using CommunityToolkit.Mvvm.ComponentModel;
using WinShell.Core.Settings;

namespace WinShell.App.Features.Settings;

// Dispatcher-affine, with a single owned operation. Appearance writes never modify grants.
public sealed partial class SettingsViewModel(IApplicationSettingsStore store) : ObservableObject, IAsyncDisposable
{
    private readonly CancellationTokenSource _loadCancellation = new();

    private bool _busy, _closing;

    private Task _pending = Task.CompletedTask;
    private Task? _close;
    public ApplicationSettings Settings
    {
        get;
        private set { if (SetProperty(ref field, value)) OnPropertyChanged(nameof(Theme)); }
    } = new();
    public AppearanceTheme Theme => Settings.Theme;
    [ObservableProperty]
    public partial string Status { get; private set; } = "";
    [ObservableProperty]
    public partial bool HasSaveFailure { get; private set; }
    public bool CanEdit => !_busy && !_closing;

    public Task LoadAsync() => RunAsync(async () =>
    {
        var saved = await store.ReadAsync(_loadCancellation.Token);
        if (!_closing) { Settings = saved; HasSaveFailure = false; Status = ""; }
    }, saving: false);

    public Task SetThemeAsync(AppearanceTheme theme)
    {
        if (!Enum.IsDefined(theme)) throw new ArgumentOutOfRangeException(nameof(theme));
        return CanEdit ? SetSettingsAsync(Settings with { Theme = theme }) : Task.CompletedTask;
    }

    public Task SetSettingsAsync(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (!CanEdit) return Task.CompletedTask;
        Settings = settings;
        return SaveAsync();
    }

    public Task SaveAsync() => RunAsync(async () =>
    {
        // Close joins an admitted save so the last explicit choice survives immediate exit.
        var admittedSettings = Settings;
        await store.SaveAsync(admittedSettings, CancellationToken.None);
        HasSaveFailure = false;
        Status = "设置已保存。";
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
                if (saving) HasSaveFailure = true;
                var reason = error switch
                {
                    UnauthorizedAccessException => "没有访问设置的权限。",
                    TimeoutException => "设置正在被其他窗口使用，请稍后重试。",
                    InvalidDataException => "已有设置损坏或版本不受支持，原文件已保留。",
                    _ => "无法访问应用设置，请重试。"
                };
                Status = (saving ? "设置未保存。" : "未能读取设置。") + reason;
            }
            finally { _busy = false; OnPropertyChanged(nameof(CanEdit)); }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_close is not null) return new ValueTask(_close);
        _closing = true; OnPropertyChanged(nameof(CanEdit));
        _loadCancellation.Cancel();
        return new ValueTask(_close = CloseAsync());
    }
    private async Task CloseAsync()
    {
        try { await _pending; }
        finally { _loadCancellation.Dispose(); }
    }
}
