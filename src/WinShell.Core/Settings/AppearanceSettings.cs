namespace WinShell.Core.Settings;

public enum AppearanceTheme { System, Light, Dark }

public interface IAppearanceSettingsStore
{
    Task<AppearanceTheme> ReadAsync(CancellationToken cancellationToken);
    Task SaveAsync(AppearanceTheme theme, CancellationToken cancellationToken);
}
