param([string]$ArchivePath)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$destination = Join-Path $repository 'artifacts/powershell/7.6.6'
$archive = Join-Path $repository 'artifacts/powershell/PowerShell-7.6.6-win-x64.zip'
New-Item -ItemType Directory -Force -Path (Split-Path $archive) | Out-Null
if ($ArchivePath) { Copy-Item -LiteralPath ([IO.Path]::GetFullPath($ArchivePath)) -Destination $archive }
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest 'https://github.com/PowerShell/PowerShell/releases/download/v7.6.6/PowerShell-7.6.6-win-x64.zip' -OutFile $archive -TimeoutSec 120
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '02FE458BE20493FBDF43F61EA20610B811EE6C738AB1676C61B9CFCD1A33C860') { throw 'Official PowerShell archive hash mismatch.' }
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    if ($zip.Entries.Count -gt 10000) { throw 'Portable PowerShell entry budget exceeded.' }
    $expandedBytes = 0L
    $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $manifest = foreach ($entry in $zip.Entries) {
        $expandedBytes += $entry.Length
        if ($expandedBytes -gt 1GB) { throw 'Portable PowerShell extraction budget exceeded.' }
        $target = [IO.Path]::GetFullPath([IO.Path]::Combine($destination, $entry.FullName))
        if (!$target.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive path escaped package root.' }
        if ($entry.FullName.EndsWith('/')) { continue }
        if (!$files.Add($target)) { throw 'Duplicate archive path.' }
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        [pscustomobject]@{ Path = $entry.FullName; Bytes = $entry.Length; Sha256 = (Get-FileHash -LiteralPath $target).Hash }
    }
    foreach ($actual in Get-ChildItem -LiteralPath $destination -File -Recurse -Force) {
        if (!$files.Contains($actual.FullName)) { throw "Unmapped portable file: $($actual.FullName)" }
    }
    foreach ($required in @('pwsh.exe', 'LICENSE.txt', 'ThirdPartyNotices.txt')) {
        if (!$files.Contains((Join-Path $destination $required))) { throw "Missing upstream content: $required" }
    }
    $manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path (Split-Path $destination) 'manifest.json') -Encoding utf8
    Write-Output "Verified official PowerShell7.6.6 x64: $($files.Count) files, $expandedBytes expanded bytes, complete upstream license/notices."
} finally { $zip.Dispose() }
