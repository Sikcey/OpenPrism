[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Launch this script yourself in an administrator PowerShell. It never requests elevation.'
}
$workspace = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$sid = $identity.User.Value
$pipeName = 'OpenPrism.Maintenance.' + $sid.Replace('-', '_')
$security = [IO.Pipes.PipeSecurity]::new()
$security.SetAccessRuleProtection($true, $false)
$security.AddAccessRule([IO.Pipes.PipeAccessRule]::new($identity.User, [IO.Pipes.PipeAccessRights]::ReadWrite, 'Allow'))
$security.AddAccessRule([IO.Pipes.PipeAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'), [IO.Pipes.PipeAccessRights]::FullControl, 'Allow'))
$reportDirectory = Join-Path $workspace 'artifacts/admin-runner'
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
Write-Host "OpenPrism maintenance runner ready. Pipe: $pipeName"
Write-Host 'Only fixed maintenance operations are accepted. Close this window or send Stop to revoke access.'
$running = $true
$round2 = Join-Path $workspace 'artifacts/live-round2'
$captureOwned = $false
while ($running) {
    $pipe = [IO.Pipes.NamedPipeServerStream]::new($pipeName, [IO.Pipes.PipeDirection]::InOut, 1, [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous, 4096, 4096, $security)
    try {
        $pipe.WaitForConnection()
        $reader = [IO.StreamReader]::new($pipe, [Text.UTF8Encoding]::new($false), $false, 4096, $true)
        $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 4096, $true)
        $writer.AutoFlush = $true
        try {
            $pendingRead = $reader.ReadLineAsync()
            if (-not $pendingRead.Wait(10000)) { throw 'Request timeout.' }
            $line = $pendingRead.Result
            if ($null -eq $line -or $line.Length -gt 4096) { throw 'Invalid request size.' }
            $request = $line | ConvertFrom-Json
            $backend = Join-Path $workspace 'artifacts/OpenPrism/backend/WGTun.Service.exe'
            $operation = [string]$request.operation
            $arguments = @()
            switch ($operation) {
 'SteeringCapture' {
  $response = (& (Join-Path $workspace 'scripts/steering-capture.ps1')) | ConvertFrom-Json
  break
 }
                'Ping' { $response = @{ success = $true; operation = $operation; elevated = $true }; break }
                'Versions' {
                    $files = @('C:\Program Files\WGTun\Broker\WGTun.Service.exe','C:\Program Files\WGTun\Broker\WGTun.Core.dll', (Join-Path $workspace 'artifacts/live-round2/fresh/backend/WGTun.Service.exe'), (Join-Path $workspace 'artifacts/live-round2/fresh/backend/WGTun.Core.dll'))
                    $response = @{ success = $true; files = @($files | ForEach-Object { $f = Get-Item -LiteralPath $_; @{path=$f.FullName; version=$f.VersionInfo.FileVersion; sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} }); service = @(Get-CimInstance Win32_Service -Filter "Name='WGTunBroker'" | Select-Object Name,State,PathName,ProcessId) }
                    break
                }
                'BackupRound2' {
                    $backup = Join-Path $round2 'private-backup'
                    if (Test-Path -LiteralPath $backup) { throw 'Backup exists; refusing overwrite.' }
                    New-Item -ItemType Directory -Path $backup | Out-Null
                    Copy-Item -LiteralPath 'C:\Program Files\WGTun\Broker' -Destination (Join-Path $backup 'Broker') -Recurse
                    Copy-Item -LiteralPath (Join-Path $env:LOCALAPPDATA 'WGTun') -Destination (Join-Path $backup 'UserData') -Recurse
                    'complete' | Set-Content -LiteralPath (Join-Path $backup 'complete.txt')
                    $response = @{ success = $true; operation = $operation }; break
                }
                'InstallFresh' {
                    if (-not (Test-Path -LiteralPath (Join-Path $round2 'private-backup/complete.txt'))) { throw 'Complete BackupRound2 before fresh installation.' }
                    if (-not (Test-Path -LiteralPath (Join-Path $round2 'host.lock/owner'))) { throw 'Acquire the host lock before installation.' }
                    $backend = Join-Path $round2 'fresh/backend/WGTun.Service.exe'; $arguments = @('--install-broker')
                }
                'CaptureStart' {
                    # Never stop or replace another capture session. Start fails if one is active.
                    if ($captureOwned) { throw 'Round2 capture already active.' }
                    $capture = Join-Path $round2 ('udp-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '.etl')
                    $output = & pktmon start --capture --comp nics --pkt-size 128 --file-size 64 --file-name $capture 2>&1
                    if ($LASTEXITCODE -ne 0) { throw ($output -join "`n") }
                    $captureOwned = $true
                    $response = @{ success = $true; path = $capture; output = ($output -join "`n") }; break
                }
                'CaptureStop' {
                    if (-not $captureOwned) { throw 'No owned capture to stop.' }
                    $output = & pktmon stop 2>&1
                    if ($LASTEXITCODE -ne 0) { throw ($output -join "`n") }
                    $captureOwned = $false
                    & pktmon etl2txt $capture --out ($capture + '.txt') --hex | Out-Null
                    if ($LASTEXITCODE -ne 0) { throw 'Capture text conversion failed.' }
                    $response = @{ success = $true; path = $capture; text = ($capture + '.txt') }; break
                }
                'Stop' { if ($captureOwned) { throw 'Stop the owned capture first.' }; $running = $false; $response = @{ success = $true; operation = $operation }; break }
                'Snapshot' {
                    $response = @{ success = $true; operation = $operation; routes = @(Get-NetRoute | Select-Object InterfaceIndex, DestinationPrefix, NextHop, RouteMetric); dns = @(Get-DnsClientServerAddress | Select-Object InterfaceIndex, AddressFamily, ServerAddresses); service = @(Get-Service WGTunBroker -ErrorAction SilentlyContinue | Select-Object Name, Status) }
                    break
                }
                'InstallCore' { $arguments = @('--install-broker') }
                'InstallByeDpi' { $arguments = @('--install-broker', 'bootstrap:byedpi') }
                'InstallZapret' { $arguments = @('--install-broker', 'bootstrap:zapret') }
                'RemoveByeDpi' { $arguments = @('--install-broker', 'remove-component:byedpi') }
                'RemoveZapret' { $arguments = @('--install-broker', 'remove-component:zapret') }
                'RemoveServices' { $arguments = @('--remove-broker') }
                default { throw 'Unsupported operation. Arbitrary shell commands are not accepted.' }
            }
            if ($arguments.Count -gt 0) {
                if (-not (Test-Path -LiteralPath $backend)) { throw 'Build artifacts/OpenPrism first.' }
                $start = [Diagnostics.ProcessStartInfo]::new()
                $start.FileName = $backend
                $start.Arguments = $arguments -join ' '
                $start.WorkingDirectory = Split-Path -Parent $backend
                $start.UseShellExecute = $false
                $start.CreateNoWindow = $true
                $process = [Diagnostics.Process]::Start($start)
                try {
                    if (-not $process.WaitForExit(180000)) { throw 'Maintenance timed out; inspect service state before retrying.' }
                    $response = @{ success = ($process.ExitCode -eq 0); operation = $operation; exitCode = $process.ExitCode }
                } finally { $process.Dispose() }
            }
            $response | ConvertTo-Json -Depth 8 -Compress | Set-Content -LiteralPath (Join-Path $reportDirectory 'last-response.json') -Encoding UTF8
            $writer.WriteLine(($response | ConvertTo-Json -Depth 8 -Compress))
        } catch {
            $writer.WriteLine((@{ success = $false; message = $_.Exception.Message } | ConvertTo-Json -Compress))
        } finally { $reader.Dispose(); $writer.Dispose() }
    } finally { $pipe.Dispose() }
}
