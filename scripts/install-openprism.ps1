[CmdletBinding()]
param([string]$PackageDirectory = $PSScriptRoot,
      [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'OpenPrism'),
      [ValidateSet('core','byedpi','zapret','byedpi,zapret')][string]$SelectedComponent = 'core',
      [switch]$DevelopmentPackage, [switch]$ValidateOnly, [switch]$Fixture)
$ErrorActionPreference = 'Stop'
$TrustedPublisher = '' # Replaced in a signed release.
$package = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\')
$install = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
function Assert-NoLinks([string]$path) {
    for ($entry = $path; $entry; $entry = Split-Path -Parent $entry) {
        if ((Test-Path -LiteralPath $entry) -and ((Get-Item -LiteralPath $entry -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked paths are not supported.' }
    }
}
Assert-NoLinks $package
Assert-NoLinks $install
if ($Fixture -and -not $install.StartsWith((Join-Path $env:TEMP 'OpenPrism-Fixture-'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixtures must remain in temporary fixture directories.' }
if ($package -eq $install -or $package.StartsWith($install + '\', [StringComparison]::OrdinalIgnoreCase) -or $install.StartsWith($package + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package and installation must be separate.' }
$manifestPath = Join-Path $package 'package-manifest.json'
$manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
if ($manifestBytes.Length -gt 1048576) { throw 'Manifest too large.' }
if (-not $DevelopmentPackage) {
    if (-not $TrustedPublisher) { throw 'No trusted publisher configured. Use a signed release or explicitly select DevelopmentPackage.' }
    Add-Type -AssemblyName System.Security
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($manifestBytes), $true)
    $cms.Decode([IO.File]::ReadAllBytes((Join-Path $package 'package-manifest.p7s')))
    $cms.CheckSignature($false)
    if ($cms.SignerInfos.Count -ne 1 -or $cms.SignerInfos[0].Certificate.Thumbprint -ne $TrustedPublisher) { throw 'Untrusted publisher.' }
}
$manifest = [Text.Encoding]::UTF8.GetString($manifestBytes).TrimStart([char]0xfeff) | ConvertFrom-Json
if ($manifest.product -ne 'OpenPrism' -or -not $manifest.files) { throw 'Wrong product or empty manifest.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    $relative = [string]$entry.path
    if (-not $relative -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or ($relative -split '[/\\]' | Where-Object { $_ -in '.','..','' -or $_.EndsWith('.') -or $_.EndsWith(' ') })) { throw 'Unsafe package path.' }
    if (-not $seen.Add($relative.Replace('\','/'))) { throw 'Duplicate package path.' }
    $file = [IO.Path]::GetFullPath((Join-Path $package $relative))
    if (-not $file.StartsWith($package + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package escaped root.' }
    Assert-NoLinks $file
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash -ne $entry.sha256) { throw "Package hash mismatch: $relative" }
}
foreach ($required in @('WGTun.exe','backend/WGTun.Service.exe','backend/WGTun.Core.dll','backend/WGTun.Tun.exe','uninstall-openprism.ps1')) { if (-not $seen.Contains($required)) { throw "Missing paired payload: $required" } }
if ($ValidateOnly) { Write-Output 'Package validated.'; return }
if ((Test-Path -LiteralPath $install) -and -not (Test-Path -LiteralPath (Join-Path $install 'package-manifest.json'))) { throw 'Unrecognized install directory preserved.' }
$stage = $install + '-stage-' + [Guid]::NewGuid().ToString('N')
$backup = $install + '-previous-' + [Guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Path $stage | Out-Null
foreach ($entry in $manifest.files) {
    $target = Join-Path $stage $entry.path
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath (Join-Path $package $entry.path) -Destination $target
}
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $stage 'package-manifest.json')
if (Test-Path -LiteralPath (Join-Path $package 'package-manifest.p7s')) { Copy-Item -LiteralPath (Join-Path $package 'package-manifest.p7s') -Destination $stage }
if (Test-Path -LiteralPath $install) { Move-Item -LiteralPath $install -Destination $backup }
try {
    Move-Item -LiteralPath $stage -Destination $install
    if (-not $Fixture) {
        $answer = & (Join-Path $install 'backend/WGTun.Service.exe') install-service $SelectedComponent | ConvertFrom-Json
        if (-not $answer.success -or $LASTEXITCODE -ne 0) { throw 'Paired service update failed.' }
        $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OpenPrism'
        New-Item -Path $key -Force | Out-Null
        $command = 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -InstallDirectory "{1}"' -f (Join-Path $install 'uninstall-openprism.ps1'), $install
        if ($seen.Contains('OpenPrism-Uninstall.exe')) {
            $command = '"{0}"' -f (Join-Path $install 'OpenPrism-Uninstall.exe')
        } elseif ($seen.Contains('OpenPrism-Setup.exe')) {
            $command = ('"{0}" -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "{1}" -InstallDirectory "{2}" -Interactive' -f (Join-Path $PSHOME 'powershell.exe'), (Join-Path $install 'uninstall-openprism.ps1'), $install)
        }
        foreach ($pair in @{DisplayName='OpenPrism'; DisplayVersion=[string]$manifest.version; Publisher='OpenPrism'; InstallLocation=$install; UninstallString=$command; DisplayIcon=(Join-Path $install 'WGTun.exe')}.GetEnumerator()) { New-ItemProperty -Path $key -Name $pair.Key -Value $pair.Value -PropertyType String -Force | Out-Null }
        foreach ($name in @('NoModify','NoRepair')) { New-ItemProperty -Path $key -Name $name -Value 1 -PropertyType DWord -Force | Out-Null }
    }
} catch {
    if (Test-Path -LiteralPath $backup) {
        if (Test-Path -LiteralPath $install) { Move-Item -LiteralPath $install -Destination ($install + '-failed-' + [Guid]::NewGuid().ToString('N')) }
        Move-Item -LiteralPath $backup -Destination $install
        # The failed or declined attempt must not trigger another elevation prompt.
    }
    throw
}
Write-Output "Installed. Restart OpenPrism. Previous files, if any, remain at $backup until no longer in use."
