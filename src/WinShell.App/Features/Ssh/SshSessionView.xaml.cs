using Microsoft.UI.Xaml;
namespace WinShell.App.Features.Ssh;
public sealed partial class SshSessionView
{
    // Installed by the page at Loaded so its shutdown joins UI continuations too.
    public Func<Func<Task>, Task>? RunOwnedActionAsync { get; set; }
    public SshSessionView() => InitializeComponent();
    private async void Connect_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (DataContext is SshSessionViewModel session && RunOwnedActionAsync is { } run)
                await run(session.ConnectAsync);
        }
        catch (Exception)
        {
            (DataContext as SshSessionViewModel)?.ReportActionFailure();
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs args)
    { if (DataContext is SshSessionViewModel session) session.Cancel(); }
    private async void Disconnect_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (DataContext is SshSessionViewModel session && RunOwnedActionAsync is { } run)
                await run(session.DisconnectAsync);
        }
        catch (Exception)
        {
            (DataContext as SshSessionViewModel)?.ReportActionFailure();
        }
    }
}
