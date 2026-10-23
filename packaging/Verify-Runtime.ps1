param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot ('../artifacts/verification/runtime-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff'))),
    [string]$DotnetExecutable = 'dotnet'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sourceRoot = (Resolve-Path -LiteralPath $SourceDirectory).Path
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$outputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (!(Test-Path -LiteralPath (Join-Path $sourceRoot 'src/WinShell.App/Features/Mcp/McpClientLaunchConfiguration.cs') -PathType Leaf)) { throw 'Source directory is missing the MCP launch configuration implementation.' }
if (!(Test-Path -LiteralPath $publishRoot -PathType Container)) { throw 'Publish directory is missing.' }
if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) { throw 'Expected version must not be empty.' }
$dotnetPath = (Get-Command -Name $DotnetExecutable -CommandType Application -ErrorAction Stop).Source
if (Test-Path -LiteralPath $outputRoot) { throw 'Use a fresh output directory to preserve previous evidence.' }
if ($outputRoot.Equals($publishRoot, [StringComparison]::OrdinalIgnoreCase) -or $outputRoot.StartsWith($publishRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must be written outside the publish payload.' }
New-Item -ItemType Directory -Path $outputRoot | Out-Null

if (-not ('WinShellReleaseRuntimeProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public sealed class WinShellReleaseProbeResult
{
    public int? ExitCode { get; set; }
    public bool TimedOut { get; set; }
    public bool Forced { get; set; }
    public bool Joined { get; set; }
    public string FailureCode { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public string ProtocolVersion { get; set; }
    public string ServerName { get; set; }
    public string ServerVersion { get; set; }
    public bool InitializedSent { get; set; }
}

public static class WinShellReleaseRuntimeProbe
{
    private static async Task<string> ReadBoundedAsync(Stream stream, TaskCompletionSource<string> firstLine, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[2048];
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > 65536) throw new IOException("output_budget_exceeded");
            long previousLength = output.Length;
            output.Write(buffer, 0, count);
            if (firstLine != null && !firstLine.Task.IsCompleted)
            {
                for (int index = 0; index < count; index++)
                    if (buffer[index] == 10)
                    {
                        firstLine.TrySetResult(Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)previousLength + index)).TrimEnd('\r'));
                        break;
                    }
            }
        }
        if (firstLine != null) firstLine.TrySetException(new IOException("initialize_reply_missing"));
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    public static async Task<WinShellReleaseProbeResult> RunAsync(string executable, string[] arguments, bool initializeMcp, int timeoutSeconds, string expectedVersion, string workingDirectory = null)
    {
        var result = new WinShellReleaseProbeResult();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable), StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var firstLine = initializeMcp ? new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        Task<string> output = null, errorOutput = null;
        bool started = false;
        try
        {
            if (!process.Start()) throw new IOException("process_start_failed");
            started = true;
            output = ReadBoundedAsync(process.StandardOutput.BaseStream, firstLine, deadline.Token);
            errorOutput = ReadBoundedAsync(process.StandardError.BaseStream, null, deadline.Token);
            if (initializeMcp)
            {
                var initialize = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", id = 1, method = "initialize",
                    @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "WinShell-release-probe", version = expectedVersion } }
                });
                await process.StandardInput.WriteLineAsync(initialize).WaitAsync(deadline.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().WaitAsync(deadline.Token).ConfigureAwait(false);
                var line = await firstLine.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                using var response = JsonDocument.Parse(line);
                var root = response.RootElement;
                if (root.GetProperty("jsonrpc").GetString() != "2.0" || root.GetProperty("id").GetInt32() != 1 || root.TryGetProperty("error", out _))
                    throw new IOException("invalid_initialize_response");
                var initialized = root.GetProperty("result");
                result.ProtocolVersion = initialized.GetProperty("protocolVersion").GetString();
                var server = initialized.GetProperty("serverInfo");
                result.ServerName = server.GetProperty("name").GetString();
                result.ServerVersion = server.GetProperty("version").GetString();
                if (result.ProtocolVersion != "2025-11-25" || result.ServerName != "WinShell" || result.ServerVersion.Length > 64)
                    throw new IOException("unexpected_initialize_metadata");
                await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}").WaitAsync(deadline.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().WaitAsync(deadline.Token).ConfigureAwait(false);
                result.InitializedSent = true;
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(output, errorOutput).WaitAsync(deadline.Token).ConfigureAwait(false);
            result.Output = await output.ConfigureAwait(false);
            result.Error = await errorOutput.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { result.TimedOut = true; result.FailureCode = "deadline_exceeded"; }
        catch (Exception error) { result.FailureCode = error.GetType().Name; }
        finally
        {
            if (started)
            {
                try
                {
                    if (!process.HasExited) { result.Forced = true; process.Kill(entireProcessTree: true); }
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    result.ExitCode = process.ExitCode;
                }
                catch { result.FailureCode = result.FailureCode ?? "process_cleanup_failed"; }
                deadline.Cancel();
                try
                {
                    await Task.WhenAll(output, errorOutput).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    result.Output = output.Result;
                    result.Error = errorOutput.Result;
                }
                catch { result.FailureCode = result.FailureCode ?? "pipe_cleanup_failed"; }
                result.Joined = process.HasExited && output.IsCompleted && errorOutput.IsCompleted;
            }
        }
        return result;
    }
}
'@
}

$runtimeInventory = @()
$probes = @()
$failure = $null
try {
    $powerShellDirectories = @(Get-ChildItem -LiteralPath (Join-Path $publishRoot 'PowerShell') -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'pwsh.exe') -PathType Leaf })
    if ($powerShellDirectories.Count -ne 1) { throw 'Publish payload must contain exactly one bundled PowerShell directory.' }
    $powerShellRelative = [IO.Path]::GetRelativePath($publishRoot, $powerShellDirectories[0].FullName)
    foreach ($relative in @('WinShell.App.runtimeconfig.json', 'Terminal/WinShell.Terminal.NativeHost.runtimeconfig.json', 'Mcp/WinShell.Mcp.runtimeconfig.json', 'McpHttp/WinShell.Mcp.Http.runtimeconfig.json', (Join-Path $powerShellRelative 'pwsh.runtimeconfig.json'))) {
        $path = Join-Path $publishRoot $relative
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -gt 32768) { throw "Missing or oversized runtime configuration: $relative" }
        $options = (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).runtimeOptions
        $frameworks = @($options.includedFrameworks | ForEach-Object { [pscustomobject]@{Name=$_.name;Version=$_.version} })
        if (!@($frameworks | Where-Object Name -eq 'Microsoft.NETCore.App').Count) { throw "Missing self-contained .NET runtime declaration: $relative" }
        $runtimeFiles = @(foreach ($name in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) {
            $file = Join-Path (Split-Path $path) $name
            if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing self-contained runtime asset: $relative/$name" }
            [pscustomobject]@{Name=$name;Bytes=(Get-Item -LiteralPath $file).Length}
        })
        $runtimeInventory += [pscustomobject]@{RuntimeConfig=$relative;IncludedFrameworks=$frameworks;CoreFiles=$runtimeFiles}
    }

    $cases = @(
        @{Name='PowerShell';Path=(Join-Path $powerShellRelative 'pwsh.exe');Arguments=@('-NoLogo','-NoProfile','-NonInteractive','-Command',"[Console]::Out.WriteLine('WINSHELL_RELEASE_POWERSHELL_OK'); exit 0");ExpectedExit=0;Mcp=$false},
        @{Name='Terminal';Path='Terminal/WinShell.Terminal.NativeHost.exe';Arguments=@();ExpectedExit=2;Mcp=$false},
        @{Name='Mcp';Path='Mcp/WinShell.Mcp.exe';Arguments=@();ExpectedExit=0;Mcp=$true},
        @{Name='McpHttp';Path='McpHttp/WinShell.Mcp.Http.exe';Arguments=@('--invalid');ExpectedExit=2;Mcp=$false}
    )
    foreach ($case in $cases) {
        $result = [WinShellReleaseRuntimeProbe]::RunAsync((Join-Path $publishRoot $case.Path), [string[]]$case.Arguments, $case.Mcp, 30, $ExpectedVersion).GetAwaiter().GetResult()
        $marker = switch ($case.Name) {
            'PowerShell' { $result.Output.Trim() -eq 'WINSHELL_RELEASE_POWERSHELL_OK' -and $result.Error.Length -eq 0 }
            'Terminal' { $result.Output.Length -eq 0 -and $result.Error.Length -eq 0 }
            'Mcp' { $result.InitializedSent -and $result.ServerVersion -eq $ExpectedVersion -and $result.Error.Trim() -match '^MCP_STOP code=[12] leases=\d+ sends=\d+$' }
            'McpHttp' { $result.Output.Length -eq 0 -and $result.Error.Trim() -eq 'MCP_HTTP_INVALID_CONFIGURATION' }
        }
        $observedMarker = if (!$marker) { '<unexpected output redacted>' } elseif ($case.Name -eq 'PowerShell') { $result.Output.Trim() } elseif ($case.Name -eq 'Terminal') { '<no output>' } else { $result.Error.Trim() }
        $passed = $result.ExitCode -eq $case.ExpectedExit -and $marker -and !$result.TimedOut -and !$result.Forced -and $result.Joined -and !$result.FailureCode
        $probes += [pscustomobject]@{Name=$case.Name;Executable=$case.Path;ExpectedExitCode=$case.ExpectedExit;ExitCode=$result.ExitCode;Marker=$observedMarker;MarkerMatched=[bool]$marker;TimedOut=$result.TimedOut;Forced=$result.Forced;Joined=$result.Joined;FailureCode=$result.FailureCode;StdoutBytes=[Text.Encoding]::UTF8.GetByteCount($result.Output);StderrBytes=[Text.Encoding]::UTF8.GetByteCount($result.Error);ProtocolVersion=$result.ProtocolVersion;ServerName=$result.ServerName;ServerVersion=$result.ServerVersion;InitializedSent=$result.InitializedSent;Passed=[bool]$passed}
        Write-Output "$($case.Name): exit=$($result.ExitCode), marker=$marker, joined=$($result.Joined), passed=$passed"
    }
    $testProject = Join-Path $PSScriptRoot 'tests/McpLaunch.Tests.csproj'
    $testArguments = @('run','--project',$testProject,'-c','Release','--artifacts-path',(Join-Path $outputRoot 'mcp-launch-tests'),"-p:SourceDirectory=$sourceRoot",'--','--publish-directory',$publishRoot)
    $configurationResult = [WinShellReleaseRuntimeProbe]::RunAsync($dotnetPath, [string[]]$testArguments, $false, 60, $ExpectedVersion, $sourceRoot).GetAwaiter().GetResult()
    $configurationResult.Output | Set-Content -LiteralPath (Join-Path $outputRoot 'mcp-launch-configuration.stdout.log') -Encoding utf8
    $configurationResult.Error | Set-Content -LiteralPath (Join-Path $outputRoot 'mcp-launch-configuration.stderr.log') -Encoding utf8
    $configurationPassed = $configurationResult.ExitCode -eq 0 -and $configurationResult.Output.Contains('PASS published MCP configuration uses apphost with empty arguments') -and !$configurationResult.FailureCode -and !$configurationResult.Forced -and $configurationResult.Joined
    $probes += [pscustomobject]@{Name='McpClientLaunchConfiguration';ExitCode=$configurationResult.ExitCode;Command=(Join-Path $publishRoot 'Mcp/WinShell.Mcp.exe');Arguments=@();MarkerMatched=$configurationPassed;TimedOut=$configurationResult.TimedOut;Forced=$configurationResult.Forced;Joined=$configurationResult.Joined;FailureCode=$configurationResult.FailureCode;Passed=$configurationPassed}
    Write-Output "McpClientLaunchConfiguration: exit=$($configurationResult.ExitCode), passed=$configurationPassed"
    if (@($probes | Where-Object { !$_.Passed }).Count) { throw 'One or more published runtime probes failed.' }
}
catch { $failure = $_.Exception.Message }
finally {
    [pscustomobject]@{Utc=[DateTime]::UtcNow.ToString('o');SourceDirectory=$sourceRoot;PublishDirectory=$publishRoot;DotnetExecutable=$dotnetPath;ExpectedVersion=$ExpectedVersion;RuntimeInventory=$runtimeInventory;Probes=$probes;Failure=$failure;Passed=($null -eq $failure);Scope='Process startup and MCP initialize/initialized/EOF only; no tool calls, HTTP listener, real-server connection, GUI or renderer interaction.'} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'runtime-verification.json') -Encoding utf8
}
if ($failure) { throw $failure }
Write-Output "PASS published runtime checks. Evidence: $(Join-Path $outputRoot 'runtime-verification.json')"
