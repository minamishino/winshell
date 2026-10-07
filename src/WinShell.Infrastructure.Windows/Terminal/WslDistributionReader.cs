using System.Diagnostics;
using System.Text;
namespace WinShell.Infrastructure.Windows.Terminal;

public sealed class WslDistributionReader
{
    public static string ExecutablePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
    public async Task<IReadOnlyList<WslDistribution>> ReadAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var start = new ProcessStartInfo(ExecutablePath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--list"); start.ArgumentList.Add("--quiet");
        using var process = Process.Start(start) ?? throw new IOException("无法启动 WSL 查询。");
        var totalBytes = 0;
        async Task<byte[]> ReadBoundedAsync(Stream stream)
        {
            using var bytes = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                if (count == 0) return bytes.ToArray();
                if (Interlocked.Add(ref totalBytes, count) > 65536) throw new InvalidDataException("WSL 查询输出超过64KiB限制。");
                bytes.Write(buffer, 0, count);
            }
        }
        var output = ReadBoundedAsync(process.StandardOutput.BaseStream);
        var error = ReadBoundedAsync(process.StandardError.BaseStream);
        var exit = process.WaitForExitAsync(deadline.Token);
        try
        {
            // A failed reader must cancel the other reader/process wait immediately.
            var tasks = new List<Task> { output, error, exit };
            while (tasks.Count != 0) { var completed = await Task.WhenAny(tasks).ConfigureAwait(false); await completed.ConfigureAwait(false); tasks.Remove(completed); }
            var stdout = await output.ConfigureAwait(false);
            var stderr = await error.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException($"WSL 查询失败（{process.ExitCode}）：{Decode(stderr.Length == 0 ? stdout : stderr)}");
            return Parse(stdout);
        }
        finally
        {
            await deadline.CancelAsync().ConfigureAwait(false);
            if (!process.HasExited) process.Kill(); // Exact query client only; never a distribution command.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(output, error, exit).ConfigureAwait(false); }
            catch (Exception) { /* Original failure is owned by the awaited operation above. */ }
        }
    }
    internal static IReadOnlyList<WslDistribution> Parse(byte[] bytes)
    {
        if (bytes.Length > 65536) throw new InvalidDataException("WSL 查询输出超过64KiB限制。");
        var names = Decode(bytes).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (names.Length > 64) throw new InvalidDataException("WSL 发行版超过64项限制。");
        return names.Select(name => new WslDistribution(name)).ToArray();
    }
    private static string Decode(byte[] bytes)
    {
        if ((bytes.Length & 1) != 0) throw new InvalidDataException("WSL 查询不是完整UTF16LE文本。");
        return new UnicodeEncoding(false, false, true).GetString(bytes).TrimStart('\uFEFF');
    }
}
