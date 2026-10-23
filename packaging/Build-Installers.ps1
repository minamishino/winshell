#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SourceDirectory,
    [Parameter(Mandatory)][string] $PublishDirectory,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string] $Version,
    [string] $WixExecutable = 'wix',
    [switch] $AcceptWixEula
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path -LiteralPath $SourceDirectory).Path
$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path.TrimEnd('\')
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory).TrimEnd('\')
$wixPath = (Get-Command $WixExecutable -CommandType Application -ErrorAction Stop).Source
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
if ($outputPath.Equals($publishPath, [StringComparison]::OrdinalIgnoreCase) -or
    $outputPath.StartsWith($publishPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be outside PublishDirectory so generated installers cannot enter the payload.'
}
if (-not (Test-Path -LiteralPath (Join-Path $publishPath 'WinShell.App.exe') -PathType Leaf)) {
    throw "PublishDirectory must contain the finished WinShell.App.exe payload: $publishPath"
}
$iconPath = Join-Path $repositoryRoot 'src\WinShell.App\Assets\winshell.ico'
$licensePath = Join-Path $repositoryRoot 'LICENSE'
foreach ($requiredFile in @($iconPath, $licensePath)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required packaging source is missing: $requiredFile"
    }
}

$reportedVersion = & $wixPath --version
if ($LASTEXITCODE -ne 0 -or "$reportedVersion" -notmatch '^7\.0\.0(?:\+|$)') {
    throw "This packaging snapshot requires WiX 7.0.0. Reported version: $reportedVersion"
}

function Invoke-Wix {
    param([Parameter(Mandatory)][string[]] $Arguments)
    if ($AcceptWixEula) {
        $Arguments += @('-acceptEula', 'wix7')
    }
    & $wixPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "WiX failed with exit code $LASTEXITCODE ($($Arguments[0]))."
    }
}

function Test-MsiPackage {
    param([string] $MsiPath, [string] $IntermediatePath)
    $arguments = @('msi', 'validate', $MsiPath, '-sice', 'ICE38', '-sice', 'ICE64',
        '-pdb', (Join-Path $IntermediatePath 'Package.wixpdb'))
    if ($AcceptWixEula) { $arguments += @('-acceptEula', 'wix7') }
    $validationOutput = @(& $wixPath @arguments 2>&1 | ForEach-Object { "$_" })
    $validationExitCode = $LASTEXITCODE
    $validationOutput | Set-Content -LiteralPath (Join-Path $IntermediatePath 'msi-validation.log')
    $validationErrors = @($validationOutput | Where-Object { $_ -match 'error WIX\d+:' })
    if ($validationExitCode -eq 0 -and $validationErrors.Count -eq 0) {
        Write-Host 'MSI validation passed; see msi-validation.log for warnings.'
        return
    }

    # These are valid Microsoft language IDs rejected by ICE03 on some Windows hosts.
    # Match exact target path + language + diagnostic; never disable ICE03 as a whole.
    $localeLanguages = @{ 'gd-gb' = '1169'; 'mi-nz' = '1153'; 'ug-cn' = '1152' }
    $allowedFileIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $installer = $null
    $database = $null
    $view = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($MsiPath, 0)
        $query = 'SELECT `File`.`File`, `File`.`FileName`, `File`.`Language`, `Directory`.`DefaultDir`, `Directory`.`Directory_Parent` FROM `File`, `Component`, `Directory` WHERE `File`.`Component_` = `Component`.`Component` AND `Component`.`Directory_` = `Directory`.`Directory`'
        $view = $database.OpenView($query)
        $view.Execute()
        while ($record = $view.Fetch()) {
            try {
                $values = @(foreach ($column in 1..5) {
                    $record.GetType().InvokeMember('StringData', [Reflection.BindingFlags]::GetProperty, $null, $record, @($column))
                })
                $fileName = ($values[1] -split '\|')[-1]
                $locale = ($values[3] -split '\|')[-1]
                if ($values[4] -eq 'INSTALLFOLDER' -and $localeLanguages.ContainsKey($locale) -and
                    $values[2] -eq $localeLanguages[$locale] -and
                    $fileName -in @('Microsoft.ui.xaml.dll.mui', 'Microsoft.UI.Xaml.Phone.dll.mui')) {
                    [void] $allowedFileIds.Add($values[0])
                }
            }
            finally { [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
        }
    }
    finally {
        if ($null -ne $view) { $view.Close() }
        foreach ($comObject in @($view, $database, $installer)) {
            if ($null -ne $comObject) { [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($comObject) }
        }
    }

    $unexpectedErrors = @($validationErrors | Where-Object {
        $_ -notmatch 'error WIX0204: ICE03: Invalid Language Id; Table: File, Column: Language, Key\(s\): ([A-Za-z0-9_.]+)$' -or
        -not $allowedFileIds.Contains($Matches[1])
    })
    if ($validationExitCode -ne 204 -or $validationErrors.Count -eq 0 -or $allowedFileIds.Count -gt 6 -or $unexpectedErrors.Count -ne 0) {
        $validationOutput | Write-Host
        throw "MSI validation failed with unexpected diagnostics (exit $validationExitCode)."
    }
    Write-Host "MSI validation accepted $($validationErrors.Count) exact Windows App SDK MUI language exceptions; all other errors remain blocking. See msi-validation.log."
}

$intermediatePath = Join-Path $outputPath '.wix'
New-Item -ItemType Directory -Path $outputPath | Out-Null
New-Item -ItemType Directory -Path $intermediatePath | Out-Null

# Retain one ProductCode for the same version, including repeated builds and the EXE wrapper.
$identityBytes = [Text.Encoding]::UTF8.GetBytes("minamishino.WinShell.MSI.x64.$Version")
$identityHash = [Security.Cryptography.SHA256]::HashData($identityBytes)
$productCode = [Guid]::new([byte[]] $identityHash[0..15]).ToString('B').ToUpperInvariant()

# The license displayed by both installers is generated from the repository's actual license.
$licenseText = [IO.File]::ReadAllText($licensePath)
$rtfText = $licenseText.Replace('\', '\\').Replace('{', '\{').Replace('}', '\}')
$rtfText = $rtfText -replace '\r?\n', '\par '
$licenseRtfPath = Join-Path $intermediatePath 'License.rtf'
[IO.File]::WriteAllText($licenseRtfPath, ('{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs18 ' + $rtfText + '}'), [Text.Encoding]::ASCII)

$msiPath = Join-Path $outputPath "WinShell-$Version-win-x64.msi"
$bundlePath = Join-Path $outputPath "WinShell-$Version-win-x64-setup.exe"
$commonArguments = @(
    '-arch', 'x64', '-culture', 'en-US',
    '-d', "Version=$Version",
    '-d', "IconPath=$iconPath",
    '-d', "LicenseRtfPath=$licenseRtfPath"
)

# Local extension cache: no global extension state, floating package versions, or automatic EULA acceptance.
Push-Location -LiteralPath $intermediatePath
try {
    foreach ($extension in @('WixToolset.UI.wixext', 'WixToolset.Util.wixext', 'WixToolset.BootstrapperApplications.wixext')) {
        Invoke-Wix -Arguments @('extension', 'add', "$extension/7.0.0")
    }

    # WiX Files uses file key paths. ICE38/ICE64 assume roaming per-user components;
    # this package has a fixed, non-roaming LocalAppData location and non-advertised shortcut.
    # Keep every other ICE enabled; never suppress all MSI validation.
    Invoke-Wix -Arguments (@('build', (Join-Path $PSScriptRoot 'Package.wxs')) + $commonArguments + @(
        '-ext', 'WixToolset.UI.wixext/7.0.0', '-ext', 'WixToolset.Util.wixext/7.0.0',
        '-d', "PublishDirectory=$publishPath", '-d', "ProductCode=$productCode",
        '-intermediateFolder', (Join-Path $intermediatePath 'msi'),
        '-pdb', (Join-Path $intermediatePath 'Package.wixpdb'), '-out', $msiPath
    ))

    Test-MsiPackage -MsiPath $msiPath -IntermediatePath $intermediatePath

    Invoke-Wix -Arguments (@('build', (Join-Path $PSScriptRoot 'Bundle.wxs')) + $commonArguments + @(
        '-ext', 'WixToolset.BootstrapperApplications.wixext/7.0.0', '-d', "MsiPath=$msiPath",
        '-intermediateFolder', (Join-Path $intermediatePath 'bundle'),
        '-pdb', (Join-Path $intermediatePath 'Bundle.wixpdb'), '-out', $bundlePath
    ))
}
finally {
    Pop-Location
}

Get-Item -LiteralPath $msiPath, $bundlePath |
    Select-Object FullName, Length, @{Name = 'SHA256'; Expression = { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }}
