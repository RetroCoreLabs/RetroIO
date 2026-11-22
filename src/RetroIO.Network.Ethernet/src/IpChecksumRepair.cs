//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Network.Ethernet
{
    /// <summary>
    /// Repairs IPv4 / TCP / UDP checksums on frames captured from a host network card.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THIS EXISTS. A modern network card computes the IPv4 header, TCP and UDP
    /// checksums in hardware on the way out (transmit checksum offload). pcap taps the host
    /// stack ABOVE that hardware, so a frame the host itself is sending is captured BEFORE the
    /// checksum is filled in. Bridge that frame straight into an emulated 1980s card and the
    /// guest sees a checksum error and drops the packet - correctly, because the checksum
    /// really is wrong on the bytes it was handed.
    /// </para>
    /// <para>
    /// MEASURED 2026-08-19 on a Norsk Data ND-100 running COSMOS TCP/IP D02 (legacy RetroCore
    /// code, Emulated.HW/Common/Network/IpChecksumRepair.cs, which this class is ported from).
    /// Frames from the capturing Windows host all carried an IPv4 header checksum of 0000:
    /// from the host "45 00 00 29 32 0C 40 00 80 06 00 00" (TCP, checksum 0000), from the host
    /// "45 00 00 41 37 40 40 00 80 11 00 00" (UDP, checksum 0000), from the router
    /// "45 00 00 34 10 29 40 00 3B 06 64 E5" (checksum 64E5, correct). SINTRAN logged
    /// "TCPP0-TCP Error in checksum" a few times a second. Turning transmit checksum offload
    /// off on the host adapter stopped it, which confirmed the cause - but that is a
    /// per-adapter manual setting a user has to know about. Repairing the frame here makes the
    /// bridge work whatever the host's offload settings are.
    /// </para>
    /// <para>
    /// WHAT IT DELIBERATELY DOES NOT DO. It does not blindly rewrite every checksum. A checksum
    /// is recomputed only when the one present fails to verify, because offload sometimes
    /// leaves a PARTIAL pseudo-header sum rather than zero (so "is it zero" is not a sufficient
    /// test), and because a frame from another machine on the segment already has a correct
    /// checksum and must be passed through untouched. Genuinely corrupt frames therefore get
    /// "repaired" too. That is a deliberate trade: the bridge exists to deliver traffic to a
    /// guest, and a real card on a real wire would not have seen the corruption in the first
    /// place (the wire's own frame check sequence would have thrown the frame away).
    /// </para>
    /// <para>
    /// No allocation: everything works in place on the caller's buffer.
    /// </para>
    /// </remarks>
    public static class IpChecksumRepair
    {
        // ── Constants ──

        /// <summary>
        /// EtherType of an IPv4 datagram.
        /// </summary>
        private const ushort EtherTypeIpv4 = 0x0800;

        /// <summary>
        /// EtherType of an 802.1Q VLAN tag.
        /// </summary>
        private const ushort EtherTypeVlan = 0x8100;

        /// <summary>
        /// IPv4 protocol number of TCP.
        /// </summary>
        private const byte ProtocolTcp = 6;

        /// <summary>
        /// IPv4 protocol number of UDP.
        /// </summary>
        private const byte ProtocolUdp = 17;

        // ── Public methods ──

        /// <summary>
        /// Verifies and, where wrong, recomputes the IPv4 header and TCP/UDP checksums of one
        /// Ethernet frame, in place.
        /// </summary>
        /// <param name="frame">
        /// The whole Ethernet frame starting at the destination MAC. Anything that is not an
        /// IPv4 datagram is left exactly as it is; a fragment gets only its IP header repaired.
        /// </param>
        /// <returns>
        /// Which checksums were rewritten.
        /// </returns>
        public static RepairedChecksums RepairInPlace(Span<byte> frame)
        {
            // Ethernet header: 6 dst + 6 src + 2 type.
            if (frame.Length < 14)
            {
                return RepairedChecksums.None;
            }

            int ipOffset = 14;
            ushort etherType = (ushort)((frame[12] << 8) | frame[13]);

            // One level of 802.1Q tagging: the real EtherType sits 4 bytes further on.
            if (etherType == EtherTypeVlan)
            {
                if (frame.Length < 18)
                {
                    return RepairedChecksums.None;
                }
                etherType = (ushort)((frame[16] << 8) | frame[17]);
                ipOffset = 18;
            }

            if (etherType != EtherTypeIpv4)
            {
                return RepairedChecksums.None;
            }

            if (frame.Length < ipOffset + 20)
            {
                return RepairedChecksums.None;
            }

            Span<byte> ip = frame.Slice(ipOffset);

            // Version must be 4; IHL is in 32-bit words and covers any options.
            if ((ip[0] >> 4) != 4)
            {
                return RepairedChecksums.None;
            }

            int ihl = (ip[0] & 0x0F) * 4;
            if (ihl < 20 || ip.Length < ihl)
            {
                return RepairedChecksums.None;
            }

            // Trust the IP total length over the frame length: Ethernet pads short frames
            // to 60 bytes, and that padding is not part of the datagram.
            int ipTotalLength = (ip[2] << 8) | ip[3];
            if (ipTotalLength < ihl)
            {
                return RepairedChecksums.None;
            }
            if (ipTotalLength > ip.Length)
            {
                // Truncated capture (snaplen) - the payload sum would be wrong, so only the
                // header, which is entirely present, can be repaired.
                ipTotalLength = ihl;
            }

            RepairedChecksums result = RepairedChecksums.None;

            // ---- IPv4 header ------------------------------------------------------
            // The checksum field itself counts as zero while summing, so a correct header
            // sums to 0xFFFF over its whole length.
            if (Ones16(ip.Slice(0, ihl)) != 0xFFFF)
            {
                ip[10] = 0;
                ip[11] = 0;
                ushort hdr = (ushort)~Ones16(ip.Slice(0, ihl));
                ip[10] = (byte)(hdr >> 8);
                ip[11] = (byte)hdr;
                result |= RepairedChecksums.IpHeader;
            }

            // ---- TCP / UDP --------------------------------------------------------
            // Only the first fragment carries the transport header, and a non-final
            // fragment does not carry the whole payload the checksum covers, so anything
            // fragmented is left alone.
            bool moreFragments = (ip[6] & 0x20) != 0;
            int fragmentOffset = ((ip[6] & 0x1F) << 8) | ip[7];
            if (moreFragments || fragmentOffset != 0)
            {
                return result;
            }

            byte protocol = ip[9];
            if (protocol != ProtocolTcp && protocol != ProtocolUdp)
            {
                return result;
            }

            int payloadLength = ipTotalLength - ihl;
            int minimumHeader = protocol == ProtocolTcp ? 20 : 8;
            if (payloadLength < minimumHeader)
            {
                return result;
            }

            Span<byte> payload = ip.Slice(ihl, payloadLength);
            int checksumOffset = protocol == ProtocolTcp ? 16 : 6;

            // A UDP checksum of zero is legal in IPv4 and means "not computed". We cannot
            // tell that apart from an offloaded frame the hardware never finished, so we
            // compute one: a correct checksum is always acceptable to the receiver,
            // whereas leaving an unfinished partial sum is not.
            uint sum = PseudoHeaderSum(ip, protocol, payloadLength);
            sum += Sum16(payload);

            ushort want = Fold(sum);
            // Verify against what is there now, with the field included in the sum.
            if (want != 0xFFFF)
            {
                ushort saved = (ushort)((payload[checksumOffset] << 8) | payload[checksumOffset + 1]);
                payload[checksumOffset] = 0;
                payload[checksumOffset + 1] = 0;

                uint fresh = PseudoHeaderSum(ip, protocol, payloadLength) + Sum16(payload);
                ushort computed = (ushort)~Fold(fresh);

                // RFC 768: a computed UDP checksum of zero is transmitted as all ones,
                // because zero is the "no checksum" marker.
                if (computed == 0 && protocol == ProtocolUdp)
                {
                    computed = 0xFFFF;
                }

                payload[checksumOffset] = (byte)(computed >> 8);
                payload[checksumOffset + 1] = (byte)computed;

                if (computed != saved)
                {
                    result |= protocol == ProtocolTcp ? RepairedChecksums.Tcp : RepairedChecksums.Udp;
                }
            }

            return result;
        }

        // ── Private methods ──

        /// <summary>
        /// TCP/UDP pseudo header: source and destination address, zero, protocol, and the
        /// transport length. Summed separately so the payload span stays contiguous.
        /// </summary>
        private static uint PseudoHeaderSum(ReadOnlySpan<byte> ip, byte protocol, int transportLength)
        {
            uint sum = 0;
            // Source and destination addresses, four 16-bit words at IP offsets 12..19.
            sum += (uint)((ip[12] << 8) | ip[13]);
            sum += (uint)((ip[14] << 8) | ip[15]);
            sum += (uint)((ip[16] << 8) | ip[17]);
            sum += (uint)((ip[18] << 8) | ip[19]);
            sum += protocol;
            sum += (uint)transportLength;
            return sum;
        }

        /// <summary>
        /// Raw 16-bit one's-complement sum, big-endian, odd trailing byte padded.
        /// </summary>
        private static uint Sum16(ReadOnlySpan<byte> data)
        {
            uint sum = 0;
            int i = 0;
            int pairs = data.Length - 1;
            // Straight indexed loop - no LINQ, no enumerator, no bounds surprises.
            while (i < pairs)
            {
                sum += (uint)((data[i] << 8) | data[i + 1]);
                i += 2;
            }
            if (i < data.Length)
            {
                // Odd length: the last byte is the high half of a zero-padded word.
                sum += (uint)(data[i] << 8);
            }
            return sum;
        }

        /// <summary>
        /// Folds the 32-bit accumulator down to 16 bits.
        /// </summary>
        private static ushort Fold(uint sum)
        {
            while ((sum >> 16) != 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }
            return (ushort)sum;
        }

        /// <summary>
        /// Folded one's-complement sum of a span; 0xFFFF means "verifies".
        /// </summary>
        private static ushort Ones16(ReadOnlySpan<byte> data)
        {
            return Fold(Sum16(data));
        }
    }

    /// <summary>
    /// What <see cref="IpChecksumRepair.RepairInPlace"/> actually changed.
    /// </summary>
    [Flags]
    public enum RepairedChecksums
    {
        /// <summary>
        /// Nothing was rewritten.
        /// </summary>
        None = 0,

        /// <summary>
        /// The IPv4 header checksum was rewritten.
        /// </summary>
        IpHeader = 1,

        /// <summary>
        /// The TCP checksum was rewritten.
        /// </summary>
        Tcp = 2,

        /// <summary>
        /// The UDP checksum was rewritten.
        /// </summary>
        Udp = 4,
    }
}
