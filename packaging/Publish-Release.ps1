#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SourceDirectory,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $DotnetExecutable = 'dotnet',
    [string] $WixExecutable = 'wix',
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string] $Version,
    [switch] $AcceptWixEula
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$sourcePath = (Resolve-Path -LiteralPath $SourceDirectory).Path
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) {
    throw "OutputDirectory already exists; choose a new directory: $outputPath"
}
if (-not $AcceptWixEula) {
    throw 'WiX 7 requires explicit EULA acceptance. Pass -AcceptWixEula only after accepting its terms.'
}
$parsedVersion = [version] $Version
if ($parsedVersion.Major -gt 255 -or $parsedVersion.Minor -gt 255 -or $parsedVersion.Build -gt 65535) {
    throw 'MSI version must fit major.minor.build limits: 255.255.65535.'
}
$Version = $parsedVersion.ToString(3)
$dotnetPath = (Get-Command $DotnetExecutable -CommandType Application -ErrorAction Stop).Source
$wixPath = (Get-Command $WixExecutable -CommandType Application -ErrorAction Stop).Source
$solutionPath = Join-Path $sourcePath 'WinShell.slnx'
if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "SourceDirectory must contain WinShell.slnx: $sourcePath"
}

function Invoke-Dotnet {
    param([Parameter(Mandatory)][string[]] $Arguments)
    $PSNativeCommandUseErrorActionPreference = $false
    & $dotnetPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

New-Item -ItemType Directory -Path $outputPath | Out-Null
$publishRoot = Join-Path $outputPath 'publish'
$payloadPath = Join-Path $publishRoot 'WinShell'
$assetsPath = Join-Path $outputPath 'assets'
$logsPath = Join-Path $outputPath 'logs'
New-Item -ItemType Directory -Path $logsPath | Out-Null

function Invoke-LoggedStep {
    param([string] $Name, [scriptblock] $Action)
    $logPath = Join-Path $logsPath "$Name.log"
    Write-Host "Starting $Name (log: $logPath)"
    try {
        & $Action *> $logPath
    }
    catch {
        Get-Content -LiteralPath $logPath -Tail 80 | Write-Host
        throw
    }
    Write-Host "Completed $Name"
}

# Resolve global.json and relative project paths in the supplied checkout. Each preparation
# script downloads a pinned archive on a fresh runner and verifies its immutable hash.
Push-Location -LiteralPath $sourcePath
try {
    Invoke-LoggedStep -Name 'prepare-ssh-source' -Action {
        & (Join-Path $sourcePath 'src/WinShell.SshNet.Source/Prepare-Source.ps1')
    }
    Invoke-LoggedStep -Name 'prepare-powershell' -Action {
        & (Join-Path $sourcePath 'src/WinShell.Infrastructure.Windows/Terminal/Prepare-PowerShell.ps1')
    }
    Invoke-LoggedStep -Name 'prepare-native-engine' -Action {
        & (Join-Path $sourcePath 'src/WinShell.Terminal.NativeHost/Initialize-StableNativeEngine.ps1')
    }

    Invoke-LoggedStep -Name 'restore' -Action {
        & (Join-Path $PSScriptRoot 'Restore-ReleaseDependencies.ps1') -SourceDirectory $sourcePath `
            -DotnetExecutable $dotnetPath
    }
    $publishArguments = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:RestoreLockedMode=true', '-p:RuntimeIdentifiers=win-x64', "-p:Version=$Version", "-p:AssemblyVersion=$Version.0",
        "-p:FileVersion=$Version.0", "-p:InformationalVersion=$Version")

    # App's build publishes and copies the independent HTTP process and its licenses.
    # Publish the other executable entrypoints explicitly so each gets its own .NET host.
    Invoke-LoggedStep -Name 'publish-app' -Action {
        Invoke-Dotnet -Arguments (@('publish', 'src/WinShell.App/WinShell.App.csproj') +
            $publishArguments + @('-p:Platform=x64', '-o', $payloadPath))
    }
    Invoke-LoggedStep -Name 'publish-terminal' -Action {
        Invoke-Dotnet -Arguments (@('publish', 'src/WinShell.Terminal.NativeHost/WinShell.Terminal.NativeHost.csproj') +
            $publishArguments + @('-p:Platform=AnyCPU', '-o', (Join-Path $payloadPath 'Terminal')))
    }
    Invoke-LoggedStep -Name 'publish-mcp' -Action {
        Invoke-Dotnet -Arguments (@('publish', 'src/WinShell.Mcp/WinShell.Mcp.csproj') +
            $publishArguments + @('-p:Platform=AnyCPU', '-o', (Join-Path $payloadPath 'Mcp')))
    }

    foreach ($entrypoint in @('WinShell.App', 'Terminal/WinShell.Terminal.NativeHost',
        'Mcp/WinShell.Mcp', 'McpHttp/WinShell.Mcp.Http')) {
        foreach ($extension in @('.exe', '.dll', '.deps.json', '.runtimeconfig.json')) {
            if (-not (Test-Path -LiteralPath (Join-Path $payloadPath ($entrypoint + $extension)) -PathType Leaf)) {
                throw "Missing published entrypoint asset: $entrypoint$extension"
            }
        }
        $runtime = Get-Content -LiteralPath (Join-Path $payloadPath "$entrypoint.runtimeconfig.json") -Raw |
            ConvertFrom-Json -AsHashtable
        if (-not $runtime.runtimeOptions.includedFrameworks -or $runtime.runtimeOptions.ContainsKey('framework')) {
            throw "Entrypoint is not self-contained: $entrypoint"
        }
    }

    Invoke-LoggedStep -Name 'finalize-payload' -Action {
        & (Join-Path $PSScriptRoot 'Finalize-Payload.ps1') -SourceDirectory $sourcePath `
            -PublishDirectory $payloadPath -Version $Version
    }
    Invoke-LoggedStep -Name 'build-installers' -Action {
        & (Join-Path $PSScriptRoot 'Build-Installers.ps1') -SourceDirectory $sourcePath `
            -PublishDirectory $payloadPath -OutputDirectory $assetsPath -Version $Version `
            -WixExecutable $wixPath -AcceptWixEula:$AcceptWixEula
    }

    Write-Host 'Creating portable ZIP and SHA256SUMS.txt'
    $zipPath = Join-Path $assetsPath "WinShell-$Version-win-x64-portable.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($publishRoot, $zipPath,
        [IO.Compression.CompressionLevel]::Optimal, $false)
    $releaseFiles = @(
        Get-Item -LiteralPath $zipPath
        Get-Item -LiteralPath (Join-Path $assetsPath "WinShell-$Version-win-x64.msi")
        Get-Item -LiteralPath (Join-Path $assetsPath "WinShell-$Version-win-x64-setup.exe")
    )
    $checksumLines = $releaseFiles | Sort-Object Name | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    }
    [IO.File]::WriteAllText((Join-Path $assetsPath 'SHA256SUMS.txt'),
        ($checksumLines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    $releaseFiles | Select-Object FullName, Length
}
finally {
    Pop-Location
}
