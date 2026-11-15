[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateSet('Ping','Snapshot','InstallCore','InstallByeDpi','InstallZapret','RemoveByeDpi','RemoveZapret','RemoveServices','Stop')][string]$Operation)
$ErrorActionPreference = 'Stop'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', ('OpenPrism.Maintenance.' + $sid.Replace('-', '_')), [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
try {
    $pipe.Connect(1500)
    $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 4096, $true)
    $reader = [IO.StreamReader]::new($pipe, [Text.UTF8Encoding]::new($false), $false, 4096, $true)
    try {
        $writer.AutoFlush = $true
        $writer.WriteLine((@{ operation = $Operation } | ConvertTo-Json -Compress))
        $pendingRead = $reader.ReadLineAsync()
        if (-not $pendingRead.Wait(190000)) { throw 'Maintenance response timed out.' }
        $pendingRead.Result
    } finally { $reader.Dispose(); $writer.Dispose() }
} finally { $pipe.Dispose() }
