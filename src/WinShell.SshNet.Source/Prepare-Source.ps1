param([switch]$Baseline)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$evidenceRoot = Join-Path $repositoryRoot 'artifacts/sftp-product/source'
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
        foreach ($actualFile in Get-ChildItem -LiteralPath $destination -File -Recurse -Force) {
            if (!$expectedFiles.Contains($actualFile.FullName)) { throw "Unmapped source cache file: $($actualFile.FullName)" }
        }
    } finally { $zip.Dispose() }
}
$patchManifest = @()
if (!$Baseline) {
    $patchNames = @('Shell')
    $patchNames += 'Sftp'
    foreach ($patchName in $patchNames) {
        $patchInput = Join-Path $PSScriptRoot "$patchName.patch"
        $patch = Join-Path $evidenceRoot "$patchName.apply.patch"
        # The archive is LF; Git may check the patch out as CRLF. Upstream text attributes
        # also use core.eol on Windows, so pin both settings. No whitespace relaxation.
        [IO.File]::WriteAllText($patch, [IO.File]::ReadAllText($patchInput).Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
        if ($patchName -eq 'Sftp' -and (Get-FileHash -LiteralPath $patch).Hash -ne '62EF757CDBE8396AAE1402C6C059EE6A4A48E30F9376AB9072B2F244CE290FA8') { throw 'Reviewed Sftp patch hash mismatch.' }
        $patchManifest += [pscustomobject]@{ Path="$patchName.patch"; InputSha256=(Get-FileHash $patchInput).Hash; CanonicalSha256=(Get-FileHash $patch).Hash }
        & git -c core.autocrlf=false -c core.eol=lf -C $repositoryRoot apply --unidiff-zero --check --directory="artifacts/sftp-product/source/patched/SSH.NET-$commit" $patch
        if ($LASTEXITCODE -ne 0) { throw "$patchName patch check failed." }
        & git -c core.autocrlf=false -c core.eol=lf -C $repositoryRoot apply --unidiff-zero --directory="artifacts/sftp-product/source/patched/SSH.NET-$commit" $patch
        if ($LASTEXITCODE -ne 0) { throw "$patchName patch apply failed." }
    }
}
$expectedChanges = @{
    'src/Renci.SshNet/Shell.cs' = @('9B4568F25756DD547D9F58210F6F6A599943A7666DCC7EFE5CC1E3F193C430B8','5A58631D87A232AD6FA3E7AFBD22B7D29D77E69BD0D3C075E15A42AC1E59AF92')
    'src/Renci.SshNet/Sftp/SftpSession.cs' = @('C4789D52B3312BBF4CE32BC8E3BF1B9D553998B2F663E914B988E8553871C6AC','3EEC163A974B734DFC9FC1DA06722B0E4699C611A148669D6E8DAA83048107FB')
    'src/Renci.SshNet/Sftp/Responses/SftpNameResponse.cs' = @('AB857C8BC4B25545A05977B2F8C625A4EF900BE77901AEB0F536A52A1D2E18BC','F9713C005445C9306458928E8F1EBB5AE51D4F2E2EDA135081CC8DD49000946F')
    'src/Renci.SshNet/Sftp/SftpFileAttributes.cs' = @('86F6E82BA04EBA4CD8796AFEEC1186603DA7607D0F00118CA728A6BAB347F704','9960CBE1D0F2532BD9D943158332322D849CB6B61BA2CAE1D3EFFB68EC1B9459')
    'src/Renci.SshNet/Sftp/Requests/SftpReadRequest.cs' = @('612501231904E08CD702F9AE0EFB41BD504CEB8163E73D5EB6AB1252E389322E','CCF20B759FA48BE5A16E5FAC91FF92ABFB4DE3CE3F2B499D55CE8BFCE3B42350')
}
$originalRoot = Join-Path $evidenceRoot "original/SSH.NET-$commit"
$patchedRoot = Join-Path $evidenceRoot "patched/SSH.NET-$commit"
# Verify the final file set too: a patch must not smuggle an extra wildcard Compile input past the manifest.
$originalFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($originalFile in Get-ChildItem -LiteralPath $originalRoot -File -Recurse -Force) {
    [void]$originalFiles.Add([IO.Path]::GetRelativePath($originalRoot, $originalFile.FullName))
}
foreach ($patchedFile in Get-ChildItem -LiteralPath $patchedRoot -File -Recurse -Force) {
    $relative = [IO.Path]::GetRelativePath($patchedRoot, $patchedFile.FullName)
    if (!$originalFiles.Contains($relative)) { throw "Unexpected added source after patch: $relative" }
}
$manifest = foreach ($originalFile in Get-ChildItem -LiteralPath $originalRoot -File -Recurse -Force) {
    $relative = [IO.Path]::GetRelativePath($originalRoot, $originalFile.FullName)
    $before = (Get-FileHash -LiteralPath $originalFile.FullName).Hash
    $after = (Get-FileHash -LiteralPath (Join-Path $patchedRoot $relative)).Hash
    $canonicalPath = $relative.Replace('\','/')
    if ($expectedChanges.ContainsKey($canonicalPath)) {
        $expected = $expectedChanges[$canonicalPath]
        $expectedAfter = if ($Baseline) { $expected[0] } else { $expected[1] }
        if ($before -ne $expected[0] -or $after -ne $expectedAfter) { throw "Declared source hash mismatch: $relative" }
    } elseif ($before -ne $after) { throw "Unexpected changed source: $relative" }
    [pscustomobject]@{ Path=$relative; OriginalSha256=$before; BuildSha256=$after; Unchanged=($before -eq $after) }
}
if (!$Baseline) {
    $shellPath = Join-Path $patchedRoot 'src/Renci.SshNet/Shell.cs'
    $shellHash = (Get-FileHash -LiteralPath $shellPath).Hash
    if ($shellHash -ne '5A58631D87A232AD6FA3E7AFBD22B7D29D77E69BD0D3C075E15A42AC1E59AF92') { throw 'Reviewed canonical Shell source hash mismatch.' }
}
$patchManifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'patch-manifest.json') -Encoding utf8
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'source-manifest.json') -Encoding utf8
Write-Output "Verified snapshot $commit; $($manifest.Count) files audited; baseline=$Baseline"
