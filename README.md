# WinShell

**面向 Windows 的原生 SSH 工作台。**

将 SSH 终端、远程文件传输、端口转发、Docker 容器操作和本地 Shell 集中在一个 WinUI 3 应用中，并通过 MCP 服务端向 AI 客户端提供经过授权的远程操作能力。

[下载最新版](https://github.com/minamishino/winshell/releases/latest) · [反馈问题](https://github.com/minamishino/winshell/issues) · [构建记录](https://github.com/minamishino/winshell/actions) · [Apache-2.0 许可证](LICENSE)

## 功能

| 功能 | 当前能力 |
| --- | --- |
| SSH 终端 | 应用内交互式终端，支持密码与受限 RSA 私钥认证、主机公钥核验、输入输出与窗口尺寸同步 |
| 连接管理 | 新建、编辑、分组和搜索连接配置，密码由 Windows 凭据管理器保存 |
| 文件传输 | SFTP、FTP、FTPS 目录浏览与单文件上传、下载，显示传输进度并支持取消；FTPS 支持显式与隐式 TLS |
| SSH 隧道 | 本地端口转发、远程端口转发和动态 SOCKS 转发，监听地址限定为回环地址 |
| 远程 Docker | 通过 SSH 查看容器与日志，启动、停止、重启容器，执行命令或打开容器交互终端 |
| 本地终端 | 通过 ConPTY 运行 PowerShell 和已安装的 WSL 发行版 |
| AI / MCP | 为外部 AI 客户端提供 SSH 脚本和 Docker 工具，支持 stdio、仅本机的 Streamable HTTP 及按连接配置授权 |

应用页面、导航和对话框使用原生 **WinUI 3 / Windows App SDK**。终端渲染采用 Windows Terminal 的原生组件，通过独立承载进程接入；交互与布局参考 Tabby 和 PowerShell。

## 下载与安装

前往 [GitHub Releases](https://github.com/minamishino/winshell/releases/latest)，按需要选择：

| 文件 | 使用方式 |
| --- | --- |
| `WinShell-<版本>-win-x64-setup.exe` | 通过安装向导安装 |
| `WinShell-<版本>-win-x64.msi` | 使用 Windows Installer 安装 |
| `WinShell-<版本>-win-x64-portable.zip` | 完整解压后运行 `WinShell/WinShell.App.exe` |
| `SHA256SUMS.txt` | 核对下载文件的 SHA-256 |

- **系统要求：** Windows 10 22H2（build 19045）或更新的 x64 Windows，当前不提供 x86 / ARM64 版本。
- **运行依赖：** 发行包包含 .NET、Windows App SDK、原生终端、MCP 和 PowerShell 运行文件，无需另行安装 .NET SDK。请保留完整应用目录。
- **安装位置：** `%LOCALAPPDATA%\Programs\WinShell`；配置位于 `%LOCALAPPDATA%\WinShell`。免安装版也使用此配置目录。
- EXE 与 MSI 是同一应用的两种安装入口，升级和卸载请沿用原安装渠道。

初始版本 **0.0.1** 尚未进行发行者签名，Windows 可能显示未知发布者提示。功能范围、变更和验证情况以对应版本的[发布说明](https://github.com/minamishino/winshell/releases)为准。

## 快速上手

1. 打开「连接」，新建并保存 SSH 配置，填写主机、端口、用户名及认证信息。
2. 连接目标主机。首次连接时，通过可信渠道核对完整主机公钥指纹后再确认信任；已知主机公钥变化会被拒绝。
3. 在「终端」中使用 SSH 会话，也可以打开本地 PowerShell 或已安装的 WSL 发行版。
4. 在「文件」「隧道」或「Docker」中选择相应配置，执行文件传输、端口转发或容器操作。

使用 FTP / FTPS 时，先创建对应协议的连接配置。使用 WSL 前需自行安装发行版；使用远程 Docker 前，SSH 用户需能在目标主机运行 Docker 命令。

## 接入 AI 客户端

WinShell 提供 **Model Context Protocol（MCP）服务端**，供支持 MCP 的外部客户端调用已授权的 SSH 和 Docker 能力。

1. 先保存 SSH 配置，并通过一次 SSH 连接完成主机公钥核验。
2. 打开「设置 → AI / MCP」，选择接入方式：
   - **stdio：** 点击「复制配置」，将生成的 JSON 合并到客户端配置中；服务进程由客户端按需启动。
   - **Streamable HTTP：** 生成令牌、设置端口并启动本机服务，再复制客户端配置。服务仅监听 `127.0.0.1`，使用 Bearer 令牌认证；需要时可启用旧版 SSE 兼容。
3. 在权限区域为目标连接勾选所需能力，并点击「保存权限」。连接服务与授予工具权限是两个独立步骤。

HTTP 服务随所属 WinShell 窗口关闭。移动应用目录后，应重新复制客户端配置。

权限适用于同一 Windows 用户的本地 MCP 客户端；Docker 授权覆盖所选主机的全部容器。任意 Shell 脚本权限本身也能调用 Docker，请按实际需要授权，并仅将含令牌的配置交给可信客户端。

## 当前范围与限制

项目仍处于早期迭代阶段，当前有以下边界：

- **终端并发：** SSH、PowerShell、WSL 和 Docker 终端共用最多 2 个会话的预算，启动及关闭中的会话也占用名额。
- **SSH 私钥：** 仅支持传统 RSA PKCS#1 PEM，文件不超过 16 KiB、RSA 不超过 8192 位，可无口令或使用 AES-256-CBC legacy PEM 加密。暂不支持 OpenSSH、PuTTY、PKCS8、Ed25519 / EC 私钥；请勿为适配格式而移除或削弱私钥加密。
- **文件操作：** 当前提供目录浏览和单文件上传下载；目录列表为有界快照，达到上限后不支持继续分页。
- **功能范围：** Docker 页面聚焦容器操作；WSL 入口用于打开已有发行版终端。
- **验收范围：** 自动构建和发布检查不代表所有 Windows 版本、DPI、主题、键盘 / IME、Narrator、真实远程服务及安装升级路径均已完成人工验收。

## 从源码构建

### 环境准备

- Windows x64，版本要求同上。
- Git。
- PowerShell **7.2 或更高版本**（`pwsh`）。
- [.NET SDK **10.0.401**](https://dotnet.microsoft.com/download/dotnet/10.0)，与 [global.json](global.json) 保持一致；仓库禁用了 SDK 版本自动滚动。
- 可访问 GitHub 和 NuGet 的网络环境。

### 获取源码并构建

在 PowerShell 7 中执行，确认每一步成功后再继续：

```powershell
git clone https://github.com/minamishino/winshell.git
cd winshell

# 准备固定版本的 SSH.NET 源码、原生终端及便携 PowerShell
pwsh -NoProfile -File src/WinShell.SshNet.Source/Prepare-Source.ps1
pwsh -NoProfile -File src/WinShell.Terminal.NativeHost/Initialize-StableNativeEngine.ps1
pwsh -NoProfile -File src/WinShell.Infrastructure.Windows/Terminal/Prepare-PowerShell.ps1

# 按锁文件还原并构建整个解决方案
dotnet restore WinShell.slnx --locked-mode -p:Platform=x64
dotnet build WinShell.slnx -c Release --no-restore -p:Platform=x64

# 启动桌面应用
dotnet run --project src/WinShell.App/WinShell.App.csproj -c Release --no-build -p:Platform=x64
```

准备脚本会下载固定版本并校验 SHA-256；SSH.NET 源码准备还会应用仓库内的补丁。构建过程同时准备终端和 MCP 伴随进程，请勿只复制主 EXE 运行。

以上准备、还原和构建步骤与 [Windows 构建工作流](.github/workflows/build.yml)一致。生成 EXE / MSI / ZIP 分发包需要额外的 WiX 工具链，详见[打包说明](packaging/README.md)。

### 项目结构

```text
src/
├─ WinShell.App/                     WinUI 3 桌面界面与 ViewModel
├─ WinShell.Core/                    领域模型、业务契约与资源预算
├─ WinShell.Infrastructure.Ssh/      SSH、SFTP、隧道与远程 Docker
├─ WinShell.SshNet.Source/           固定 SSH.NET 源码与补丁
├─ WinShell.Infrastructure.Ftp/      FTP / FTPS 协议适配
├─ WinShell.Infrastructure.Windows/  凭据、本地存储、ConPTY 与 WSL
├─ WinShell.Terminal.NativeHost/     原生终端承载进程
├─ WinShell.Terminal.Shared/         终端进程通信共享代码
├─ WinShell.Mcp/                     MCP stdio 服务端
└─ WinShell.Mcp.Http/                本机 MCP HTTP 服务端
packaging/                          发布与安装包工具
```

主要技术包括 C# / .NET、WinUI 3、CommunityToolkit.Mvvm、SSH.NET、FluentFTP 和 ConPTY。依赖版本由中央包管理与锁文件固定。

## 参与贡献

欢迎通过 [Issues](https://github.com/minamishino/winshell/issues) 反馈问题或提出建议，也欢迎提交聚焦单一功能的 Pull Request。

- 问题报告请包含应用版本、Windows 版本、复现步骤和预期行为；涉及界面时可附截图。
- 请先移除日志和截图中的密码、私钥、令牌及敏感连接信息。
- UI 继续使用原生 WinUI 3；网络和文件 I/O 应支持取消，明确释放会话及其资源。
- 提交说明应写明实际行为变化和已执行的验证。网络功能使用隔离目标验证，UI 变化需单独检查实际交互。
- 提交信息采用 `类型(模块):中文描述`，例如 `fix(ssh):修复连接取消后的状态显示`。

## 致谢与许可证

感谢以下开源项目：

- [Windows Terminal](https://github.com/microsoft/terminal)：原生终端组件与 Windows 命令行基础设施。
- [PowerShell](https://github.com/PowerShell/PowerShell)：本地命令行环境。
- [SSH.NET](https://github.com/sshnet/SSH.NET)：SSH 协议实现。
- [FluentFTP](https://github.com/robinrodricks/FluentFTP)：FTP / FTPS 协议实现。
- [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) 与 [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)：桌面应用与 MVVM 基础。
- [Tabby](https://github.com/Eugeny/tabby)：终端工作台交互与布局参考。

WinShell 使用 [Apache License 2.0](LICENSE)。第三方组件遵循各自许可证，相关声明随源码和发行包提供。
