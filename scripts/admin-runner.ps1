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
                'Ping' { $response = @{ success = $true; operation = $operation; elevated = $true }; break }
                'Stop' { $running = $false; $response = @{ success = $true; operation = $operation }; break }
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
