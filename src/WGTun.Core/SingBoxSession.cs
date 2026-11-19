using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
namespace WGTun.Core;

internal sealed class SingBoxJob : IDisposable
{
    private IntPtr handle;
    public SingBoxJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new Win32Exception();
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) { Dispose(); throw new Win32Exception(); }
    }
    public Process Start(string executable, string[] args, string directory, string? outputPath = null)
    {
        // CREATE_SUSPENDED closes the Process.Start/AssignProcessToJobObject orphan race.
        using var output = outputPath == null ? null : new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        if (output != null) {
            var outputHandle = output.SafeFileHandle.DangerousGetHandle();
            if (!SetHandleInformation(outputHandle, 1, 1)) throw new Win32Exception();
            startup.Flags = 0x100; startup.Output = outputHandle; startup.Error = outputHandle;
        }
        var command = new StringBuilder(Quote(executable) + " " + string.Join(" ", args.Select(Quote)));
        if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, output != null, 0x08000004, IntPtr.Zero, directory, ref startup, out var info)) throw new Win32Exception();
        try {
            if (output != null) SetHandleInformation(output.SafeFileHandle.DangerousGetHandle(), 1, 0);
            if (!AssignProcessToJobObject(handle, info.Process)) throw new Win32Exception();
            var process = Process.GetProcessById((int)info.ProcessId);
            _ = process.Handle; // Retain a queryable handle before a short-lived child exits.
            if (ResumeThread(info.Thread) == uint.MaxValue) { process.Dispose(); throw new Win32Exception(); }
            return process;
        } catch { TerminateProcess(info.Process, 1); throw; }
        finally { CloseHandle(info.Thread); CloseHandle(info.Process); }
    }
    private static string Quote(string value) => "\"" + System.Text.RegularExpressions.Regex.Replace(value, "(\\\\*)\"", "$1$1\\\"").TrimEnd('\\') + new string('\\', value.Reverse().TakeWhile(c => c == '\\').Count() * 2) + "\"";
    public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long PerProcess, PerJob; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int Size; public string? Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags; public ushort ShowWindow, ReservedSize; public IntPtr ReservedData, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int type, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
