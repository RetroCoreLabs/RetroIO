using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Ethernet;
using RetroIO.Protocols.Xmsg.Live.Seam;
using RetroIO.Protocols.Xmsg.Node.Seam;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Live.Tests
{
    /// <summary>
    /// Gate for <see cref="EthernetLink"/>: the Ethernet half of the <see cref="ILink"/> seam
    /// carries a SINTRAN datagram up and down over an in-process segment, and stays bound to the
    /// one peer it was created for even when other stations are broadcasting on the same segment.
    /// </summary>
    /// <remarks>
    /// The topology modelled here is the live one: D19999 (the C# node) and D100 and D102 all on
    /// one Ethernet segment, with D103 reached over HDLC via D100 and therefore not present here.
    /// </remarks>
    public sealed class EthernetLinkTests
    {
        private const ushort LocalSystem = 19999;   // D19999, the C# node
        private const ushort PeerSystem = 100;      // D100, the node it is bound to
        private const ushort OtherSystem = 102;     // D102, a third station on the same segment

        // A SINTRAN reachability datagram: 7 words, marker 0x2113, word 6 the ones-complement
        // checksum. Used purely as opaque bytes - the link must not parse or alter it.
        private static readonly byte[] SintranDatagram =
            Convert.FromHexString("2113001900660064FFFF0001DE08");

        /// <summary>
        /// The MAC is built from the system number stored in REVERSED byte order, which is the
        /// opposite of the SINTRAN header's big-endian node field. Every node captured so far was
        /// numbered under 256, so the reversal was invisible; 19999 = 0x4E1F makes it visible, and
        /// this test is what stops the two representations being confused.
        /// </summary>
        [Fact]
        public void LocalMac_ReversesTheSystemNumberBytes()
        {
            using EthernetLink link = CreateLink(out InProcessEthernetSegment _, out IEthernetBackend _);

            Span<byte> mac = stackalloc byte[NdMacAddress.Length];
            link.LocalMac.Write(mac);

            // 08 00 26 | 1F 4E (19999 = 0x4E1F reversed) | 00 physical user
            Assert.Equal(new byte[] { 0x08, 0x00, 0x26, 0x1F, 0x4E, 0x00 }, mac.ToArray());
        }

        /// <summary>
        /// Peer MAC derivation for node 100, cross-checked against the captured frame in
        /// COSMOS-ETHERNET-TRANSPORT-FRAMING-2026-08-01.md, which shows node 100 as
        /// <c>08 00 26 64 00 00</c>.
        /// </summary>
        [Fact]
        public void PeerMac_MatchesTheCapturedAddressForNode100()
        {
            using EthernetLink link = CreateLink(out InProcessEthernetSegment _, out IEthernetBackend _);

            Span<byte> mac = stackalloc byte[NdMacAddress.Length];
            link.PeerMac.Write(mac);

            Assert.Equal(new byte[] { 0x08, 0x00, 0x26, 0x64, 0x00, 0x00 }, mac.ToArray());
        }

        /// <summary>
        /// A started link is Starting, not Active: the segment is up but the peer's link id is not
        /// known until the peer speaks, so there is nothing this node can legally address.
        /// </summary>
        [Fact]
        public void Start_IsStartingAndRefusesToSendUntilThePeerIsHeard()
        {
            using EthernetLink link = CreateLink(out InProcessEthernetSegment _, out IEthernetBackend _);

            Assert.Equal(LinkStatus.Stopped, link.Status);
            link.Start();

            Assert.Equal(LinkStatus.Starting, link.Status);
            Assert.False(link.HasLearnedPeer);
            Assert.False(link.SendData(SintranDatagram));
        }

        /// <summary>
        /// The peer's first data frame makes the link usable: the datagram surfaces up byte-exact,
        /// the status goes Active, and an acknowledgement goes back carrying the received sequence
        /// PLUS ONE.
        /// </summary>
        [Fact]
        public void PeerDataFrame_DeliversPayloadUp_GoesActive_AndIsAcknowledged()
        {
            using EthernetLink link = CreateLink(
                out InProcessEthernetSegment segment, out IEthernetBackend _);

            List<byte[]> received = new List<byte[]>();
            link.PayloadReceived += (_, payload, length) =>
            {
                byte[] copy = new byte[length];
                Array.Copy(payload, copy, length);
                received.Add(copy);
            };

            List<LinkStatus> transitions = new List<LinkStatus>();
            link.StatusChanged += (_, _, next, _) => transitions.Add(next);

            link.Start();

            // A separate port stands in for D100 and injects a raw data frame. It has to be raw:
            // an EthernetLink on the far side would itself be waiting to learn ITS peer, so two
            // C# links can never open a conversation with each other. On the live segment the ND
            // node is the one that speaks first.
            SpyPort peer = new SpyPort(segment, "d100");
            peer.SendDataFrame(PeerSystem, LocalSystem, sequence: 0x07, SintranDatagram);

            Assert.True(link.HasLearnedPeer);
            Assert.Equal(LinkStatus.Active, link.Status);
            Assert.Contains(LinkStatus.Active, transitions);

            byte[] datagram = Assert.Single(received);
            Assert.Equal(SintranDatagram, datagram);

            // The acknowledgement: kind 0x3F, sequence received + 1.
            byte[] ack = Assert.Single(peer.Frames);
            Assert.True(NdLinkHeader.TryParse(
                new ReadOnlySpan<byte>(ack, Ieee8023Frame.PayloadOffset, ack.Length - Ieee8023Frame.PayloadOffset),
                out NdLinkHeader header));
            Assert.True(header.IsAcknowledge);
            Assert.Equal(0x08, header.Sequence);
        }

        /// <summary>
        /// A reply sent from INSIDE the payload handler for the peer's very first datagram must go
        /// out. This is the answered-or-not gate.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The defect this pins down, MEASURED against a live SINTRAN on 2026-08-04: the link
        /// delivered the datagram upward and only went Active after the whole frame had been
        /// handled, so the reply the datagram provoked was refused by
        /// <see cref="EthernetLink.SendData"/> for a link that still read
        /// <see cref="LinkStatus.Starting"/>, and was then discarded without a trace.
        /// </para>
        /// <para>
        /// It only bit when the peer's FIRST frame to us was data rather than a connection request -
        /// that is, whenever the ND machine still had the connection open from a previous run of
        /// this node. The existing tests all missed it because they check the status and the reply
        /// AFTER the handler has returned, by which time the status is Active either way. The
        /// question this asks is what the status was DURING the handler.
        /// </para>
        /// <para>
        /// The consequence on the live machine was severe: a request that gets no answer hangs the
        /// calling SINTRAN terminal, and ESC will not abort it.
        /// </para>
        /// </remarks>
        [Fact]
        public void ReplyToPeersFirstDatagram_IsSent_NotRefused()
        {
            using EthernetLink link = CreateLink(
                out InProcessEthernetSegment segment, out IEthernetBackend _);

            link.Start();

            // Answer from inside the handler, exactly as the node stack does: the datagram arrives,
            // is dispatched, and its reply is handed straight back down.
            bool sendAccepted = false;
            LinkStatus statusDuringHandler = LinkStatus.Stopped;
            link.PayloadReceived += (_, _, _) =>
            {
                statusDuringHandler = link.Status;
                sendAccepted = link.SendData(SintranDatagram);
            };

            SpyPort peer = new SpyPort(segment, "d100");
            peer.SendDataFrame(PeerSystem, LocalSystem, sequence: 0x00, SintranDatagram);

            Assert.Equal(LinkStatus.Active, statusDuringHandler);
            Assert.True(sendAccepted, "the reply to the peer's first datagram was refused by the link");

            // Two frames went back: the acknowledgement, and the reply itself carrying the datagram.
            bool sawDataReply = false;
            for (int i = 0; i < peer.Frames.Count; i++)
            {
                byte[] frame = peer.Frames[i];
                if (NdLinkHeader.TryParse(
                        new ReadOnlySpan<byte>(
                            frame, Ieee8023Frame.PayloadOffset, frame.Length - Ieee8023Frame.PayloadOffset),
                        out NdLinkHeader header)
                    && header.IsData)
                {
                    sawDataReply = true;
                }
            }

            Assert.True(sawDataReply, "no data frame carrying the reply reached the segment");
        }

        /// <summary>
        /// The peer filter. A third station broadcasting on the shared segment must NOT be adopted
        /// as this link's peer.
        /// </summary>
        /// <remarks>
        /// This is the defect the filter exists to prevent: <see cref="NdLinkLayer"/> learns its
        /// peer from any frame addressed to this node, whoever sent it. D19999 is defined as a
        /// remote system on BOTH D100 and D102, so both of them address it directly - and without
        /// this filter a link bound to D100 would silently start addressing D102 instead.
        /// The frame injected below is unicast to D19999 for exactly that reason.
        /// </remarks>
        [Fact]
        public void FrameFromAnotherStation_IsIgnoredAndDoesNotStealThePeer()
        {
            using EthernetLink link = CreateLink(
                out InProcessEthernetSegment segment, out IEthernetBackend _);

            int payloads = 0;
            link.PayloadReceived += (_, _, _) => payloads++;
            link.Start();

            SpyPort other = new SpyPort(segment, "d102");
            other.SendDataFrame(OtherSystem, LocalSystem, sequence: 0x01, SintranDatagram);

            Assert.False(link.HasLearnedPeer);
            Assert.Equal(LinkStatus.Starting, link.Status);
            Assert.Equal(0, payloads);
            Assert.Equal(1, link.FramesFromOtherStations);

            // Nothing was sent back - not even an acknowledgement.
            Assert.Empty(other.Frames);
        }

        /// <summary>
        /// Once the peer is known, a datagram sent down reaches the peer byte-exact inside a data
        /// frame addressed to it, and the sequence advances.
        /// </summary>
        [Fact]
        public void SendData_ReachesThePeerOnceLearned()
        {
            using EthernetLink link = CreateLink(
                out InProcessEthernetSegment segment, out IEthernetBackend _);
            link.Start();

            SpyPort peer = new SpyPort(segment, "d100");
            peer.SendDataFrame(PeerSystem, LocalSystem, sequence: 0x01, SintranDatagram);
            peer.Frames.Clear();   // drop the acknowledgement of that first frame

            Assert.True(link.SendData(SintranDatagram));

            byte[] frame = Assert.Single(peer.Frames);
            Assert.True(Ieee8023Frame.TryParse(
                frame,
                out NdMacAddress destination,
                out NdMacAddress source,
                out int payloadOffset,
                out int payloadLength));

            Assert.True(destination.Equals(link.PeerMac));
            Assert.True(source.Equals(link.LocalMac));

            Assert.True(NdLinkHeader.TryParse(
                new ReadOnlySpan<byte>(frame, payloadOffset, payloadLength), out NdLinkHeader header));
            Assert.True(header.IsData);
            Assert.Equal(SintranDatagram.Length, header.PayloadLength);

            byte[] carried = new byte[header.PayloadLength];
            Array.Copy(frame, payloadOffset + NdLinkHeader.Length, carried, 0, carried.Length);
            Assert.Equal(SintranDatagram, carried);
        }

        /// <summary>
        /// Stop is idempotent, reports the transition once, and a frame arriving afterwards cannot
        /// resurrect the link.
        /// </summary>
        [Fact]
        public void Stop_IsIdempotentAndLateFramesDoNotResurrectTheLink()
        {
            using EthernetLink link = CreateLink(
                out InProcessEthernetSegment segment, out IEthernetBackend _);

            List<LinkStatus> transitions = new List<LinkStatus>();
            link.StatusChanged += (_, _, next, _) => transitions.Add(next);

            link.Start();
            link.Stop();
            link.Stop();

            Assert.Equal(LinkStatus.Stopped, link.Status);

            SpyPort peer = new SpyPort(segment, "d100");
            peer.SendDataFrame(PeerSystem, LocalSystem, sequence: 0x01, SintranDatagram);

            Assert.Equal(LinkStatus.Stopped, link.Status);

            // Starting, Stopping, Stopped - and no second Stopped from the repeated call.
            Assert.Equal(
                new[] { LinkStatus.Starting, LinkStatus.Stopping, LinkStatus.Stopped },
                transitions);
        }

        /// <summary>
        /// Builds a link for D19999 bound to D100 on a fresh in-process segment.
        /// </summary>
        /// <param name="segment">
        /// Receives the segment the link is attached to, so a test can attach further stations.
        /// </param>
        /// <param name="backend">
        /// Receives the link's own port.
        /// </param>
        /// <returns>
        /// The link, not yet started.
        /// </returns>
        private static EthernetLink CreateLink(
            out InProcessEthernetSegment segment, out IEthernetBackend backend)
        {
            segment = new InProcessEthernetSegment();
            backend = segment.CreatePort("d19999");
            return new EthernetLink("eth-d100", LocalSystem, PeerSystem, backend);
        }

        /// <summary>
        /// A station on the segment that records every frame it receives and can inject a raw ND
        /// data frame, standing in for a real ND node.
        /// </summary>
        private sealed class SpyPort
        {
            private readonly IEthernetBackend _backend;

            /// <summary>
            /// Initialises the station and attaches it to a segment.
            /// </summary>
            /// <param name="segment">
            /// The segment to attach to.
            /// </param>
            /// <param name="name">
            /// A short name for logs.
            /// </param>
            public SpyPort(InProcessEthernetSegment segment, string name)
            {
                _backend = segment.CreatePort(name);
                _backend.OnPacketReceived += (data, length) =>
                {
                    byte[] copy = new byte[length];
                    Array.Copy(data, copy, length);
                    Frames.Add(copy);
                };

                _backend.Start();
            }

            /// <summary>
            /// Gets the frames this station has received, in arrival order.
            /// </summary>
            public List<byte[]> Frames { get; } = new List<byte[]>();

            /// <summary>
            /// Injects one ND data frame onto the segment.
            /// </summary>
            /// <param name="fromSystem">
            /// The sending node's ND system number.
            /// </param>
            /// <param name="toSystem">
            /// The destination node's ND system number.
            /// </param>
            /// <param name="sequence">
            /// The link sequence number to stamp on the frame.
            /// </param>
            /// <param name="datagram">
            /// The SINTRAN datagram to carry.
            /// </param>
            public void SendDataFrame(
                ushort fromSystem, ushort toSystem, byte sequence, byte[] datagram)
            {
                // A link id the peer chose. Its origin is unknown and it is never derived - this is
                // an arbitrary stand-in for whatever a real node would put in the sender field.
                const ushort PeerLinkId = 0x0042;

                byte[] llcPayload = new byte[NdLinkHeader.Length + datagram.Length];
                NdLinkHeader
                    .Data(sequence, PeerLinkId, NdLinkLayer.UnknownPeerLinkId, (ushort)datagram.Length)
                    .Write(llcPayload);
                Array.Copy(datagram, 0, llcPayload, NdLinkHeader.Length, datagram.Length);

                byte[] frame = new byte[Ieee8023Frame.MinimumFrameLength + llcPayload.Length];
                int written = Ieee8023Frame.Build(
                    NdMacAddress.FromSystemNumber(toSystem),
                    NdMacAddress.FromSystemNumber(fromSystem),
                    llcPayload,
                    frame);

                _backend.SendPacket(frame, 0, written);
            }
        }
    }
}
