[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts/OpenPrism', [string]$CertificateThumbprint = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    $output = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
    if (-not $output.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must stay in the project workspace.' }
    New-Item -ItemType Directory -Force -Path $output | Out-Null
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
    if (Test-Path -LiteralPath (Join-Path $backendOutput 'dependencies')) { throw 'Dependency payloads must not be bundled in the application.' }
    cargo build --manifest-path src/WGTun.Native/Cargo.toml --release
    if ($LASTEXITCODE -ne 0) { throw 'Rust build failed.' }
    Copy-Item -LiteralPath 'src/WGTun.Native/target/release/wgtun-native.exe' -Destination (Join-Path $output 'WGTun.exe') -Force
    Copy-Item -LiteralPath 'README.md' -Destination (Join-Path $output 'README.md') -Force
    New-Item -ItemType Directory -Force -Path (Join-Path $output 'docs') | Out-Null
    Copy-Item -LiteralPath 'docs/current-verification.md' -Destination (Join-Path $output 'docs/current-verification.md') -Force
    Copy-Item -LiteralPath 'scripts/install-openprism.ps1','scripts/uninstall-openprism.ps1' -Destination $output -Force
    Copy-Item -LiteralPath 'README.tr.md','README.ru.md','README.zh-CN.md' -Destination $output -Force
    New-Item -ItemType Directory -Force -Path (Join-Path $output 'docs/images'),(Join-Path $output 'dependencies/notices'),(Join-Path $output 'src/WGTun.Core') | Out-Null
    Copy-Item -LiteralPath 'docs/images/main-menu.png' -Destination (Join-Path $output 'docs/images/main-menu.png') -Force
    Copy-Item -LiteralPath 'docs/capture-boundary.md','docs/installation.md' -Destination (Join-Path $output 'docs') -Force
    Copy-Item -Path 'dependencies/notices/*' -Destination (Join-Path $output 'dependencies/notices') -Force
    Copy-Item -LiteralPath 'src/WGTun.Core/StrategySources.md' -Destination (Join-Path $output 'src/WGTun.Core/StrategySources.md') -Force
    $files = @(Get-ChildItem -LiteralPath $output -File -Recurse | Where-Object { $_.Name -notin @('package-manifest.json','package-manifest.p7s') } | ForEach-Object { @{path=$_.FullName.Substring($output.Length+1).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
    @{product='OpenPrism'; version='0.2.0'; signed=$false; files=$files} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'package-manifest.json') -Encoding UTF8
    Write-Output (Join-Path $output 'WGTun.exe')
} finally { Pop-Location }
