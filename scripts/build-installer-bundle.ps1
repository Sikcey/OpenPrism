[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory,
      [Parameter(Mandatory)][string]$OutputFile,
      [string]$CertificateThumbprint = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$archive = Join-Path ([IO.Path]::GetTempPath()) ('OpenPrism-Package-' + [Guid]::NewGuid().ToString('N') + '.zip')
$oldPackage = $env:OPENPRISM_BUNDLE_PATH
$oldPublisher = $env:OPENPRISM_INSTALLER_PUBLISHER
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($package, $archive)
    $env:OPENPRISM_BUNDLE_PATH = $archive
    $env:OPENPRISM_INSTALLER_PUBLISHER = $CertificateThumbprint
    cargo build --locked --manifest-path (Join-Path $repo 'installer/Cargo.toml') --release --features bundle --bin openprism-bundle
    if ($LASTEXITCODE -ne 0) { throw 'Standalone installer build failed.' }
    Copy-Item -LiteralPath (Join-Path $repo 'installer/target/release/openprism-bundle.exe') -Destination $OutputFile -Force
} finally {
    $env:OPENPRISM_BUNDLE_PATH = $oldPackage
    $env:OPENPRISM_INSTALLER_PUBLISHER = $oldPublisher
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
}
