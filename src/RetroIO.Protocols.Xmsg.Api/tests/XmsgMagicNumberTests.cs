//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Api;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Api.Tests
{
    /// <summary>
    /// Pins the magic-number bit layout carved from the XMSG L03 kernel.
    /// </summary>
    /// <remarks>
    /// Evidence and full disassembly: DOC/XMSG-MAGIC-NUMBER-LAYOUT-CARVED-2026-07-26.md.
    /// The three facts these tests protect are the ones the kernel code shows:
    ///  - ZCRMG builds the low word with SHA ZIN 7 then ORA of the port block's RNMAG word.
    ///  - ZRAND masks the random part with SAT 127 plus RAND ST DA, so it is seven bits.
    ///  - MFM2P recovers the port with SHA ZIN SHR 7 and returns the high word as the system.
    /// </remarks>
    public sealed class XmsgMagicNumberTests
    {
        /// <summary>
        /// The three fields occupy the widths and positions the kernel packs them into.
        /// </summary>
        [Fact]
        public void Fields_SplitAtBitSevenAndSixteen()
        {
            // system 103, port 5, random 0x2A -> 0x0067_02AA
            XmsgMagicNumber magic = XmsgMagicNumber.Create(103, 5, 0x2A);

            Assert.Equal(0x006702AAu, magic.Value);
            Assert.Equal(103, magic.SystemNumber);
            Assert.Equal(5, magic.PortNumber);
            Assert.Equal(0x2A, magic.Random);
        }

        /// <summary>
        /// The low word is exactly the kernel's port-shifted-left-seven or random.
        /// </summary>
        [Fact]
        public void PortWord_IsPortShiftedSevenOrRandom()
        {
            XmsgMagicNumber magic = XmsgMagicNumber.Create(100, 0x1FF, 0x7F);

            Assert.Equal((ushort)((0x1FF << 7) | 0x7F), magic.PortWord);
            Assert.Equal(magic.LowWord, magic.PortWord);
            Assert.Equal(magic.HighWord, magic.SystemNumber);
        }

        /// <summary>
        /// Decomposing an arbitrary raw value agrees with recomposing it.
        /// </summary>
        [Fact]
        public void Decompose_And_Recompose_RoundTrip()
        {
            XmsgMagicNumber original = new XmsgMagicNumber(0x1234ABCD);

            XmsgMagicNumber rebuilt = XmsgMagicNumber.Create(
                original.SystemNumber,
                original.PortNumber,
                original.Random);

            Assert.Equal(original, rebuilt);
        }

        /// <summary>
        /// The register-pair form agrees with the field form.
        /// </summary>
        [Fact]
        public void FromRegisterPair_MatchesFieldComposition()
        {
            XmsgMagicNumber fromFields = XmsgMagicNumber.Create(102, 9, 3);
            XmsgMagicNumber fromRegisters = XmsgMagicNumber.FromRegisterPair(102, (ushort)((9 << 7) | 3));

            Assert.Equal(fromFields, fromRegisters);
        }

        /// <summary>
        /// The port field is nine bits and the random field seven; wider values are rejected
        /// rather than silently truncated into the neighbouring field.
        /// </summary>
        [Fact]
        public void Create_RejectsOversizedFields()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => XmsgMagicNumber.Create(100, 0x200, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => XmsgMagicNumber.Create(100, 1, 0x80));
        }

        /// <summary>
        /// A zero magic number identifies no port at all.
        /// </summary>
        [Fact]
        public void None_IsZeroAndReportsNoPort()
        {
            Assert.True(XmsgMagicNumber.None.IsNone);
            Assert.Equal(0u, XmsgMagicNumber.None.Value);
            Assert.Equal(0, XmsgMagicNumber.None.PortNumber);
        }
    }
}
