//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers.Binary;

namespace RetroIO.Network.Router
{
    /// <summary>
    /// IPv4 addresses and header fields (RFC 791).
    /// </summary>
    /// <remarks>
    /// An address is held as a 32-bit number in host order: 10.42.0.1 is 0x0A2A0001.
    /// <para>
    /// Header layout used here (RFC 791 section 3.1):
    ///  - byte 0: version (top 4 bits, 4) and header length in 32-bit words (bottom 4 bits, at least 5).
    ///  - bytes 2-3: total length, header and data, in bytes.
    ///  - byte 8: time to live.
    ///  - bytes 10-11: header checksum, the ones' complement of the ones' complement sum of the
    ///    header's 16-bit words.
    ///  - bytes 12-15: source address; bytes 16-19: destination address.
    /// </para>
    /// </remarks>
    public static class Ipv4
    {
        /// <summary>
        /// The shortest IPv4 header, in bytes.
        /// </summary>
        public const int MinHeader = 20;

        /// <summary>
        /// The limited broadcast address 255.255.255.255 (RFC 919, RFC 1812 section 4.2.3.1).
        /// </summary>
        public const uint LimitedBroadcast = 0xFFFFFFFF;

        /// <summary>
        /// Reads a dotted address such as <c>10.42.0.1</c>.
        /// </summary>
        /// <param name="text">
        /// Four numbers from 0 to 255 joined by dots.
        /// </param>
        /// <returns>
        /// The address.
        /// </returns>
        /// <exception cref="FormatException">
        /// Thrown when <paramref name="text"/> is not a dotted IPv4 address.
        /// </exception>
        public static uint Parse(string text)
        {
            if (text == null)
            {
                throw new FormatException("no address");
            }

            uint address = 0;
            int part = 0;
            int value = -1;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || text[i] == '.')
                {
                    if (value < 0 || part == 4)
                    {
                        throw new FormatException("not an IPv4 address: " + text);
                    }

                    address = (address << 8) | (uint)value;
                    part++;
                    value = -1;
                    continue;
                }

                char c = text[i];
                if (c < '0' || c > '9')
                {
                    throw new FormatException("not an IPv4 address: " + text);
                }

                value = (value < 0 ? 0 : value * 10) + (c - '0');
                if (value > 255)
                {
                    throw new FormatException("not an IPv4 address: " + text);
                }
            }

            if (part != 4)
            {
                throw new FormatException("not an IPv4 address: " + text);
            }

            return address;
        }

        /// <summary>
        /// Writes an address as dotted text.
        /// </summary>
        /// <param name="address">
        /// The address.
        /// </param>
        /// <returns>
        /// The text, for example <c>10.42.0.1</c>.
        /// </returns>
        public static string Format(uint address)
        {
            return (address >> 24) + "." + ((address >> 16) & 0xFF) + "." + ((address >> 8) & 0xFF) + "." + (address & 0xFF);
        }

        /// <summary>
        /// Gets the source address of a packet.
        /// </summary>
        /// <param name="packet">
        /// The packet, at least <see cref="MinHeader"/> bytes.
        /// </param>
        /// <returns>
        /// The source address.
        /// </returns>
        public static uint Source(ReadOnlySpan<byte> packet)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(12, 4));
        }

        /// <summary>
        /// Gets the destination address of a packet.
        /// </summary>
        /// <param name="packet">
        /// The packet, at least <see cref="MinHeader"/> bytes.
        /// </param>
        /// <returns>
        /// The destination address.
        /// </returns>
        public static uint Destination(ReadOnlySpan<byte> packet)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(16, 4));
        }

        /// <summary>
        /// Computes the header checksum (RFC 791 section 3.1) over a header whose checksum field holds
        /// whatever it holds.
        /// </summary>
        /// <param name="header">
        /// The header, an even number of bytes.
        /// </param>
        /// <returns>
        /// The ones' complement sum of the header's 16-bit words. A header with a correct checksum
        /// sums to FFFFh.
        /// </returns>
        public static ushort Sum(ReadOnlySpan<byte> header)
        {
            uint sum = 0;
            for (int i = 0; i + 1 < header.Length; i += 2)
            {
                sum += (uint)((header[i] << 8) | header[i + 1]);
            }

            while ((sum >> 16) != 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }

            return (ushort)sum;
        }

        /// <summary>
        /// Checks the version, lengths and checksum of a packet's header.
        /// </summary>
        /// <param name="packet">
        /// The packet as received.
        /// </param>
        /// <param name="totalLength">
        /// The packet's own length from its header, when the header is good.
        /// </param>
        /// <returns>
        /// <c>true</c> when the header is good and the packet holds at least its total length.
        /// </returns>
        public static bool TryCheckHeader(ReadOnlySpan<byte> packet, out int totalLength)
        {
            totalLength = 0;
            if (packet.Length < MinHeader || (packet[0] >> 4) != 4)
            {
                return false;
            }

            int headerLength = (packet[0] & 0x0F) * 4;
            int total = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
            if (headerLength < MinHeader || total < headerLength || total > packet.Length)
            {
                return false;
            }

            if (Sum(packet.Slice(0, headerLength)) != 0xFFFF)
            {
                return false;
            }

            totalLength = total;
            return true;
        }

        /// <summary>
        /// Lowers the time to live of a packet by one and corrects its header checksum.
        /// </summary>
        /// <remarks>
        /// The checksum is changed the way RFC 1624 section 3 gives, eqn. 3: HC' = ~(~HC + ~m + m'),
        /// where m is the 16-bit word holding the TTL before the change and m' after it.
        /// </remarks>
        /// <param name="packet">
        /// The packet; its TTL must be at least 1.
        /// </param>
        public static void DecrementTtl(Span<byte> packet)
        {
            ushort oldWord = (ushort)((packet[8] << 8) | packet[9]);
            packet[8]--;
            ushort newWord = (ushort)((packet[8] << 8) | packet[9]);
            ushort checksum = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(10, 2));
            uint sum = (uint)(ushort)~checksum + (ushort)~oldWord + newWord;
            while ((sum >> 16) != 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }

            BinaryPrimitives.WriteUInt16BigEndian(packet.Slice(10, 2), (ushort)~sum);
        }
    }
}
