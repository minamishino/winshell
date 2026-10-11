using System.Text.Json;
using WinShell.Core.Settings;
using WinShell.Infrastructure.Windows.Profiles;

namespace WinShell.Infrastructure.Windows.Settings;

public sealed class JsonApplicationSettingsStore(string configurationDirectory) : IApplicationSettingsStore
{
    private const int MaximumBytes = 16 * 1024;
    private readonly string _directory = Path.GetFullPath(configurationDirectory);
    public Task<ApplicationSettings> ReadAsync(CancellationToken cancellationToken) => LockedAsync(ReadCoreAsync, cancellationToken);
    public Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken) => LockedAsync(async token =>
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        // Never replace an invalid or unsupported existing document, including the legacy source.
        await ReadCoreAsync(token).ConfigureAwait(false);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, theme = settings.Theme.ToString(), startupPage = settings.StartupPage.ToString(),
            terminal = new { fontFamily = settings.Terminal.FontFamily.ToString(), fontSize = settings.Terminal.FontSize, cursorStyle = settings.Terminal.CursorStyle.ToString() },
            defaultShell = settings.DefaultShell.ToString(), wslDistribution = settings.WslDistribution,
            confirmMultilinePaste = settings.ConfirmMultilinePaste, confirmTabClose = settings.ConfirmTabClose
        });
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("应用设置超过 16 KiB。");
        var destination = Path.Combine(_directory, "settings.json");
        var temporary = Path.Combine(_directory, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                ProfileStorageAccess.EnsureCurrentUserFileAccess(temporary);
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (ProfileStorageAccess.IsRegularFileOrMissing(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
            ProfileStorageAccess.EnsureCurrentUserFileAccess(destination);
            return settings;
        }
        finally { File.Delete(temporary); }
    }, cancellationToken);

    private Task<T> LockedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        acquisition.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            ProfileStorageAccess.EnsureCurrentUserAccess(_directory, ["settings.json", "settings.lock", "appearance.json", "appearance.lock"]);
            await using var fileLock = await ProfileStorageAccess.AcquireFileLockAsync(Path.Combine(_directory, "settings.lock"), acquisition.Token).ConfigureAwait(false);
            acquisition.CancelAfter(Timeout.InfiniteTimeSpan);
            cancellationToken.ThrowIfCancellationRequested();
            ProfileStorageAccess.EnsureCurrentUserFileAccess(Path.Combine(_directory, "settings.json"));
            ProfileStorageAccess.EnsureCurrentUserFileAccess(Path.Combine(_directory, "settings.lock"));
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && acquisition.IsCancellationRequested)
        { throw new TimeoutException("等待应用设置锁超过 5 秒。", error); }
    }, cancellationToken);

    private async Task<ApplicationSettings> ReadCoreAsync(CancellationToken token)
    {
        var path = Path.Combine(_directory, "settings.json");
        if (!ProfileStorageAccess.IsRegularFileOrMissing(path))
            return new() { Theme = await new JsonAppearanceSettingsStore(_directory).ReadAsync(token).ConfigureAwait(false) };
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (input.Length > MaximumBytes) throw new InvalidDataException("应用设置超过 16 KiB。");
        var bytes = new byte[(int)input.Length];
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4, AllowDuplicateProperties = false });
            var root = document.RootElement;
            ExactProperties(root, ["schemaVersion", "theme", "startupPage", "terminal", "defaultShell", "wslDistribution", "confirmMultilinePaste", "confirmTabClose"]);
            if (!root.GetProperty("schemaVersion").TryGetInt32(out var version) || version != 1) throw new InvalidDataException("不支持的应用设置版本。");
            var terminal = root.GetProperty("terminal");
            ExactProperties(terminal, ["fontFamily", "fontSize", "cursorStyle"]);
            if (!terminal.GetProperty("fontSize").TryGetInt32(out var fontSize)) throw new InvalidDataException("终端字号无效。");
            var settings = new ApplicationSettings
            {
                Theme = ReadEnum<AppearanceTheme>(root.GetProperty("theme")), StartupPage = ReadEnum<StartupPage>(root.GetProperty("startupPage")),
                Terminal = new() { FontFamily = ReadEnum<TerminalFontFamily>(terminal.GetProperty("fontFamily")), FontSize = fontSize, CursorStyle = ReadEnum<TerminalCursorStyle>(terminal.GetProperty("cursorStyle")) },
                DefaultShell = ReadEnum<DefaultShell>(root.GetProperty("defaultShell")), WslDistribution = root.GetProperty("wslDistribution").GetString()!,
                ConfirmMultilinePaste = root.GetProperty("confirmMultilinePaste").GetBoolean(), ConfirmTabClose = root.GetProperty("confirmTabClose").GetBoolean()
            };
            settings.Validate();
            return settings;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("应用设置 JSON 或字段无效，原文件已保留。", error); }
    }
    private static T ReadEnum<T>(JsonElement element) where T : struct, Enum
    {
        if (element.ValueKind == JsonValueKind.String && Enum.TryParse<T>(element.GetString(), false, out var value) &&
            Enum.IsDefined(value) && element.GetString() == value.ToString()) return value;
        throw new InvalidDataException("应用设置枚举值无效。");
    }
    private static void ExactProperties(JsonElement element, string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != names.Length || names.Any(name => !element.TryGetProperty(name, out _)))
            throw new InvalidDataException("不支持的应用设置格式。");
    }
}
