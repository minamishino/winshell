#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SourceDirectory,
    [Parameter(Mandatory)][string] $PublishDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string] $Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = (Resolve-Path -LiteralPath $SourceDirectory).Path
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $publish 'WinShell.App.exe') -PathType Leaf)) {
    throw 'Missing published application.'
}
Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination (Join-Path $publish 'LICENSE.txt')
$licenseRoot = Join-Path $publish 'Licenses'
New-Item -ItemType Directory -Force -Path $licenseRoot | Out-Null
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$packageRoots = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$inventory = [Collections.Generic.List[object]]::new()

function Copy-PackageNotices {
    param([string] $Package, [string] $PackagePath, [string] $Sha512)
    $notices = @(Get-ChildItem -LiteralPath $PackagePath -File |
        Where-Object { $_.Name -match '^(LICENSE|NOTICE|THIRD.?PARTY)' })
    if ($notices.Count -gt 0) {
        $destination = Join-Path $licenseRoot ($Package -replace '/', '-')
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        $notices | Copy-Item -Destination $destination
    }
    $inventory.Add([pscustomobject]@{ Package = $Package; PackageSha512 = $Sha512; Notices = @($notices | ForEach-Object Name) })
}

foreach ($project in @('WinShell.App', 'WinShell.Terminal.NativeHost', 'WinShell.Mcp', 'WinShell.Mcp.Http')) {
    $assets = Get-Content -LiteralPath (Join-Path $repository "src/$project/obj/project.assets.json") -Raw |
        ConvertFrom-Json -AsHashtable
    foreach ($root in $assets.packageFolders.Keys) { [void] $packageRoots.Add($root) }
    foreach ($libraryName in $assets.libraries.Keys) {
        $library = $assets.libraries[$libraryName]
        if ($library.type -ne 'package' -or -not $seen.Add($libraryName)) { continue }
        $packagePath = $packageRoots | ForEach-Object { Join-Path $_ $library.path } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
        if (-not $packagePath) { throw "Package missing: $libraryName" }
        Copy-PackageNotices -Package $libraryName -PackagePath $packagePath -Sha512 $library.sha512
    }
}

# Self-contained runtimeconfig files record the exact runtime shipped for each process.
# Do not substitute the SDK version or the newest runtime installed on the build machine.
$frameworkPackages = @{
    'Microsoft.NETCore.App' = 'microsoft.netcore.app.runtime.win-x64'
    'Microsoft.AspNetCore.App' = 'microsoft.aspnetcore.app.runtime.win-x64'
}
foreach ($runtimeConfig in @('WinShell.App.runtimeconfig.json',
    'Terminal/WinShell.Terminal.NativeHost.runtimeconfig.json', 'Mcp/WinShell.Mcp.runtimeconfig.json',
    'McpHttp/WinShell.Mcp.Http.runtimeconfig.json')) {
    $runtime = Get-Content -LiteralPath (Join-Path $publish $runtimeConfig) -Raw | ConvertFrom-Json -AsHashtable
    if (-not $runtime.runtimeOptions.includedFrameworks) { throw "No self-contained frameworks in $runtimeConfig" }
    foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
        if (-not $frameworkPackages.ContainsKey($framework.name) -or $framework.version -notmatch '^\d+\.\d+\.\d+$') {
            throw "Unsupported runtime framework in ${runtimeConfig}: $($framework.name)/$($framework.version)"
        }
        $runtimePackage = "$($frameworkPackages[$framework.name])/$($framework.version)"
        if (-not $seen.Add($runtimePackage)) { continue }
        $runtimePath = $packageRoots | ForEach-Object { Join-Path $_ $runtimePackage } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
        if (-not $runtimePath) { throw "Runtime package missing: $runtimePackage" }
        $hashFile = Get-ChildItem -LiteralPath $runtimePath -Filter '*.nupkg.sha512' -File | Select-Object -First 1
        if (-not $hashFile) { throw "Runtime package hash missing: $runtimePackage" }
        Copy-PackageNotices -Package $runtimePackage -PackagePath $runtimePath `
            -Sha512 ([IO.File]::ReadAllText($hashFile.FullName).Trim())
    }
}
$inventory | Sort-Object Package | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $licenseRoot 'packages.json') -Encoding utf8
@"
WinShell $Version — Windows x64

运行：打开 WinShell.App.exe。不要将 EXE 从本目录单独移出。
要求：Windows 10 22H2 (build 19045) 或更新的 x64 Windows。
本发行包附带 .NET、Windows App SDK、终端和 PowerShell 运行文件，无需安装 .NET SDK。

配置保存在 %LOCALAPPDATA%\WinShell；卸载或删除应用文件不会主动删除这些配置。
连接、凭据和主机公钥需要用户自行配置。WSL 功能需要用户已安装 WSL 发行版。
MCP stdio 客户端可从设置页复制配置，或者以 Mcp\WinShell.Mcp.exe 作为 command，args 为空数组。

尚未完成所有 Windows 版本、DPI、主题、键盘/IME、Narrator 和真实远程服务的人工验收。
本发行包未签名，Windows 可能显示未知发布者提示。
项目：https://github.com/minamishino/winshell
项目许可证：LICENSE.txt（Apache-2.0）。
第三方许可：Licenses、Mcp、McpHttp\Licenses、Terminal 与 PowerShell 下的许可/声明文件。
NuGet 包清单位于 Licenses\packages.json；Microsoft.Extensions/System 的 MIT 声明由 .NET 运行时许可证及其第三方声明共同提供。
"@ | Set-Content -LiteralPath (Join-Path $publish 'README.txt') -Encoding utf8
Write-Output "Collected notices for $($seen.Count) resolved packages."
