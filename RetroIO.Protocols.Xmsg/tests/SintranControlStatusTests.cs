using System;

using RetroIO.Protocols.Xmsg;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Gate for reading a peer's REFUSAL out of a BAD STATUS control datagram, from the real
    /// bytes D100 sent.
    /// </summary>
    /// <remarks>
    /// <para><b>What was wrong</b></para>
    /// <para>
    /// D100 answered our connect letters with CONTROL datagrams carrying the BAD STATUS bit and a
    /// negative <see cref="XroutError"/> in word 5. Nothing read them, so the push driver logged
    /// "no answer to the connect letter" while the machine was naming the fault. 39 such datagrams
    /// across one session, against three "no answer" lines in a single push log.
    /// </para>
    /// <para><b>Why these particular bytes</b></para>
    /// <para>
    /// Both frames below are REAL, taken from live logs rather than built here - the house rule
    /// after four defects once hid behind tests that used frames we had composed ourselves. The
    /// <c>-3</c> case is the stronger evidence of the two: the same machine ALSO printed "Another
    /// port already has this name" as plain text at a terminal, so the numeric decode is confirmed
    /// against something read independently of the wire.
    /// </para>
    /// </remarks>
    public sealed class SintranControlStatusTests
    {
        /// <summary>
        /// Pulls header words 1 and 5 out of a full LAPB-framed datagram.
        /// </summary>
        /// <param name="hex">
        /// The frame bytes as captured, starting at the LAPB address byte.
        /// </param>
        /// <param name="datagramType">
        /// Receives header word 1, <c>XDTYP</c>.
        /// </param>
        /// <param name="scratch">
        /// Receives header word 5, <c>XDSCR</c>.
        /// </param>
        private static void ReadWords(string hex, out ushort datagramType, out ushort scratch)
        {
            byte[] b = Convert.FromHexString(hex);

            // addr(1) ctrl(1) then the 7 header words: XDROU XDTYP XDDNA XDSNA XDREF XDSCR XDCSM
            datagramType = (ushort)((b[4] << 8) | b[5]);
            scratch = (ushort)((b[12] << 8) | b[13]);
        }

        /// <summary>
        /// The refusal that blocked every transfer: message table space full.
        /// </summary>
        [Fact]
        public void D100RefusalDecodesAsMessageTableSpaceFull()
        {
            ReadWords("0962211300074E1F00640003FFDE908046B0", out ushort type, out ushort scratch);

            Assert.True(SintranControlStatus.IsControl(type));
            Assert.True(SintranControlStatus.IsBadStatus(type));

            Assert.True(SintranControlStatus.TryGetRefusal(type, scratch, out XroutError error));
            Assert.Equal(XroutError.XRMFL, error);
        }

        /// <summary>
        /// The other real refusal, whose meaning the machine also printed in plain English.
        /// </summary>
        [Fact]
        public void TheOtherRealRefusalDecodesAsDuplicateName()
        {
            ReadWords("074021FE00174E1F0064FFFFFFFD8F694ECB", out ushort type, out ushort scratch);

            Assert.True(SintranControlStatus.IsBadStatus(type));

            Assert.True(SintranControlStatus.TryGetRefusal(type, scratch, out XroutError error));
            Assert.Equal(XroutError.XRDDF, error);
        }

        /// <summary>
        /// An INITIALISE control datagram is not a refusal, and its word 5 is a size - reading it
        /// as an error would invent a fault out of the number 1.
        /// </summary>
        [Fact]
        public void AnInitialiseControlDatagramIsNotARefusal()
        {
            ReadWords("09A2211300194E1F0064FFFF0001904F4407", out ushort type, out ushort scratch);

            Assert.True(SintranControlStatus.IsControl(type));
            Assert.False(SintranControlStatus.IsBadStatus(type));
            Assert.False(SintranControlStatus.TryGetRefusal(type, scratch, out _));
        }

        /// <summary>
        /// An ordinary data datagram is not a control datagram at all.
        /// </summary>
        [Fact]
        public void AnOrdinaryDataDatagramIsNotControl()
        {
            // XDTYP 0x000E = SD + ED + confirm-delivery: a one-datagram message, not control.
            ReadWords("09002113000E00644E1F0010002290290000", out ushort type, out ushort scratch);

            Assert.False(SintranControlStatus.IsControl(type));
            Assert.False(SintranControlStatus.IsBadStatus(type));
            Assert.False(SintranControlStatus.TryGetRefusal(type, scratch, out _));
        }

        /// <summary>
        /// A positive word 5 on a bad-status datagram is not read as an error code.
        /// </summary>
        /// <remarks>
        /// The code is carried negated. Treating a positive value as a reason would turn scratch
        /// that happens to be non-zero into a confident, wrong diagnosis.
        /// </remarks>
        [Fact]
        public void APositiveScratchIsNotReadAsAReason()
        {
            Assert.False(SintranControlStatus.TryGetRefusal(
                SintranControlStatus.ControlBit | SintranControlStatus.BadStatusBit,
                0x0001,
                out _));
        }
    }
}
