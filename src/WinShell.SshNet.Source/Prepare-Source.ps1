param([switch]$Baseline)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$evidenceRoot = Join-Path $repositoryRoot 'artifacts/ssh-script-20261004/source'
$commit = '7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e'
$expectedHash = 'C75DAF0093744F12BF50F9E7708C6460203DDD824EA7D2F441DEF7066F114CF0'
$archive = Join-Path $evidenceRoot 'upstream.zip'
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri "https://codeload.github.com/sshnet/SSH.NET/zip/$commit" -OutFile $archive -TimeoutSec 30
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Immutable source archive hash mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($copy in @('original', 'patched')) {
    $destination = [IO.Path]::GetFullPath((Join-Path $evidenceRoot $copy))
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        if ($zip.Entries.Count -gt 10000) { throw 'Archive entry budget exceeded.' }
        $expandedBytes = 0L
        $expectedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            $expandedBytes += $entry.Length
            if ($expandedBytes -gt 100MB) { throw 'Archive byte budget exceeded.' }
            $target = [IO.Path]::GetFullPath([IO.Path]::Combine($destination, $entry.FullName))
            if (!$target.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive path escaped source cache.' }
            if ($entry.FullName.EndsWith('/')) { continue }
            if (!$expectedFiles.Add($target)) { throw 'Duplicate archive path.' }
            New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
        foreach ($actualFile in Get-ChildItem -LiteralPath $destination -File -Recurse) {
            if (!$expectedFiles.Contains($actualFile.FullName)) { throw "Unmapped source cache file: $($actualFile.FullName)" }
        }
    } finally { $zip.Dispose() }
}
if (!$Baseline) {
    # Only the owned apply copy is canonicalized; the reviewed tracked patch stays byte-for-byte intact.
    $patch = Join-Path $evidenceRoot 'Shell.apply.patch'
    $trackedPatch = Join-Path $PSScriptRoot 'Shell.patch'
    [IO.File]::WriteAllText($patch, [IO.File]::ReadAllText($trackedPatch).Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
    Get-FileHash -LiteralPath $trackedPatch,$patch | Format-Table | Out-String | Set-Content (Join-Path $evidenceRoot 'patch-hashes.txt')
    & git -c core.autocrlf=false -c core.eol=lf -C $repositoryRoot apply --unidiff-zero --check --directory="artifacts/ssh-script-20261004/source/patched/SSH.NET-$commit" $patch
    if ($LASTEXITCODE -ne 0) { throw 'Patch check failed.' }
    & git -c core.autocrlf=false -c core.eol=lf -C $repositoryRoot apply --unidiff-zero --directory="artifacts/ssh-script-20261004/source/patched/SSH.NET-$commit" $patch
    if ($LASTEXITCODE -ne 0) { throw 'Patch apply failed.' }
}
$originalRoot = Join-Path $evidenceRoot "original/SSH.NET-$commit"
$patchedRoot = Join-Path $evidenceRoot "patched/SSH.NET-$commit"
# Verify the final file set too: a patch must not smuggle an extra wildcard Compile input past the manifest.
$originalFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($originalFile in Get-ChildItem -LiteralPath $originalRoot -File -Recurse) {
    [void]$originalFiles.Add([IO.Path]::GetRelativePath($originalRoot, $originalFile.FullName))
}
foreach ($patchedFile in Get-ChildItem -LiteralPath $patchedRoot -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($patchedRoot, $patchedFile.FullName)
    if (!$originalFiles.Contains($relative)) { throw "Unexpected added source after patch: $relative" }
}
$manifest = foreach ($originalFile in Get-ChildItem -LiteralPath $originalRoot -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($originalRoot, $originalFile.FullName)
    $before = (Get-FileHash -LiteralPath $originalFile.FullName).Hash
    $after = (Get-FileHash -LiteralPath (Join-Path $patchedRoot $relative)).Hash
    if ($before -ne $after -and $relative.Replace('\','/') -ne 'src/Renci.SshNet/Shell.cs') { throw "Unexpected changed source: $relative" }
    [pscustomobject]@{ Path=$relative; OriginalSha256=$before; BuildSha256=$after; Unchanged=($before -eq $after) }
}
if (!$Baseline) {
    $shellPath = Join-Path $patchedRoot 'src/Renci.SshNet/Shell.cs'
    $shellHash = (Get-FileHash -LiteralPath $shellPath).Hash
    if ($shellHash -ne '5A58631D87A232AD6FA3E7AFBD22B7D29D77E69BD0D3C075E15A42AC1E59AF92') { throw 'Reviewed canonical Shell source hash mismatch.' }
}
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'source-manifest.json') -Encoding utf8
Write-Output "Verified snapshot $commit; $($manifest.Count) files audited; baseline=$Baseline"
