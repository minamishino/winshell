namespace WinShell.Core.Settings;

public enum StartupPage { Terminal, Connections }
public enum DefaultShell { PowerShell, Wsl }
public enum TerminalFontFamily : byte { CascadiaCode, Consolas }
public enum TerminalCursorStyle : byte
{
    BlinkingBlock = 1, SteadyBlock, BlinkingUnderline, SteadyUnderline, BlinkingBar, SteadyBar
}

public sealed record TerminalPreferences
{
    public TerminalFontFamily FontFamily { get; init; } = TerminalFontFamily.CascadiaCode;
    public int FontSize { get; init; } = 12;
    public TerminalCursorStyle CursorStyle { get; init; } = TerminalCursorStyle.BlinkingBar;
    public void Validate()
    {
        if (!Enum.IsDefined(FontFamily) || FontSize is < 8 or > 32 || !Enum.IsDefined(CursorStyle))
            throw new ArgumentException("终端字体、字号或光标样式无效。");
    }
}

public sealed record ApplicationSettings
{
    public AppearanceTheme Theme { get; init; } = AppearanceTheme.System;
    public StartupPage StartupPage { get; init; } = StartupPage.Terminal;
    public TerminalPreferences Terminal { get; init; } = new();
    public DefaultShell DefaultShell { get; init; } = DefaultShell.PowerShell;
    public string WslDistribution { get; init; } = "";
    public bool ConfirmMultilinePaste { get; init; } = true;
    public bool ConfirmTabClose { get; init; } = true;
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Terminal);
        if (!Enum.IsDefined(Theme) || !Enum.IsDefined(StartupPage) || !Enum.IsDefined(DefaultShell) ||
            WslDistribution is not { Length: <= 128 } || WslDistribution.Any(char.IsControl) ||
            (DefaultShell == DefaultShell.Wsl && string.IsNullOrWhiteSpace(WslDistribution)))
            throw new ArgumentException("应用设置无效；WSL 默认目标需要显式发行版名称（最多 128 字符）。");
        Terminal.Validate();
    }
}

public interface IApplicationSettingsStore
{
    Task<ApplicationSettings> ReadAsync(CancellationToken cancellationToken);
    Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken);
}
