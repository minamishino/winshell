using System.Text.Json;
using WinShell.Core.Settings;
using WinShell.Infrastructure.Windows.Profiles;

namespace WinShell.Infrastructure.Windows.Settings;

public sealed class JsonAppearanceSettingsStore(string configurationDirectory) : IAppearanceSettingsStore
{
    private readonly string _directory = Path.GetFullPath(configurationDirectory);
    private const int MaximumBytes = 4096;

    public Task<AppearanceTheme> ReadAsync(CancellationToken cancellationToken) =>
        LockedAsync(ReadCoreAsync, cancellationToken);

    public Task SaveAsync(AppearanceTheme theme, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(theme)) throw new ArgumentOutOfRangeException(nameof(theme));
        return LockedAsync(async token =>
        {
            await ReadCoreAsync(token).ConfigureAwait(false);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, theme = theme.ToString() });
            var destination = Path.Combine(_directory, "appearance.json");
            var temporary = Path.Combine(_directory, $"appearance.{Guid.NewGuid():N}.tmp");
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
                return theme;
            }
            finally { File.Delete(temporary); }
        }, cancellationToken);
    }

    private Task<T> LockedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        acquisition.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            ProfileStorageAccess.EnsureCurrentUserAccess(_directory, ["appearance.json", "appearance.lock"]);
            await using var fileLock = await ProfileStorageAccess.AcquireFileLockAsync(Path.Combine(_directory, "appearance.lock"), acquisition.Token).ConfigureAwait(false);
            acquisition.CancelAfter(Timeout.InfiniteTimeSpan);
            cancellationToken.ThrowIfCancellationRequested();
            ProfileStorageAccess.EnsureCurrentUserFileAccess(Path.Combine(_directory, "appearance.json"));
            ProfileStorageAccess.EnsureCurrentUserFileAccess(Path.Combine(_directory, "appearance.lock"));
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && acquisition.IsCancellationRequested)
        { throw new TimeoutException("等待外观设置锁超过 5 秒。", error); }
    }, cancellationToken);

    private async Task<AppearanceTheme> ReadCoreAsync(CancellationToken token)
    {
        var path = Path.Combine(_directory, "appearance.json");
        if (!ProfileStorageAccess.IsRegularFileOrMissing(path)) return AppearanceTheme.System;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (input.Length > MaximumBytes) throw new InvalidDataException("外观设置超过 4 KiB。");
        var bytes = new byte[(int)input.Length];
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4, AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1 ||
                !root.TryGetProperty("theme", out var theme) || theme.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("不支持的外观设置格式。");
            return theme.GetString() switch
            {
                "System" => AppearanceTheme.System,
                "Light" => AppearanceTheme.Light,
                "Dark" => AppearanceTheme.Dark,
                _ => throw new InvalidDataException("外观主题无效。")
            };
        }
        catch (JsonException error) { throw new InvalidDataException("外观设置 JSON 无效。", error); }
    }
}
