//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Protocols.Xmsg.Fa;
using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Pins the refusal decode against bytes captured from a real ND-100.
    /// </summary>
    /// <remarks>
    /// Every body here was read off the wire on 2026-08-18 against D100, not built by our own
    /// encoder - a codec tested only against its own output proves the two agree, not that either
    /// is right. See <c>DOC\CARVE-FA-READ-REFUSAL-2026-08-18.md</c>.
    /// </remarks>
    public sealed class FaRefusalCodecTests
    {
        /// <summary>
        /// The OpenFile reply for a file that does not exist carries SINTRAN error 46.
        /// </summary>
        /// <remarks>
        /// The refusal that matters: it arrives on the FIRST step of the ladder, so a pull can fail
        /// immediately instead of climbing on against a file that was never opened.
        /// </remarks>
        [Fact]
        public void TheOpenReplyForAMissingFileSaysNoSuchFileName()
        {
            byte[] body = Convert.FromHexString(
                "07F00002810091699200059200" + "02" + "F20001" + "A2002E" + "F200FF" + "00");

            ushort status;
            Assert.True(FaRefusalCodec.TryReadStatus(body, out status));

            // 46 decimal, 0x2E - "NO SUCH FILE NAME" in SINTRAN's own error table.
            Assert.Equal(46, status);
        }

        /// <summary>
        /// A successful read reply is not a refusal.
        /// </summary>
        /// <remarks>
        /// This is the test that stops the fix causing a worse bug than it removes. The body is the
        /// first of 53 ReadFile replies from a transfer that completed and produced a byte-exact
        /// file, and it must not be read as a failure.
        /// </remarks>
        [Fact]
        public void ASuccessfulReadReplyCarriesNoRefusal()
        {
            byte[] body = Convert.FromHexString(
                "07F00002840091" + "4C" + "920008" + "920005" + "F200FF" + "8C");

            ushort status;
            Assert.False(FaRefusalCodec.TryReadStatus(body, out status));
            Assert.Equal(0, status);
        }

        /// <summary>
        /// The follow-on refusals after a refused open are read too.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The three operations issued after the refused open - SetBlockSize, SiiiSpecial and
        /// ReadFile - are each answered with <c>A2 4104</c>. That number is NOT explained: it is not
        /// 46 and matches no ND error number found in the Reference-Manuals.
        /// </para>
        /// <para>
        /// It is pinned here as a VALUE, deliberately without a name. What is measured is that it is
        /// identical for two different missing filenames, so it is a fixed code rather than anything
        /// derived from the request. Give it a name when somebody carves one, not before.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheFollowOnRefusalsAreReadEvenThoughTheirCodeIsUnexplained()
        {
            byte[] body = Convert.FromHexString(
                "07F00002820091699200079200" + "03" + "F20001" + "A24104" + "F200FF" + "00");

            ushort status;
            Assert.True(FaRefusalCodec.TryReadStatus(body, out status));
            Assert.Equal(0x4104, status);
        }

        /// <summary>
        /// A body too short to hold a QFORM stream is not a refusal.
        /// </summary>
        /// <remarks>
        /// A reply that cannot be read is not a reply that refuses. Reporting a refusal here would
        /// turn a truncated frame into a confident, wrong error message.
        /// </remarks>
        [Fact]
        public void AShortBodyIsNotARefusal()
        {
            byte[] body = new byte[] { 0x07, 0xF0, 0x00, 0x02 };

            ushort status;
            Assert.False(FaRefusalCodec.TryReadStatus(body, out status));
        }

        /// <summary>
        /// A selector 1 with nothing behind it is not a refusal.
        /// </summary>
        [Fact]
        public void ASelectorWithNoValueBehindItIsNotARefusal()
        {
            byte[] body = Convert.FromHexString("07F00002810091699200059200" + "02" + "F20001");

            ushort status;
            Assert.False(FaRefusalCodec.TryReadStatus(body, out status));
        }
    }
}
