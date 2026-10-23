#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PublishDirectory,
    [Parameter(Mandatory)][string] $MsiPath,
    [Parameter(Mandatory)][string] $BundlePath,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [Parameter(Mandatory)][string] $WixExecutable,
    [switch] $AcceptWixEula
)

# Extract only: do not install, launch, register, repair, or rebuild the release.
# The MSI database supplies actual target paths; Get-FileHash streams file data.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path.TrimEnd('\', '/')
$msiPath = (Resolve-Path -LiteralPath $MsiPath).Path
$setupPath = (Resolve-Path -LiteralPath $BundlePath).Path
$wixPath = (Get-Command $WixExecutable -CommandType Application -ErrorAction Stop).Source
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if ($outputPath -eq $publishRoot -or $outputPath.StartsWith($publishRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Verification output must be outside the publish directory.'
}
if (Test-Path -LiteralPath $outputPath) {
    if (@(Get-ChildItem -LiteralPath $outputPath -Force).Count -ne 0) {
        throw 'Verification output must be empty, preserving earlier evidence and preventing stale extracted files.'
    }
}
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$extractedFileRoot = Join-Path $outputPath 'msi\extracted\File'

function Invoke-WixExtraction([string[]] $Arguments, [string] $LogName) {
    if ($AcceptWixEula) { $Arguments += @('-acceptEula', 'wix7') }
    $logPath = Join-Path $outputPath $LogName
    & $wixPath @Arguments *> $logPath
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $logPath | Write-Host
        throw "WiX extraction failed with exit $LASTEXITCODE; see $logPath."
    }
}

function Get-AssetEvidence([string] $Path) {
    $file = Get-Item -LiteralPath $Path
    [pscustomobject]@{
        Path = $file.FullName
        Length = $file.Length
        Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    }
}

function Read-MsiTable($Database, [string] $Query, [string[]] $Columns) {
    $view = $null
    try {
        $view = $Database.OpenView($Query)
        [void]$view.Execute()
        while ($record = $view.Fetch()) {
            try {
                $row = [ordered]@{}
                for ($column = 1; $column -le $Columns.Count; $column++) {
                    $row[$Columns[$column - 1]] = $record.GetType().InvokeMember(
                        'StringData', [Reflection.BindingFlags]::GetProperty,
                        $null, $record, @($column))
                }
                [pscustomobject]$row
            }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
        }
    }
    finally {
        if ($null -ne $view) {
            try { [void]$view.Close() }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
        }
    }
}

$assetBefore = @(Get-AssetEvidence $msiPath; Get-AssetEvidence $setupPath)
Invoke-WixExtraction @('msi', 'decompile', $msiPath, '-out', (Join-Path $outputPath 'msi\Package.wxs'), '-x', (Join-Path $outputPath 'msi\extracted')) 'msi-decompile.log'
Invoke-WixExtraction @('burn', 'extract', $setupPath, '-out', (Join-Path $outputPath 'burn\payloads'), '-outba', (Join-Path $outputPath 'burn\ba')) 'burn-extract.log'
$embeddedMsiFiles = @(Get-ChildItem -LiteralPath (Join-Path $outputPath 'burn\payloads') -Recurse -File -Filter '*.msi')
if ($embeddedMsiFiles.Count -ne 1) { throw "Expected one embedded MSI, found $($embeddedMsiFiles.Count)." }
$embeddedMsiPath = $embeddedMsiFiles[0].FullName
$installer = $null
$database = $null
$msiSummary = $null
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.OpenDatabase($msiPath, 0)
    $directories = @(Read-MsiTable $database 'SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`' @('Id', 'Parent', 'Name'))
    $components = @(Read-MsiTable $database 'SELECT `Component`, `Directory_` FROM `Component`' @('Id', 'Directory'))
    $msiFiles = @(Read-MsiTable $database 'SELECT `File`, `Component_`, `FileName`, `FileSize` FROM `File`' @('Id', 'Component', 'Name', 'Length'))
    $properties = @(Read-MsiTable $database 'SELECT `Property`, `Value` FROM `Property`' @('Name', 'Value'))
    $msiSummary = $database.SummaryInformation(0)
    $msiTemplate = $msiSummary.Property(7)
}
finally {
    foreach ($comObject in @($msiSummary, $database, $installer)) {
        if ($null -ne $comObject) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($comObject) }
    }
}

$directoryMap = @{}
foreach ($directory in $directories) { $directoryMap[$directory.Id] = $directory }
$componentMap = @{}
foreach ($component in $components) { $componentMap[$component.Id] = $component.Directory }
function Get-TargetDirectory([string] $DirectoryId) {
    $segments = [Collections.Generic.List[string]]::new()
    $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    while ($DirectoryId -ne 'INSTALLFOLDER') {
        if (-not $DirectoryId -or -not $visited.Add($DirectoryId) -or -not $directoryMap.ContainsKey($DirectoryId)) {
            throw "File directory is not under INSTALLFOLDER: $DirectoryId"
        }
        $directory = $directoryMap[$DirectoryId]
        $targetName = (($directory.Name -split ':')[0] -split '\|')[-1]
        if ($targetName -ne '.') { $segments.Insert(0, $targetName) }
        $DirectoryId = $directory.Parent
    }
    $segments -join '\'
}

$payloadFiles = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Force)
$payloadByPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
foreach ($file in $payloadFiles) {
    $relativePath = [IO.Path]::GetRelativePath($publishRoot, $file.FullName)
    $payloadByPath.Add($relativePath, $file)
}
$installedByPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$fileIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$rows = [Collections.Generic.List[object]]::new()
$issues = [Collections.Generic.List[object]]::new()
foreach ($file in $msiFiles) {
    $targetDirectory = Get-TargetDirectory $componentMap[$file.Component]
    $fileName = ($file.Name -split '\|')[-1]
    $relativePath = if ($targetDirectory) { Join-Path $targetDirectory $fileName } else { $fileName }
    if (-not $installedByPath.TryAdd($relativePath, $file)) { throw "Duplicate installation target: $relativePath" }
    [void]$fileIds.Add($file.Id)
    $extractedPath = Join-Path $extractedFileRoot $file.Id
    if (-not $payloadByPath.ContainsKey($relativePath)) {
        $issues.Add([pscustomobject]@{ Type = 'ExtraMsiTarget'; Path = $relativePath })
        continue
    }
    if (-not (Test-Path -LiteralPath $extractedPath -PathType Leaf)) {
        $issues.Add([pscustomobject]@{ Type = 'MissingExtractedFile'; Path = $relativePath; Id = $file.Id })
        continue
    }
    $expected = $payloadByPath[$relativePath]
    $actual = Get-Item -LiteralPath $extractedPath
    $expectedHash = (Get-FileHash -LiteralPath $expected.FullName -Algorithm SHA256).Hash
    $actualHash = (Get-FileHash -LiteralPath $actual.FullName -Algorithm SHA256).Hash
    $tableLength = [long]$file.Length
    $matched = $expected.Length -eq $actual.Length -and $expected.Length -eq $tableLength -and $expectedHash -ceq $actualHash
    $row = [pscustomobject]@{
        TargetRelativePath = $relativePath
        MsiFileId = $file.Id
        PayloadLength = $expected.Length
        MsiTableLength = $tableLength
        ExtractedLength = $actual.Length
        PayloadSha256 = $expectedHash
        ExtractedSha256 = $actualHash
        Matches = $matched
    }
    $rows.Add($row)
    if (-not $matched) { $issues.Add([pscustomobject]@{ Type = 'ContentMismatch'; Path = $relativePath }) }
}
foreach ($relativePath in $payloadByPath.Keys) {
    if (-not $installedByPath.ContainsKey($relativePath)) {
        $issues.Add([pscustomobject]@{ Type = 'MissingMsiTarget'; Path = $relativePath })
    }
}
$extractedFiles = @(Get-ChildItem -LiteralPath $extractedFileRoot -File -Force)
foreach ($file in $extractedFiles) {
    if (-not $fileIds.Contains($file.Name)) { $issues.Add([pscustomobject]@{ Type = 'UnexpectedExtractedFile'; Path = $file.Name }) }
}
$embeddedEvidence = Get-AssetEvidence $embeddedMsiPath
$assetAfter = @(Get-AssetEvidence $msiPath; Get-AssetEvidence $setupPath)
$assetsUnchanged = $true
for ($index = 0; $index -lt $assetBefore.Count; $index++) {
    if ($assetBefore[$index].Length -ne $assetAfter[$index].Length -or $assetBefore[$index].Sha256 -cne $assetAfter[$index].Sha256) {
        $assetsUnchanged = $false
    }
}
$embeddedMsiMatches = $embeddedEvidence.Length -eq $assetAfter[0].Length -and $embeddedEvidence.Sha256 -ceq $assetAfter[0].Sha256
$propertyMap = @{}
foreach ($property in $properties) { $propertyMap[$property.Name] = $property.Value }
[xml]$decompiled = Get-Content -LiteralPath (Join-Path $outputPath 'msi\Package.wxs') -Raw
[xml]$burnManifest = Get-Content -LiteralPath (Join-Path $outputPath 'burn\ba\manifest.xml') -Raw
[xml]$baManifest = Get-Content -LiteralPath (Join-Path $outputPath 'burn\ba\BootstrapperApplicationData.xml') -Raw
$package = $decompiled.SelectSingleNode('/*/*[local-name()="Package"]')
$registration = $burnManifest.SelectSingleNode('/*/*[local-name()="Registration"]')
$bundlePackages = @($burnManifest.SelectNodes('/*/*[local-name()="Chain"]/*[local-name()="MsiPackage"]'))
if ($bundlePackages.Count -ne 1) { throw "Expected one MSI in Burn's chain, found $($bundlePackages.Count)." }
$bundlePackage = $bundlePackages[0]
$baBundle = $baManifest.SelectSingleNode('/*/*[local-name()="WixBundleProperties"]')
$baPackage = $baManifest.SelectSingleNode('/*/*[local-name()="WixPackageProperties"]')
$manifestPayload = $burnManifest.SelectSingleNode('/*/*[local-name()="Payload" and @Id="WinShellMsi"]')
$payloadSha512 = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA512).Hash
$bundleStream = [IO.File]::OpenRead($setupPath)
$bundleReader = [IO.BinaryReader]::new($bundleStream)
try {
    if ($bundleReader.ReadUInt16() -ne 0x5A4D) { throw 'Bundle has no DOS header.' }
    $bundleStream.Position = 0x3C
    $peOffset = $bundleReader.ReadInt32()
    $bundleStream.Position = $peOffset
    if ($bundleReader.ReadUInt32() -ne 0x4550) { throw 'Bundle has no PE header.' }
    $bundleMachine = $bundleReader.ReadUInt16()
}
finally { $bundleReader.Dispose() }
$metadata = [pscustomobject]@{
    ProductVersion = $propertyMap['ProductVersion']
    ProductCode = $propertyMap['ProductCode']
    UpgradeCode = $propertyMap['UpgradeCode']
    MsiTemplate = $msiTemplate
    BundlePeMachine = $bundleMachine
    Scope = $package.Scope
    VersionMatches = $propertyMap['ProductVersion'] -eq $package.Version -and $package.Version -eq $registration.Version -and $package.Version -eq $bundlePackage.Version -and $package.Version -eq $baPackage.Version
    ProductCodeMatches = $propertyMap['ProductCode'] -eq $package.ProductCode -and $package.ProductCode -eq $bundlePackage.ProductCode -and $package.ProductCode -eq $baPackage.ProductCode
    UpgradeCodeMatches = $propertyMap['UpgradeCode'] -eq $package.UpgradeCode -and $package.UpgradeCode -eq $bundlePackage.UpgradeCode -and $package.UpgradeCode -eq $baPackage.UpgradeCode
    IsPerUser = $package.Scope -eq 'perUser' -and $registration.Scope -eq 'perUser' -and $bundlePackage.Scope -eq 'perUser' -and $baBundle.Scope -eq 'perUser' -and -not $propertyMap.ContainsKey('ALLUSERS') -and -not $propertyMap.ContainsKey('MSIINSTALLPERUSER')
    IsX64 = ($msiTemplate -split ';')[0] -eq 'x64' -and $bundleMachine -eq 0x8664 -and $burnManifest.DocumentElement.Win64 -eq 'yes'
    EmbeddedManifestMatches = $manifestPayload.Packaging -eq 'embedded' -and [long]$manifestPayload.FileSize -eq $assetAfter[0].Length -and $manifestPayload.Hash -ceq $payloadSha512
}
$metadataPassed = $metadata.VersionMatches -and $metadata.ProductCodeMatches -and $metadata.UpgradeCodeMatches -and $metadata.IsPerUser -and $metadata.IsX64 -and $metadata.EmbeddedManifestMatches
$summary = [pscustomobject]@{
    CheckedAtUtc = [DateTime]::UtcNow.ToString('O')
    MsiOpenedReadOnly = $true
    InstallationPerformed = $false
    PublishRoot = $publishRoot
    MsiFileCount = $msiFiles.Count
    ExtractedFileCount = $extractedFiles.Count
    PayloadFileCount = $payloadFiles.Count
    ComparedFileCount = $rows.Count
    MatchedFileCount = @($rows | Where-Object Matches).Count
    PayloadBytes = [long]($payloadFiles | Measure-Object -Property Length -Sum).Sum
    MsiTableFileBytes = [long]($msiFiles | Measure-Object -Property Length -Sum).Sum
    IssueCount = $issues.Count
    Issues = @($issues.ToArray())
    EmbeddedMsiMatchesStandalone = $embeddedMsiMatches
    AssetsUnchangedDuringVerification = $assetsUnchanged
    FinalAssets = $assetAfter
    EmbeddedMsi = $embeddedEvidence
    Metadata = $metadata
    VerificationPassed = $issues.Count -eq 0 -and $rows.Count -eq $payloadFiles.Count -and $msiFiles.Count -eq $extractedFiles.Count -and $embeddedMsiMatches -and $assetsUnchanged -and $metadataPassed
}
$rows | Export-Csv -LiteralPath (Join-Path $outputPath 'msi-payload-comparison.csv') -NoTypeInformation -Encoding utf8
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputPath 'installer-verification.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 8
if (-not $summary.VerificationPassed) { throw 'Installer verification failed; see installer-verification.json.' }
