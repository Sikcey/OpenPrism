using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WGTun.Core.Experimental;

internal static class FilterProbe
{
    private const string DllHash = "16ABD6A029E65557C6A309BEA7B13BF81FFF4E193567582E1CDDBF6719F323E0";
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool Compile(string filter, int layer, IntPtr output, uint size, out IntPtr error, out uint position);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool Evaluate(string filter, byte[] packet, uint length, byte[] address);

    internal static object Run(string dll)
    {
        using (var input = File.OpenRead(dll))
            if (Convert.ToHexString(SHA256.HashData(input)) != DllHash) throw new IOException("WinDivert DLL hash mismatch.");
        IntPtr library = NativeLibrary.Load(Path.GetFullPath(dll));
        try {
            var compile = Marshal.GetDelegateForFunctionPointer<Compile>(NativeLibrary.GetExport(library, "WinDivertHelperCompileFilter"));
            var eval = Marshal.GetDelegateForFunctionPointer<Evaluate>(NativeLibrary.GetExport(library, "WinDivertHelperEvalFilter"));
            var reports = new List<object>();
            foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 }) {
                foreach (var protocol in new[] { ProtocolType.Tcp, ProtocolType.Udp }) {
                    using var lease = new WindowsPortLease(family, protocol, 32);
                    string filter = ZapretFilterBuilder.Build(8, [lease], 443);
                    if (!compile(filter, 0, IntPtr.Zero, 0, out IntPtr error, out uint position))
                        throw new IOException($"Filter rejected at {position}: {Marshal.PtrToStringAnsi(error)}");
                    int tested = 0;
                    foreach (bool outbound in new[] { true, false }) {
                        foreach (string scenario in new[] { "selected", "ordinary", "wrong-interface", "impostor", "loopback", "wrong-destination" }) {
                            int localPort = scenario == "ordinary" ? (lease.StartPort == 65504 ? 40000 : lease.StartPort + lease.Count) : lease.StartPort;
                            int remotePort = scenario == "wrong-destination" ? 444 : 443;
                            byte[] packet = Packet(family, protocol, outbound ? localPort : remotePort, outbound ? remotePort : localPort);
                            byte[] address = new byte[80];
                            uint flags = (outbound ? 1U << 17 : 0) | (family == AddressFamily.InterNetworkV6 ? 1U << 20 : 0)
                                | (scenario == "loopback" ? 1U << 18 : 0) | (scenario == "impostor" ? 1U << 19 : 0);
                            BinaryPrimitives.WriteUInt32LittleEndian(address.AsSpan(8), flags);
                            BinaryPrimitives.WriteUInt32LittleEndian(address.AsSpan(16), scenario == "wrong-interface" ? 1008U : 8U);
                            bool matched = eval(filter, packet, (uint)packet.Length, address);
                            if (matched != (scenario == "selected")) throw new IOException($"Isolation failed: {family} {protocol} {outbound} {scenario} matched={matched}");
                            tested++;
                        }
                    }
                    reports.Add(new { family = family.ToString(), protocol = protocol.ToString(), tested, filter, success = true });
                }
            }
            return new { success = true, driverOpened = false, evaluatedBy = "Pinned upstream WinDivertHelperEvalFilter", tests = reports };
        } finally { NativeLibrary.Free(library); }
    }
    private static byte[] Packet(AddressFamily family, ProtocolType protocol, int src, int dst)
    {
        int ip = family == AddressFamily.InterNetwork ? 20 : 40, transport = protocol == ProtocolType.Tcp ? 20 : 8;
        byte[] packet = new byte[ip + transport];
        if (ip == 20) {
            packet[0] = 0x45; BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
            packet[8] = 64; packet[9] = (byte)protocol; packet[12] = 192; packet[13] = 0; packet[14] = 2; packet[15] = 1;
            packet[16] = 1; packet[17] = 1; packet[18] = 1; packet[19] = 1;
        } else {
            packet[0] = 0x60; BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)transport);
            packet[6] = (byte)protocol; packet[7] = 64; packet[8] = 0x20; packet[9] = 1; packet[10] = 0x0d; packet[11] = 0xb8; packet[23] = 1;
            packet[24] = 0x26; packet[25] = 6; packet[26] = 0x47; packet[27] = 0; packet[39] = 1;
        }
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(ip), (ushort)src);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(ip + 2), (ushort)dst);
        if (protocol == ProtocolType.Tcp) { packet[ip + 12] = 0x50; packet[ip + 13] = 2; }
        else BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(ip + 4), (ushort)transport);
        return packet;
    }
}
