[CmdletBinding()]
param([string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'OpenPrism'), [switch]$Fixture, [switch]$Interactive)
$ErrorActionPreference = 'Stop'
$install = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if ($Fixture -and -not $install.StartsWith((Join-Path $env:TEMP 'OpenPrism-Fixture-'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture escaped temporary directory.' }
for ($parent = $install; $parent; $parent = Split-Path -Parent $parent) { if ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked install path.' } }
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OpenPrism'
if (-not $Fixture -and (Get-ItemProperty -LiteralPath $key).InstallLocation -ne $install) { throw 'Installation registration mismatch.' }
$manifestPath = Join-Path $install 'package-manifest.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.product -ne 'OpenPrism') { throw 'Wrong product.' }
if ($Interactive) {
    if ($Fixture) { throw 'Interactive uninstall requires a registered installation.' }
    $name = 'OpenPrism-Uninstall.exe'
    $entry = @($manifest.files | Where-Object { $_.path -eq $name })
    if ($entry.Count -eq 0) {
        $name = 'OpenPrism-Setup.exe'
        $entry = @($manifest.files | Where-Object { $_.path -eq $name })
    }
    $setup = Join-Path $install $name
    if ($entry.Count -ne 1 -or ((Get-Item -LiteralPath $setup -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne $entry[0].sha256) { throw 'Native setup is missing or modified.' }
    # Run outside the installation so Windows can remove the original executable.
    $work = Join-Path ([IO.Path]::GetTempPath()) ('OpenPrism-Uninstall-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    $copy = Join-Path $work $name
    try {
        Copy-Item -LiteralPath $setup -Destination $copy
        $window = Start-Process -FilePath $copy -ArgumentList '--uninstall' -WorkingDirectory $work -WindowStyle Normal -PassThru
        $window.WaitForExit()
        if ($window.ExitCode -ne 0) { throw 'Native uninstaller exited unexpectedly.' }
    } finally {
        if (Test-Path -LiteralPath $copy) { Remove-Item -LiteralPath $copy -Force }
        Remove-Item -LiteralPath $work -Force
    }
    return
}
$files = @()
foreach ($entry in $manifest.files) {
    $relative = [string]$entry.path
    $path = [IO.Path]::GetFullPath((Join-Path $install $relative))
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or -not $path.StartsWith($install + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe inventory path.' }
    for ($parent = $path; $parent -and $parent.Length -ge $install.Length; $parent = Split-Path -Parent $parent) {
        if ((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked inventory file.' }
    }
    $files += @{ path=$path; sha256=$entry.sha256; relative=$relative }
    if (-not $Fixture -and $relative.StartsWith('backend/') -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw 'Modified backend preserved; cleanup cannot safely execute it.' }
}
if (-not $Fixture) {
    Get-Process WGTun -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $install 'WGTun.exe') } | Stop-Process -Force
    $result = & (Join-Path $install 'backend/WGTun.Service.exe') --uninstall-app | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $result.success) { throw 'Service/data cleanup failed; application files preserved.' }
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $value = (Get-ItemProperty -LiteralPath $runKey -Name WGTun -ErrorAction SilentlyContinue).WGTun
    if ($value -eq ('"' + (Join-Path $install 'WGTun.exe') + '" --startup')) { Remove-ItemProperty -LiteralPath $runKey -Name WGTun }
    $desktop = [Environment]::GetFolderPath('Desktop')
    if ($desktop) {
        $link = Join-Path $desktop 'OpenPrism.lnk'
        if ((Test-Path -LiteralPath $link -PathType Leaf) -and -not ((Get-Item -LiteralPath $link -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
            if ($shortcut.TargetPath -eq (Join-Path $install 'WGTun.exe')) { Remove-Item -LiteralPath $link -Force }
        }
    }
}
foreach ($file in $files) {
    if ((Test-Path -LiteralPath $file.path -PathType Leaf) -and (Get-FileHash -Algorithm SHA256 -LiteralPath $file.path).Hash -eq $file.sha256) { Remove-Item -LiteralPath $file.path -Force }
}
foreach ($name in @('package-manifest.json','package-manifest.p7s')) { $path=Join-Path $install $name; if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force } }
Get-ChildItem -LiteralPath $install -Directory -Recurse -Force | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object { if (-not (Get-ChildItem -LiteralPath $_.FullName -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $_.FullName -Force } }
if (-not (Get-ChildItem -LiteralPath $install -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $install -Force }
if (-not $Fixture) { Remove-Item -LiteralPath $key -Force }
Write-Output 'OpenPrism removed. Modified and unrecognized files were preserved.'
