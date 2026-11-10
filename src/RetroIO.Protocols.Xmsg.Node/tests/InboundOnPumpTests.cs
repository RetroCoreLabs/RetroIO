//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading.Tasks;

using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Node.Seam;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// <see cref="XmsgNodeHost.DeliverInboundOnPump"/>: payloads queued by the link's thread and
    /// processed on the pump thread.
    /// </summary>
    /// <remarks>
    /// The setting exists because an Ethernet backend delivers payloads on its own receive thread
    /// while the caller's loop runs <see cref="XmsgNodeHost.Pump"/>, and the node's state has no
    /// lock. These tests use the captured reachability request from
    /// <c>NodeHostReachabilityTests</c>, because it gets a one-frame, byte-exact reply.
    /// </remarks>
    public sealed class InboundOnPumpTests
    {
        // Captured request 100 -> 102 and our captured reply 102 -> 100.
        private const string Request = "2113001900660064FFFF0001DE08";
        private const string Reply = "2113001300640066FFFF0001DE0E";

        [Fact]
        public void WithTheSettingOff_ARequestIsAnsweredAtOnce()
        {
            FakeLink link = new FakeLink("fake:off");
            XmsgNodeHost host = BuildHost(link);

            link.RaisePayload(Convert.FromHexString(Request));

            Assert.Single(link.Sent);
            Assert.Equal(0, host.PendingInboundCount);
        }

        [Fact]
        public void WithTheSettingOn_NothingIsAnsweredUntilPump()
        {
            FakeLink link = new FakeLink("fake:on");
            XmsgNodeHost host = BuildHost(link);
            host.DeliverInboundOnPump = true;

            link.RaisePayload(Convert.FromHexString(Request));

            Assert.Empty(link.Sent);
            Assert.Equal(1, host.PendingInboundCount);

            host.Pump();

            Assert.Equal(0, host.PendingInboundCount);
            Assert.Single(link.Sent);
            Assert.Equal(Convert.FromHexString(Reply), link.Sent[0]);
        }

        [Fact]
        public void TheQueuedPayloadIsACopy_SoTheLinkMayReuseItsBuffer()
        {
            FakeLink link = new FakeLink("fake:copy");
            XmsgNodeHost host = BuildHost(link);
            host.DeliverInboundOnPump = true;

            byte[] buffer = Convert.FromHexString(Request);
            link.RaisePayload(buffer);

            // A real link reuses one receive buffer. Wipe it before the pump runs: if the node
            // kept a reference instead of a copy, it would now answer garbage or nothing.
            Array.Clear(buffer, 0, buffer.Length);

            host.Pump();

            Assert.Single(link.Sent);
            Assert.Equal(Convert.FromHexString(Reply), link.Sent[0]);
        }

        [Fact]
        public async Task PayloadsDeliveredFromAnotherThread_AreAllAnsweredOnThePumpThread()
        {
            const int Count = 50;
            FakeLink link = new FakeLink("fake:thread");
            XmsgNodeHost host = BuildHost(link);
            host.DeliverInboundOnPump = true;

            await Task.Run(() =>
            {
                for (int i = 0; i < Count; i++)
                {
                    link.RaisePayload(Convert.FromHexString(Request));
                }
            });

            // Nothing was processed on the delivering thread.
            Assert.Empty(link.Sent);

            host.Pump();

            Assert.Equal(Count, link.Sent.Count);
            for (int i = 0; i < Count; i++)
            {
                Assert.Equal(Convert.FromHexString(Reply), link.Sent[i]);
            }
        }

        private static XmsgNodeHost BuildHost(FakeLink link)
        {
            RoutingTableEntry[] routes = new RoutingTableEntry[]
            {
                new RoutingTableEntry(100, XroutConnectionType.Neighbour, 1, 1, 0),
            };

            return new XmsgNodeHost(link, 102, routes, new NullResponderSequenceStore());
        }
    }
}
