WinShell {{VERSION}}，原生 WinUI 3 / Windows App SDK 桌面 SSH 工作台。

| 下载文件 | 用途 |
| --- | --- |
| `WinShell-{{VERSION}}-win-x64-setup.exe` | EXE 安装向导 |
| `WinShell-{{VERSION}}-win-x64.msi` | Windows Installer 安装包 |
| `WinShell-{{VERSION}}-win-x64-portable.zip` | 完整解压后运行 `WinShell/WinShell.App.exe` |
| `SHA256SUMS.txt` | SHA-256 校验值 |

要求 Windows 10 22H2（build 19045）或更新的 x64 Windows。包含 .NET、Windows App SDK、原生终端、MCP 与 PowerShell 运行文件；无需安装 .NET SDK。不能单独复制主 EXE 而遗漏其依赖目录。

包含 SSH、文件传输、隧道、远程 Docker、本地 PowerShell、WSL 入口及 MCP 服务端。WSL 需要用户已安装发行版；远程连接需要自行配置；MCP 工具需单独授权。

由 GitHub Actions 针对对应版本标签编译，完成 MCP 配置回归、发布目录进程/协议检查、MSI 文件完整性及 EXE 内嵌 MSI 核对后发布。详细构建记录见下方链接。

安装位置为当前用户的 `%LOCALAPPDATA%\Programs\WinShell`，配置位于 `%LOCALAPPDATA%\WinShell`。MSI 与 EXE 是同一程序的不同安装入口，升级和卸载请继续使用原安装渠道。

初始发行版未作发行者签名，Windows 可能显示未知发布者提示。尚未完成全部 Windows 版本、DPI、主题、键盘/IME、Narrator、真实远程服务及安装升级路径的人工验收；自动构建检查不等同于这些人工验收。目前不提供 x86 或 ARM64 版本。

项目使用 Apache-2.0，第三方许可证随包提供。
