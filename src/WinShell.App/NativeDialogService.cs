using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinShell.App;

// One shared instance for the window: WinUI permits only one ContentDialog per XamlRoot.
public sealed class NativeDialogService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public void Dispose() => _gate.Dispose();
    public async Task<bool> ConfirmAsync(XamlRoot root, string title, string content, string accept,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dialog = new ContentDialog
            {
                XamlRoot = root, Title = title,
                Content = new ScrollViewer { Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, MaxHeight = 400 },
                PrimaryButtonText = accept, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            using var registration = cancellationToken.Register(() => dialog.DispatcherQueue.TryEnqueue(() => dialog.Hide()));
            var result = await dialog.ShowAsync();
            return !cancellationToken.IsCancellationRequested && result == ContentDialogResult.Primary;
        }
        finally { _gate.Release(); }
    }
}
