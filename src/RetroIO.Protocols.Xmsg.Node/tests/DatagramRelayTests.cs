//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Node.Seam;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Gate for route-through: the relay rule rewrites word 0 and word 6 and NOTHING else, and the
    /// router forwards a datagram out of the link that reaches its destination.
    /// </summary>
    /// <remarks>
    /// The topology modelled is the live one: this node is D100 in the middle, with D103 on an
    /// HDLC line and D19999 on the Ethernet segment. D103 cannot reach D19999 except through here.
    /// </remarks>
    public sealed class DatagramRelayTests
    {
        private const ushort NodeD103 = 103;
        private const ushort NodeD19999 = 19999;

        /// <summary>
        /// A datagram from D103 to D19999, with a correct header checksum in word 6.
        /// </summary>
        /// <returns>
        /// The datagram bytes.
        /// </returns>
        private static byte[] DatagramD103ToD19999()
        {
            byte[] datagram = new byte[]
            {
                0x21, 0x13,             // word 0 - markers, not yet relayed
                0x00, 0x19,             // word 1 - type 0x00, subtype 0x19
                0x4E, 0x1F,             // word 2 - destination 19999, BIG-endian
                0x00, 0x67,             // word 3 - source 103
                0x01, 0x2C,             // word 4 - Flags 1
                0x00, 0x01,             // word 5 - Flags 2
                0x00, 0x00,             // word 6 - checksum, filled in below
                0xDE, 0xAD, 0xBE, 0xEF  // body
            };

            SintranDatagramRelay.WriteChecksum(datagram);
            return datagram;
        }

        /// <summary>
        /// The relay rule in full: Marker 2 goes 0x13 -> 0x12, the checksum is recomputed, and
        /// every other byte - endpoints, Flags 1, Flags 2, subtype, body - is untouched.
        /// </summary>
        [Fact]
        public void MakeRelayed_ChangesOnlyMarker2AndTheChecksum()
        {
            byte[] original = DatagramD103ToD19999();
            byte[] relayed = (byte[])original.Clone();

            Assert.True(SintranDatagramRelay.MakeRelayed(relayed));

            // Byte 1 is a decrementing HOP COUNT, not a marker: relaying once takes it from
            // InitialHopCount to InitialHopCount-1, which is one hop taken.
            Assert.Equal(SintranDatagramRelay.InitialHopCount - 1, relayed[1]);
            Assert.Equal(1, SintranDatagramRelay.HopsTaken(relayed));
            Assert.True(SintranDatagramRelay.IsRelayed(relayed));
            Assert.True(SintranDatagramRelay.HasValidChecksum(relayed));

            // Byte for byte, only offsets 1, 12 and 13 may differ.
            Assert.Equal(original.Length, relayed.Length);
            for (int i = 0; i < original.Length; i++)
            {
                if (i == 1 || i == 12 || i == 13)
                {
                    continue;
                }

                Assert.True(
                    original[i] == relayed[i],
                    $"byte {i} changed: a relay must rewrite word 0 and word 6 only");
            }
        }

        /// <summary>
        /// The checksum genuinely changes when the marker does - i.e. the recompute is not a no-op
        /// that merely looks right because the old value was copied through.
        /// </summary>
        [Fact]
        public void MakeRelayed_ActuallyRecomputesTheChecksum()
        {
            byte[] original = DatagramD103ToD19999();
            byte[] relayed = (byte[])original.Clone();
            SintranDatagramRelay.MakeRelayed(relayed);

            ushort before = (ushort)((original[12] << 8) | original[13]);
            ushort after = (ushort)((relayed[12] << 8) | relayed[13]);

            Assert.NotEqual(before, after);

            // Marker 2 fell by 1 (0x13 -> 0x12), so the ones-complement sum falls by 1 and the
            // complement rises by 1. Anything else means the recompute did not run over the header.
            Assert.Equal((ushort)(before + 1), after);
        }

        /// <summary>
        /// Flags 1 is NOT renumbered by a relay: the sequence belongs to the originating link and
        /// acknowledgements are end-to-end.
        /// </summary>
        [Fact]
        public void MakeRelayed_LeavesFlags1Alone()
        {
            byte[] relayed = DatagramD103ToD19999();
            SintranDatagramRelay.MakeRelayed(relayed);

            Assert.Equal(0x01, relayed[8]);
            Assert.Equal(0x2C, relayed[9]);
        }

        /// <summary>
        /// A datagram that does not open with Marker 1, or is shorter than the 14-byte header, is
        /// left untouched rather than half-rewritten.
        /// </summary>
        [Fact]
        public void MakeRelayed_RefusesMalformedInput()
        {
            byte[] tooShort = new byte[] { 0x21, 0x13, 0x00, 0x19 };
            Assert.False(SintranDatagramRelay.MakeRelayed(tooShort));
            Assert.Equal(0x13, tooShort[1]);

            byte[] wrongMarker = DatagramD103ToD19999();
            wrongMarker[0] = 0x22;
            Assert.False(SintranDatagramRelay.MakeRelayed(wrongMarker));
            Assert.Equal(0x13, wrongMarker[1]);
        }

        /// <summary>
        /// The header is SEVEN words: a 14-byte datagram is the shortest well-formed one, and 13
        /// bytes is not enough. This is the off-by-one the superseded model carried.
        /// </summary>
        [Fact]
        public void HeaderIsSevenWords_ThirteenBytesIsNotEnough()
        {
            Assert.Equal(14, SintranDatagramRelay.HeaderSize);

            byte[] thirteen = new byte[13];
            thirteen[0] = 0x21;
            thirteen[1] = 0x13;
            Assert.False(SintranDatagramRelay.MakeRelayed(thirteen));
        }

        /// <summary>
        /// The routing decision: a datagram from D103 addressed to D19999 goes out of the Ethernet
        /// link, relayed, and never back down the link it came from.
        /// </summary>
        [Fact]
        public void Route_ForwardsToTheLinkThatReachesTheDestination()
        {
            FakeLink hdlcToD103 = new FakeLink("hdlc-d103");
            FakeLink ethToD19999 = new FakeLink("eth-d19999");

            DatagramRelay relay = new DatagramRelay();
            relay.AddLink(hdlcToD103, NodeD103);
            relay.AddLink(ethToD19999, NodeD19999);

            List<ushort> forwarded = new List<ushort>();
            relay.Relayed += (_, _, destination) => forwarded.Add(destination);

            byte[] datagram = DatagramD103ToD19999();
            Assert.True(relay.Route(hdlcToD103, datagram, datagram.Length));

            Assert.Equal(1, relay.DatagramsRelayed);
            Assert.Equal(new ushort[] { NodeD19999 }, forwarded);

            // It went out of the Ethernet link only.
            Assert.Empty(hdlcToD103.Sent);
            byte[] sent = Assert.Single(ethToD19999.Sent);

            Assert.True(SintranDatagramRelay.IsRelayed(sent));
            Assert.True(SintranDatagramRelay.HasValidChecksum(sent));
            Assert.Equal(NodeD19999, SintranDatagramRelay.GetDestinationNode(sent));
            Assert.Equal(NodeD103, SintranDatagramRelay.GetSourceNode(sent));
        }

        /// <summary>
        /// A datagram delivered up by a registered link is forwarded automatically, without the
        /// caller driving <see cref="DatagramRelay.Route"/> by hand.
        /// </summary>
        [Fact]
        public void PayloadArrivingOnALink_IsForwardedAutomatically()
        {
            FakeLink hdlcToD103 = new FakeLink("hdlc-d103");
            FakeLink ethToD19999 = new FakeLink("eth-d19999");

            DatagramRelay relay = new DatagramRelay();
            relay.AddLink(hdlcToD103, NodeD103);
            relay.AddLink(ethToD19999, NodeD19999);

            byte[] datagram = DatagramD103ToD19999();
            hdlcToD103.RaisePayload(datagram);

            Assert.Equal(1, relay.DatagramsRelayed);
            Assert.Single(ethToD19999.Sent);
        }

        /// <summary>
        /// With no route for the destination the datagram is dropped and reported, NOT flooded out
        /// of every other link - no reachability protocol has been established, so flooding would
        /// be a guess.
        /// </summary>
        [Fact]
        public void Route_DropsWhenNoRouteIsKnown_AndDoesNotFlood()
        {
            FakeLink hdlcToD103 = new FakeLink("hdlc-d103");
            FakeLink ethToD102 = new FakeLink("eth-d102");

            DatagramRelay relay = new DatagramRelay();
            relay.AddLink(hdlcToD103, NodeD103);
            relay.AddLink(ethToD102, 102);

            List<string> reasons = new List<string>();
            relay.NotRelayed += (_, _, reason) => reasons.Add(reason);

            byte[] datagram = DatagramD103ToD19999();   // addressed to 19999, which has no route
            Assert.False(relay.Route(hdlcToD103, datagram, datagram.Length));

            Assert.Equal(0, relay.DatagramsRelayed);
            Assert.Equal(1, relay.DatagramsDropped);
            Assert.Equal(new[] { "no route" }, reasons);

            Assert.Empty(hdlcToD103.Sent);
            Assert.Empty(ethToD102.Sent);
        }

        /// <summary>
        /// A datagram addressed to one of OUR node numbers is passed over, not counted as a drop.
        /// </summary>
        /// <remarks>
        /// This is the normal case on a shared link: most of what arrives is for us, and the node
        /// host beside the relay answers it. Counting each one as an unroutable drop would bury a
        /// genuine topology mistake under thousands of false ones, so it has its own counter.
        /// </remarks>
        [Fact]
        public void Route_PassesOverDatagramsAddressedToUs()
        {
            FakeLink hdlcToD103 = new FakeLink("hdlc-d103");
            FakeLink ethToD19999 = new FakeLink("eth-d19999");

            DatagramRelay relay = new DatagramRelay();
            relay.AddLink(hdlcToD103, NodeD103);
            relay.AddLink(ethToD19999, NodeD19999);

            // We ARE 19999 in this run, so traffic for it is ours to answer, not to forward.
            relay.AddLocalNode(NodeD19999);

            List<string> reasons = new List<string>();
            relay.NotRelayed += (_, _, reason) => reasons.Add(reason);

            byte[] datagram = DatagramD103ToD19999();
            Assert.False(relay.Route(hdlcToD103, datagram, datagram.Length));

            Assert.Equal(1, relay.DatagramsForUs);
            Assert.Equal(0, relay.DatagramsDropped);
            Assert.Equal(0, relay.DatagramsRelayed);

            // Nothing reported as a failure, and nothing sent anywhere.
            Assert.Empty(reasons);
            Assert.Empty(hdlcToD103.Sent);
            Assert.Empty(ethToD19999.Sent);
        }

        /// <summary>
        /// Being a local node wins over having a route, so we never forward our own traffic away.
        /// </summary>
        /// <remarks>
        /// The route for 19999 points at a link here AND 19999 is declared local. If the route won,
        /// a datagram meant for this process would be sent out onto the wire instead of answered -
        /// which would look, from the far end, like the node had gone silent.
        /// </remarks>
        [Fact]
        public void Route_LocalNodeWinsOverAMatchingRoute()
        {
            FakeLink hdlcToD103 = new FakeLink("hdlc-d103");
            FakeLink ethToD19999 = new FakeLink("eth-d19999");

            DatagramRelay relay = new DatagramRelay();
            relay.AddLink(hdlcToD103, NodeD103);
            relay.AddLink(ethToD19999, NodeD19999);
            relay.AddLocalNode(NodeD19999);

            byte[] datagram = DatagramD103ToD19999();
            hdlcToD103.RaisePayload(datagram);

            Assert.Equal(0, relay.DatagramsRelayed);
            Assert.Equal(1, relay.DatagramsForUs);
            Assert.Empty(ethToD19999.Sent);
        }

        /// <summary>
        /// A datagram is never sent back out of the link it arrived on, which would be a routing
        /// loop.
        /// </summary>
        [Fact]
        public void Route_NeverSendsBackOutTheArrivalLink()
        {
            FakeLink link = new FakeLink("eth");

            DatagramRelay relay = new DatagramRelay();
            relay.AddLink(link, NodeD19999);

            byte[] datagram = DatagramD103ToD19999();
            Assert.False(relay.Route(link, datagram, datagram.Length));

            Assert.Empty(link.Sent);
            Assert.Equal(1, relay.DatagramsDropped);
        }

        /// <summary>
        /// Removing a link stops it being forwarded to and stops its arrivals being routed.
        /// </summary>
        [Fact]
        public void RemoveLink_DropsItsRoutesAndUnsubscribes()
        {
            FakeLink hdlcToD103 = new FakeLink("hdlc-d103");
            FakeLink ethToD19999 = new FakeLink("eth-d19999");

            DatagramRelay relay = new DatagramRelay();
            relay.AddLink(hdlcToD103, NodeD103);
            relay.AddLink(ethToD19999, NodeD19999);

            relay.RemoveLink(ethToD19999);

            byte[] datagram = DatagramD103ToD19999();
            hdlcToD103.RaisePayload(datagram);

            Assert.Equal(0, relay.DatagramsRelayed);
            Assert.Empty(ethToD19999.Sent);

            // And arrivals on a removed link are no longer routed at all.
            relay.RemoveLink(hdlcToD103);
            long droppedBefore = relay.DatagramsDropped;
            hdlcToD103.RaisePayload(datagram);
            Assert.Equal(droppedBefore, relay.DatagramsDropped);
        }

        // The ILink double these tests use now lives in FakeLink.cs, shared with the relay-node
        // tests. Its remarks record why FakeLinkAcceptanceTests deliberately keeps a different one.
    }
}
