//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using NUnit.Framework;
using RetroIO.Link.Lapb;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Unit tests for the CRC-16-CCITT frame check sequence used by HDLC/LAPB.
    /// </summary>
    /// <remarks>
    /// Validates the residue property used by <see cref="Fcs16.IsValid"/>, real compute/validate
    /// round-trips for SABM and UA address/control fields, corruption detection, and equivalence with the
    /// bit-by-bit CRC method currently in HdlcFramer (so that implementation can be routed through this
    /// helper). Note: the <c>0x01 0x3F 0x00 0x64</c> "SABM" bytes used elsewhere in the suite are a mock
    /// frame fed to LAPB after FCS stripping; <c>0x00 0x64</c> is not a real CRC-16 FCS.
    /// </remarks>
    [TestFixture]
    public class Fcs16Tests
    {
        // Reference bit-by-bit CRC-16-CCITT (poly 0x8408) matching HdlcFramer's implementation.
        private static ushort BitMethodFcs(byte[] data)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < data.Length; i++)
            {
                crc ^= data[i];
                for (int b = 0; b < 8; b++)
                {
                    if ((crc & 0x0001) != 0)
                    {
                        crc = (ushort)((crc >> 1) ^ 0x8408);
                    }
                    else
                    {
                        crc = (ushort)(crc >> 1);
                    }
                }
            }

            return (ushort)(crc ^ 0xFFFF);
        }

        // Builds a frame from data plus its real computed FCS.
        private static byte[] FrameWithFcs(byte[] data)
        {
            byte[] frame = new byte[data.Length + 2];
            Array.Copy(data, frame, data.Length);
            Fcs16.WriteFcs(data, frame.AsSpan(data.Length));
            return frame;
        }

        [Test]
        public void RealSabmFrame_ComputeThenValidate_RoundTrips()
        {
            // SABM address+control is 0x01 0x3F; the real CRC-16 FCS is computed, not the 0x00 0x64
            // mock trailer used elsewhere in the test suite (those frames are fed to LAPB post-FCS-strip).
            byte[] frame = FrameWithFcs(new byte[] { 0x01, 0x3F });
            Assert.That(Fcs16.IsValid(frame), Is.True);
        }

        [Test]
        public void RealUaFrame_ComputeThenValidate_RoundTrips()
        {
            // UA address+control is 0x01 0x73.
            byte[] frame = FrameWithFcs(new byte[] { 0x01, 0x73 });
            Assert.That(Fcs16.IsValid(frame), Is.True);
        }

        [Test]
        public void IsValid_CorruptedFrame_ReturnsFalse()
        {
            byte[] frame = FrameWithFcs(new byte[] { 0x01, 0x3F });
            frame[1] ^= 0x01; // flip one bit in the control byte after the FCS was computed
            Assert.That(Fcs16.IsValid(frame), Is.False);
        }

        [Test]
        public void IsValid_TooShort_ReturnsFalse()
        {
            Assert.That(Fcs16.IsValid(new byte[] { 0x01 }), Is.False);
        }

        [Test]
        public void ComputeThenAppend_SatisfiesResidueProperty()
        {
            byte[] data = new byte[] { 0x03, 0x0F, 0x10, 0x20, 0xFB, 0x00 };
            byte[] frame = new byte[data.Length + 2];
            Array.Copy(data, frame, data.Length);
            Fcs16.WriteFcs(data, frame.AsSpan(data.Length));

            Assert.That(Fcs16.IsValid(frame), Is.True);
        }

        [Test]
        public void Compute_MatchesBitByBitMethod_ForVariousInputs()
        {
            byte[][] inputs =
            {
                new byte[] { 0x01, 0x3F },
                new byte[] { 0x01, 0x73 },
                new byte[] { 0x03, 0x00, 0x10, 0x0B, 0x00, 0x64 },
                new byte[] { 0xFF, 0x00, 0xAA, 0x55, 0x7E, 0x7D, 0x20, 0x1F },
                new byte[256]
            };

            for (int i = 0; i < inputs[4].Length; i++)
            {
                inputs[4][i] = (byte)i;
            }

            for (int i = 0; i < inputs.Length; i++)
            {
                ushort table = Fcs16.Compute(inputs[i]);
                ushort bits = BitMethodFcs(inputs[i]);
                Assert.That(table, Is.EqualTo(bits), $"input index {i}");
            }
        }
    }
}
