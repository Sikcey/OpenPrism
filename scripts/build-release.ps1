[CmdletBinding()]
param([Parameter(Mandatory)][string]$CertificateThumbprint,
      [Parameter(Mandatory)][string]$ExpectedPublisher,
      [string]$TimestampUrl = 'http://timestamp.digicert.com')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$cert = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction Stop
if (-not $cert.HasPrivateKey -or $cert.Subject -ne $ExpectedPublisher -or $cert.NotAfter -le (Get-Date)) { throw 'Signing certificate, private key, or publisher is invalid.' }
$out = Join-Path $repo 'artifacts/release-package'
if (Test-Path -LiteralPath $out) { throw 'Remove the previous release-package explicitly before building.' }
& (Join-Path $PSScriptRoot 'build-native.ps1') -OutputDirectory 'artifacts/release-package' -CertificateThumbprint $CertificateThumbprint
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item (Join-Path $PSScriptRoot 'install-openprism.ps1'), (Join-Path $PSScriptRoot 'uninstall-openprism.ps1') -Destination $out
$installer = Join-Path $out 'install-openprism.ps1'
(Get-Content -Raw -LiteralPath $installer).Replace("$" + "TrustedPublisher = ''", "$" + "TrustedPublisher = '" + $cert.Thumbprint + "'") | Set-Content -LiteralPath $installer -Encoding UTF8
function Sign-ReleaseFile([string]$file) {
    $signed = Set-AuthenticodeSignature -LiteralPath $file -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $TimestampUrl
    if ($signed.Status -ne 'Valid') { throw "Signing failed: $file ($($signed.Status))" }
    $check = Get-AuthenticodeSignature -LiteralPath $file
    if ($check.Status -ne 'Valid' -or $check.SignerCertificate.Thumbprint -ne $cert.Thumbprint -or $check.SignerCertificate.Subject -ne $ExpectedPublisher -or -not $check.TimeStamperCertificate) { throw "Signature verification failed: $file" }
}
$own = @((Join-Path $out 'WGTun.exe'), (Join-Path $out 'OpenPrism-Setup.exe'), (Join-Path $out 'OpenPrism-Uninstall.exe'), (Join-Path $out 'backend/WGTun.Service.exe'), (Join-Path $out 'backend/WGTun.Core.dll'), (Join-Path $out 'backend/WGTun.Service.dll'), (Join-Path $out 'install-openprism.ps1'), (Join-Path $out 'uninstall-openprism.ps1'))
foreach ($file in $own) { Sign-ReleaseFile $file }
$files = @(Get-ChildItem -LiteralPath $out -File -Recurse | Where-Object { $_.Name -notin @('package-manifest.json','package-manifest.p7s') } | ForEach-Object { @{ path = $_.FullName.Substring($out.Length + 1).Replace('\','/'); sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash; size = $_.Length } })
@{ product = 'OpenPrism'; version = '0.3.0'; files = $files } | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $out 'package-manifest.json')
Add-Type -AssemblyName System.Security
$manifestBytes = [IO.File]::ReadAllBytes((Join-Path $out 'package-manifest.json'))
$cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($manifestBytes), $true)
$signer = [Security.Cryptography.Pkcs.CmsSigner]::new($cert)
$signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
$cms.ComputeSignature($signer)
[IO.File]::WriteAllBytes((Join-Path $out 'package-manifest.p7s'), $cms.Encode())
$bundle = Join-Path $repo 'artifacts/OpenPrism-Installer.exe'
& (Join-Path $PSScriptRoot 'build-installer-bundle.ps1') -PackageDirectory $out -OutputFile $bundle -CertificateThumbprint $cert.Thumbprint
Sign-ReleaseFile $bundle
Copy-Item -LiteralPath (Join-Path $out 'OpenPrism-Uninstall.exe') -Destination (Join-Path $repo 'artifacts/OpenPrism-Uninstall.exe') -Force
$zip = Join-Path $repo 'artifacts/OpenPrism-win-x64.zip'
if (Test-Path -LiteralPath $zip) { throw 'Release archive already exists.' }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
(Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash + '  OpenPrism-win-x64.zip' | Set-Content -Encoding ASCII ($zip + '.sha256')
