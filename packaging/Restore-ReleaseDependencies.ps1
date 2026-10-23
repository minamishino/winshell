#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SourceDirectory,
    [string] $DotnetExecutable = 'dotnet'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sourcePath = (Resolve-Path -LiteralPath $SourceDirectory).Path
$dotnetPath = (Get-Command $DotnetExecutable -CommandType Application -ErrorAction Stop).Source

function Invoke-Restore {
    param([string[]] $RestoreArguments)
    $PSNativeCommandUseErrorActionPreference = $false
    & $dotnetPath restore (Join-Path $sourcePath 'WinShell.slnx') '-p:Platform=x64' @RestoreArguments
    if ($LASTEXITCODE -ne 0) { throw "Release restore failed with exit code $LASTEXITCODE." }
}

function Get-PackageSnapshot {
    param([string] $LockPath)
    $lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json -AsHashtable
    $snapshot = @{}
    foreach ($framework in $lock.dependencies.Keys) {
        $packages = @{}
        foreach ($name in $lock.dependencies[$framework].Keys) {
            $package = $lock.dependencies[$framework][$name]
            # Project versions follow the tagged source; only external packages are locked here.
            if ($package.type -eq 'Project') { continue }
            $packages[$name] = $package | ConvertTo-Json -Compress -Depth 30
        }
        $snapshot[$framework] = $packages
    }
    return ,$snapshot
}

Push-Location -LiteralPath $sourcePath
try {
    # First prove the checked-in dependency graph without changing it.
    Invoke-Restore -RestoreArguments @('--locked-mode')
    $originalLocks = @{}
    foreach ($projectDirectory in Get-ChildItem -LiteralPath (Join-Path $sourcePath 'src') -Directory) {
        $lockPath = Join-Path $projectDirectory.FullName 'packages.lock.json'
        if (Test-Path -LiteralPath $lockPath -PathType Leaf) {
            $originalLocks[$lockPath] = Get-PackageSnapshot -LockPath $lockPath
        }
    }
    if ($originalLocks.Count -eq 0) { throw 'No source dependency locks found.' }

    # RID is propagated as RuntimeIdentifiers so references that remove the singular
    # RuntimeIdentifier still use the same lock graph. Derive the missing RID groups,
    # then reject any external package change before building executable code.
    Invoke-Restore -RestoreArguments @('--force-evaluate', '-p:RuntimeIdentifiers=win-x64')
    foreach ($lockPath in $originalLocks.Keys) {
        $original = $originalLocks[$lockPath]
        $current = Get-PackageSnapshot -LockPath $lockPath
        foreach ($framework in $original.Keys) {
            foreach ($name in $original[$framework].Keys) {
                if (-not $current.ContainsKey($framework) -or
                    -not $current[$framework].ContainsKey($name) -or
                    $current[$framework][$name] -cne $original[$framework][$name]) {
                    throw "Locked dependency changed or disappeared: $lockPath / $framework / $name"
                }
            }
        }
        foreach ($framework in $current.Keys) {
            $originalFramework = $framework
            if (-not $original.ContainsKey($framework)) {
                if (-not $framework.EndsWith('/win-x64', [StringComparison]::Ordinal)) {
                    throw "Unexpected new dependency target: $lockPath / $framework"
                }
                $originalFramework = $framework.Substring(0, $framework.Length - '/win-x64'.Length)
                if (-not $original.ContainsKey($originalFramework)) {
                    throw "No locked base target for $lockPath / $framework"
                }
            }
            foreach ($name in $current[$framework].Keys) {
                if (-not $original[$originalFramework].ContainsKey($name) -or
                    $current[$framework][$name] -cne $original[$originalFramework][$name]) {
                    throw "Unapproved release dependency: $lockPath / $framework / $name"
                }
            }
        }
    }
    Invoke-Restore -RestoreArguments @('--locked-mode', '-p:RuntimeIdentifiers=win-x64')
    Write-Host "Verified $($originalLocks.Count) release lock files: all external package records unchanged."
}
finally { Pop-Location }
