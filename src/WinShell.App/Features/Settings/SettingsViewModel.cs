using CommunityToolkit.Mvvm.ComponentModel;
using WinShell.Core.Settings;

namespace WinShell.App.Features.Settings;

// Dispatcher-affine, with a single owned operation. Appearance writes never modify grants.
public sealed class SettingsViewModel(IApplicationSettingsStore store) : ObservableObject, IAsyncDisposable
{
    private readonly CancellationTokenSource _loadCancellation = new();
    private ApplicationSettings _settings = new();
    private string _status = "";
    private bool _busy, _closing;
    private bool _hasSaveFailure;
    private Task _pending = Task.CompletedTask;
    private Task? _close;
    public ApplicationSettings Settings
    {
        get => _settings;
        private set { if (SetProperty(ref _settings, value)) OnPropertyChanged(nameof(Theme)); }
    }
    public AppearanceTheme Theme => Settings.Theme;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool HasSaveFailure { get => _hasSaveFailure; private set => SetProperty(ref _hasSaveFailure, value); }
    public bool CanEdit => !_busy && !_closing;

    public Task LoadAsync() => RunAsync(async () =>
    {
        var saved = await store.ReadAsync(_loadCancellation.Token);
        if (!_closing) { Settings = saved; HasSaveFailure = false; Status = ""; }
    }, saving: false);

    public Task SetThemeAsync(AppearanceTheme theme)
    {
        if (!Enum.IsDefined(theme)) throw new ArgumentOutOfRangeException(nameof(theme));
        if (!CanEdit) return Task.CompletedTask;
        return SetSettingsAsync(Settings with { Theme = theme });
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
