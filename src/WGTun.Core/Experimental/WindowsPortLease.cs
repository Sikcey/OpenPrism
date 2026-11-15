using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace WGTun.Core.Experimental;

// Experimental isolation primitive. Not enabled in a production connection mode.
// The reservation and every consuming socket must live in the same process.
public sealed class WindowsPortLease : IDisposable
{
    private const int Acquire = unchecked((int)0x98000064);
    private const int Associate = unchecked((int)0x98000066);
    private readonly Socket reservation;
    private readonly byte[] token;
    private readonly object gate = new();
    private bool disposed;
    public AddressFamily Family { get; }
    public ProtocolType Protocol { get; }
    public int StartPort { get; }
    public int Count { get; }
    public bool Contains(int port) => port >= StartPort && port < StartPort + Count;

    public WindowsPortLease(AddressFamily family, ProtocolType protocol, ushort count = 64)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (family is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            || protocol is not (ProtocolType.Tcp or ProtocolType.Udp) || count is < 4 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(count));
        Family = family; Protocol = protocol;
        reservation = NewSocket();
        try {
            byte[] request = new byte[4], reply = new byte[16];
            // StartPort=0 is a wildcard; NumberOfPorts is host byte order.
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), count);
            int size = reservation.IOControl(Acquire, request, reply);
            if (size != 16) throw new IOException("Unexpected Windows port reservation structure.");
            StartPort = BinaryPrimitives.ReadUInt16BigEndian(reply);
            Count = BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(2));
            token = reply[8..16];
            if (StartPort == 0 || Count != count || StartPort + Count > 65536 || token.All(b => b == 0))
                throw new IOException("Invalid Windows port reservation.");
        } catch { reservation.Dispose(); throw; }
    }
    private Socket NewSocket() => new(Family, Protocol == ProtocolType.Tcp ? SocketType.Stream : SocketType.Dgram, Protocol);
    public Socket CreateBoundSocket(IPAddress address)
    {
        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (address.AddressFamily != Family) throw new ArgumentException("Address family mismatch.");
            var socket = NewSocket();
            try {
                socket.ExclusiveAddressUse = true;
                socket.IOControl(Associate, token, null);
                socket.Bind(new IPEndPoint(address, 0));
                if (!Contains(((IPEndPoint)socket.LocalEndPoint!).Port)) throw new IOException("Socket escaped reserved port pool.");
                return socket;
            } catch { socket.Dispose(); throw; }
        }
    }
    public void Dispose()
    {
        lock (gate) { if (!disposed) { disposed = true; reservation.Dispose(); } }
    }
}
