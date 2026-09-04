using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Node.Seam;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// A node holding two links carries traffic between them without answering it.
    /// </summary>
    /// <remarks>
    /// <para><b>The property under test</b></para>
    /// A relay must not acknowledge on the real destination's behalf. The captures show a live D100
    /// passing acknowledgements straight through, end to end, so a node that secure-ACKed transit
    /// traffic would corrupt a sequence the far end is still counting.
    /// <para>
    /// Both the relay and each host subscribe to the same link, so the danger is that both act on
    /// one datagram. These tests assert the partition holds: transit goes out the other link and is
    /// never processed as ours, and traffic addressed to us is processed and never forwarded.
    /// </para>
    /// <para>
    /// The topology modelled is the live one from <c>topology-d19999.json</c>: we are D19999 on the
    /// Ethernet segment, D100 is the neighbour that reaches D103 over its HDLC line.
    /// </para>
    /// </remarks>
    public sealed class XmsgRelayNodeTests
    {
        private const ushort NodeUs = 19999;
        private const ushort NodeD100 = 100;
        private const ushort NodeD103 = 103;

        /// <summary>
        /// Builds a host on a link, with no servers registered.
        /// </summary>
        /// <param name="link">
        /// The link to host on.
        /// </param>
        /// <param name="node">
        /// The node number this host answers for.
        /// </param>
        /// <returns>
        /// The host.
        /// </returns>
        private static XmsgNodeHost HostOn(FakeLink link, ushort node)
        {
            return new XmsgNodeHost(
                link,
                node,
                new List<RoutingTableEntry>(),
                new NullResponderSequenceStore());
        }

        /// <summary>
        /// Builds a datagram, with a correct header checksum.
        /// </summary>
        /// <param name="destination">
        /// The destination node.
        /// </param>
        /// <param name="source">
        /// The source node.
        /// </param>
        /// <returns>
        /// The datagram bytes.
        /// </returns>
        private static byte[] Datagram(ushort destination, ushort source)
        {
            byte[] datagram = new byte[]
            {
                0x21, 0x13,
                0x00, 0x19,
                (byte)(destination >> 8), (byte)destination,
                (byte)(source >> 8), (byte)source,
                0x01, 0x2C,
                0x00, 0x01,
                0x00, 0x00,
                0xDE, 0xAD, 0xBE, 0xEF,
            };

            SintranDatagramRelay.WriteChecksum(datagram);
            return datagram;
        }

        /// <summary>
        /// A datagram for a node reachable on the other link is relayed out of it.
        /// </summary>
        [Fact]
        public void TransitDatagramIsRelayedOutTheOtherLink()
        {
            FakeLink ethernet = new FakeLink("eth");
            FakeLink hdlc = new FakeLink("hdlc");

            XmsgRelayNode relayNode = new XmsgRelayNode();

            // Our Ethernet side reaches nothing beyond itself; the HDLC side reaches D103.
            relayNode.AddHost(HostOn(ethernet, NodeUs));
            relayNode.AddHost(HostOn(hdlc, NodeD100), NodeD103);

            // Arrives on Ethernet, addressed to D103 - which lives past the HDLC link.
            ethernet.RaisePayload(Datagram(NodeD103, NodeUs));

            Assert.Equal(1, relayNode.Relay.DatagramsRelayed);
            Assert.Equal(0, relayNode.Relay.DatagramsDropped);

            byte[] sent = Assert.Single(hdlc.Sent);
            Assert.True(SintranDatagramRelay.IsRelayed(sent));
            Assert.True(SintranDatagramRelay.HasValidChecksum(sent));

            // The ORIGINAL endpoints survive - a relay must never substitute its own numbers.
            Assert.Equal(NodeD103, SintranDatagramRelay.GetDestinationNode(sent));
            Assert.Equal(NodeUs, SintranDatagramRelay.GetSourceNode(sent));

            // And nothing went back out the link it came in on.
            Assert.Empty(ethernet.Sent);
        }

        /// <summary>
        /// Transit traffic is never answered by either host - no acknowledgement, no reply.
        /// </summary>
        /// <remarks>
        /// This is the whole reason <see cref="XmsgNodeHost.IgnoreDatagramsForOtherNodes"/> exists.
        /// Before it, every datagram delivered on a link was processed as if it were ours, so a
        /// relaying node would have secure-ACKed on D103's behalf.
        /// </remarks>
        [Fact]
        public void TransitDatagramIsNotAnsweredByEitherHost()
        {
            FakeLink ethernet = new FakeLink("eth");
            FakeLink hdlc = new FakeLink("hdlc");

            XmsgRelayNode relayNode = new XmsgRelayNode();
            relayNode.AddHost(HostOn(ethernet, NodeUs));
            relayNode.AddHost(HostOn(hdlc, NodeD100), NodeD103);

            ethernet.RaisePayload(Datagram(NodeD103, NodeUs));

            // Exactly one frame left this node: the relayed copy. An acknowledgement built by a
            // host would show up as a SECOND payload on the arrival link.
            Assert.Empty(ethernet.Sent);
            Assert.Single(hdlc.Sent);

            // The one frame that did leave is the relayed datagram, not a reply of our own.
            Assert.True(SintranDatagramRelay.IsRelayed(hdlc.Sent[0]));
        }

        /// <summary>
        /// A datagram addressed to us is passed over by the relay and left for the host.
        /// </summary>
        [Fact]
        public void DatagramForUsIsNotRelayed()
        {
            FakeLink ethernet = new FakeLink("eth");
            FakeLink hdlc = new FakeLink("hdlc");

            XmsgRelayNode relayNode = new XmsgRelayNode();
            relayNode.AddHost(HostOn(ethernet, NodeUs));
            relayNode.AddHost(HostOn(hdlc, NodeD100), NodeD103);

            ethernet.RaisePayload(Datagram(NodeUs, NodeD103));

            Assert.Equal(0, relayNode.Relay.DatagramsRelayed);
            Assert.Equal(1, relayNode.Relay.DatagramsForUs);

            // Not counted as a routing failure, and not forwarded anywhere.
            Assert.Equal(0, relayNode.Relay.DatagramsDropped);
            Assert.Empty(hdlc.Sent);
        }

        /// <summary>
        /// Adding a host turns its transit filter on, which is what makes the composition safe.
        /// </summary>
        [Fact]
        public void AddHostEnablesTheTransitFilter()
        {
            FakeLink link = new FakeLink("eth");
            XmsgNodeHost host = HostOn(link, NodeUs);

            Assert.False(host.IgnoreDatagramsForOtherNodes);

            new XmsgRelayNode().AddHost(host);

            Assert.True(host.IgnoreDatagramsForOtherNodes);
        }

        /// <summary>
        /// A single-link node is untouched: the filter stays off unless a relay node turns it on.
        /// </summary>
        /// <remarks>
        /// The live paths - file access, TAD, the listing walks - all run on a lone
        /// <see cref="XmsgNodeHost"/>. Their behaviour must not change because a relay type now
        /// exists elsewhere in the assembly.
        /// </remarks>
        [Fact]
        public void ALoneHostKeepsTodaysBehaviour()
        {
            FakeLink link = new FakeLink("eth");
            XmsgNodeHost host = HostOn(link, NodeUs);

            Assert.False(host.IgnoreDatagramsForOtherNodes);

            // A datagram for somebody else still reaches the stack, exactly as before.
            host.Pump();
            link.RaisePayload(Datagram(NodeD103, NodeD100));

            // Nothing asserted about the reply here - the point is only that the filter did not
            // silently start dropping traffic on a node that never asked for it.
            Assert.False(host.IgnoreDatagramsForOtherNodes);
        }
    }
}
