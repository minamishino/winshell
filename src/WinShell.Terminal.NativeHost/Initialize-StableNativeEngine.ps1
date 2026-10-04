param([string] $ArchivePath)
$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $PSScriptRoot 'StableNativeEngine.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$destination = Join-Path $PSScriptRoot 'artifacts/stable-native'
# Fixed owned destination; reject junctions/symlinks before any writes.
function Assert-OrdinaryPath([string] $path) {
    $current = [IO.Path]::GetFullPath($path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -Force -LiteralPath $current).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point rejected: $current" }
        }
        $current = Split-Path $current -Parent
    }
}
Assert-OrdinaryPath $destination
New-Item -ItemType Directory -Force -Path $destination | Out-Null
if (-not $ArchivePath) {
    $ArchivePath = Join-Path $destination 'release.zip'
    Assert-OrdinaryPath $ArchivePath
    if (-not (Test-Path -LiteralPath $ArchivePath)) {
        $client = [Net.Http.HttpClient]::new()
        $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(3))
        $response = $null; $inputStream = $null; $outputStream = $null
        try {
            $response = $client.GetAsync($manifest.archiveUrl, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $timeout.Token).GetAwaiter().GetResult()
            $response.EnsureSuccessStatusCode() | Out-Null
            $inputStream = $response.Content.ReadAsStreamAsync($timeout.Token).GetAwaiter().GetResult()
            $outputStream = [IO.File]::Open($ArchivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $buffer = [byte[]]::new(65536); $total = 0L
            while (($count = $inputStream.ReadAsync($buffer, 0, $buffer.Length, $timeout.Token).GetAwaiter().GetResult()) -gt 0) {
                $total += $count
                if ($total -gt $manifest.archiveBytes) { throw 'Archive download exceeds fixed byte budget.' }
                $outputStream.Write($buffer, 0, $count)
            }
        } finally {
            if ($outputStream) { $outputStream.Dispose() }
            if ($inputStream) { $inputStream.Dispose() }
            if ($response) { $response.Dispose() }
            $timeout.Dispose(); $client.Dispose()
        }
    }
}
if ((Get-Item -LiteralPath $ArchivePath).Length -ne $manifest.archiveBytes -or
    (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $manifest.archiveSha256) { throw 'Official archive size/hash mismatch.' }
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ArchivePath).Path)
try {
    $total = 0L
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        if (-not $seen.Add($entry.FullName) -or -not $entry.FullName.StartsWith($manifest.entryRoot, [StringComparison]::Ordinal) -or
            $entry.FullName.Contains('..') -or $entry.FullName.Contains('\') -or $entry.FullName.Contains(':')) { throw 'Unsafe or duplicate archive entry.' }
        $total += $entry.Length
        if ($total -gt $manifest.expandedBytes) { throw 'Expanded byte budget exceeded.' }
    }
    if ($archive.Entries.Count -ne $manifest.entryCount -or $total -ne $manifest.expandedBytes) { throw 'Archive inventory mismatch.' }
    foreach ($file in $manifest.files) {
        if ([IO.Path]::GetFileName($file.name) -ne $file.name) { throw 'Manifest output must be a flat file name.' }
        $entry = $archive.GetEntry($manifest.entryRoot + $file.name)
        if (-not $entry -or $entry.Length -ne $file.bytes) { throw "Entry length mismatch: $($file.name)" }
        $target = Join-Path $destination $file.name
        Assert-OrdinaryPath $target
        $inputStream = $entry.Open()
        $outputStream = $null
        try {
            $outputStream = [IO.File]::Open($target, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $inputStream.CopyTo($outputStream, 65536)
        } finally { if ($outputStream) { $outputStream.Dispose() }; $inputStream.Dispose() }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) { throw "Extracted hash mismatch: $($file.name)" }
    }
} finally { $archive.Dispose() }
"Verified stable native engine $($manifest.release) at $destination; only native DLL and complete third-party notices extracted."
