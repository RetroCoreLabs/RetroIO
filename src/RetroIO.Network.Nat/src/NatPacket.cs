//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers.Binary;

namespace RetroIO.Network.Nat
{
    /// <summary>
    /// Byte-level helpers for the built-in NAT: header offsets, the Internet checksum and the
    /// builders for the Ethernet, IPv4, UDP and TCP headers the NAT sends to the guest.
    /// </summary>
    /// <remarks>
    /// Network byte order is big-endian, so every field goes through
    /// <see cref="BinaryPrimitives"/>. IPv4 addresses are carried as <see cref="uint"/> in host
    /// order (10.0.2.2 is 0x0A000202). The checksum is the 16-bit one's complement of the one's
    /// complement sum of 16-bit words (RFC 791 section 3.1; computing it, RFC 1071). UDP and TCP
    /// add the pseudo header of source, destination, protocol and length (RFC 768, RFC 9293
    /// section 3.1).
    /// </remarks>
    public static class NatPacket
    {
        // ── Constants ──

        /// <summary>
        /// Length of an Ethernet II header: two addresses and the type.
        /// </summary>
        public const int EthernetHeaderBytes = 14;

        /// <summary>
        /// Length of an IPv4 header without options.
        /// </summary>
        public const int Ipv4HeaderBytes = 20;

        /// <summary>
        /// Length of a UDP header.
        /// </summary>
        public const int UdpHeaderBytes = 8;

        /// <summary>
        /// Length of a TCP header without options.
        /// </summary>
        public const int TcpHeaderBytes = 20;

        /// <summary>
        /// Ethernet type of IPv4.
        /// </summary>
        public const ushort EtherTypeIpv4 = 0x0800;

        /// <summary>
        /// Ethernet type of ARP.
        /// </summary>
        public const ushort EtherTypeArp = 0x0806;

        /// <summary>
        /// IP protocol number of ICMP.
        /// </summary>
        public const byte ProtocolIcmp = 1;

        /// <summary>
        /// IP protocol number of TCP.
        /// </summary>
        public const byte ProtocolTcp = 6;

        /// <summary>
        /// IP protocol number of UDP.
        /// </summary>
        public const byte ProtocolUdp = 17;

        /// <summary>
        /// Time to live the NAT puts on what it sends (RFC 1700 recommends 64).
        /// </summary>
        public const byte DefaultTtl = 64;

        /// <summary>
        /// The shortest Ethernet frame without FCS; shorter frames are padded with zeros.
        /// </summary>
        public const int MinFrameBytes = 60;

        /// <summary>
        /// TCP flag FIN.
        /// </summary>
        public const byte TcpFin = 0x01;

        /// <summary>
        /// TCP flag SYN.
        /// </summary>
        public const byte TcpSyn = 0x02;

        /// <summary>
        /// TCP flag RST.
        /// </summary>
        public const byte TcpRst = 0x04;

        /// <summary>
        /// TCP flag PSH.
        /// </summary>
        public const byte TcpPsh = 0x08;

        /// <summary>
        /// TCP flag ACK.
        /// </summary>
        public const byte TcpAck = 0x10;

        // ── Public methods ──

        /// <summary>
        /// Reads a 32-bit IPv4 address (host order) from four bytes in network order.
        /// </summary>
        /// <param name="bytes">
        /// The four bytes.
        /// </param>
        /// <returns>
        /// The address, 10.0.2.2 as 0x0A000202.
        /// </returns>
        public static uint ReadAddress(ReadOnlySpan<byte> bytes)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }

        /// <summary>
        /// Writes a 32-bit IPv4 address (host order) as four bytes in network order.
        /// </summary>
        /// <param name="bytes">
        /// Where the four bytes go.
        /// </param>
        /// <param name="address">
        /// The address.
        /// </param>
        public static void WriteAddress(Span<byte> bytes, uint address)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, address);
        }

        /// <summary>
        /// Adds 16-bit big-endian words of <paramref name="data"/> to a running one's complement sum.
        /// </summary>
        /// <remarks>
        /// An odd last byte counts as the high byte of a word whose low byte is zero (RFC 1071
        /// section 4.1). The sum is kept in 32 bits and folded only at the end by
        /// <see cref="Fold"/>.
        /// </remarks>
        /// <param name="data">
        /// The bytes.
        /// </param>
        /// <param name="sum">
        /// The sum so far.
        /// </param>
        /// <returns>
        /// The new sum, not folded.
        /// </returns>
        public static uint Sum(ReadOnlySpan<byte> data, uint sum)
        {
            int i = 0;
            int even = data.Length & ~1;
            while (i < even)
            {
                sum += (uint)((data[i] << 8) | data[i + 1]);
                i += 2;
            }
            if (i < data.Length)
            {
                sum += (uint)(data[i] << 8);
            }
            return sum;
        }

        /// <summary>
        /// Folds a 32-bit one's complement sum to 16 bits and takes its complement: the checksum.
        /// </summary>
        /// <param name="sum">
        /// The sum from <see cref="Sum"/>.
        /// </param>
        /// <returns>
        /// The checksum to write into the header.
        /// </returns>
        public static ushort Fold(uint sum)
        {
            while ((sum >> 16) != 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }
            return (ushort)~sum;
        }

        /// <summary>
        /// The one's complement sum of the UDP/TCP pseudo header: source, destination, zero,
        /// protocol and the UDP/TCP length (RFC 768; RFC 9293 section 3.1).
        /// </summary>
        /// <param name="source">
        /// Source address.
        /// </param>
        /// <param name="destination">
        /// Destination address.
        /// </param>
        /// <param name="protocol">
        /// IP protocol number.
        /// </param>
        /// <param name="length">
        /// Length of the UDP or TCP header plus data.
        /// </param>
        /// <returns>
        /// The sum, not folded.
        /// </returns>
        public static uint PseudoHeaderSum(uint source, uint destination, byte protocol, int length)
        {
            return (source >> 16) + (source & 0xFFFF) + (destination >> 16) + (destination & 0xFFFF) + protocol + (uint)length;
        }

        /// <summary>
        /// Checks an IPv4 header checksum: the sum over the whole header, checksum included,
        /// folds to all ones (RFC 1071 section 1).
        /// </summary>
        /// <param name="header">
        /// The IPv4 header, options included.
        /// </param>
        /// <returns>
        /// <c>true</c> when the checksum is right.
        /// </returns>
        public static bool Ipv4HeaderChecksumOk(ReadOnlySpan<byte> header)
        {
            return Fold(Sum(header, 0)) == 0;
        }

        /// <summary>
        /// Writes an Ethernet II header.
        /// </summary>
        /// <param name="frame">
        /// The frame; the header goes at index 0.
        /// </param>
        /// <param name="destination">
        /// Destination MAC, six bytes.
        /// </param>
        /// <param name="source">
        /// Source MAC, six bytes.
        /// </param>
        /// <param name="etherType">
        /// The Ethernet type.
        /// </param>
        public static void WriteEthernet(Span<byte> frame, ReadOnlySpan<byte> destination, ReadOnlySpan<byte> source, ushort etherType)
        {
            destination.Slice(0, 6).CopyTo(frame);
            source.Slice(0, 6).CopyTo(frame.Slice(6));
            BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(12), etherType);
        }

        /// <summary>
        /// Writes a 20-byte IPv4 header with its checksum (no options, Don't Fragment clear).
        /// </summary>
        /// <param name="header">
        /// Where the header goes.
        /// </param>
        /// <param name="totalLength">
        /// The IP total length: header plus payload.
        /// </param>
        /// <param name="identification">
        /// The identification field.
        /// </param>
        /// <param name="protocol">
        /// The IP protocol number.
        /// </param>
        /// <param name="source">
        /// Source address.
        /// </param>
        /// <param name="destination">
        /// Destination address.
        /// </param>
        public static void WriteIpv4(Span<byte> header, int totalLength, ushort identification, byte protocol, uint source, uint destination)
        {
            header[0] = 0x45;
            header[1] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(header.Slice(2), (ushort)totalLength);
            BinaryPrimitives.WriteUInt16BigEndian(header.Slice(4), identification);
            BinaryPrimitives.WriteUInt16BigEndian(header.Slice(6), 0);
            header[8] = DefaultTtl;
            header[9] = protocol;
            BinaryPrimitives.WriteUInt16BigEndian(header.Slice(10), 0);
            WriteAddress(header.Slice(12), source);
            WriteAddress(header.Slice(16), destination);
            BinaryPrimitives.WriteUInt16BigEndian(header.Slice(10), Fold(Sum(header.Slice(0, Ipv4HeaderBytes), 0)));
        }

        /// <summary>
        /// Writes a UDP header and its checksum over the pseudo header, header and data.
        /// </summary>
        /// <param name="segment">
        /// The UDP header followed by its data, exactly the UDP length long.
        /// </param>
        /// <param name="source">
        /// Source address (for the pseudo header).
        /// </param>
        /// <param name="destination">
        /// Destination address (for the pseudo header).
        /// </param>
        /// <param name="sourcePort">
        /// Source port.
        /// </param>
        /// <param name="destinationPort">
        /// Destination port.
        /// </param>
        public static void WriteUdp(Span<byte> segment, uint source, uint destination, ushort sourcePort, ushort destinationPort)
        {
            BinaryPrimitives.WriteUInt16BigEndian(segment, sourcePort);
            BinaryPrimitives.WriteUInt16BigEndian(segment.Slice(2), destinationPort);
            BinaryPrimitives.WriteUInt16BigEndian(segment.Slice(4), (ushort)segment.Length);
            BinaryPrimitives.WriteUInt16BigEndian(segment.Slice(6), 0);
            ushort checksum = Fold(Sum(segment, PseudoHeaderSum(source, destination, ProtocolUdp, segment.Length)));
            // RFC 768: a computed checksum of zero is sent as all ones (zero means "no checksum").
            BinaryPrimitives.WriteUInt16BigEndian(segment.Slice(6), checksum == 0 ? (ushort)0xFFFF : checksum);
        }

        /// <summary>
        /// Writes a TCP header (with an optional MSS option) and its checksum over the pseudo
        /// header, header and data. The data must already be in place after the header.
        /// </summary>
        /// <param name="segment">
        /// The TCP header followed by its data, exactly the segment long.
        /// </param>
        /// <param name="headerBytes">
        /// 20, or 24 with the MSS option.
        /// </param>
        /// <param name="source">
        /// Source address.
        /// </param>
        /// <param name="destination">
        /// Destination address.
        /// </param>
        /// <param name="sourcePort">
        /// Source port.
        /// </param>
        /// <param name="destinationPort">
        /// Destination port.
        /// </param>
        /// <param name="sequence">
        /// Sequence number.
        /// </param>
        /// <param name="acknowledgment">
        /// Acknowledgment number.
        /// </param>
        /// <param name="flags">
        /// The flag bits (FIN 01h ... ACK 10h).
        /// </param>
        /// <param name="window">
        /// The window the sender offers.
        /// </param>
        /// <param name="mss">
        /// The MSS to offer when <paramref name="headerBytes"/> is 24.
        /// </param>
        public static void WriteTcp(Span<byte> segment, int headerBytes, uint source, uint destination, ushort sourcePort, ushort destinationPort,
            uint sequence, uint acknowledgment, byte flags, ushort window, ushort mss)
        {
            BinaryPrimitives.WriteUInt16BigEndian(segment, sourcePort);
            BinaryPrimitives.WriteUInt16BigEndian(segment.Slice(2), destinationPort);
            BinaryPrimitives.WriteUInt32BigEndian(segment.Slice(4), sequence);
            BinaryPrimitives.WriteUInt32BigEndian(segment.Slice(8), acknowledgment);
            segment[12] = (byte)((headerBytes / 4) << 4);
            segment[13] = flags;
            BinaryPrimitives.WriteUInt16BigEndian(segment.Slice(14), window);
            BinaryPrimitives.WriteUInt16BigEndian(segment.Slice(16), 0);
            BinaryPrimitives.WriteUInt16BigEndian(segment.Slice(18), 0);
            if (headerBytes == 24)
            {
                // RFC 9293 section 3.2: kind 2, length 4, the MSS.
                segment[20] = 2;
                segment[21] = 4;
                BinaryPrimitives.WriteUInt16BigEndian(segment.Slice(22), mss);
            }
            BinaryPrimitives.WriteUInt16BigEndian(segment.Slice(16), Fold(Sum(segment, PseudoHeaderSum(source, destination, ProtocolTcp, segment.Length))));
        }

        /// <summary>
        /// Gives an address in dotted form, for status lines.
        /// </summary>
        /// <param name="address">
        /// The address.
        /// </param>
        /// <returns>
        /// For example "10.0.2.15".
        /// </returns>
        public static string Dotted(uint address)
        {
            return (address >> 24) + "." + ((address >> 16) & 0xFF) + "." + ((address >> 8) & 0xFF) + "." + (address & 0xFF);
        }

        /// <summary>
        /// True when <paramref name="a"/> comes before <paramref name="b"/> in 32-bit sequence
        /// space (RFC 9293 section 3.4: comparisons are modulo 2^32).
        /// </summary>
        /// <param name="a">
        /// First sequence number.
        /// </param>
        /// <param name="b">
        /// Second sequence number.
        /// </param>
        /// <returns>
        /// <c>true</c> when <paramref name="a"/> comes before <paramref name="b"/> in sequence space.
        /// </returns>
        public static bool SeqBefore(uint a, uint b)
        {
            return (int)(a - b) < 0;
        }
    }
}
