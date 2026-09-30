using System.Buffers.Binary;
using System.Net;

namespace Aurora86.Core.Devices.Network.Nat;

/// <summary>
/// The wire formats the NAT reads and writes: Ethernet, ARP, IPv4, and the
/// three transports on top. Everything is big-endian, as the network is.
///
/// Frames are built into exact-size arrays rather than into a shared buffer
/// because each one is queued for the card to collect later, and a queue of
/// frames that all pointed at the same buffer would be a queue of the last
/// one written.
/// </summary>
internal static class Packet
{
    public const int EthernetHeader = 14;
    public const int IpHeader = 20;
    public const int UdpHeader = 8;
    public const int TcpHeader = 20;
    public const int Mtu = 1500;

    public const ushort TypeIp = 0x0800;
    public const ushort TypeArp = 0x0806;

    public const byte ProtocolIcmp = 1;
    public const byte ProtocolTcp = 6;
    public const byte ProtocolUdp = 17;

    public static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);

    public static uint ReadU32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);

    public static void WriteU16(Span<byte> data, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(data[offset..], value);

    public static void WriteU32(Span<byte> data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(data[offset..], value);

    /// <summary>An IPv4 address as the 32-bit number it is on the wire.</summary>
    public static uint ToUInt32(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    public static IPAddress ToAddress(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }

    /// <summary>
    /// The one's-complement sum every Internet checksum is built on. Given a
    /// starting value so that the pseudo-header of TCP and UDP can be folded
    /// in before the segment itself.
    /// </summary>
    public static ushort Checksum(ReadOnlySpan<byte> data, uint initial = 0)
    {
        uint sum = initial;
        int i = 0;
        for (; i + 1 < data.Length; i += 2) sum += (uint)((data[i] << 8) | data[i + 1]);
        if (i < data.Length)
        {
            sum += (uint)(data[i] << 8);
        }
        while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    /// <summary>The pseudo-header sum TCP and UDP checksums start from.</summary>
    public static uint PseudoHeader(uint source, uint destination, byte protocol, int length)
    {
        return (source >> 16) + (source & 0xFFFF)
             + (destination >> 16) + (destination & 0xFFFF)
             + protocol + (uint)length;
    }

    /// <summary>
    /// Builds a complete frame carrying an IPv4 packet: Ethernet and IP
    /// headers filled in and checksummed, the payload copied after them.
    /// </summary>
    public static byte[] BuildIp(ReadOnlySpan<byte> destinationMac, ReadOnlySpan<byte> sourceMac,
                                 uint source, uint destination, byte protocol,
                                 ReadOnlySpan<byte> payload, ushort identification = 0)
    {
        byte[] frame = new byte[EthernetHeader + IpHeader + payload.Length];
        Span<byte> span = frame;
        destinationMac.CopyTo(span);
        sourceMac.CopyTo(span[6..]);
        WriteU16(span, 12, TypeIp);

        Span<byte> ip = span[EthernetHeader..];
        ip[0] = 0x45;
        ip[1] = 0;
        WriteU16(ip, 2, (ushort)(IpHeader + payload.Length));
        WriteU16(ip, 4, identification);
        WriteU16(ip, 6, 0x4000); // don't fragment
        ip[8] = 64;
        ip[9] = protocol;
        WriteU32(ip, 12, source);
        WriteU32(ip, 16, destination);
        WriteU16(ip, 10, Checksum(ip[..IpHeader]));

        payload.CopyTo(ip[IpHeader..]);
        return frame;
    }

    /// <summary>A UDP datagram, checksummed, ready to go inside an IP packet.</summary>
    public static byte[] BuildUdp(uint source, uint destination, ushort sourcePort, ushort destinationPort,
                                  ReadOnlySpan<byte> payload)
    {
        byte[] datagram = new byte[UdpHeader + payload.Length];
        Span<byte> span = datagram;
        WriteU16(span, 0, sourcePort);
        WriteU16(span, 2, destinationPort);
        WriteU16(span, 4, (ushort)datagram.Length);
        payload.CopyTo(span[UdpHeader..]);
        ushort sum = Checksum(span, PseudoHeader(source, destination, ProtocolUdp, datagram.Length));
        // A zero checksum means "none" in UDP, so a sum that comes out to
        // zero is sent as all ones, which means the same thing.
        WriteU16(span, 6, sum == 0 ? (ushort)0xFFFF : sum);
        return datagram;
    }

    /// <summary>A TCP segment with no options, checksummed.</summary>
    public static byte[] BuildTcp(uint source, uint destination, ushort sourcePort, ushort destinationPort,
                                  uint sequence, uint acknowledgement, byte flags, ushort window,
                                  ReadOnlySpan<byte> payload, ushort? mss = null)
    {
        int options = mss.HasValue ? 4 : 0;
        int header = TcpHeader + options;
        byte[] segment = new byte[header + payload.Length];
        Span<byte> span = segment;
        WriteU16(span, 0, sourcePort);
        WriteU16(span, 2, destinationPort);
        WriteU32(span, 4, sequence);
        WriteU32(span, 8, acknowledgement);
        span[12] = (byte)((header / 4) << 4);
        span[13] = flags;
        WriteU16(span, 14, window);
        if (mss.HasValue)
        {
            span[20] = 2;
            span[21] = 4;
            WriteU16(span, 22, mss.Value);
        }
        payload.CopyTo(span[header..]);
        WriteU16(span, 16, Checksum(span, PseudoHeader(source, destination, ProtocolTcp, segment.Length)));
        return segment;
    }

    /// <summary>An ARP frame, request or reply, for an IPv4 address.</summary>
    public static byte[] BuildArp(ReadOnlySpan<byte> destinationMac, ReadOnlySpan<byte> sourceMac,
                                  ushort operation, ReadOnlySpan<byte> senderMac, uint senderIp,
                                  ReadOnlySpan<byte> targetMac, uint targetIp)
    {
        byte[] frame = new byte[EthernetHeader + 28];
        Span<byte> span = frame;
        destinationMac.CopyTo(span);
        sourceMac.CopyTo(span[6..]);
        WriteU16(span, 12, TypeArp);
        Span<byte> arp = span[EthernetHeader..];
        WriteU16(arp, 0, 1);      // Ethernet
        WriteU16(arp, 2, TypeIp); // IPv4
        arp[4] = 6;
        arp[5] = 4;
        WriteU16(arp, 6, operation);
        senderMac.CopyTo(arp[8..]);
        WriteU32(arp, 14, senderIp);
        targetMac.CopyTo(arp[18..]);
        WriteU32(arp, 24, targetIp);
        return frame;
    }
}

/// <summary>TCP flag bits, in the order they sit in the header.</summary>
internal static class TcpFlags
{
    public const byte Fin = 0x01;
    public const byte Syn = 0x02;
    public const byte Rst = 0x04;
    public const byte Psh = 0x08;
    public const byte Ack = 0x10;
}
