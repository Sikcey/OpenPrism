[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts/OpenPrism', [string]$CertificateThumbprint = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    $output = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
    if (-not $output.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must stay in the project workspace.' }
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    & (Join-Path $PSScriptRoot 'build-tun.ps1')
    # Never ship files retained from a previous engine in an existing output folder.
    $backendOutput = Join-Path $output 'backend'
    if (Test-Path -LiteralPath $backendOutput) { Remove-Item -LiteralPath $backendOutput -Recurse -Force }
    dotnet run --project tests/WGTun.Checks/WGTun.Checks.csproj -c Release --no-launch-profile
    if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
    dotnet run --project src/WGTun.Core/Tests/WGTun.DependencyChecks.csproj -c Release --no-launch-profile
    if ($LASTEXITCODE -ne 0) { throw 'Dependency download checks failed.' }
    dotnet run --project tests/WGTun.CaptureBoundary -c Release --no-launch-profile
    if ($LASTEXITCODE -ne 0) { throw 'Capture boundary checks failed.' }
    dotnet run --project src/WGTun.Service/WGTun.Service.csproj -c Release -- verify-configs (Join-Path $repo 'artifacts/config-checks')
    if ($LASTEXITCODE -ne 0) { throw 'Pinned engine configuration checks failed.' }
    dotnet publish src/WGTun.Service/WGTun.Service.csproj -p:ReleasePublisherThumbprint=$CertificateThumbprint -c Release -r win-x64 --self-contained true -o (Join-Path $output 'backend') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Backend publish failed.' }
    Copy-Item -LiteralPath (Join-Path $repo 'artifacts/tun-engine/WGTun.Tun.exe') -Destination (Join-Path $output 'backend/WGTun.Tun.exe')
    & (Join-Path $output 'backend/WGTun.Service.exe') verify-packet-engine
    if ($LASTEXITCODE -ne 0) { throw 'Packet engine and broker do not match.' }
    if (Test-Path -LiteralPath (Join-Path $backendOutput 'dependencies')) { throw 'Dependency payloads must not be bundled in the application.' }
    cargo build --manifest-path src/WGTun.Native/Cargo.toml --release
    if ($LASTEXITCODE -ne 0) { throw 'Rust build failed.' }
    Copy-Item -LiteralPath 'src/WGTun.Native/target/release/wgtun-native.exe' -Destination (Join-Path $output 'WGTun.exe') -Force
    $previousInstallerPublisher = $env:OPENPRISM_INSTALLER_PUBLISHER
    try {
        $env:OPENPRISM_INSTALLER_PUBLISHER = $CertificateThumbprint
        cargo build --locked --manifest-path installer/Cargo.toml --release
        if ($LASTEXITCODE -ne 0) { throw 'Native installer build failed.' }
    } finally {
        $env:OPENPRISM_INSTALLER_PUBLISHER = $previousInstallerPublisher
    }
    Copy-Item -LiteralPath 'installer/target/release/openprism-installer.exe' -Destination (Join-Path $output 'OpenPrism-Setup.exe') -Force
    Copy-Item -LiteralPath 'installer/target/release/openprism-uninstall.exe' -Destination (Join-Path $output 'OpenPrism-Uninstall.exe') -Force
    Copy-Item -LiteralPath 'README.md' -Destination (Join-Path $output 'README.md') -Force
    New-Item -ItemType Directory -Force -Path (Join-Path $output 'docs') | Out-Null
    Copy-Item -LiteralPath 'docs/current-verification.md' -Destination (Join-Path $output 'docs/current-verification.md') -Force
    Copy-Item -LiteralPath 'scripts/install-openprism.ps1','scripts/uninstall-openprism.ps1' -Destination $output -Force
    if (-not $CertificateThumbprint) {
        Copy-Item -LiteralPath 'scripts/install-development.cmd' -Destination (Join-Path $output 'Install.cmd') -Force
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $output 'src/WGTun.Tun'),(Join-Path $output 'scripts') | Out-Null
    Copy-Item -Path 'src/WGTun.Tun/*.go','src/WGTun.Tun/go.mod','src/WGTun.Tun/go.sum','src/WGTun.Tun/COPYING' -Destination (Join-Path $output 'src/WGTun.Tun') -Force
    Copy-Item -LiteralPath 'scripts/build-tun.ps1' -Destination (Join-Path $output 'scripts') -Force
    Copy-Item -LiteralPath 'README.tr.md','README.ru.md','README.zh-CN.md' -Destination $output -Force
    New-Item -ItemType Directory -Force -Path (Join-Path $output 'docs/images'),(Join-Path $output 'dependencies/notices'),(Join-Path $output 'src/WGTun.Core') | Out-Null
    Copy-Item -LiteralPath 'docs/images/main-menu.png' -Destination (Join-Path $output 'docs/images/main-menu.png') -Force
    Copy-Item -LiteralPath 'docs/capture-boundary.md','docs/installation.md' -Destination (Join-Path $output 'docs') -Force
    Copy-Item -Path 'dependencies/notices/*' -Destination (Join-Path $output 'dependencies/notices') -Force
    Copy-Item -LiteralPath 'src/WGTun.Core/StrategySources.md' -Destination (Join-Path $output 'src/WGTun.Core/StrategySources.md') -Force
    $files = @(Get-ChildItem -LiteralPath $output -File -Recurse | Where-Object { $_.Name -notin @('package-manifest.json','package-manifest.p7s') } | ForEach-Object { @{path=$_.FullName.Substring($output.Length+1).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash; size=$_.Length} })
    @{product='OpenPrism'; version='0.3.0'; signed=$false; files=$files} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'package-manifest.json') -Encoding UTF8
    if (-not $CertificateThumbprint) {
        $bundle = Join-Path (Split-Path -Parent $output) 'OpenPrism-Installer.exe'
        & (Join-Path $PSScriptRoot 'build-installer-bundle.ps1') -PackageDirectory $output -OutputFile $bundle
        Copy-Item -LiteralPath (Join-Path $output 'OpenPrism-Uninstall.exe') -Destination (Join-Path (Split-Path -Parent $output) 'OpenPrism-Uninstall.exe') -Force
        Write-Output $bundle
    }
    Write-Output (Join-Path $output 'WGTun.exe')
} finally { Pop-Location }
