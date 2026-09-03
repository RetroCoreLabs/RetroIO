using Xunit;

namespace RetroIO.Protocols.Xmsg.Ethernet.Tests
{
    /// <summary>
    /// A connection request from the peer means the old connection is gone, so the send state that
    /// belonged to it must go with it.
    /// </summary>
    /// <remarks>
    /// <para><b>MEASURED on the live segment, 2026-08-27</b></para>
    /// <para>
    /// The disconnect-request path already does this reset, and its comment explains why: a peer
    /// that tears the link down with one of our frames unacknowledged leaves OutstandingFrames at
    /// 1, the unpositioned window is also 1, and 1 is not less than 1 - so every datagram from then
    /// on is queued and nothing ever leaves this node again. The connection-request path did NOT do
    /// it, and that is the same fault by the other door.
    /// </para>
    /// <para>
    /// What it cost, from the hub capture. The runner restarted while D100 was still mid-run on
    /// link 048C, so our send sequence began again at 0 where D100 expected a far higher number.
    /// D100 discarded that frame in silence - no acknowledgement and no error:
    /// </para>
    /// <code>
    /// 17:07:16.824  100-> us  DT seq=44 snd=0001 rcv=048C
    /// 17:07:16.839  us -> 100 AK seq=45
    /// 17:07:16.939  us -> 100 DT seq=00 snd=048C rcv=0001   our frame, never acknowledged
    /// 17:07:56.642  100-> us  DT seq=45   the same payload again
    /// 17:08:36.454  100-> us  DT seq=46   and again
    /// 17:09:16.241  100-> us  CR seq=79 snd=0000 rcv=048D   D100 gives up, opens a NEW link
    /// </code>
    /// <para>
    /// D100 rebuilt the link perfectly well. This node confirmed it and then never transmitted
    /// again: in the following fifty-eight minutes it put ZERO data frames on the wire, while its
    /// own log reported four connect letters "accepted by our transport". The queue was still
    /// blocked behind the frame outstanding on the connection that no longer existed.
    /// </para>
    /// <para>
    /// Note what is NOT claimed here. Nothing in this file stops the sequence restarting at 0 -
    /// that is a separate question, since the Ethernet send sequence is not persisted across a
    /// restart the way the HDLC one is. This only makes the peer's own recovery work, so a link
    /// the peer rebuilds is a link this node can use again.
    /// </para>
    /// </remarks>
    public sealed class NdLinkConnectionRequestResetTests
    {
        private const ushort LocalSystem = 9999;
        private const ushort PeerSystem = 100;
        private const ushort LocalLinkId = 0x4242;
        private const ushort PeerLinkId = 0x59C1;

        /// <summary>
        /// Collects the frames the layer transmits.
        /// </summary>
        private sealed class FrameSink
        {
            /// <summary>
            /// Every frame handed to the sink, each copied to its own array.
            /// </summary>
            public System.Collections.Generic.List<byte[]> Frames { get; } =
                new System.Collections.Generic.List<byte[]>();

            /// <summary>
            /// Takes one frame from the layer.
            /// </summary>
            /// <param name="data">
            /// The buffer holding the frame.
            /// </param>
            /// <param name="length">
            /// How many bytes of it are the frame.
            /// </param>
            public void Send(byte[] data, int length)
            {
                byte[] copy = new byte[length];
                System.Array.Copy(data, copy, length);
                Frames.Add(copy);
            }

            /// <summary>
            /// Counts the transmitted frames of one ND kind.
            /// </summary>
            /// <param name="kind">
            /// The ND frame kind to count.
            /// </param>
            /// <returns>
            /// How many frames in <see cref="Frames"/> carry that kind.
            /// </returns>
            public int CountOfKind(byte kind)
            {
                int n = 0;
                for (int i = 0; i < Frames.Count; i++)
                {
                    byte[] f = Frames[i];
                    if (Ieee8023Frame.TryParse(
                            new System.ReadOnlySpan<byte>(f, 0, f.Length),
                            out NdMacAddress _, out NdMacAddress _,
                            out int payloadOffset, out int payloadLength)
                        && payloadLength >= NdLinkHeader.Length
                        && NdLinkHeader.TryParse(
                            new System.ReadOnlySpan<byte>(f, payloadOffset, payloadLength),
                            out NdLinkHeader header)
                        && header.Kind == kind)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        /// <summary>
        /// A frame from the peer to this node, of the given kind.
        /// </summary>
        /// <param name="kind">
        /// The ND frame kind byte.
        /// </param>
        /// <param name="sequence">
        /// The sequence the frame carries.
        /// </param>
        /// <param name="trailing">
        /// The trailing field, whose meaning depends on the kind.
        /// </param>
        /// <returns>
        /// A complete Ethernet frame from the peer to the local node.
        /// </returns>
        private static byte[] PeerFrame(byte kind, byte sequence, ushort trailing)
        {
            byte[] llcPayload = new byte[NdLinkHeader.Length];
            new NdLinkHeader(kind, sequence, LocalLinkId, PeerLinkId, trailing).Write(llcPayload);

            byte[] buffer = new byte[Ieee8023Frame.PayloadOffset + llcPayload.Length
                                     + Ieee8023Frame.MinimumFrameLength];
            int written = Ieee8023Frame.Build(
                NdMacAddress.FromSystemNumber(LocalSystem),
                NdMacAddress.FromSystemNumber(PeerSystem),
                llcPayload,
                buffer);

            byte[] frame = new byte[written];
            System.Array.Copy(buffer, frame, written);
            return frame;
        }

        /// <summary>
        /// The peer opening a new connection frees a queue left stalled on the old one.
        /// </summary>
        [Fact]
        public void AConnectionRequestFreesTheQueueLeftOnTheOldConnection()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            // The peer speaks first, so its identity is learned - but it has acknowledged nothing,
            // so our position is unknown and the window is a single frame.
            byte[] peerData = PeerFrame((byte)NdLinkFrameKind.Data, 0x44, 0);
            layer.HandleFrame(peerData, peerData.Length);
            Assert.True(layer.HasLearnedPeer);
            Assert.False(layer.HasLearnedPeerPosition);

            byte[] datagram = new byte[] { 0x21, 0x13, 0x00, 0x03 };

            // One frame goes out and is never acknowledged - on the live segment the peer was
            // discarding it because our sequence had restarted at 0.
            Assert.Equal(NdSendOutcome.Transmitted, layer.SendDatagram(datagram));
            Assert.Equal(1, layer.OutstandingFrames);

            // The next is held, and correctly so: the window is one and nothing has come back.
            Assert.Equal(NdSendOutcome.Queued, layer.SendDatagram(datagram));
            Assert.Equal(1, layer.QueuedDatagrams);

            int dataFramesBefore = sink.CountOfKind((byte)NdLinkFrameKind.Data);

            // The peer gives up on the old connection and opens a new one, exactly as D100 did at
            // 17:09:16. That is the peer saying the old link, and everything outstanding on it, is
            // gone.
            byte[] connectionRequest =
                PeerFrame((byte)NdLinkFrameKind.ConnectionRequest, 0x79, PeerSystem);
            layer.HandleFrame(connectionRequest, connectionRequest.Length);

            // It is confirmed...
            Assert.Equal(1, layer.ConnectionRequestsReceived);

            // ...and the held datagram goes out on the new connection. Before this was fixed the
            // queue stayed at 1 for ever and this node never transmitted again.
            Assert.Equal(0, layer.QueuedDatagrams);
            Assert.True(
                sink.CountOfKind((byte)NdLinkFrameKind.Data) > dataFramesBefore,
                "the datagram held on the old connection must be sent on the new one");
        }

        /// <summary>
        /// A connection request clears what was outstanding, because it belonged to a connection
        /// that no longer exists.
        /// </summary>
        [Fact]
        public void AConnectionRequestClearsWhatWasOutstandingOnTheOldConnection()
        {
            FrameSink sink = new FrameSink();
            NdLinkLayer layer = new NdLinkLayer(LocalSystem, LocalLinkId, sink.Send);

            byte[] peerData = PeerFrame((byte)NdLinkFrameKind.Data, 0x44, 0);
            layer.HandleFrame(peerData, peerData.Length);

            byte[] datagram = new byte[] { 0x21, 0x13, 0x00, 0x03 };
            Assert.Equal(NdSendOutcome.Transmitted, layer.SendDatagram(datagram));
            Assert.Equal(1, layer.OutstandingFrames);

            byte[] connectionRequest =
                PeerFrame((byte)NdLinkFrameKind.ConnectionRequest, 0x79, PeerSystem);
            layer.HandleFrame(connectionRequest, connectionRequest.Length);

            // Nothing is outstanding on a connection that is gone, and our position on the new one
            // has to be learned again from its first acknowledgement.
            Assert.Equal(0, layer.OutstandingFrames);
            Assert.False(layer.HasLearnedPeerPosition);
        }
    }
}
