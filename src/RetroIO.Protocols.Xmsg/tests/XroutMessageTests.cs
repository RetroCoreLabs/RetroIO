//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Xmsg;
using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Verifies XROUT standard-message (letter) TLV parsing and building
    /// (XMSG-API.md section 4).
    /// </summary>
    public sealed class XroutMessageTests
    {
        [Fact]
        public void ParsesHeaderAndStringParameter_FromWorkedExample()
        {
            // Based on the COSMOS guide XMPBLOC worked example: serial 123, status 0,
            // remainder length 32, param#1 = string ("HI"), param#2 length 14.
            // A third integer parameter (length 10) is added so the three blocks sum
            // to the declared remainder length of 32 (4 + 16 + 12), keeping the
            // fixture internally consistent. Every block starts on an even boundary.
            byte[] body = TestHex.Parse(
                "7B 00 00 20 " +                                  // serial=123, service=0, length=32
                "FF 02 48 49 " +                                  // param#1 string (0xFF = -1) len 2 = "HI"
                "02 0E 41 42 43 44 45 46 47 48 49 4A 4B 4C 4D 4E " + // param#2 integer, len 14
                "03 0A 51 52 53 54 55 56 57 58 59 5A");           // param#3 integer, len 10

            XroutMessage message = XroutMessage.Parse(body);

            Assert.Equal(123, message.Serial);
            Assert.Equal(0, message.Service);
            Assert.Equal(32, message.Length);
            Assert.Equal(3, message.Parameters.Count);

            // param#1: negative type byte => string; value = two's-complement of param number.
            XroutParameter p1 = message.Parameters[0];
            Assert.True(p1.IsString);
            Assert.Equal(1, p1.ParameterNumber);
            Assert.Equal(2, p1.Length);
            Assert.Equal("HI", p1.AsText());

            // param#2: positive type byte => integer, number 2, length 14.
            XroutParameter p2 = message.Parameters[1];
            Assert.False(p2.IsString);
            Assert.Equal(2, p2.ParameterNumber);
            Assert.Equal(14, p2.Length);

            // param#3: integer, number 3, length 10.
            XroutParameter p3 = message.Parameters[2];
            Assert.False(p3.IsString);
            Assert.Equal(3, p3.ParameterNumber);
            Assert.Equal(10, p3.Length);
        }

        [Fact]
        public void Builder_InsertsEvenAlignmentFill()
        {
            // An odd-length first block forces a 0x00 fill byte before the next block.
            XroutMessage message = new XroutMessageBuilder()
                .WithSerial(5)
                .WithService(XroutService.XSLET)
                .AddInteger(1, new byte[] { 0x7F })   // 1 data byte -> block length 3 (odd)
                .AddString(2, "AB")                    // must start on an even boundary
                .Build();

            byte[] remainder = message.BuildRemainder();

            // Block1 = FF? no: integer#1 = 01 01 7F (3 bytes); fill 00; string#2 = FE 02 41 42.
            Assert.Equal(8, remainder.Length);
            Assert.Equal(0x01, remainder[0]); // integer param number 1
            Assert.Equal(0x01, remainder[1]); // length 1
            Assert.Equal(0x7F, remainder[2]); // data
            Assert.Equal(0x00, remainder[3]); // even-alignment fill byte
            Assert.Equal(0xFE, remainder[4]); // string param number 2 (two's-complement -2)
            Assert.Equal(0x02, remainder[5]); // length 2
            Assert.Equal(0x41, remainder[6]);
            Assert.Equal(0x42, remainder[7]);
        }

        [Fact]
        public void Builder_RoundTrip_ParsesBackEqual()
        {
            XroutMessage built = new XroutMessageBuilder()
                .WithSerial(123)
                .WithServiceByte(0)
                .AddString(1, "HI")
                .AddInteger16(2, 0x1234)
                .Build();

            byte[] wire = built.ToArray();
            XroutMessage parsed = XroutMessage.Parse(wire);

            Assert.Equal(built.Serial, parsed.Serial);
            Assert.Equal(built.Service, parsed.Service);
            Assert.Equal(built.Length, parsed.Length);
            Assert.Equal(built.Parameters.Count, parsed.Parameters.Count);
            Assert.Equal("HI", parsed.Parameters[0].AsText());
            Assert.True(parsed.Parameters[0].IsString);
            Assert.False(parsed.Parameters[1].IsString);
            Assert.Equal(2, parsed.Parameters[1].ParameterNumber);

            // Re-serialise must be byte-identical.
            Assert.Equal(wire, parsed.ToArray());
        }
    }
}
