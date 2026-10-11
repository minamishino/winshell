using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinShell.App;

// One shared instance for the window: WinUI permits only one ContentDialog per XamlRoot.
public sealed class NativeDialogService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public Action<bool>? PresentationChanged { private get; set; }
    public void Dispose() => _gate.Dispose();
    public async Task<bool> ConfirmAsync(XamlRoot root, string title, string content, string accept,
        CancellationToken cancellationToken)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer { Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, MaxHeight = 400 },
            PrimaryButtonText = accept, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        return await ShowAsync(root, dialog, cancellationToken) == ContentDialogResult.Primary;
    }

    public async Task<ContentDialogResult> ShowAsync(XamlRoot root, ContentDialog dialog,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PresentationChanged?.Invoke(true);
            dialog.XamlRoot = root;
            var themeRoot = (FrameworkElement)root.Content;
            void ThemeChanged(FrameworkElement sender, object args) => dialog.RequestedTheme = sender.ActualTheme;
            dialog.RequestedTheme = themeRoot.ActualTheme;
            themeRoot.ActualThemeChanged += ThemeChanged;
            try
            {
                using var registration = cancellationToken.Register(() => dialog.DispatcherQueue.TryEnqueue(() => dialog.Hide()));
                var result = await dialog.ShowAsync();
                return cancellationToken.IsCancellationRequested ? ContentDialogResult.None : result;
            }
            finally { themeRoot.ActualThemeChanged -= ThemeChanged; }
        }
        finally { try { PresentationChanged?.Invoke(false); } finally { _gate.Release(); } }
    }
}
