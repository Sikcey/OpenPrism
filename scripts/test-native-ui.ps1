[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$Page = 'connection'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NativeUiTest {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public delegate bool EnumWindowProc(IntPtr h, IntPtr p);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder value, int capacity);
    public static IntPtr FindOwnedWindow(int pid) {
        IntPtr found=IntPtr.Zero; long largest=0;
        EnumWindows((h,p)=>{ uint owner; GetWindowThreadProcessId(h,out owner); RECT r;
            if(owner==(uint)pid && GetWindowRect(h,out r)) {
                var title=new System.Text.StringBuilder(256);GetWindowText(h,title,256);
                long area=(long)(r.Right-r.Left)*(r.Bottom-r.Top);
                if(title.ToString().Contains("OpenPrism") && r.Right-r.Left>=400 && r.Bottom-r.Top>=300 && area>largest){found=h;largest=area;}
            } return true;
        },IntPtr.Zero); return found;
    }
}
'@
[NativeUiTest]::SetProcessDPIAware() | Out-Null
$exe = (Resolve-Path -LiteralPath $Executable).Path
if ([IO.Path]::GetExtension($exe) -ine '.exe') { throw 'Executable must be an .exe file.' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$out = (Resolve-Path -LiteralPath $OutputDirectory).Path
$report = [ordered]@{ executable = $exe; page = $Page; started = $false; error = $null; exitCode = $null; cleanup = 'not started'; notes = @('CPU is process CPU time divided by wall time; 100% equals one logical CPU.', 'Loaded modules are a snapshot, not proof of absence of managed code. Inspect executable imports and startup architecture separately.', 'PrintWindow may produce blank or incomplete output for some renderers; inspect the PNG.') }
$p = $null
try {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = "--smoke-no-network --page=$Page"
    $psi.WorkingDirectory = Split-Path -Parent $exe
    $psi.UseShellExecute = $false
    $psi.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $foregroundBefore = [NativeUiTest]::GetForegroundWindow()
    $p = [Diagnostics.Process]::Start($psi)
    if ($null -eq $p) { throw 'Process.Start returned no process.' }
    $report.started = $true; $report.pid = $p.Id; $report.startTime = $p.StartTime.ToString('o')
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $hwnd = [IntPtr]::Zero
    while ([DateTime]::UtcNow -lt $deadline) {
        $p.Refresh()
        if ($p.HasExited) { $report.exitCode = $p.ExitCode; throw "Process exited before opening a window (exit code $($p.ExitCode))." }
        $hwnd = [NativeUiTest]::FindOwnedWindow($p.Id)
        if ($hwnd -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($hwnd -eq [IntPtr]::Zero) { throw 'No main window appeared within 15 seconds; hidden windows may not expose a main window handle.' }
    $rect = New-Object NativeUiTest+RECT
    if (-not [NativeUiTest]::GetWindowRect($hwnd, [ref]$rect)) { throw 'GetWindowRect failed.' }
    $width = $rect.Right - $rect.Left; $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0 -or $width -gt 8192 -or $height -gt 8192) { throw "Invalid window dimensions: ${width}x${height}." }
    $report.initialBounds = @{ x=$rect.Left; y=$rect.Top; width=$width; height=$height }
    $report.foregroundPreserved = ([NativeUiTest]::GetForegroundWindow() -eq $foregroundBefore)
    $bitmap = New-Object Drawing.Bitmap $width, $height
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $dc = $graphics.GetHdc()
        try { $report.printWindowSucceeded = [NativeUiTest]::PrintWindow($hwnd, $dc, 2) }
        finally { $graphics.ReleaseHdc($dc) }
        $bitmap.Save((Join-Path $out "$Page.png"), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
    $report.observedMinimum = 'Not asserted: programmatic SetWindowPos can bypass interactive sizing constraints.'
    $p.Refresh(); $cpuStart = $p.TotalProcessorTime.TotalSeconds; $watch = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 3
    $p.Refresh(); $watch.Stop()
    if ($p.HasExited) { $report.exitCode = $p.ExitCode; throw 'Process exited during idle sampling.' }
    $report.idleCpuPercentOneCore = [math]::Round(100 * ($p.TotalProcessorTime.TotalSeconds - $cpuStart) / $watch.Elapsed.TotalSeconds, 2)
    $report.sampleSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 2)
    $report.workingSetBytes = $p.WorkingSet64
    try { $report.modules = @($p.Modules | ForEach-Object { $_.FileName }) } catch { $report.modulesError = $_.Exception.Message }
} catch { $report.error = $_.Exception.Message }
finally {
    if ($null -ne $p) {
        try {
            $p.Refresh()
            if (-not $p.HasExited) { $p.Kill(); if (-not $p.WaitForExit(5000)) { throw 'Owned process did not exit within five seconds.' }; $report.cleanup = 'terminated owned process' }
            else { $report.cleanup = 'owned process already exited' }
            $report.exitCode = $p.ExitCode
        } catch { $report.cleanup = "failed: $($_.Exception.Message)" }
        $p.Dispose()
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out "$Page.json") -Encoding UTF8
}
if ($report.error -or $report.cleanup -like 'failed:*') { Write-Error "Native UI verification failed: $($report.error); cleanup: $($report.cleanup)" }
Write-Host "Artifacts: $out"
