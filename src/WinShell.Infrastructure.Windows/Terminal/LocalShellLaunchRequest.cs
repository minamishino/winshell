namespace WinShell.Infrastructure.Windows.Terminal;

public abstract record LocalShellLaunchRequest
{
    private protected LocalShellLaunchRequest() { }
    public sealed record PowerShell : LocalShellLaunchRequest;
    public sealed record Wsl(WslDistribution Distribution) : LocalShellLaunchRequest;
}

public sealed record WslDistribution
{
    public string Name { get; }
    public WslDistribution(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 32700 || name.Contains('\0') || name.Contains('\r') || name.Contains('\n'))
            throw new ArgumentException("WSL 发行版名称无效。", nameof(name));
        Name = name;
    }
    public override string ToString() => Name;
}
