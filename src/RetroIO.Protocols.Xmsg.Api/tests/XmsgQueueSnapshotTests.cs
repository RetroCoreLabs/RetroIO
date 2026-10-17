//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Api.Tests
{
    /// <summary>
    /// <c>XFGSM</c>, checked against ND's own worked example.
    /// </summary>
    /// <remarks>
    /// <para><b>Where the expected values come from</b></para>
    /// ND wrote the example, not us. <c>X-MESSAGE 210373L</c> section 6.3 sets up a task that opens
    /// ports 6, 9 and 3 in that order, queues one message on each, and states the answer:
    /// <c>A=7, D=2, X=1</c>. Using their arithmetic rather than ours is the whole point - the bit
    /// order is REVERSED against open order, which is the one thing about this function that is
    /// easy to get backwards and impossible to notice.
    /// <para>
    /// This function was recorded for months as "blocked on evidence" because Appendix A of the
    /// COSMOS Programmer Guide has no section for it. The release description does.
    /// </para>
    /// </remarks>
    public sealed class XmsgQueueSnapshotTests
    {
        /// <summary>
        /// Builds a kernel.
        /// </summary>
        /// <returns>
        /// A kernel with an arbitrary system number.
        /// </returns>
        private static XmsgKernel NewKernel()
        {
            return new XmsgKernel(102, 0x1234, null);
        }

        /// <summary>
        /// Queues one message of a given type on a port.
        /// </summary>
        /// <param name="kernel">
        /// The kernel.
        /// </param>
        /// <param name="target">
        /// The port to queue on.
        /// </param>
        /// <param name="sender">
        /// The port the message is sent from.
        /// </param>
        /// <param name="flags">
        /// Send flags. NOTE that <c>Route</c> does NOT produce an <c>XMROU</c>: XFROU means "send
        /// this TO XROUT", and the type is decided by <c>ChooseType</c>, where the routed type
        /// comes from <c>Forward</c> (XFFWD) - what XROUT itself uses passing a letter on. This
        /// line claimed the opposite for as long as no test could tell the difference.
        /// </param>
        private static void QueueOne(
            XmsgKernel kernel, XmsgPortNumber target, XmsgPortNumber sender, XmsgSendFlags flags)
        {
            XmsgMagicNumber targetMagic;
            kernel.ConvertPortToMagic(target, out targetMagic);

            XmsgMessageIdentifier message;
            kernel.ReserveBuffer(8, XmsgBufferOptions.None, out message);

            Assert.False(kernel.Send(targetMagic, sender, flags).IsError);
        }

        /// <summary>
        /// With no ports open, every mask is empty rather than undefined.
        /// </summary>
        [Fact]
        public void NoPortsGivesEmptyMasks()
        {
            XmsgQueueSnapshot snapshot = NewKernel().GetGeneralStatusMultiple();

            Assert.Equal(0, snapshot.Queued);
            Assert.Equal(0, snapshot.RouterFirst);
            Assert.Equal(0, snapshot.ReturnedFirst);
            Assert.False(snapshot.HasMessage(0));
        }

        /// <summary>
        /// A port with nothing queued does not appear, even though it is open.
        /// </summary>
        [Fact]
        public void AnOpenButQuietPortIsNotFlagged()
        {
            XmsgKernel kernel = NewKernel();

            XmsgPortNumber quiet;
            kernel.OpenPort(out quiet);

            Assert.Equal(0, kernel.GetGeneralStatusMultiple().Queued);
        }

        /// <summary>
        /// Bit 0 is the port opened LAST, which is the reverse of open order.
        /// </summary>
        /// <remarks>
        /// Asserted on its own, before the full example, because it is the single fact that makes
        /// every other expected value in this file what it is.
        /// </remarks>
        [Fact]
        public void BitZeroIsTheMostRecentlyOpenedPort()
        {
            XmsgKernel kernel = NewKernel();

            XmsgPortNumber first;
            XmsgPortNumber second;
            XmsgPortNumber sender;
            kernel.OpenPort(out first);
            kernel.OpenPort(out second);
            kernel.OpenPort(out sender);

            // Queue on the FIRST-opened port only.
            QueueOne(kernel, first, sender, XmsgSendFlags.None);

            XmsgQueueSnapshot snapshot = kernel.GetGeneralStatusMultiple();

            // Three ports open, so the first-opened is bit 2 - not bit 0.
            Assert.True(snapshot.HasMessage(2));
            Assert.False(snapshot.HasMessage(0));
            Assert.Equal(1 << 2, snapshot.Queued);
        }

        /// <summary>
        /// ND's worked example, in the shape this kernel can actually produce.
        /// </summary>
        /// <remarks>
        /// <para>
        /// ND's own example uses three ports holding an ordinary message, a router message and a
        /// returned one. This test stages only the ordinary arm, which is what it was written for;
        /// all three together are now in <see cref="NdsWorkedExampleReproducedInFull"/>.
        /// </para>
        /// <para>
        /// It used to say here that the router arm could not be staged at all - that a local send
        /// never produces an <c>XMROU</c> whatever flags are passed. That was true when it was
        /// written and is not any more: nothing in the kernel set that type until XFFWD was made to
        /// produce it, which is what letters arriving from XROUT actually are.
        /// </para>
        /// </remarks>
        [Fact]
        public void SeveralPortsMapToReversedBitsWithOrdinaryMessages()
        {
            XmsgKernel kernel = NewKernel();

            XmsgPortNumber openedFirst;
            XmsgPortNumber openedSecond;
            XmsgPortNumber sender;

            kernel.OpenPort(out openedFirst);
            kernel.OpenPort(out openedSecond);
            kernel.OpenPort(out sender);

            QueueOne(kernel, openedFirst, sender, XmsgSendFlags.None);
            QueueOne(kernel, openedSecond, sender, XmsgSendFlags.None);

            XmsgQueueSnapshot snapshot = kernel.GetGeneralStatusMultiple();

            // Three ports open: first-opened is bit 2, second bit 1, sender bit 0 and quiet.
            Assert.True(snapshot.HasMessage(2));
            Assert.True(snapshot.HasMessage(1));
            Assert.False(snapshot.HasMessage(0));
            Assert.Equal((1 << 2) | (1 << 1), snapshot.Queued);

            // ND's rule: XMTNO, XMHIP and XMBNC all leave BOTH type bits clear.
            Assert.Equal(0, snapshot.RouterFirst);
            Assert.Equal(0, snapshot.ReturnedFirst);
            Assert.Equal(XmsgMessageType.XMTNO, snapshot.FirstMessageType(2));
        }

        /// <summary>
        /// A message returned by closing its port shows up in the returned mask.
        /// </summary>
        /// <remarks>
        /// Closing a port returns its secure messages to their senders as <c>XMTRE</c>, which is the
        /// one path in this kernel that genuinely produces the type ND's X-register reports.
        /// </remarks>
        [Fact]
        public void ReturnedMessageShowsAsXmtre()
        {
            XmsgKernel kernel = NewKernel();

            XmsgPortNumber sender;
            XmsgPortNumber target;
            kernel.OpenPort(out sender);
            kernel.OpenPort(out target);

            QueueOne(kernel, target, sender, XmsgSendFlags.Secure);

            // Closing the target hands its secure message back to the sender, typed XMTRE.
            kernel.ClosePort(target);

            XmsgQueueSnapshot snapshot = kernel.GetGeneralStatusMultiple();

            // Only the sender remains open, so it is bit 0.
            Assert.True(snapshot.HasMessage(0));
            Assert.Equal(XmsgMessageType.XMTRE, snapshot.FirstMessageType(0));
            Assert.Equal(1, snapshot.ReturnedFirst);
            Assert.Equal(0, snapshot.RouterFirst);
        }

        /// <summary>
        /// Only the FIRST queued message sets the type bits.
        /// </summary>
        /// <remarks>
        /// ND's example makes this explicit: once the special message at the head is received, the
        /// type bit clears on the next call even though the port still has traffic. A reading that
        /// ORed every queued message together would pass the simple cases and fail here.
        /// </remarks>
        [Fact]
        public void OnlyTheFirstQueuedMessageSetsTheTypeBits()
        {
            XmsgKernel kernel = NewKernel();

            XmsgPortNumber sender;
            XmsgPortNumber target;
            kernel.OpenPort(out sender);
            kernel.OpenPort(out target);

            // A secure message that will come back as XMTRE, and an ordinary one behind it.
            QueueOne(kernel, target, sender, XmsgSendFlags.Secure);
            kernel.ClosePort(target);
            QueueOne(kernel, sender, sender, XmsgSendFlags.None);

            // sender is the only port left, so bit 0. Its head is the returned message.
            Assert.Equal(
                XmsgMessageType.XMTRE, kernel.GetGeneralStatusMultiple().FirstMessageType(0));

            // Take the returned message off; the ordinary one is now the head and the bit clears.
            Assert.True(kernel.Receive(sender, XmsgWaitOptions.None).Received);

            XmsgQueueSnapshot after = kernel.GetGeneralStatusMultiple();
            Assert.True(after.HasMessage(0));
            Assert.Equal(XmsgMessageType.XMTNO, after.FirstMessageType(0));
            Assert.Equal(0, after.ReturnedFirst);
        }

        /// <summary>
        /// XFRTN's returned message does NOT carry <c>XMTRE</c> today. Recorded, not asserted as
        /// correct.
        /// </summary>
        /// <remarks>
        /// <para><b>An open question, pinned so it cannot drift unnoticed</b></para>
        /// <c>ReturnMessage</c> sets the type to <c>XMTRE</c> and then calls <c>Deliver</c>, which
        /// unconditionally overwrites it with <c>XMTNO</c> or <c>XMTHI</c>. So a task that receives
        /// a message returned by <c>XFRTN</c> sees an ordinary message, while one returned by a port
        /// CLOSING sees <c>XMTRE</c>.
        /// <para>
        /// Whether that is a defect is genuinely unsettled. <c>XFRTN</c> is how a server replies to
        /// a request, and a reply is arguably ordinary traffic; but the function is named "return
        /// message" and the type is named "returned". No capture in this project shows a real
        /// machine's answer, and the COSMOS guide's section on XFRTN does not name a message type.
        /// </para>
        /// <para>
        /// So the behaviour is pinned as it stands rather than changed on a reading. If a capture
        /// later shows <c>XMTRE</c> on an XFRTN reply, this test fails and says exactly what to fix.
        /// </para>
        /// </remarks>
        [Fact]
        public void XfrtnReplyIsNotTypedReturned_Unverified()
        {
            XmsgKernel kernel = NewKernel();

            XmsgPortNumber sender;
            XmsgPortNumber server;
            kernel.OpenPort(out sender);
            kernel.OpenPort(out server);

            QueueOne(kernel, server, sender, XmsgSendFlags.None);

            XmsgReceiveResult got = kernel.Receive(server, XmsgWaitOptions.None);
            Assert.True(got.Received);
            Assert.False(kernel.ReturnMessage(got.Message, 0, server, XmsgSendFlags.None).IsError);

            // sender was opened first of two, so bit 1.
            XmsgQueueSnapshot snapshot = kernel.GetGeneralStatusMultiple();
            Assert.True(snapshot.HasMessage(1));

            // MEASURED, not endorsed - see the remarks.
            Assert.Equal(XmsgMessageType.XMTNO, snapshot.FirstMessageType(1));
        }

        /// <summary>
        /// A bit position outside the 16 the masks carry is answered false, not indexed.
        /// </summary>
        [Theory]
        [InlineData(-1)]
        [InlineData(16)]
        [InlineData(99)]
        public void BitsOutsideTheMaskAreRefused(int bit)
        {
            XmsgQueueSnapshot snapshot = new XmsgQueueSnapshot(0xFFFF, 0xFFFF, 0xFFFF);

            Assert.False(snapshot.HasMessage(bit));
            Assert.Equal(XmsgMessageType.XMTNO, snapshot.FirstMessageType(bit));
        }

        /// <summary>
        /// ND's worked example, all three arms, with their arithmetic.
        /// </summary>
        /// <remarks>
        /// <para><b>A=7, D=2, X=1 - their numbers, not ours</b></para>
        /// <para>
        /// <c>X-MESSAGE 210373L</c> section 6.3 opens three ports and puts one message on each: an
        /// ordinary one, a router one and a returned one. Until XFFWD was made to produce an
        /// <c>XMROU</c>, the middle arm could not be staged through this kernel at all and the
        /// example could only be reproduced in part.
        /// </para>
        /// <para>
        /// Reproducing it whole is worth more than the sum of the three separate tests, because the
        /// three answers are computed from ONE walk of the port list. A bit-order mistake that
        /// happens to look right with a single flagged port shows up here as the wrong word.
        /// </para>
        /// <para><b>The staging order is deliberate</b></para>
        /// <para>
        /// Bit 0 is the port opened MOST RECENTLY, so the ports are opened oldest-first and the
        /// temporary port used to produce the returned message is opened LAST and then closed -
        /// which takes it back out of the list and leaves the other three on the bits this example
        /// needs. Closing it is not tidying: closing is what returns its secure message.
        /// </para>
        /// </remarks>
        [Fact]
        public void NdsWorkedExampleReproducedInFull()
        {
            XmsgKernel kernel = NewKernel();

            XmsgPortNumber ordinary;      // opened first  -> bit 2
            XmsgPortNumber routed;        // opened second -> bit 1
            XmsgPortNumber returned;      // opened third  -> bit 0
            XmsgPortNumber temporary;     // opened last, then closed

            kernel.OpenPort(out ordinary);
            kernel.OpenPort(out routed);
            kernel.OpenPort(out returned);
            kernel.OpenPort(out temporary);

            // ---- the RETURNED arm: a secure message comes back when its target closes ----------
            QueueOne(kernel, temporary, returned, XmsgSendFlags.Secure);
            kernel.ClosePort(temporary);

            // ---- the ORDINARY arm --------------------------------------------------------------
            QueueOne(kernel, ordinary, returned, XmsgSendFlags.None);

            // ---- the ROUTER arm: a letter, as XROUT forwards one --------------------------------
            XmsgMagicNumber routedMagic;
            Assert.False(kernel.ConvertPortToMagic(routed, out routedMagic).IsError);
            Assert.True(kernel.Deliver(
                routedMagic,
                new XmsgMagicNumber(0x2222),
                new byte[] { 0x01, 0x02 },
                XmsgSendFlags.Forward).IsSuccess);

            XmsgQueueSnapshot snapshot = kernel.GetGeneralStatusMultiple();

            // Each arm is on the bit its open order gives it.
            Assert.Equal(XmsgMessageType.XMTNO, snapshot.FirstMessageType(2));
            Assert.Equal(XmsgMessageType.XMROU, snapshot.FirstMessageType(1));
            Assert.Equal(XmsgMessageType.XMTRE, snapshot.FirstMessageType(0));

            // ND'S OWN ANSWER. A=7 all three queued, D=2 the router arm on bit 1, X=1 the returned
            // arm on bit 0.
            Assert.Equal(7, snapshot.Queued);
            Assert.Equal(2, snapshot.RouterFirst);
            Assert.Equal(1, snapshot.ReturnedFirst);
        }
    }
}