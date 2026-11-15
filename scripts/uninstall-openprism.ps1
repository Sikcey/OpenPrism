[CmdletBinding()]
param([string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'OpenPrism'), [switch]$Fixture)
$ErrorActionPreference = 'Stop'
$install = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if ($Fixture -and -not $install.StartsWith((Join-Path $env:TEMP 'OpenPrism-Fixture-'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture escaped temporary directory.' }
for ($parent = $install; $parent; $parent = Split-Path -Parent $parent) { if ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked install path.' } }
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OpenPrism'
if (-not $Fixture -and (Get-ItemProperty -LiteralPath $key).InstallLocation -ne $install) { throw 'Installation registration mismatch.' }
$manifestPath = Join-Path $install 'package-manifest.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.product -ne 'OpenPrism') { throw 'Wrong product.' }
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
}
foreach ($file in $files) {
    if ((Test-Path -LiteralPath $file.path -PathType Leaf) -and (Get-FileHash -Algorithm SHA256 -LiteralPath $file.path).Hash -eq $file.sha256) { Remove-Item -LiteralPath $file.path -Force }
}
foreach ($name in @('package-manifest.json','package-manifest.p7s')) { $path=Join-Path $install $name; if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force } }
Get-ChildItem -LiteralPath $install -Directory -Recurse -Force | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object { if (-not (Get-ChildItem -LiteralPath $_.FullName -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $_.FullName -Force } }
if (-not (Get-ChildItem -LiteralPath $install -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $install -Force }
if (-not $Fixture) { Remove-Item -LiteralPath $key -Force }
Write-Output 'OpenPrism removed. Modified and unrecognized files were preserved.'
