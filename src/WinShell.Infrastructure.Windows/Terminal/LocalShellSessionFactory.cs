namespace WinShell.Infrastructure.Windows.Terminal;

public sealed class LocalShellSessionFactory
{
    public string PowerShellPath => Path.Combine(AppContext.BaseDirectory, "PowerShell", "7.6.6", "pwsh.exe");
    private static string[] PowerShellArguments()
    {
        return ["-NoLogo", "-NoProfile"];
    }
    public ConptyShellSession Create(LocalShellLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return request switch
        {
            LocalShellLaunchRequest.PowerShell => File.Exists(PowerShellPath)
                ? new(PowerShellPath, PowerShellArguments(), directory)
                : throw new FileNotFoundException("随应用提供的 PowerShell 7.6.6 文件缺失，请重新安装完整应用。", PowerShellPath),
            LocalShellLaunchRequest.Wsl { Distribution: { } distribution } => new(WslDistributionReader.ExecutablePath,
                ["--distribution", distribution.Name], directory),
            _ => throw new ArgumentException("不支持的本地终端类型。", nameof(request))
        };
    }
}
