using WinShell.Core.Profiles;

namespace WinShell.App.Features.Workspace;

public sealed class ProfileTabViewModel : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public ProfileTabViewModel(ConnectionProfile profile)
    {
        Profile = profile;
        LifetimeToken = _lifetime.Token;
    }

    public Guid TabId { get; } = Guid.NewGuid();
    public Guid ProfileId => Profile.Id;
    public string Title => Profile.DisplayName;
    public ConnectionProfile Profile { get; }
    public CancellationToken LifetimeToken { get; }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        try { _lifetime.Cancel(); }
        finally { _lifetime.Dispose(); }
        return ValueTask.CompletedTask;
    }
}
