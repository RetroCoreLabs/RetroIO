//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Ethernet;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Ethernet.Tests
{
    /// <summary>
    /// Tests the ND link layer: peer learning, the acknowledgement rule, sequencing, and the
    /// self-frame guard.
    /// </summary>
    public sealed class NdLinkLayerTests
    {
        private const ushort LocalSystem = 9999;
        private const ushort PeerSystem = 100;
        private const ushort LocalLinkId = 0x4242;
        private const ushort PeerLinkId = 0x59C1;

        /// <summary>
        /// Collects frames a layer hands to its transport.
        /// </summary>
        private sealed class FrameSink
        {
            /// <summary>
            /// Every frame sent, each copied out of the shared buffer.
            /// </summary>
            public List<byte[]> Frames { get; } = new List<byte[]>();

            /// <summary>
            /// Records one frame.
            /// </summary>
            /// <param name="data">
            /// The buffer holding the frame.
            /// </param>
            /// <param name="length">
            /// The number of valid bytes.
            /// </param>
            public void Send(byte[] data, int length)
            {
                byte[] copy = new byte[length];
                Array.Copy(data, copy, length);
                Frames.Add(copy);
            }
        }

        /// <summary>
        /// Builds a data frame as the peer would send it.
        /// </summary>
        /// <param name="sequence">
        /// The sequence number to use.
        /// </param>
        /// <param name="datagram">
        /// The datagram to carry.
        /// </param>
        /// <returns>
        /// A complete Ethernet frame from the peer to the local node.
        /// </returns>
        private static byte[] BuildPeerDataFrame(byte sequence, byte[] datagram)
        {
            byte[] llcPayload = new byte[NdLinkHeader.Length + datagram.Length];
            // This frame comes FROM the peer, so the first id field is OUR reference (the
            // destination) and the second is the peer's own. That order is not what the builder's
            // parameter names suggest; it is what D100's own NPDU trace shows on the wire, where a
            // connection request carries zero in the first field and the sender's reference in the
            // second. The fixture used to fill them the other way round, which meant these tests
            // agreed with the parser only because both were wrong in the same direction.
            NdLinkHeader.Data(sequence, LocalLinkId, PeerLinkId, (ushort)datagram.Length).Write(llcPayload);
            Array.Copy(datagram, 0, llcPayload, NdLinkHeader.Length, datagram.Length);

            byte[] buffer = new byte[Ieee8023Frame.PayloadOffset + llcPayload.Length + Ieee8023Frame.MinimumFrameLength];
            int written = Ieee8023Frame.Build(
                NdMacAddress.FromSystemNumber(LocalSystem),
                NdMacAddress.FromSystemNumber(PeerSystem),
                llcPayload,
                buffer);

            byte[] frame = new byte[written];
            Array.Copy(buffer, frame, written);
            return frame;
        }

        /// <summary>
        /// A received data frame is acknowledged with the received sequence plus one, and the
        /// acknowledgement carries no payload.
        /// </summary>
        [Fact]
        public void ReceivedDataFrameIsAcknowledgedWithSequencePlusOne()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            byte[] datagram = new byte[] { 0x21, 0x13, 0x00, 0x0E };
            Assert.True(layer.HandleFrame(BuildPeerDataFrame(0x43, datagram), BuildPeerDataFrame(0x43, datagram).Length));

            Assert.Single(sink.Frames);
            byte[] ack = sink.Frames[0];

            Assert.True(NdLinkHeader.TryParse(ack.AsSpan(Ieee8023Frame.PayloadOffset), out NdLinkHeader header));
            Assert.True(header.IsAcknowledge);
            Assert.Equal(0x44, header.Sequence);
            Assert.Equal(0, header.PayloadLength);
            // First id field is the DESTINATION's reference, second is ours - the property names
            // are back to front, see the note in the fixture's frame builder.
            Assert.Equal(PeerLinkId, header.SenderLinkId);
            Assert.Equal(LocalLinkId, header.ReceiverLinkId);
        }

        /// <summary>
        /// Builds a disconnect request as the peer would send it.
        /// </summary>
        /// <returns>
        /// A complete Ethernet frame from the peer to the local node.
        /// </returns>
        /// <remarks>
        /// <c>0x6F</c>, disconnect BY NETWORK SERVICE, which is the only one of the two disconnect
        /// kinds ever seen on the wire - D100 answered a connection request of ours with exactly
        /// this on 2026-08-11 (<c>0B02 6F 00 26 0001 0000 0105</c>) and again on 2026-08-17.
        /// </remarks>
        private static byte[] BuildPeerDisconnectFrame()
        {
            byte[] llcPayload = new byte[NdLinkHeader.Length];
            new NdLinkHeader(
                (byte)NdLinkFrameKind.DisconnectRequestByNetworkService,
                0x00, LocalLinkId, PeerLinkId, 0).Write(llcPayload);

            byte[] buffer = new byte[Ieee8023Frame.PayloadOffset + llcPayload.Length + Ieee8023Frame.MinimumFrameLength];
            int written = Ieee8023Frame.Build(
                NdMacAddress.FromSystemNumber(LocalSystem),
                NdMacAddress.FromSystemNumber(PeerSystem),
                llcPayload,
                buffer);

            byte[] frame = new byte[written];
            Array.Copy(buffer, frame, written);
            return frame;
        }

        /// <summary>
        /// Builds an acknowledgement frame as the peer would send it.
        /// </summary>
        /// <param name="nextExpected">
        /// The sequence the peer says it expects from us next.
        /// </param>
        /// <returns>
        /// A complete Ethernet frame from the peer to the local node.
        /// </returns>
        private static byte[] BuildPeerAckFrame(byte nextExpected)
        {
            byte[] llcPayload = new byte[NdLinkHeader.Length];
            // Same field order as the peer's data frames - our reference first, the peer's second.
            new NdLinkHeader(
                (byte)NdLinkFrameKind.Acknowledge, nextExpected, LocalLinkId, PeerLinkId, 0).Write(llcPayload);

            byte[] buffer = new byte[Ieee8023Frame.PayloadOffset + llcPayload.Length + Ieee8023Frame.MinimumFrameLength];
            int written = Ieee8023Frame.Build(
                NdMacAddress.FromSystemNumber(LocalSystem),
                NdMacAddress.FromSystemNumber(PeerSystem),
                llcPayload,
                buffer);

            byte[] frame = new byte[written];
            Array.Copy(buffer, frame, written);
            return frame;
        }

        /// <summary>
        /// Brings a layer to the state a link is in once the peer has placed us: peer known, its
        /// position learned and in step, sink emptied.
        /// </summary>
        /// <param name="layer">
        /// The layer to bring up.
        /// </param>
        /// <param name="sink">
        /// Its frame sink, which is cleared.
        /// </param>
        /// <remarks>
        /// Only ONE frame goes out before the peer's first acknowledgement - see
        /// <see cref="NdLinkLayer.UnpositionedWindow"/> and the test named for it. Tests about the
        /// full window have to get past that first, exactly as a real conversation does.
        /// </remarks>
        private static void BringUpWithPeerInStep(NdLinkLayer layer, FrameSink sink)
        {
            byte[] incoming = BuildPeerDataFrame(0x10, new byte[] { 0x01 });
            layer.HandleFrame(incoming, incoming.Length);

            layer.SendDatagram(new byte[] { 0x21, 0x13 });

            // We sent sequence 0, so a peer in step expects 1 next.
            byte[] ack = BuildPeerAckFrame(0x01);
            layer.HandleFrame(ack, ack.Length);

            sink.Frames.Clear();
        }

        /// <summary>
        /// The sequence wraps at SEVEN bits, so 0x7F is followed by 0x00 and 0x80 is never sent.
        /// </summary>
        /// <remarks>
        /// This test used to assert <c>AcknowledgeFor(0xFF) == 0x00</c>, an eight-bit wrap. That
        /// passed while describing a value no ND ever puts on the wire. Counted over the three real
        /// captures in <c>DOC\captures\FA-READ-WRITE-2026-08-04\</c> the highest sequence is
        /// <c>0x7F</c> and no frame has bit 7 set - see
        /// <see cref="NdLinkHeader.SequenceModulus"/>.
        /// </remarks>
        [Fact]
        public void AcknowledgementSequenceWrapsAtSevenBits()
        {
            Assert.Equal(0x00, NdLinkHeader.AcknowledgeFor(0x7F, LocalLinkId, PeerLinkId).Sequence);
            Assert.Equal(0x7F, NdLinkHeader.AcknowledgeFor(0x7E, LocalLinkId, PeerLinkId).Sequence);
        }

        /// <summary>
        /// Nothing this node sends ever carries a sequence with bit 7 set.
        /// </summary>
        /// <remarks>
        /// Drives 300 datagrams through the link - more than two full turns of the seven-bit space -
        /// acknowledging each one so the window keeps opening. Before 2026-08-11 the sequence
        /// wrapped at 256 and this ran straight through <c>0x80</c>.
        /// </remarks>
        [Fact]
        public void OutgoingSequenceNeverSetsBitSeven()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            byte[] incoming = BuildPeerDataFrame(0x10, new byte[] { 0x01 });
            layer.HandleFrame(incoming, incoming.Length);
            sink.Frames.Clear();

            byte[] datagram = new byte[] { 0x21, 0x13, 0x00, 0x0E };
            for (int i = 0; i < 300; i++)
            {
                Assert.Equal(NdSendOutcome.Transmitted, layer.SendDatagram(datagram));

                // Acknowledge everything we have sent, so the window is never the thing that stops
                // this test - the point here is the sequence, not the window.
                byte[] ack = BuildPeerAckFrame(layer.NextSequence);
                layer.HandleFrame(ack, ack.Length);
            }

            for (int i = 0; i < sink.Frames.Count; i++)
            {
                Assert.True(NdLinkHeader.TryParse(
                    sink.Frames[i].AsSpan(Ieee8023Frame.PayloadOffset), out NdLinkHeader header));
                Assert.True(
                    header.Sequence < NdLinkHeader.SequenceModulus,
                    "frame " + i + " carries sequence 0x" + header.Sequence.ToString("X2"));
            }
        }

        /// <summary>
        /// Sending stops at the window and starts again when the peer acknowledges.
        /// </summary>
        /// <remarks>
        /// The defect this pins: the layer used to send every datagram immediately, so a live D100
        /// ended up 33 frames behind and retransmitted everything it had not seen acknowledged. See
        /// <see cref="NdLinkLayer.SendWindow"/>.
        /// </remarks>
        [Fact]
        public void SendingStopsAtTheWindowAndResumesOnAcknowledgement()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            // The bring-up leaves our next sequence at 1, so the window covers frames 1 to 4.
            BringUpWithPeerInStep(layer, sink);

            byte[] datagram = new byte[] { 0x21, 0x13, 0x00, 0x0E };
            for (int i = 0; i < NdLinkLayer.SendWindow + 3; i++)
            {
                // WHICH of the two the caller gets is the point: the first SendWindow go out, and
                // everything past the window is held. Asserting only "accepted" for both is what
                // let a parked frame pass for a sent one everywhere else.
                NdSendOutcome expected = i < NdLinkLayer.SendWindow ? NdSendOutcome.Transmitted : NdSendOutcome.Queued;
                Assert.Equal(expected, layer.SendDatagram(datagram));
            }

            Assert.Equal(NdLinkLayer.SendWindow, sink.Frames.Count);
            Assert.Equal(NdLinkLayer.SendWindow, layer.OutstandingFrames);
            Assert.Equal(3, layer.QueuedDatagrams);

            // The peer takes frame 1 and so expects 2. Exactly one more goes out - not the backlog.
            byte[] ack = BuildPeerAckFrame(0x02);
            layer.HandleFrame(ack, ack.Length);

            Assert.Equal(NdLinkLayer.SendWindow + 1, sink.Frames.Count);
            Assert.Equal(2, layer.QueuedDatagrams);

            // And the peer taking everything sent so far drains what is left.
            ack = BuildPeerAckFrame(0x06);
            layer.HandleFrame(ack, ack.Length);

            Assert.Equal(NdLinkLayer.SendWindow + 3, sink.Frames.Count);
            Assert.Equal(0, layer.QueuedDatagrams);
        }

        /// <summary>
        /// A repeated acknowledgement does not re-open the window.
        /// </summary>
        /// <remarks>
        /// A peer that is behind re-sends its acknowledgements. Taking an older value would move the
        /// low edge backwards and let a burst out - the very thing the window exists to prevent.
        /// </remarks>
        [Fact]
        public void ARepeatedAcknowledgementDoesNotReopenTheWindow()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            BringUpWithPeerInStep(layer, sink);

            byte[] datagram = new byte[] { 0x21, 0x13, 0x00, 0x0E };
            for (int i = 0; i < NdLinkLayer.SendWindow + 2; i++)
            {
                layer.SendDatagram(datagram);
            }

            byte[] ack = BuildPeerAckFrame(0x03);
            layer.HandleFrame(ack, ack.Length);
            int afterFirstAck = sink.Frames.Count;

            // The same acknowledgement again says nothing new.
            layer.HandleFrame(ack, ack.Length);
            Assert.Equal(afterFirstAck, sink.Frames.Count);

            // An older one is behind the edge and must be ignored too.
            byte[] stale = BuildPeerAckFrame(0x02);
            layer.HandleFrame(stale, stale.Length);
            Assert.Equal(afterFirstAck, sink.Frames.Count);
            Assert.Equal(NdLinkLayer.SendWindow, layer.OutstandingFrames);
        }

        /// <summary>
        /// A frame the peer re-sends is counted and announced, acknowledged again, AND still
        /// delivered upward.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The delivery is the part that looks wrong and is not. This layer dropped a repeat for
        /// one afternoon, reasoned from what a sequence number is for. A live pull then stalled
        /// dead after four blocks with 174 repeats, because D100's retransmission is driven by the
        /// DATAGRAM layer waiting for a subtype-0x03 acknowledgement that the layers above build
        /// from the datagram itself. Swallow the repeat and that acknowledgement is never rebuilt,
        /// so the peer's retransmission can never resolve.
        /// </para>
        /// <para>
        /// The peer must also still get its link acknowledgement - it repeated precisely because
        /// it did not see the first one.
        /// </para>
        /// </remarks>
        [Fact]
        public void ARepeatedFrameIsCountedAcknowledgedAndStillDelivered()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            int deliveries = 0;
            layer.PayloadReceived += (payload, length) => deliveries++;

            byte reportedSequence = 0xFF;
            byte reportedExpected = 0xFF;
            layer.OnDuplicateDataFrameReceived += (sequence, expected) =>
            {
                reportedSequence = sequence;
                reportedExpected = expected;
            };

            byte[] datagram = new byte[] { 0x21, 0x13, 0x00, 0x0E };
            byte[] first = BuildPeerDataFrame(0x10, datagram);
            layer.HandleFrame(first, first.Length);

            byte[] second = BuildPeerDataFrame(0x11, datagram);
            layer.HandleFrame(second, second.Length);

            Assert.Equal(2, deliveries);
            Assert.Equal(0, layer.DuplicateDataFramesReceived);
            sink.Frames.Clear();

            // The peer did not see our acknowledgement of 0x11 and sends it again.
            layer.HandleFrame(second, second.Length);

            // Counted and announced, so it is never invisible...
            Assert.Equal(1, layer.DuplicateDataFramesReceived);
            Assert.Equal(0x11, reportedSequence);
            Assert.Equal(0x12, reportedExpected);

            // ...and STILL delivered, or the datagram acknowledgement the peer is waiting for is
            // never rebuilt and its retransmission cannot resolve. See the remarks.
            Assert.Equal(3, deliveries);

            // Still acknowledged, or the peer would repeat for ever.
            Assert.Single(sink.Frames);
            Assert.True(NdLinkHeader.TryParse(
                sink.Frames[0].AsSpan(Ieee8023Frame.PayloadOffset), out NdLinkHeader ack));
            Assert.True(ack.IsAcknowledge);
            Assert.Equal(0x12, ack.Sequence);
        }

        /// <summary>
        /// A peer that carried its numbering over from our previous session teaches us where it is,
        /// and the frame it threw away goes again at the right sequence.
        /// </summary>
        /// <remarks>
        /// MEASURED against a live D100 on 2026-08-11: restarting the runner without restarting
        /// XMSG left D100 acknowledging 49, where our previous session had ended, while we opened at
        /// 0. Every frame we sent was discarded and the conversation never started, with no error
        /// anywhere above the link.
        /// </remarks>
        [Fact]
        public void ThePeersFirstAcknowledgementTeachesUsOurSequence()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            byte[] incoming = BuildPeerDataFrame(0x10, new byte[] { 0x01 });
            layer.HandleFrame(incoming, incoming.Length);
            sink.Frames.Clear();

            Assert.False(layer.HasLearnedPeerPosition);

            // Only ONE frame goes out before the peer has placed us, however many we hand over.
            byte[] first = new byte[] { 0x21, 0x13, 0x00, 0x0E, 0xAA };
            byte[] second = new byte[] { 0x21, 0x13, 0x00, 0x0E, 0xBB };
            Assert.Equal(NdSendOutcome.Transmitted, layer.SendDatagram(first));
            Assert.Equal(NdSendOutcome.Queued, layer.SendDatagram(second));
            Assert.Single(sink.Frames);
            Assert.Equal(1, layer.QueuedDatagrams);

            // The peer is at 49, not 1, because it never restarted. It threw our frame away.
            byte[] ack = BuildPeerAckFrame(49);
            layer.HandleFrame(ack, ack.Length);

            Assert.True(layer.HasLearnedPeerPosition);

            // The lost frame goes again at 49, and the queue then drains behind it.
            Assert.Equal(3, sink.Frames.Count);
            Assert.True(NdLinkHeader.TryParse(
                sink.Frames[1].AsSpan(Ieee8023Frame.PayloadOffset), out NdLinkHeader resent));
            Assert.Equal(49, resent.Sequence);
            Assert.Equal(
                first,
                sink.Frames[1].AsSpan(
                    Ieee8023Frame.PayloadOffset + NdLinkHeader.Length, first.Length).ToArray());

            Assert.True(NdLinkHeader.TryParse(
                sink.Frames[2].AsSpan(Ieee8023Frame.PayloadOffset), out NdLinkHeader next));
            Assert.Equal(50, next.Sequence);
            Assert.Equal(0, layer.QueuedDatagrams);
        }

        /// <summary>
        /// A peer that really did start fresh needs no correction, and nothing is sent twice.
        /// </summary>
        [Fact]
        public void APeerThatIsAlreadyInStepGetsNoResend()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            byte[] incoming = BuildPeerDataFrame(0x10, new byte[] { 0x01 });
            layer.HandleFrame(incoming, incoming.Length);
            sink.Frames.Clear();

            byte[] datagram = new byte[] { 0x21, 0x13, 0x00, 0x0E };
            Assert.Equal(NdSendOutcome.Transmitted, layer.SendDatagram(datagram));
            Assert.Single(sink.Frames);

            // We sent sequence 0, so a peer in step expects 1 next.
            byte[] ack = BuildPeerAckFrame(0x01);
            layer.HandleFrame(ack, ack.Length);

            Assert.True(layer.HasLearnedPeerPosition);
            Assert.Single(sink.Frames);
            Assert.Equal(0, layer.OutstandingFrames);

            // And the full window is open from here on.
            for (int i = 0; i < NdLinkLayer.SendWindow; i++)
            {
                Assert.Equal(NdSendOutcome.Transmitted, layer.SendDatagram(datagram));
            }

            Assert.Equal(1 + NdLinkLayer.SendWindow, sink.Frames.Count);
        }

        /// <summary>
        /// A peer that stops acknowledging fills the queue and then gets a refusal, rather than
        /// taking the machine's memory with it.
        /// </summary>
        [Fact]
        public void TheQueueIsBoundedAndRefusesWhenFull()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            BringUpWithPeerInStep(layer, sink);

            byte[] datagram = new byte[] { 0x21, 0x13, 0x00, 0x0E };
            for (int i = 0; i < NdLinkLayer.SendWindow + NdLinkLayer.MaxQueuedDatagrams; i++)
            {
                NdSendOutcome expected = i < NdLinkLayer.SendWindow ? NdSendOutcome.Transmitted : NdSendOutcome.Queued;
                Assert.Equal(expected, layer.SendDatagram(datagram));
            }

            // Only NOW is it refused: the window is full AND the queue behind it is full.
            Assert.Equal(NdSendOutcome.Refused, layer.SendDatagram(datagram));
            Assert.Equal(1, layer.DatagramsRefusedQueueFull);
            Assert.Equal(NdLinkLayer.MaxQueuedDatagrams, layer.QueuedDatagrams);
            Assert.Equal(NdLinkLayer.SendWindow, sink.Frames.Count);
        }

        /// <summary>
        /// The peer's link id and address are learned from the first frame, not derived.
        /// </summary>
        [Fact]
        public void PeerIdentityIsLearnedFromTheFirstFrame()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            Assert.False(layer.HasLearnedPeer);
            Assert.Equal(NdLinkLayer.UnknownPeerLinkId, layer.PeerLinkId);

            byte[] frame = BuildPeerDataFrame(0x10, new byte[] { 0x01 });
            layer.HandleFrame(frame, frame.Length);

            Assert.True(layer.HasLearnedPeer);
            Assert.Equal(PeerLinkId, layer.PeerLinkId);
            Assert.True(layer.PeerMac.TryGetSystemNumber(out ushort peerSystem));
            Assert.Equal(PeerSystem, peerSystem);
        }

        /// <summary>
        /// Nothing is sent before the peer is known, because a frame addressed to nobody is not
        /// worth putting on the segment.
        /// </summary>
        [Fact]
        public void SendIsRefusedUntilThePeerIsKnown()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            Assert.Equal(NdSendOutcome.Refused, layer.SendDatagram(new byte[] { 0x21, 0x13 }));
            Assert.Empty(sink.Frames);
        }

        /// <summary>
        /// Outgoing data frames carry an increasing sequence and the learned peer's link id.
        /// </summary>
        [Fact]
        public void OutgoingDataFramesIncrementTheSequence()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            BringUpWithPeerInStep(layer, sink);

            byte[] datagram = new byte[] { 0x21, 0x13, 0x00, 0x0E, 0x00, 0x64 };
            Assert.Equal(NdSendOutcome.Transmitted, layer.SendDatagram(datagram));
            Assert.Equal(NdSendOutcome.Transmitted, layer.SendDatagram(datagram));

            Assert.Equal(2, sink.Frames.Count);

            Assert.True(NdLinkHeader.TryParse(sink.Frames[0].AsSpan(Ieee8023Frame.PayloadOffset), out NdLinkHeader first));
            Assert.True(NdLinkHeader.TryParse(sink.Frames[1].AsSpan(Ieee8023Frame.PayloadOffset), out NdLinkHeader second));

            Assert.True(first.IsData);
            Assert.Equal(datagram.Length, first.PayloadLength);
            // We are the sender here, so OUR reference is in the second field and the peer's in the
            // first - see the note in the fixture's frame builder.
            Assert.Equal(LocalLinkId, first.ReceiverLinkId);
            Assert.Equal(PeerLinkId, first.SenderLinkId);
            Assert.Equal(unchecked((byte)(first.Sequence + 1)), second.Sequence);
        }

        /// <summary>
        /// The datagram is delivered up intact.
        /// </summary>
        [Fact]
        public void DatagramIsDeliveredUpIntact()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            byte[] received = Array.Empty<byte>();
            layer.PayloadReceived += (payload, length) =>
            {
                received = new byte[length];
                Array.Copy(payload, received, length);
            };

            byte[] datagram = new byte[] { 0x21, 0x13, 0x00, 0x0E, 0x00, 0x64, 0x00, 0x66 };
            byte[] frame = BuildPeerDataFrame(0x20, datagram);
            layer.HandleFrame(frame, frame.Length);

            Assert.Equal(datagram, received);
        }

        /// <summary>
        /// A frame from our own address is ignored.
        /// </summary>
        /// <remarks>
        /// On a multicast segment a node hears its own transmissions. Processing them would
        /// acknowledge our own data and corrupt the sequence, so the guard matters in practice and
        /// not only in theory.
        /// </remarks>
        [Fact]
        public void OwnLoopedBackFrameIsIgnored()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            byte[] llcPayload = new byte[NdLinkHeader.Length];
            NdLinkHeader.Data(0x01, LocalLinkId, PeerLinkId, 0).Write(llcPayload);

            byte[] buffer = new byte[Ieee8023Frame.MinimumFrameLength];
            int written = Ieee8023Frame.Build(
                NdMacAddress.FromSystemNumber(PeerSystem),
                NdMacAddress.FromSystemNumber(LocalSystem),   // source = US
                llcPayload,
                buffer);

            Assert.False(layer.HandleFrame(buffer, written));
            Assert.Empty(sink.Frames);
            Assert.False(layer.HasLearnedPeer);
        }

        /// <summary>
        /// An acknowledgement is counted but never itself acknowledged.
        /// </summary>
        [Fact]
        public void AcknowledgementIsNotAcknowledged()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            byte[] llcPayload = new byte[NdLinkHeader.Length];
            NdLinkHeader.AcknowledgeFor(0x30, PeerLinkId, LocalLinkId).Write(llcPayload);

            byte[] buffer = new byte[Ieee8023Frame.MinimumFrameLength];
            int written = Ieee8023Frame.Build(
                NdMacAddress.FromSystemNumber(LocalSystem),
                NdMacAddress.FromSystemNumber(PeerSystem),
                llcPayload,
                buffer);

            Assert.True(layer.HandleFrame(buffer, written));
            Assert.Empty(sink.Frames);
            Assert.Equal(1, layer.AcknowledgementsReceived);
            Assert.Equal(0, layer.DataFramesReceived);
        }

        /// <summary>
        /// An unrecognised frame kind is surfaced rather than dropped or thrown.
        /// </summary>
        [Fact]
        public void UnknownFrameKindIsSurfaced()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            byte reported = 0;
            layer.OnUnknownFrameKindReceived += kind => reported = kind;

            byte[] frame = BuildPeerDataFrame(0x10, new byte[] { 0x01 });
            frame[Ieee8023Frame.PayloadOffset + 2] = 0x7E;

            Assert.True(layer.HandleFrame(frame, frame.Length));
            Assert.Equal(0x7E, reported);
        }

        /// <summary>
        /// Two link layers on one in-process segment exchange a datagram and acknowledge it.
        /// </summary>
        /// <remarks>
        /// End-to-end over the segment abstraction: proves the pieces fit together, not just that
        /// each parses in isolation.
        /// </remarks>
        [Fact]
        public void TwoNodesOnASegmentExchangeADatagram()
        {
            InProcessEthernetSegment segment = new InProcessEthernetSegment();
            IEthernetBackend portA = segment.CreatePort("a");
            IEthernetBackend portB = segment.CreatePort("b");
            portA.Start();
            portB.Start();

            NdLinkLayer nodeA = new NdLinkLayer(9999, 0x4242, (data, length) => portA.SendPacket(data, 0, length));
            NdLinkLayer nodeB = new NdLinkLayer(100, 0x59C1, (data, length) => portB.SendPacket(data, 0, length));

            portA.OnPacketReceived += (data, length) => nodeA.HandleFrame(data, length);
            portB.OnPacketReceived += (data, length) => nodeB.HandleFrame(data, length);

            byte[] receivedByB = Array.Empty<byte>();
            nodeB.PayloadReceived += (payload, length) =>
            {
                receivedByB = new byte[length];
                Array.Copy(payload, receivedByB, length);
            };

            // B must speak first so A learns who it is - A cannot invent B's link id.
            byte[] hello = new byte[] { 0x21, 0x13, 0x00, 0x19 };
            byte[] llcPayload = new byte[NdLinkHeader.Length + hello.Length];
            // B does not know A's reference yet, so the destination field is zero and B's own
            // reference goes second - exactly how a real connection request looks on the wire.
            NdLinkHeader.Data(0x01, 0x0000, 0x59C1, (ushort)hello.Length).Write(llcPayload);
            Array.Copy(hello, 0, llcPayload, NdLinkHeader.Length, hello.Length);

            byte[] buffer = new byte[Ieee8023Frame.MinimumFrameLength + llcPayload.Length];
            int written = Ieee8023Frame.Build(
                NdMacAddress.FromSystemNumber(9999),
                NdMacAddress.FromSystemNumber(100),
                llcPayload,
                buffer);
            portB.SendPacket(buffer, 0, written);

            Assert.True(nodeA.HasLearnedPeer);
            Assert.Equal(0x59C1, nodeA.PeerLinkId);

            // Now A can answer, and B should receive it and acknowledge.
            byte[] datagram = new byte[] { 0x21, 0x13, 0x00, 0x0E, 0x00, 0x64, 0x27, 0x0F };
            Assert.Equal(NdSendOutcome.Transmitted, nodeA.SendDatagram(datagram));

            Assert.Equal(datagram, receivedByB);
            Assert.True(nodeA.AcknowledgementsReceived > 0);
        }

        /// <summary>
        /// A parked datagram is never reported as a sent one.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the live failure of 2026-08-17, in one test.</b></para>
        /// <para>
        /// A TAD connect from D100 hung. Our side had decoded it, opened a session and logged
        /// "answered with 2 frame(s)", and D100 saw neither frame. The window was
        /// <see cref="NdLinkLayer.UnpositionedWindow"/> - one - because the peer had answered our
        /// announce with a disconnect rather than an acknowledgement that places us, so both replies
        /// went into the queue. <c>SendDatagram</c> returned the same true for those as for a frame
        /// on the wire, so nothing above could tell, and the terminal simply sat there.
        /// </para>
        /// <para>
        /// The assertion that matters is the third one: the second datagram must come back
        /// <see cref="NdSendOutcome.Queued"/> and NOT <see cref="NdSendOutcome.Transmitted"/>. A
        /// bool cannot express that, which is why it is an enum now.
        /// </para>
        /// </remarks>
        [Fact]
        public void AQueuedDatagramIsNotReportedAsATransmittedOne()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            // The peer is known but has NOT positioned us - exactly the state the live run was in.
            byte[] incoming = BuildPeerDataFrame(0x10, new byte[] { 0x01 });
            layer.HandleFrame(incoming, incoming.Length);
            sink.Frames.Clear();

            Assert.True(layer.HasLearnedPeer);
            Assert.False(layer.HasLearnedPeerPosition);
            Assert.Equal(1, NdLinkLayer.UnpositionedWindow);

            byte[] first = new byte[] { 0x21, 0x13, 0x00, 0x0E, 0xA1 };
            byte[] second = new byte[] { 0x21, 0x13, 0x00, 0x0E, 0xA2 };

            Assert.Equal(NdSendOutcome.Transmitted, layer.SendDatagram(first));
            Assert.Equal(NdSendOutcome.Queued, layer.SendDatagram(second));

            // One on the wire, one held - and the caller was told which.
            Assert.Single(sink.Frames);
            Assert.Equal(1, layer.QueuedDatagrams);
        }

        /// <summary>
        /// The queue drains once the peer acknowledges, and the drained frame really is sent.
        /// </summary>
        /// <remarks>
        /// The other half: queueing is only a fault when it does not end. Without this, a change
        /// that returned <see cref="NdSendOutcome.Queued"/> and then never transmitted would pass
        /// the test above while being far worse than the bug it guards.
        /// </remarks>
        [Fact]
        public void AParkedDatagramGoesOutOnceThePeerAcknowledges()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            byte[] incoming = BuildPeerDataFrame(0x10, new byte[] { 0x01 });
            layer.HandleFrame(incoming, incoming.Length);
            sink.Frames.Clear();

            byte[] first = new byte[] { 0x21, 0x13, 0x00, 0x0E, 0xB1 };
            byte[] second = new byte[] { 0x21, 0x13, 0x00, 0x0E, 0xB2 };
            Assert.Equal(NdSendOutcome.Transmitted, layer.SendDatagram(first));
            Assert.Equal(NdSendOutcome.Queued, layer.SendDatagram(second));
            Assert.Single(sink.Frames);

            // The peer places us and acknowledges what we sent: the held frame follows it out.
            byte[] ack = BuildPeerAckFrame(layer.NextSequence);
            layer.HandleFrame(ack, ack.Length);

            Assert.Equal(0, layer.QueuedDatagrams);
            Assert.Equal(2, sink.Frames.Count);
        }

        /// <summary>
        /// A disconnect frees the send window, so the link is usable again afterwards.
        /// </summary>
        /// <remarks>
        /// <para><b>The other half of the 2026-08-17 hang.</b></para>
        /// <para>
        /// Making a queued frame report itself as queued told us what was happening; this is what
        /// was actually wrong. The send state belongs to the CONNECTION, and when the peer tears the
        /// connection down it has to die with it. An outstanding frame left counted against a
        /// link that no longer exists keeps <c>OutstandingFrames</c> at one for ever, and since the
        /// unpositioned window is also one, every later datagram is queued and nothing ever leaves
        /// this node again - on a link the peer has meanwhile rebuilt perfectly well.
        /// </para>
        /// </remarks>
        [Fact]
        public void ADisconnectClearsWhatWasOutstandingSoTheWindowReopens()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            byte[] incoming = BuildPeerDataFrame(0x10, new byte[] { 0x01 });
            layer.HandleFrame(incoming, incoming.Length);
            sink.Frames.Clear();

            // One frame out, unacknowledged - the state the announce left the live run in.
            byte[] datagram = new byte[] { 0x21, 0x13, 0x00, 0x0E, 0xC1 };
            Assert.Equal(NdSendOutcome.Transmitted, layer.SendDatagram(datagram));
            Assert.Equal(1, layer.OutstandingFrames);

            // The peer tears the link down without ever acknowledging it.
            byte[] disconnect = BuildPeerDisconnectFrame();
            layer.HandleFrame(disconnect, disconnect.Length);

            Assert.Equal(1, layer.DisconnectRequestsReceived);
            Assert.Equal(0, layer.OutstandingFrames);
            Assert.False(layer.HasLearnedPeerPosition);

            // BEFORE the fix this was Queued, and stayed Queued for every frame for ever after.
            sink.Frames.Clear();
            Assert.Equal(NdSendOutcome.Transmitted, layer.SendDatagram(datagram));
            Assert.Single(sink.Frames);
        }

        /// <summary>
        /// Datagrams already queued when the peer disconnects are sent, not dropped.
        /// </summary>
        /// <remarks>
        /// They were accepted from callers who were told <see cref="NdSendOutcome.Queued"/> rather
        /// than <see cref="NdSendOutcome.Refused"/>, so throwing them away would make the earlier
        /// answer a lie. One goes immediately - the unpositioned window is one - and the rest follow
        /// as the peer acknowledges.
        /// </remarks>
        [Fact]
        public void DatagramsHeldWhenThePeerDisconnectsAreStillSent()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            byte[] incoming = BuildPeerDataFrame(0x10, new byte[] { 0x01 });
            layer.HandleFrame(incoming, incoming.Length);
            sink.Frames.Clear();

            byte[] first = new byte[] { 0x21, 0x13, 0x00, 0x0E, 0xD1 };
            byte[] held = new byte[] { 0x21, 0x13, 0x00, 0x0E, 0xD2 };
            Assert.Equal(NdSendOutcome.Transmitted, layer.SendDatagram(first));
            Assert.Equal(NdSendOutcome.Queued, layer.SendDatagram(held));
            Assert.Equal(1, layer.QueuedDatagrams);

            sink.Frames.Clear();
            byte[] disconnect = BuildPeerDisconnectFrame();
            layer.HandleFrame(disconnect, disconnect.Length);

            // The held one went out on the rebuilt connection instead of being lost.
            Assert.Equal(0, layer.QueuedDatagrams);
            Assert.Single(sink.Frames);
        }

        /// <summary>
        /// An acknowledgement behind our edge is refused AND reported.
        /// </summary>
        /// <remarks>
        /// <para><b>The signature of the deadlock found on 2026-08-17</b></para>
        /// <para>
        /// Refusing a stale acknowledgement is right - moving backwards would resend frames the peer
        /// already holds. What was missing was any way to SEE it. Three failures that looked
        /// unrelated - a TAD connect, an FA delete and a directory listing - were all frames parked
        /// behind a window that never opened, and the logs showed acknowledgements arriving in
        /// healthy numbers the whole time, because nothing distinguished one that was ACCEPTED from
        /// one that was thrown away.
        /// </para>
        /// <para>
        /// So the refusal now reports itself, and this pins that. One stale acknowledgement is
        /// ordinary; a run of them beside a climbing park queue is the deadlock.
        /// </para>
        /// </remarks>
        [Fact]
        public void AStaleAcknowledgementIsRefusedAndReported()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            BringUpWithPeerInStep(layer, sink);

            byte[] datagram = new byte[] { 0x21, 0x13, 0x00, 0x0E };
            Assert.Equal(NdSendOutcome.Transmitted, layer.SendDatagram(datagram));
            Assert.Equal(NdSendOutcome.Transmitted, layer.SendDatagram(datagram));

            // Move the peer forward, so there is an edge to be behind.
            byte[] forward = BuildPeerAckFrame(layer.NextSequence);
            layer.HandleFrame(forward, forward.Length);
            Assert.Equal(0, layer.OutstandingFrames);

            int reported = 0;
            byte reportedAck = 0;
            byte reportedExpected = 0;
            layer.StaleAcknowledgement += delegate (byte acknowledged, byte expected, byte next, int queued)
            {
                reported++;
                reportedAck = acknowledged;
                reportedExpected = expected;
            };

            // Now an acknowledgement from BEHIND that edge - a duplicate still in flight.
            byte stale = (byte)(layer.NextSequence - 1);
            byte[] behind = BuildPeerAckFrame(stale);
            layer.HandleFrame(behind, behind.Length);

            Assert.Equal(1, reported);
            Assert.Equal(stale, reportedAck);
            Assert.Equal(layer.NextSequence, reportedExpected);
        }

        /// <summary>
        /// An acknowledgement at our edge is accepted in silence.
        /// </summary>
        /// <remarks>
        /// The control. Without it a change that reported EVERY acknowledgement would satisfy the
        /// test above while making the new line useless - it would fire constantly on a healthy
        /// link, which is how a diagnostic gets ignored.
        /// </remarks>
        [Fact]
        public void AnAcknowledgementAtOurEdgeIsNotReportedAsStale()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            BringUpWithPeerInStep(layer, sink);

            int reported = 0;
            layer.StaleAcknowledgement += delegate (byte a, byte b, byte c, int d) { reported++; };

            byte[] datagram = new byte[] { 0x21, 0x13, 0x00, 0x0E };
            Assert.Equal(NdSendOutcome.Transmitted, layer.SendDatagram(datagram));

            byte[] ack = BuildPeerAckFrame(layer.NextSequence);
            layer.HandleFrame(ack, ack.Length);

            Assert.Equal(0, reported);
            Assert.Equal(0, layer.OutstandingFrames);
        }
    }
}