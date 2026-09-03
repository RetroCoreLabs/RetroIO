using System;
using System.Collections.Generic;
using System.IO;

namespace RetroIO.Protocols.Xmsg.Hdlc
{
    /// <summary>
    /// A minimal pcapng reader that extracts Ethernet/IPv4/TCP payload segments from a
    /// capture file.
    /// </summary>
    /// <remarks>
    /// <para><b>Supported blocks</b></para>
    /// Only the block types needed to walk a Wireshark/tshark capture of the
    /// <c>nd100x --hdlc</c> TCP bridge are handled:
    ///  - Section Header Block (<c>0x0A0D0D0A</c>) - sets the section byte order from the
    ///    <c>0x1A2B3C4D</c> magic.
    ///  - Interface Description Block (<c>0x00000001</c>) - records each interface link type.
    ///  - Enhanced Packet Block (<c>0x00000006</c>) - captured packet data + interface id.
    ///  - legacy Packet Block (<c>0x00000002</c>) - older captured packet data.
    /// Every other block type is skipped by its declared total length. Two link types are
    /// decoded - <c>LINKTYPE_ETHERNET</c> (1) and <c>LINKTYPE_NULL</c> (0, BSD loopback,
    /// which is what Npcap's Windows loopback adapter emits and the actual link type of
    /// the XMSG captures). Packets on other link types, and non-IPv4 / non-TCP packets
    /// (IPv6, mDNS, ARP noise), are ignored.
    /// </remarks>
    public static class PcapngReader
    {
        /// <summary>
        /// pcapng Section Header Block type. The four bytes <c>0A 0D 0D 0A</c> read the
        /// same in either byte order, which is how the reader bootstraps endianness.
        /// </summary>
        private const uint BlockSectionHeader = 0x0A0D0D0A;

        /// <summary>
        /// pcapng Interface Description Block type.
        /// </summary>
        private const uint BlockInterfaceDescription = 0x00000001;

        /// <summary>
        /// pcapng legacy Packet Block type.
        /// </summary>
        private const uint BlockPacket = 0x00000002;

        /// <summary>
        /// pcapng Enhanced Packet Block type.
        /// </summary>
        private const uint BlockEnhancedPacket = 0x00000006;

        /// <summary>
        /// Byte-order magic in a Section Header Block when read as a native-order value.
        /// </summary>
        private const uint ByteOrderMagic = 0x1A2B3C4D;

        /// <summary>
        /// DLT / link type value for BSD loopback (LINKTYPE_NULL): a 4-byte host-order
        /// address-family header precedes the IP packet. This is what Npcap's Windows
        /// loopback adapter produces, and it is the link type of the XMSG captures.
        /// </summary>
        private const ushort LinkTypeNull = 0;

        /// <summary>
        /// DLT / link type value for Ethernet framing.
        /// </summary>
        private const ushort LinkTypeEthernet = 1;

        /// <summary>
        /// BSD address family for IPv4 (AF_INET), carried in the LINKTYPE_NULL header.
        /// </summary>
        private const uint AddressFamilyInet = 2;

        /// <summary>
        /// Ethernet II ethertype for an IPv4 payload.
        /// </summary>
        private const ushort EtherTypeIpv4 = 0x0800;

        /// <summary>
        /// Ethernet II ethertype introducing an 802.1Q VLAN tag.
        /// </summary>
        private const ushort EtherTypeVlan = 0x8100;

        /// <summary>
        /// IPv4 protocol number for TCP.
        /// </summary>
        private const byte IpProtocolTcp = 6;

        /// <summary>
        /// Reads every Ethernet/IPv4/TCP payload segment from a pcapng file.
        /// </summary>
        /// <param name="path">
        /// The path of the pcapng capture file.
        /// </param>
        /// <returns>
        /// The list of non-empty TCP payload segments, in capture order.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="path"/> is null.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// Thrown when the file does not begin with a Section Header Block.
        /// </exception>
        public static IReadOnlyList<TcpSegment> ReadTcpSegments(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            byte[] file = File.ReadAllBytes(path);
            List<TcpSegment> segments = new List<TcpSegment>();

            // Link type per interface index, in declaration order.
            List<ushort> interfaceLinkTypes = new List<ushort>();
            bool littleEndian = true;
            int ordinal = 0;

            int offset = 0;
            while (offset + 8 <= file.Length)
            {
                // The block type is stored in section byte order, but the SHB type is a
                // palindrome so it decodes correctly before endianness is known.
                uint blockType = ReadU32(file, offset, littleEndian);

                if (blockType == BlockSectionHeader)
                {
                    // Determine endianness from the byte-order magic at body offset +8.
                    if (offset + 12 > file.Length)
                    {
                        break;
                    }

                    uint magicLe = ReadU32(file, offset + 8, true);
                    littleEndian = magicLe == ByteOrderMagic;

                    // A new section resets the interface table.
                    interfaceLinkTypes.Clear();
                }

                uint totalLength = ReadU32(file, offset + 4, littleEndian);
                if (totalLength < 12 || offset + (int)totalLength > file.Length)
                {
                    // Corrupt or truncated block length - stop rather than loop forever.
                    break;
                }

                int bodyStart = offset + 8;

                if (blockType == BlockInterfaceDescription)
                {
                    // IDB body: LinkType(2) Reserved(2) SnapLen(4) then options.
                    ushort linkType = ReadU16(file, bodyStart, littleEndian);
                    interfaceLinkTypes.Add(linkType);
                }
                else if (blockType == BlockEnhancedPacket)
                {
                    // EPB body: InterfaceId(4) TsHigh(4) TsLow(4) CapLen(4) OrigLen(4) Data.
                    if (bodyStart + 20 <= file.Length)
                    {
                        uint interfaceId = ReadU32(file, bodyStart, littleEndian);
                        int capturedLength = (int)ReadU32(file, bodyStart + 12, littleEndian);
                        int dataStart = bodyStart + 20;
                        TryDecodePacket(file, dataStart, capturedLength, interfaceId,
                            interfaceLinkTypes, segments, ref ordinal);
                    }
                }
                else if (blockType == BlockPacket)
                {
                    // Legacy PB body: InterfaceId(2) Drops(2) TsHigh(4) TsLow(4) CapLen(4) OrigLen(4) Data.
                    if (bodyStart + 20 <= file.Length)
                    {
                        uint interfaceId = ReadU16(file, bodyStart, littleEndian);
                        int capturedLength = (int)ReadU32(file, bodyStart + 12, littleEndian);
                        int dataStart = bodyStart + 20;
                        TryDecodePacket(file, dataStart, capturedLength, interfaceId,
                            interfaceLinkTypes, segments, ref ordinal);
                    }
                }

                offset += (int)totalLength;
            }

            return segments;
        }

        /// <summary>
        /// Decodes a single captured packet as Ethernet/IPv4/TCP and, when it carries a
        /// TCP payload, appends a segment to <paramref name="segments"/>.
        /// </summary>
        /// <param name="file">
        /// The whole capture file buffer.
        /// </param>
        /// <param name="dataStart">
        /// The offset of the captured packet data within <paramref name="file"/>.
        /// </param>
        /// <param name="capturedLength">
        /// The captured (snapped) length of the packet data in bytes.
        /// </param>
        /// <param name="interfaceId">
        /// The capturing interface index, resolved against
        /// <paramref name="interfaceLinkTypes"/>.
        /// </param>
        /// <param name="interfaceLinkTypes">
        /// The link type of each declared interface, in order.
        /// </param>
        /// <param name="segments">
        /// The list receiving any decoded TCP payload segment.
        /// </param>
        /// <param name="ordinal">
        /// The running capture-order counter, incremented for each decoded segment.
        /// </param>
        private static void TryDecodePacket(byte[] file, int dataStart, int capturedLength,
            uint interfaceId, List<ushort> interfaceLinkTypes, List<TcpSegment> segments,
            ref int ordinal)
        {
            if (capturedLength <= 0 || dataStart + capturedLength > file.Length)
            {
                return;
            }

            if (interfaceId >= (uint)interfaceLinkTypes.Count)
            {
                return;
            }

            ushort linkType = interfaceLinkTypes[(int)interfaceId];
            int end = dataStart + capturedLength;
            int ipStart;

            if (linkType == LinkTypeEthernet)
            {
                // --- Ethernet II header: dst(6) src(6) ethertype(2) ---
                int pos = dataStart + 12;
                if (pos + 2 > end)
                {
                    return;
                }

                ushort etherType = ReadU16BigEndian(file, pos);
                pos += 2;

                // Skip a single 802.1Q VLAN tag (TCI(2) inner-ethertype(2)).
                if (etherType == EtherTypeVlan)
                {
                    if (pos + 4 > end)
                    {
                        return;
                    }

                    etherType = ReadU16BigEndian(file, pos + 2);
                    pos += 4;
                }

                if (etherType != EtherTypeIpv4)
                {
                    return;
                }

                ipStart = pos;
            }
            else if (linkType == LinkTypeNull)
            {
                // --- BSD loopback: 4-byte host-order address family, then the IP packet ---
                if (dataStart + 4 > end)
                {
                    return;
                }

                // The family is written in the capturing host's byte order; accept AF_INET
                // read either way so the reader is endianness-agnostic for the pseudo-header.
                uint familyLe = ReadU32(file, dataStart, true);
                uint familyBe = ReadU32(file, dataStart, false);
                if (familyLe != AddressFamilyInet && familyBe != AddressFamilyInet)
                {
                    return;
                }

                ipStart = dataStart + 4;
            }
            else
            {
                // Unsupported link type - ignore this packet.
                return;
            }

            // --- IPv4 header ---
            if (ipStart + 20 > end)
            {
                return;
            }

            byte versionIhl = file[ipStart];
            int version = versionIhl >> 4;
            int ihl = (versionIhl & 0x0F) * 4;
            if (version != 4 || ihl < 20 || ipStart + ihl > end)
            {
                return;
            }

            int ipTotalLength = ReadU16BigEndian(file, ipStart + 2);
            byte protocol = file[ipStart + 9];
            if (protocol != IpProtocolTcp)
            {
                return;
            }

            uint srcAddr = ReadU32BigEndian(file, ipStart + 12);
            uint dstAddr = ReadU32BigEndian(file, ipStart + 16);

            // Bound the IP payload by the declared total length, clamped to captured data.
            int ipEnd = ipStart + ipTotalLength;
            if (ipEnd > end)
            {
                ipEnd = end;
            }

            // --- TCP header ---
            int tcpStart = ipStart + ihl;
            if (tcpStart + 20 > ipEnd)
            {
                return;
            }

            ushort srcPort = ReadU16BigEndian(file, tcpStart);
            ushort dstPort = ReadU16BigEndian(file, tcpStart + 2);
            uint sequence = ReadU32BigEndian(file, tcpStart + 4);
            int dataOffset = (file[tcpStart + 12] >> 4) * 4;
            if (dataOffset < 20 || tcpStart + dataOffset > ipEnd)
            {
                return;
            }

            int payloadStart = tcpStart + dataOffset;
            int payloadLength = ipEnd - payloadStart;
            if (payloadLength <= 0)
            {
                return;
            }

            byte[] payload = new byte[payloadLength];
            Array.Copy(file, payloadStart, payload, 0, payloadLength);

            StreamKey key = new StreamKey(srcAddr, srcPort, dstAddr, dstPort);
            segments.Add(new TcpSegment(key, sequence, ordinal, payload));
            ordinal++;
        }

        /// <summary>
        /// Reads a 16-bit unsigned integer in the section byte order.
        /// </summary>
        /// <param name="buffer">
        /// The source buffer.
        /// </param>
        /// <param name="offset">
        /// The byte offset to read from.
        /// </param>
        /// <param name="littleEndian">
        /// <c>true</c> to read little-endian, <c>false</c> for big-endian.
        /// </param>
        /// <returns>
        /// The decoded 16-bit value.
        /// </returns>
        private static ushort ReadU16(byte[] buffer, int offset, bool littleEndian)
        {
            if (littleEndian)
            {
                return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
            }

            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        /// <summary>
        /// Reads a 32-bit unsigned integer in the section byte order.
        /// </summary>
        /// <param name="buffer">
        /// The source buffer.
        /// </param>
        /// <param name="offset">
        /// The byte offset to read from.
        /// </param>
        /// <param name="littleEndian">
        /// <c>true</c> to read little-endian, <c>false</c> for big-endian.
        /// </param>
        /// <returns>
        /// The decoded 32-bit value.
        /// </returns>
        private static uint ReadU32(byte[] buffer, int offset, bool littleEndian)
        {
            if (littleEndian)
            {
                return (uint)(buffer[offset]
                    | (buffer[offset + 1] << 8)
                    | (buffer[offset + 2] << 16)
                    | (buffer[offset + 3] << 24));
            }

            return (uint)((buffer[offset] << 24)
                | (buffer[offset + 1] << 16)
                | (buffer[offset + 2] << 8)
                | buffer[offset + 3]);
        }

        /// <summary>
        /// Reads a big-endian 16-bit value (network byte order, used for packet fields).
        /// </summary>
        /// <param name="buffer">
        /// The source buffer.
        /// </param>
        /// <param name="offset">
        /// The byte offset to read from.
        /// </param>
        /// <returns>
        /// The decoded 16-bit value.
        /// </returns>
        private static ushort ReadU16BigEndian(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        /// <summary>
        /// Reads a big-endian 32-bit value (network byte order, used for packet fields).
        /// </summary>
        /// <param name="buffer">
        /// The source buffer.
        /// </param>
        /// <param name="offset">
        /// The byte offset to read from.
        /// </param>
        /// <returns>
        /// The decoded 32-bit value.
        /// </returns>
        private static uint ReadU32BigEndian(byte[] buffer, int offset)
        {
            return ((uint)buffer[offset] << 24)
                | ((uint)buffer[offset + 1] << 16)
                | ((uint)buffer[offset + 2] << 8)
                | buffer[offset + 3];
        }
    }
}
