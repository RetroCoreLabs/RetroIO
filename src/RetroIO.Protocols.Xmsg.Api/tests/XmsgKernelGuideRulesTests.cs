//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Api.Tests
{
    /// <summary>
    /// Proves four rules of the COSMOS Programmer Guide (ND-60.164.3) that the kernel used to
    /// skip: the Route option of XFSND, the checks of XFPRV, the options and ceiling of XFGET,
    /// and the system answer of XFM2P.
    /// </summary>
    public sealed class XmsgKernelGuideRulesTests
    {
        // ---------------------------------------------------------------------------------
        // XFSND with XFROU / XFRRO - appendix A section 3.2.11 and the XMPFSND page.
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// "Route message. Ignore the MAGNO parameter and send the message to the local routing
        /// task (XROUT)."
        /// </summary>
        [Fact]
        public void Send_Route_IgnoresTheDestinationAndGoesToTheRouterPort()
        {
            XmsgKernel kernel = NewKernel();
            XmsgPortNumber router;
            XmsgPortNumber target;
            XmsgPortNumber client;
            kernel.OpenPort(out router);
            kernel.OpenPort(out target);
            kernel.OpenPort(out client);
            Assert.True(kernel.AttachRouter(router).IsSuccess);

            XmsgMagicNumber targetMagic;
            kernel.ConvertPortToMagic(target, out targetMagic);

            Assert.True(SendBytes(kernel, client, targetMagic, new byte[] { 0x42 }, XmsgSendFlags.Route).IsSuccess);

            Assert.Equal(1, kernel.GetPortStatus(router, XmsgWaitOptions.None).QueueLength);
            Assert.Equal(0, kernel.GetPortStatus(target, XmsgWaitOptions.None).QueueLength);
        }

        /// <summary>
        /// A routed message that cannot reach XROUT comes back when it was sent secure, because
        /// the destination magic number "does not exist" (XEIMA).
        /// </summary>
        [Fact]
        public void Send_Route_WithNoRouter_ComesBackWhenSecure()
        {
            XmsgKernel kernel = NewKernel();
            XmsgPortNumber target;
            XmsgPortNumber client;
            kernel.OpenPort(out target);
            kernel.OpenPort(out client);

            XmsgMagicNumber targetMagic;
            kernel.ConvertPortToMagic(target, out targetMagic);

            Assert.True(SendBytes(kernel, client, targetMagic, new byte[] { 1 },
                XmsgSendFlags.Route | XmsgSendFlags.Secure).IsSuccess);

            Assert.Equal(0, kernel.GetPortStatus(target, XmsgWaitOptions.None).QueueLength);
            XmsgReceiveResult back = kernel.Receive(client, XmsgWaitOptions.None);
            Assert.True(back.Received);
            Assert.Equal(XmsgMessageType.XMTRE, back.MessageType);
            Assert.Equal((int)XmsgError.XEIMA, back.Extra);
        }

        /// <summary>
        /// "Remote route message. If the XFROU flag is also set, then send the message to a remote
        /// routing task (XROUT). The 16 most significant bits of remoteMagicNum is assumed to
        /// contain the system number." The captures show XROUT at port 0 of its system.
        /// </summary>
        [Fact]
        public void Send_RouteWithRemoteRoute_GoesToPortZeroOfTheNamedSystem()
        {
            RecordingSink sink = new RecordingSink();
            XmsgKernel kernel = new XmsgKernel(102, 0x1234, sink);
            XmsgPortNumber client;
            kernel.OpenPort(out client);

            // The port and random parts are junk on purpose: only the system number is read.
            XmsgMagicNumber far = XmsgMagicNumber.Create(100, 7, 43);

            Assert.True(SendBytes(kernel, client, far, new byte[] { 9 },
                XmsgSendFlags.Route | XmsgSendFlags.RemoteRoute).IsSuccess);

            Assert.Single(sink.Destinations);
            Assert.Equal(XmsgMagicNumber.Create(100, 0, 0), sink.Destinations[0]);
            Assert.True((sink.Flags[0] & XmsgSendFlags.Route) != 0);
        }

        /// <summary>
        /// "When XFROU is set, setting the XFHIP flag will act as if the XFRRO flag had been set
        /// instead" - they are the same bit, so the high-priority name routes remotely too.
        /// </summary>
        [Fact]
        public void Send_RouteWithHighPriorityBit_RoutesRemotely()
        {
            RecordingSink sink = new RecordingSink();
            XmsgKernel kernel = new XmsgKernel(102, 0x1234, sink);
            XmsgPortNumber client;
            kernel.OpenPort(out client);

            Assert.True(SendBytes(kernel, client, XmsgMagicNumber.Create(100, 7, 43), new byte[] { 9 },
                XmsgSendFlags.Route | XmsgSendFlags.HighPriority).IsSuccess);

            Assert.Equal(XmsgMagicNumber.Create(100, 0, 0), sink.Destinations[0]);
        }

        /// <summary>
        /// Plain Route (no remote bit) stays in this system even when the destination names
        /// another one: "send the message to the local routing task".
        /// </summary>
        [Fact]
        public void Send_RouteWithoutRemoteBit_NeverLeavesTheSystem()
        {
            RecordingSink sink = new RecordingSink();
            XmsgKernel kernel = new XmsgKernel(102, 0x1234, sink);
            XmsgPortNumber router;
            XmsgPortNumber client;
            kernel.OpenPort(out router);
            kernel.OpenPort(out client);
            kernel.AttachRouter(router);

            Assert.True(SendBytes(kernel, client, XmsgMagicNumber.Create(100, 7, 43), new byte[] { 9 },
                XmsgSendFlags.Route).IsSuccess);

            Assert.Empty(sink.Destinations);
            Assert.Equal(1, kernel.GetPortStatus(router, XmsgWaitOptions.None).QueueLength);
        }

        /// <summary>
        /// A datagram addressed to port 0 of this system arrives at XROUT, the fixed letter and
        /// service sink the captures show (XMSG-PROTOCOL section 7.1).
        /// </summary>
        [Fact]
        public void Deliver_ToPortZero_ReachesTheRouter()
        {
            XmsgKernel kernel = NewKernel();
            XmsgPortNumber router;
            kernel.OpenPort(out router);
            kernel.AttachRouter(router);

            XmsgStatus delivered = kernel.Deliver(
                XmsgMagicNumber.Create(102, 0, 0),
                XmsgMagicNumber.Create(100, 5, 9),
                new byte[] { 1, 2 },
                XmsgSendFlags.None);

            Assert.True(delivered.IsSuccess);
            Assert.Equal(1, kernel.GetPortStatus(router, XmsgWaitOptions.None).QueueLength);
        }

        /// <summary>
        /// Closing the router's port detaches it, so port 0 stops answering.
        /// </summary>
        [Fact]
        public void ClosingTheRouterPort_DetachesTheRouter()
        {
            XmsgKernel kernel = NewKernel();
            XmsgPortNumber router;
            kernel.OpenPort(out router);
            kernel.AttachRouter(router);
            kernel.ClosePort(router);

            XmsgStatus delivered = kernel.Deliver(
                XmsgMagicNumber.Create(102, 0, 0),
                XmsgMagicNumber.Create(100, 5, 9),
                new byte[] { 1 },
                XmsgSendFlags.None);

            Assert.True(delivered.IsError);
        }

        /// <summary>
        /// A router port has to be an open port.
        /// </summary>
        [Fact]
        public void AttachRouter_ToAPortThatIsNotOpen_Fails()
        {
            XmsgKernel kernel = NewKernel();
            XmsgStatus attached = kernel.AttachRouter(new XmsgPortNumber(9));
            Assert.Equal((int)XmsgError.XEIPN, attached.Value);
        }

        // ---------------------------------------------------------------------------------
        // XFPRV - appendix A section 4.8, the XMPFPRV page and the XEPVR error text.
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// "The program must also specify the current XMSG password" - a wrong one is refused
        /// with XEPVR, "Privilege request refused".
        /// </summary>
        [Fact]
        public void MakePrivileged_WrongPassword_IsRefusedWithXEPVR()
        {
            XmsgKernel kernel = NewKernel();
            kernel.TaskKind = XmsgTaskKind.Driver;
            kernel.VersionCode = 0x0A0B;

            XmsgStatus refused = kernel.MakePrivileged(0x0A0C);

            Assert.Equal((int)XmsgError.XEPVR, refused.Value);
            Assert.False(kernel.IsPrivileged);
        }

        /// <summary>
        /// With no version code set there is no valid password, so even a nonzero guess fails.
        /// </summary>
        [Fact]
        public void MakePrivileged_WhenNoVersionCodeIsSet_IsRefused()
        {
            XmsgKernel kernel = NewKernel();
            kernel.TaskKind = XmsgTaskKind.Driver;

            Assert.Equal((int)XmsgError.XEPVR, kernel.MakePrivileged(1).Value);
            Assert.False(kernel.IsPrivileged);
        }

        /// <summary>
        /// "The caller must be either a driver, direct task, foreground program (ring 2 or 3), or
        /// a background program logged in as user SYSTEM."
        /// </summary>
        /// <param name="kind">
        /// A kind of program the guide allows.
        /// </param>
        [Theory]
        [InlineData(XmsgTaskKind.Driver)]
        [InlineData(XmsgTaskKind.DirectTask)]
        [InlineData(XmsgTaskKind.ForegroundRing2)]
        [InlineData(XmsgTaskKind.ForegroundRing3)]
        [InlineData(XmsgTaskKind.BackgroundSystemUser)]
        public void MakePrivileged_KindsTheGuideNames_Succeed(XmsgTaskKind kind)
        {
            XmsgKernel kernel = NewKernel();
            kernel.TaskKind = kind;
            kernel.VersionCode = 0x0A0B;

            Assert.True(kernel.MakePrivileged(0x0A0B).IsSuccess);
            Assert.True(kernel.IsPrivileged);
        }

        /// <summary>
        /// A kind of program the guide does not name is refused, even with the right password.
        /// </summary>
        /// <param name="kind">
        /// A kind of program the guide does not allow.
        /// </param>
        [Theory]
        [InlineData(XmsgTaskKind.Unspecified)]
        [InlineData(XmsgTaskKind.ForegroundRing1)]
        [InlineData(XmsgTaskKind.BackgroundOtherUser)]
        public void MakePrivileged_KindsTheGuideDoesNotName_AreRefused(XmsgTaskKind kind)
        {
            XmsgKernel kernel = NewKernel();
            kernel.TaskKind = kind;
            kernel.VersionCode = 0x0A0B;

            Assert.Equal((int)XmsgError.XEPVR, kernel.MakePrivileged(0x0A0B).Value);
            Assert.False(kernel.IsPrivileged);
        }

        /// <summary>
        /// "When the task should no longer be privileged, the same call should be used, but with
        /// the A register equal to zero."
        /// </summary>
        [Fact]
        public void MakePrivileged_WithZero_GivesThePrivilegeUp()
        {
            XmsgKernel kernel = NewKernel();
            kernel.TaskKind = XmsgTaskKind.Driver;
            kernel.VersionCode = 0x0A0B;
            Assert.True(kernel.MakePrivileged(0x0A0B).IsSuccess);

            Assert.True(kernel.MakePrivileged(0).IsSuccess);

            Assert.False(kernel.IsPrivileged);
            Assert.Equal((int)XmsgError.XEPRV, kernel.DefineMaximumMemory(100).Value);
        }

        /// <summary>
        /// XFDUM returns the password, so a task can pass it on to XFPRV.
        /// </summary>
        [Fact]
        public void Dummy_ReturnsThePasswordXfprvWants()
        {
            XmsgKernel kernel = NewKernel();
            kernel.VersionCode = 0x1234;

            ushort password;
            Assert.True(kernel.Dummy(out password).IsSuccess);

            Assert.Equal(0x1234, password);
        }

        // ---------------------------------------------------------------------------------
        // XFGET - appendix A section 3.2.1, 3.2.4, 4.3 and the XETMM / XEILM error texts.
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// "The total space owned by a task cannot exceed" the XFDMM limit; going over is XETMM,
        /// "Task is not allowed any more memory". Releasing a buffer makes room again.
        /// </summary>
        [Fact]
        public void ReserveBuffer_OverTheCeiling_IsRefusedWithXETMM()
        {
            XmsgKernel kernel = PrivilegedKernel();
            Assert.True(kernel.DefineMaximumMemory(100).IsSuccess);

            XmsgMessageIdentifier first;
            Assert.True(kernel.ReserveBuffer(60, XmsgBufferOptions.None, out first).IsSuccess);

            XmsgMessageIdentifier second;
            XmsgStatus refused = kernel.ReserveBuffer(60, XmsgBufferOptions.None, out second);
            Assert.Equal((int)XmsgError.XETMM, refused.Value);
            Assert.Equal(XmsgMessageIdentifier.None, second);
            Assert.Equal(1, kernel.OwnedBufferCount);

            Assert.True(kernel.ReleaseBuffer(first).IsSuccess);
            Assert.True(kernel.ReserveBuffer(60, XmsgBufferOptions.None, out second).IsSuccess);
        }

        /// <summary>
        /// A buffer exactly filling the ceiling is allowed; one byte more is not.
        /// </summary>
        [Fact]
        public void ReserveBuffer_ExactlyTheCeiling_IsAllowed()
        {
            XmsgKernel kernel = PrivilegedKernel();
            kernel.DefineMaximumMemory(100);

            XmsgMessageIdentifier message;
            Assert.True(kernel.ReserveBuffer(100, XmsgBufferOptions.None, out message).IsSuccess);
            kernel.ReleaseBuffer(message);
            Assert.Equal((int)XmsgError.XETMM, kernel.ReserveBuffer(101, XmsgBufferOptions.None, out message).Value);
        }

        /// <summary>
        /// Space held by XFALM counts toward the ceiling, so an ordinary reservation on top of it
        /// can be refused.
        /// </summary>
        [Fact]
        public void ReserveBuffer_CountsAllocatedPoolsTowardTheCeiling()
        {
            XmsgKernel kernel = PrivilegedKernel();
            kernel.DefineMaximumMemory(100);
            Assert.True(kernel.AllocateBuffers(byteCount: 80, messageCount: 1, exclusive: false).IsSuccess);

            XmsgMessageIdentifier message;
            Assert.Equal((int)XmsgError.XETMM, kernel.ReserveBuffer(30, XmsgBufferOptions.None, out message).Value);
        }

        /// <summary>
        /// "When an XFGET is executed by that task for a buffer of that size, XMSG will first look
        /// at the task's AAML and take a message buffer from it" - that buffer is already owned, so
        /// it needs no new room under the ceiling.
        /// </summary>
        [Fact]
        public void ReserveBuffer_OfTheAllocatedSize_TakesFromTheAllocatedList()
        {
            XmsgKernel kernel = PrivilegedKernel();
            kernel.DefineMaximumMemory(64);
            Assert.True(kernel.AllocateBuffers(byteCount: 64, messageCount: 1, exclusive: false).IsSuccess);

            XmsgMessageIdentifier first;
            Assert.True(kernel.ReserveBuffer(64, XmsgBufferOptions.None, out first).IsSuccess);

            // The pool is spent, so the next one needs new room, and there is none.
            XmsgMessageIdentifier second;
            Assert.Equal((int)XmsgError.XETMM, kernel.ReserveBuffer(64, XmsgBufferOptions.None, out second).Value);

            // Releasing "puts the message back on the original task's AAML".
            kernel.ReleaseBuffer(first);
            Assert.True(kernel.ReserveBuffer(64, XmsgBufferOptions.None, out second).IsSuccess);
        }

        /// <summary>
        /// Without the exclusive option a reservation never touches the exclusive pool.
        /// </summary>
        [Fact]
        public void ReserveBuffer_WithoutExclusive_LeavesTheExclusivePoolAlone()
        {
            XmsgKernel kernel = PrivilegedKernel();
            kernel.DefineMaximumMemory(64);
            kernel.AllocateBuffers(byteCount: 64, messageCount: 1, exclusive: true);

            XmsgMessageIdentifier message;
            Assert.Equal((int)XmsgError.XETMM, kernel.ReserveBuffer(64, XmsgBufferOptions.None, out message).Value);
        }

        /// <summary>
        /// XFEXC takes one of the exclusive buffers; when none is left the call fails with XEILM
        /// ("the option XFEXC is specified and no exclusive message buffer of the requested size is
        /// available"). Releasing gives the buffer back.
        /// </summary>
        [Fact]
        public void ReserveBuffer_Exclusive_TakesFromTheExclusivePoolOnly()
        {
            XmsgKernel kernel = NewKernel();
            Assert.True(kernel.AllocateBuffers(byteCount: 64, messageCount: 2, exclusive: true).IsSuccess);

            XmsgMessageIdentifier a;
            XmsgMessageIdentifier b;
            XmsgMessageIdentifier c;
            Assert.True(kernel.ReserveBuffer(64, XmsgBufferOptions.Exclusive, out a).IsSuccess);
            Assert.True(kernel.ReserveBuffer(64, XmsgBufferOptions.Exclusive, out b).IsSuccess);
            Assert.Equal((int)XmsgError.XEILM, kernel.ReserveBuffer(64, XmsgBufferOptions.Exclusive, out c).Value);
            Assert.Equal(XmsgMessageIdentifier.None, c);

            kernel.ReleaseBuffer(a);
            Assert.True(kernel.ReserveBuffer(64, XmsgBufferOptions.Exclusive, out c).IsSuccess);
        }

        /// <summary>
        /// An exclusive reservation of a size other than the exclusive pool's is XEILM.
        /// </summary>
        [Fact]
        public void ReserveBuffer_Exclusive_WithAnotherSize_IsRefused()
        {
            XmsgKernel kernel = NewKernel();
            kernel.AllocateBuffers(byteCount: 64, messageCount: 2, exclusive: true);

            XmsgMessageIdentifier message;
            Assert.Equal((int)XmsgError.XEILM, kernel.ReserveBuffer(32, XmsgBufferOptions.Exclusive, out message).Value);
        }

        /// <summary>
        /// With no exclusive pool at all, XFEXC fails: "If no such allocated message buffer is
        /// available, an error code is returned".
        /// </summary>
        [Fact]
        public void ReserveBuffer_Exclusive_WithNoPool_IsRefused()
        {
            XmsgKernel kernel = NewKernel();

            XmsgMessageIdentifier message;
            XmsgStatus refused = kernel.ReserveBuffer(64, XmsgBufferOptions.Exclusive, out message);

            Assert.Equal((int)XmsgError.XEILM, refused.Value);
            Assert.Equal(0, kernel.OwnedBufferCount);
        }

        /// <summary>
        /// An exclusive buffer does not count against the ceiling a second time: the room was
        /// taken when XFALM set it aside.
        /// </summary>
        [Fact]
        public void ReserveBuffer_Exclusive_NeedsNoNewRoomUnderTheCeiling()
        {
            XmsgKernel kernel = PrivilegedKernel();
            Assert.True(kernel.AllocateBuffers(byteCount: 64, messageCount: 1, exclusive: true).IsSuccess);
            Assert.True(kernel.DefineMaximumMemory(64).IsSuccess);

            XmsgMessageIdentifier message;
            Assert.True(kernel.ReserveBuffer(64, XmsgBufferOptions.Exclusive, out message).IsSuccess);
        }

        /// <summary>
        /// A message sent out of the system returns its allocated buffer to the pool: "when they
        /// are released, however, or sent out of the local system, the message buffer is put back
        /// on the originating task's AAML".
        /// </summary>
        [Fact]
        public void Send_OutOfTheSystem_PutsAnAllocatedBufferBack()
        {
            RecordingSink sink = new RecordingSink();
            XmsgKernel kernel = new XmsgKernel(102, 0x1234, sink);
            kernel.AllocateBuffers(byteCount: 8, messageCount: 1, exclusive: true);
            XmsgPortNumber client;
            kernel.OpenPort(out client);

            XmsgMessageIdentifier message;
            Assert.True(kernel.ReserveBuffer(8, XmsgBufferOptions.Exclusive, out message).IsSuccess);
            Assert.True(kernel.Send(XmsgMagicNumber.Create(100, 3, 4), client, XmsgSendFlags.None).IsSuccess);

            XmsgMessageIdentifier again;
            Assert.True(kernel.ReserveBuffer(8, XmsgBufferOptions.Exclusive, out again).IsSuccess);
        }

        // ---------------------------------------------------------------------------------
        // XFM2P - appendix A section 4.4 and the carved MFM2P routine.
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// "If the magic number was that of a system, then T=3." The carved kernel routine MFM2P
        /// tests the port field first and, when it is zero, returns T=3 with port 0 and the high
        /// word as the system number.
        /// </summary>
        [Fact]
        public void ConvertMagicToPort_PortZero_IsASystem()
        {
            XmsgKernel kernel = NewKernel();

            XmsgPortNumber port;
            int system;
            XmsgStatus status = kernel.ConvertMagicToPort(XmsgMagicNumber.Create(100, 0, 0), out port, out system);

            Assert.True(status.IsSuccess);
            Assert.Equal(3, status.Value);
            Assert.Equal(0, port.Value);
            Assert.Equal(100, system);
        }

        /// <summary>
        /// The system answer does not depend on whether the system is this one, or on privilege.
        /// </summary>
        [Fact]
        public void ConvertMagicToPort_OwnSystemPortZero_IsASystemEvenWhenPrivileged()
        {
            XmsgKernel kernel = PrivilegedKernel();

            XmsgPortNumber port;
            int system;
            XmsgStatus status = kernel.ConvertMagicToPort(XmsgMagicNumber.Create(102, 0, 0), out port, out system);

            Assert.Equal(3, status.Value);
            Assert.Equal(102, system);
        }

        /// <summary>
        /// A port number that is not zero is still a port: 2 for a local port of a privileged
        /// task, 1 otherwise.
        /// </summary>
        [Fact]
        public void ConvertMagicToPort_NonZeroPort_IsStillAPort()
        {
            XmsgKernel kernel = NewKernel();

            XmsgPortNumber port;
            int system;
            XmsgStatus status = kernel.ConvertMagicToPort(XmsgMagicNumber.Create(100, 5, 9), out port, out system);

            Assert.Equal(1, status.Value);
            Assert.Equal(5, port.Value);
        }

        private static XmsgKernel NewKernel()
        {
            return new XmsgKernel(102, 0x1234, null);
        }

        private static XmsgKernel PrivilegedKernel()
        {
            XmsgKernel kernel = NewKernel();
            kernel.TaskKind = XmsgTaskKind.BackgroundSystemUser;
            kernel.VersionCode = 0x0A0B;
            Assert.True(kernel.MakePrivileged(0x0A0B).IsSuccess);
            return kernel;
        }

        private static XmsgStatus SendBytes(
            XmsgKernel kernel,
            XmsgPortNumber from,
            XmsgMagicNumber to,
            byte[] payload,
            XmsgSendFlags flags)
        {
            XmsgMessageIdentifier message;
            kernel.ReserveBuffer(payload.Length == 0 ? 1 : payload.Length, XmsgBufferOptions.None, out message);
            int written;
            kernel.Write(message, payload, 0, false, out written);
            return kernel.Send(to, from, flags);
        }

        private sealed class RecordingSink : IXmsgDatagramSink
        {
            internal List<XmsgMagicNumber> Destinations { get; } = new List<XmsgMagicNumber>();

            internal List<XmsgSendFlags> Flags { get; } = new List<XmsgSendFlags>();

            public XmsgStatus Send(
                XmsgMagicNumber destination,
                XmsgMagicNumber sender,
                ReadOnlySpan<byte> userData,
                XmsgSendFlags flags)
            {
                Destinations.Add(destination);
                Flags.Add(flags);
                return XmsgStatus.Completed;
            }
        }
    }
}
