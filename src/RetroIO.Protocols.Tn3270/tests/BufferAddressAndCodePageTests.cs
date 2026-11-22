//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;

using Xunit;

namespace RetroIO.Protocols.Tn3270.Tests
{
    public class BufferAddressAndCodePageTests
    {
        [Theory]
        [InlineData(0, 0x40, 0x40)]
        [InlineData(80, 0xC1, 0x50)]   // row 2 column 1 on an 80-column screen
        [InlineData(160, 0xC2, 0x60)]  // 160 = 2*64 + 32
        [InlineData(1919, 0x5D, 0x7F)] // last cell of 24 x 80: 29*64 + 63
        public void TwelveBitAddressesUseTheSixBitTable(int address, byte first, byte second)
        {
            Span<byte> b = stackalloc byte[2];
            BufferAddress.Encode(address, 1920, b);

            Assert.Equal(first, b[0]);
            Assert.Equal(second, b[1]);
            Assert.True(BufferAddress.TryDecode(b[0], b[1], out int back));
            Assert.Equal(address, back);
        }

        [Fact]
        public void FourteenBitAddressesAreUsedAbove4096Cells()
        {
            Span<byte> b = stackalloc byte[2];
            BufferAddress.Encode(5000, 62 * 160, b);

            Assert.Equal(0x13, b[0]);
            Assert.Equal(0x88, b[1]);
            Assert.True(BufferAddress.TryDecode(b[0], b[1], out int back));
            Assert.Equal(5000, back);
        }

        [Fact]
        public void TheReservedFlagBitsAreRejected()
        {
            Assert.False(BufferAddress.TryDecode(0x80, 0x40, out _));
        }

        [Fact]
        public void EveryTwelveBitAddressRoundTrips()
        {
            Span<byte> b = stackalloc byte[2];
            for (int a = 0; a < 4096; a++)
            {
                BufferAddress.Encode(a, 4096, b);
                Assert.True(BufferAddress.TryDecode(b[0], b[1], out int back));
                Assert.Equal(a, back);
            }
        }

        [Fact]
        public void CodePage037MatchesDotNetsIbm037ForEveryTextByte()
        {
            // A second, independent source: .NET's own IBM037 table.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding ibm037 = Encoding.GetEncoding(37);
            byte[] one = new byte[1];
            for (int b = 0x41; b <= 0xFE; b++)
            {
                one[0] = (byte)b;
                char expected = ibm037.GetString(one)[0];
                Assert.True(expected == CodePage037.ToUnicode((byte)b), "byte X'" + b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + "'");
            }

            Assert.Equal(' ', CodePage037.ToUnicode(0x40));
        }

        [Fact]
        public void TypedCharactersRoundTripThroughCodePage037()
        {
            string s = "Hello, World! 0123 $#@ ¢¬|";
            for (int i = 0; i < s.Length; i++)
            {
                Assert.True(CodePage037.TryFromUnicode(s[i], out byte e));
                Assert.Equal(s[i], CodePage037.ToUnicode(e));
            }

            Assert.True(CodePage037.TryFromUnicode(' ', out byte space));
            Assert.Equal(0x40, space);
            Assert.False(CodePage037.TryFromUnicode('€', out _));
        }

        [Fact]
        public void PfKeyAidsFollowTable3To4()
        {
            Assert.Equal(0xF1, Aid.Pf(1));
            Assert.Equal(0x7A, Aid.Pf(10));
            Assert.Equal(0x7C, Aid.Pf(12));
            Assert.Equal(0xC1, Aid.Pf(13));
            Assert.Equal(0xC9, Aid.Pf(21));
            Assert.Equal(0x4A, Aid.Pf(22));
            Assert.Equal(0x4C, Aid.Pf(24));
        }
    }
}
