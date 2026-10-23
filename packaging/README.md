# Windows release

`.github/workflows/release.yml` builds the exact `vMAJOR.MINOR.PATCH` tag on a Windows runner. `Directory.Build.props` must contain the same version. The publish job runs only after all build and verification steps succeed. It uploads MSI, EXE installer, portable ZIP and `SHA256SUMS.txt`, then publishes the draft release. An already published release is never overwritten.

For the first release, pushing `codex/release-0.0.1` builds the existing `v0.0.1` tag with the packaging scripts on that branch. This preserves the original tag. Later tags should include this workflow and its scripts. `workflow_dispatch` also accepts an existing tag once the workflow is available on the default branch.

## Build locally

Use Windows x64, PowerShell 7.2+, the SDK pinned by `global.json`, and WiX 7.0.0. Accept WiX's terms only if they apply to your use. The initial project's owner confirmed individual, noncommercial open-source development; CI passes the acceptance flag for this project.

```powershell
./packaging/Publish-Release.ps1 -SourceDirectory . -OutputDirectory ./artifacts/release-new -Version 0.0.1 -DotnetExecutable dotnet -WixExecutable wix -AcceptWixEula
```

The output directory must not already exist. Downloads and NuGet dependencies are pinned and checked. App, terminal and MCP are published with their own .NET runtime; the App build also publishes the HTTP companion. The portable ZIP contains a `WinShell/` folder whose contents must stay together.

The tagged development locks are first restored in locked mode. Missing `win-x64` lock targets are then derived in the isolated checkout: every existing external package record must remain identical, and new RID records must match the corresponding locked base target. Any package addition or change stops the build. All subsequent restores and publishes use the derived graph in locked mode. This accommodates the first tag without moving it or changing application source.

## Verification and limits

The workflow runs MCP launch-configuration regression cases, published process startup and MCP handshake checks, MSI payload path/size/SHA-256 comparisons, and an exact comparison of the MSI embedded in the EXE. It checks SHA-256 values again before uploading release assets. Build logs and verification reports are retained as a separate Actions artifact.

MSI validation keeps all checks except ICE38/ICE64, whose roaming-profile assumptions do not apply to this fixed LocalAppData, per-user package. If ICE03 rejects Microsoft's MUI language IDs, only the six known `gd-GB`, `mi-NZ`, and `ug-CN` WinUI resource files are accepted after matching their target directory, name, language ID and diagnostic. Other validation errors stop the build.

These checks do not install the application or prove upgrade/uninstall behavior, GUI accessibility, keyboard/IME, all supported Windows versions, or real-server behavior. The initial release is unsigned and supports x64 only.
