[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts/tun-engine')
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$source = Join-Path $workspace 'artifacts/research-source/sing-box'
if (-not (Test-Path -LiteralPath $source)) {
 & git clone --depth 1 --branch v1.14.4 https://github.com/SagerNet/sing-box.git $source
 if ($LASTEXITCODE -ne 0) { throw 'Could not obtain pinned sing-box source.' }
}
if ((& git -C $source rev-parse HEAD) -ne 'c005f35300ce76427dc285481ec5695f5b179cd2' -or $LASTEXITCODE -ne 0) { throw 'Unexpected upstream revision.' }
if ((& git -C $source status --porcelain) -or $LASTEXITCODE -ne 0) { throw 'Upstream checkout must remain clean.' }
$output = [IO.Path]::GetFullPath((Join-Path $workspace $OutputDirectory))
if (-not $output.StartsWith($workspace + '',[StringComparison]::OrdinalIgnoreCase)) { throw 'Output must remain in workspace.' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
Push-Location (Join-Path $workspace 'src/WGTun.Tun')
try {
 & go test -p 2 -tags with_gvisor,with_wireguard ./...
 if ($LASTEXITCODE -ne 0) { throw 'Packet engine checks failed.' }
 & go build -p 2 -trimpath -tags with_gvisor,with_wireguard -ldflags '-s -w' -o (Join-Path $output 'WGTun.Tun.exe') .
 if ($LASTEXITCODE -ne 0) { throw 'Packet engine build failed.' }
} finally { Pop-Location }
$digest = (Get-FileHash -LiteralPath (Join-Path $output 'WGTun.Tun.exe') -Algorithm SHA256).Hash
$identity = 'namespace WGTun.Core; internal static class TunBuildIdentity { internal const string Sha256 = "' + $digest + '"; }'
[IO.File]::WriteAllText((Join-Path $workspace 'src/WGTun.Core/TunBuildIdentity.cs'), $identity)
Write-Output (Join-Path $output 'WGTun.Tun.exe')
