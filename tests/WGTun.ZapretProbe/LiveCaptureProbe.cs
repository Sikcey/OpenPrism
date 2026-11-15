using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WGTun.Core.Experimental;

// Read-only packet observation. NO_INSTALL prevents this probe from installing,
// replacing, stopping or deleting any existing third-party WinDivert driver.
internal sealed class LiveCaptureProbe : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    private delegate IntPtr Open(string filter, int layer, short priority, ulong flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool Receive(IntPtr handle, [Out] byte[] packet, uint size, out uint length, [Out] byte[] address);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool Shutdown(IntPtr handle, int how);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool Close(IntPtr handle);
    private readonly IntPtr library;
    private readonly Receive receive;
    private readonly Shutdown shutdown;
    private readonly Close close;
    private IntPtr broad = new(-1), narrow = new(-1);
    private readonly Task[] readers;
    private readonly int selectedTcp, ordinaryTcp, selectedUdp, ordinaryUdp;
    private readonly int[,] counts = new int[2, 4];
    private volatile bool stopping;
    internal sealed record Result(bool Success, int[] ObservedAll, int[] MatchedOwnerFilter, string Order, bool DriverInstalled);

    public LiveCaptureProbe(string dll, uint index, WindowsPortLease tcp, WindowsPortLease udp, int st, int ot, int su, int ou)
    {
        using var input = File.OpenRead(dll);
        if (Convert.ToHexString(SHA256.HashData(input)) != "16ABD6A029E65557C6A309BEA7B13BF81FFF4E193567582E1CDDBF6719F323E0") throw new IOException("WinDivert DLL hash mismatch.");
        library = NativeLibrary.Load(Path.GetFullPath(dll));
        selectedTcp = st; ordinaryTcp = ot; selectedUdp = su; ordinaryUdp = ou;
        receive = Marshal.GetDelegateForFunctionPointer<Receive>(NativeLibrary.GetExport(library, "WinDivertRecv"));
        shutdown = Marshal.GetDelegateForFunctionPointer<Shutdown>(NativeLibrary.GetExport(library, "WinDivertShutdown"));
        close = Marshal.GetDelegateForFunctionPointer<Close>(NativeLibrary.GetExport(library, "WinDivertClose"));
        var open = Marshal.GetDelegateForFunctionPointer<Open>(NativeLibrary.GetExport(library, "WinDivertOpen"));
        try {
            string all = $"!loopback and ifIdx == {index} and ip and (ip.SrcAddr == 1.1.1.1 or ip.DstAddr == 1.1.1.1) and ((tcp and (tcp.SrcPort == {st} or tcp.DstPort == {st} or tcp.SrcPort == {ot} or tcp.DstPort == {ot})) or (udp and (udp.SrcPort == {su} or udp.DstPort == {su} or udp.SrcPort == {ou} or udp.DstPort == {ou})))";
            broad = open(all, 0, 0, 0x15); // SNIFF | RECV_ONLY | NO_INSTALL
            Check(broad);
            narrow = open(ZapretFilterBuilder.Build(index, [tcp, udp], 443, 53), 0, -1000, 0x15);
            Check(narrow);
            readers = [Task.Run(() => Read(broad, 0)), Task.Run(() => Read(narrow, 1))];
        } catch { Dispose(); throw; }
    }
    private static void Check(IntPtr handle)
    {
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Read-only WinDivert capture unavailable. No driver installation or service changes were attempted.");
    }
    private void Read(IntPtr handle, int channel)
    {
        byte[] packet = new byte[65535], address = new byte[80];
        while (receive(handle, packet, (uint)packet.Length, out uint size, address)) {
            if (size < 24 || (packet[0] >> 4) != 4) continue;
            int offset = (packet[0] & 15) * 4;
            if (offset < 20 || size < offset + 4) continue;
            bool outbound = (BinaryPrimitives.ReadUInt32LittleEndian(address.AsSpan(8)) & (1U << 17)) != 0;
            int localPort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + (outbound ? 0 : 2)));
            int bucket = packet[9] switch {
                6 when localPort == selectedTcp => 0, 6 when localPort == ordinaryTcp => 1,
                17 when localPort == selectedUdp => 2, 17 when localPort == ordinaryUdp => 3, _ => -1
            };
            if (bucket >= 0) Interlocked.Increment(ref counts[channel, bucket]);
        }
        if (!stopping) throw new IOException("WinDivert receive failed: " + Marshal.GetLastPInvokeError());
    }
    public async Task<Result> FinishAsync()
    {
        await Task.Delay(250);
        stopping = true;
        shutdown(broad, 1); shutdown(narrow, 1);
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(4));
        int[] all = Enumerable.Range(0, 4).Select(i => counts[0, i]).ToArray();
        int[] matched = Enumerable.Range(0, 4).Select(i => counts[1, i]).ToArray();
        return new(all.All(n => n > 0) && matched[0] > 0 && matched[2] > 0 && matched[1] == 0 && matched[3] == 0,
            all, matched, "selected TCP, ordinary TCP, selected UDP, ordinary UDP", false);
    }
    public void Dispose()
    {
        stopping = true;
        foreach (var handle in new[] { broad, narrow }) {
            if (handle != IntPtr.Zero && handle != new IntPtr(-1)) { shutdown(handle, 1); close(handle); }
        }
        broad = narrow = new(-1);
        // Keep delegate code loaded until blocked receive calls have unwound.
        if (readers == null || readers.All(t => t.IsCompleted)) NativeLibrary.Free(library);
    }
}
