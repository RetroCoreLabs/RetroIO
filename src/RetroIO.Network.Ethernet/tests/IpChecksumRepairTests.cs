//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using NUnit.Framework;

namespace RetroIO.Network.Ethernet.Tests
{
    /// <summary>
    /// Checksum repair for frames captured from a host network card that does transmit
    /// checksum offload. Ported with <see cref="IpChecksumRepair"/> from the legacy tests
    /// (Emulated.Tests.Chips/IpChecksumRepairTests.cs).
    /// </summary>
    /// <remarks>
    /// The real case these were written for, measured 2026-08-19 on a Norsk Data ND-100
    /// running COSMOS TCP/IP D02: every frame captured from the Windows host carried an IPv4
    /// header checksum of 0000 because the capture taps above the hardware that computes it,
    /// and SINTRAN logged "TCPP0-TCP Error in checksum" several times a second. Frames from
    /// other machines on the segment verified correctly and must not be touched.
    /// </remarks>
    [TestFixture]
    public class IpChecksumRepairTests
    {
        [Test]
        public void RepairsAnUnfilledIpHeaderChecksum()
        {
            byte[] frame = BuildFrame(6, 4, false);
            Assert.That(IpHeaderVerifies(frame, 14), Is.False, "test frame should start invalid");

            RepairedChecksums what = IpChecksumRepair.RepairInPlace(frame);

            Assert.That((what & RepairedChecksums.IpHeader) != 0, Is.True);
            Assert.That(IpHeaderVerifies(frame, 14), Is.True);
        }

        [Test]
        public void RepairsTcpAndUdpChecksums()
        {
            byte[] tcp = BuildFrame(6, 10, false);
            RepairedChecksums t = IpChecksumRepair.RepairInPlace(tcp);
            Assert.That((t & RepairedChecksums.Tcp) != 0, Is.True);
            Assert.That(TransportVerifies(tcp, 14), Is.True);

            byte[] udp = BuildFrame(17, 10, false);
            RepairedChecksums u = IpChecksumRepair.RepairInPlace(udp);
            Assert.That((u & RepairedChecksums.Udp) != 0, Is.True);
            Assert.That(TransportVerifies(udp, 14), Is.True);
        }

        [Test]
        public void LeavesAnAlreadyCorrectFrameByteForByteUntouched()
        {
            // A frame from another machine on the segment arrives complete and must not
            // be modified - this is the case that must never regress.
            byte[] frame = BuildFrame(6, 8, false);
            IpChecksumRepair.RepairInPlace(frame);          // first pass fixes it
            byte[] before = (byte[])frame.Clone();

            RepairedChecksums second = IpChecksumRepair.RepairInPlace(frame);

            Assert.That(second, Is.EqualTo(RepairedChecksums.None));
            Assert.That(frame, Is.EqualTo(before));
        }

        [Test]
        public void HandlesAPartialPseudoHeaderSumNotJustZero()
        {
            // Windows offload often leaves the partial pseudo-header sum in the field
            // rather than zero, so "checksum == 0" is not a sufficient test.
            byte[] frame = BuildFrame(6, 6, false);
            int tp = 14 + 20;
            frame[tp + 16] = 0x12;   // a plausible-looking but wrong value
            frame[tp + 17] = 0x34;

            RepairedChecksums what = IpChecksumRepair.RepairInPlace(frame);

            Assert.That((what & RepairedChecksums.Tcp) != 0, Is.True);
            Assert.That(TransportVerifies(frame, 14), Is.True);
        }

        [Test]
        public void HandlesAnOddLengthPayload()
        {
            byte[] frame = BuildFrame(17, 7, false);
            IpChecksumRepair.RepairInPlace(frame);
            Assert.That(TransportVerifies(frame, 14), Is.True);
        }

        [Test]
        public void HandlesVlanTaggedFrames()
        {
            byte[] frame = BuildFrame(6, 4, true);
            RepairedChecksums what = IpChecksumRepair.RepairInPlace(frame);

            Assert.That((what & RepairedChecksums.IpHeader) != 0, Is.True);
            Assert.That(IpHeaderVerifies(frame, 18), Is.True);
            Assert.That(TransportVerifies(frame, 18), Is.True);
        }

        [Test]
        public void IgnoresNonIpv4AndShortFrames()
        {
            byte[] arp = new byte[60];
            arp[12] = 0x08; arp[13] = 0x06;               // ARP
            byte[] before = (byte[])arp.Clone();
            Assert.That(IpChecksumRepair.RepairInPlace(arp), Is.EqualTo(RepairedChecksums.None));
            Assert.That(arp, Is.EqualTo(before));

            byte[] runt = new byte[8];
            Assert.That(IpChecksumRepair.RepairInPlace(runt), Is.EqualTo(RepairedChecksums.None));
        }

        [Test]
        public void LeavesFragmentsAlone()
        {
            // A non-first fragment has no transport header, and a non-last fragment does
            // not carry everything the checksum covers. Only the IP header is repairable.
            byte[] frame = BuildFrame(6, 8, false);
            frame[14 + 6] = 0x20;      // MF set, offset 0
            frame[14 + 7] = 0x00;

            RepairedChecksums what = IpChecksumRepair.RepairInPlace(frame);

            Assert.That((what & RepairedChecksums.IpHeader) != 0, Is.True);
            Assert.That((what & RepairedChecksums.Tcp) != 0, Is.False);
            Assert.That(IpHeaderVerifies(frame, 14), Is.True);
        }

        [Test]
        public void IgnoresEthernetPaddingBeyondTheIpTotalLength()
        {
            // Ethernet pads to 60 bytes; that padding is not part of the datagram and must
            // not be folded into the transport checksum.
            byte[] small = BuildFrame(6, 0, false);
            byte[] padded = new byte[60];
            Array.Copy(small, padded, small.Length);
            for (int i = small.Length; i < padded.Length; i++)
            {
                padded[i] = 0xAA;   // junk padding
            }

            IpChecksumRepair.RepairInPlace(padded);

            Assert.That(IpHeaderVerifies(padded, 14), Is.True);
            Assert.That(TransportVerifies(padded, 14), Is.True);
        }

        [Test]
        public void ReproducesTheMeasuredNdFrame()
        {
            // The exact bytes captured from the Windows host on 2026-08-19, which the ND
            // rejected: IPv4 TCP, header checksum 0000. 54 bytes on the wire (the capture
            // showed 55 with one trailing byte).
            byte[] frame = new byte[]
            {
                0x6E,0x63,0xF8,0x3F,0x1D,0xD3, 0x04,0x42,0x1A,0xEC,0xD0,0xF9, 0x08,0x00,
                0x45,0x00,0x00,0x28,0x32,0x0C,0x40,0x00,0x80,0x06,0x00,0x00,
                0xC0,0xA8,0x01,0xB4, 0xA0,0x4F,0x68,0x0A,
                0x5E,0xCF,0x01,0xBB,0xBD,0xFE,0xBE,0x11,0xD5,0xAF,0x07,0xAC,
                0x50,0x10,0x00,0xFF,0x00,0x00,0x00,0x00,
            };

            Assert.That(IpHeaderVerifies(frame, 14), Is.False, "as captured, the header is unfilled");

            RepairedChecksums what = IpChecksumRepair.RepairInPlace(frame);

            Assert.That((what & RepairedChecksums.IpHeader) != 0, Is.True);
            Assert.That(IpHeaderVerifies(frame, 14), Is.True);
            Assert.That(TransportVerifies(frame, 14), Is.True);
        }

        // --- helpers ---------------------------------------------------------

        /// <summary>
        /// Builds an Ethernet + IPv4 + TCP/UDP frame with deliberately zeroed checksums,
        /// exactly the shape transmit offload leaves behind.
        /// </summary>
        internal static byte[] BuildFrame(byte protocol, int payloadBytes, bool withVlan)
        {
            int ethLen = withVlan ? 18 : 14;
            int transportHeader = protocol == 6 ? 20 : 8;
            int ipTotal = 20 + transportHeader + payloadBytes;
            byte[] f = new byte[ethLen + ipTotal];

            // Ethernet: dst, src, type
            for (int i = 0; i < 6; i++) f[i] = (byte)(0x08 + i);      // dst
            for (int i = 0; i < 6; i++) f[6 + i] = (byte)(0x40 + i);  // src
            if (withVlan)
            {
                f[12] = 0x81; f[13] = 0x00;   // 802.1Q
                f[14] = 0x00; f[15] = 0x64;   // VID 100
                f[16] = 0x08; f[17] = 0x00;   // IPv4
            }
            else
            {
                f[12] = 0x08; f[13] = 0x00;   // IPv4
            }

            int ip = ethLen;
            f[ip + 0] = 0x45;                              // v4, IHL 5
            f[ip + 1] = 0x00;
            f[ip + 2] = (byte)(ipTotal >> 8);
            f[ip + 3] = (byte)ipTotal;
            f[ip + 4] = 0x32; f[ip + 5] = 0x0C;            // id
            f[ip + 6] = 0x40; f[ip + 7] = 0x00;            // DF, offset 0
            f[ip + 8] = 0x80;                              // TTL
            f[ip + 9] = protocol;
            f[ip + 10] = 0x00; f[ip + 11] = 0x00;          // header checksum: UNFILLED
            f[ip + 12] = 192; f[ip + 13] = 168; f[ip + 14] = 1; f[ip + 15] = 180;  // src
            f[ip + 16] = 192; f[ip + 17] = 168; f[ip + 18] = 1; f[ip + 19] = 40;   // dst

            int tp = ip + 20;
            f[tp + 0] = 0x1F; f[tp + 1] = 0x90;            // src port 8080
            f[tp + 2] = 0x00; f[tp + 3] = 0x17;            // dst port 23
            if (protocol == 6)
            {
                f[tp + 12] = 0x50;                         // data offset 5
                f[tp + 13] = 0x10;                         // ACK
                f[tp + 16] = 0x00; f[tp + 17] = 0x00;      // TCP checksum: UNFILLED
            }
            else
            {
                int udpLen = 8 + payloadBytes;
                f[tp + 4] = (byte)(udpLen >> 8);
                f[tp + 5] = (byte)udpLen;
                f[tp + 6] = 0x00; f[tp + 7] = 0x00;        // UDP checksum: UNFILLED
            }

            for (int i = 0; i < payloadBytes; i++)
            {
                f[tp + transportHeader + i] = (byte)(i + 1);
            }
            return f;
        }

        /// <summary>
        /// True when the IPv4 header of the frame verifies.
        /// </summary>
        internal static bool IpHeaderVerifies(byte[] frame, int ipOffset)
        {
            int ihl = (frame[ipOffset] & 0x0F) * 4;
            return Ones16(new ReadOnlySpan<byte>(frame, ipOffset, ihl)) == 0xFFFF;
        }

        /// <summary>
        /// True when the TCP or UDP checksum of the frame verifies.
        /// </summary>
        internal static bool TransportVerifies(byte[] frame, int ipOffset)
        {
            int ihl = (frame[ipOffset] & 0x0F) * 4;
            int ipTotal = (frame[ipOffset + 2] << 8) | frame[ipOffset + 3];
            byte protocol = frame[ipOffset + 9];
            int tp = ipOffset + ihl;
            int len = ipTotal - ihl;

            uint sum = 0;
            sum += (uint)((frame[ipOffset + 12] << 8) | frame[ipOffset + 13]);
            sum += (uint)((frame[ipOffset + 14] << 8) | frame[ipOffset + 15]);
            sum += (uint)((frame[ipOffset + 16] << 8) | frame[ipOffset + 17]);
            sum += (uint)((frame[ipOffset + 18] << 8) | frame[ipOffset + 19]);
            sum += protocol;
            sum += (uint)len;

            int i = 0;
            while (i < len - 1)
            {
                sum += (uint)((frame[tp + i] << 8) | frame[tp + i + 1]);
                i += 2;
            }
            if (i < len) sum += (uint)(frame[tp + i] << 8);
            while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
            return (ushort)sum == 0xFFFF;
        }

        /// <summary>
        /// Folded one's-complement sum; 0xFFFF means the range verifies.
        /// </summary>
        private static ushort Ones16(ReadOnlySpan<byte> data)
        {
            uint sum = 0;
            int i = 0;
            while (i < data.Length - 1)
            {
                sum += (uint)((data[i] << 8) | data[i + 1]);
                i += 2;
            }
            if (i < data.Length) sum += (uint)(data[i] << 8);
            while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
            return (ushort)sum;
        }
    }
}
