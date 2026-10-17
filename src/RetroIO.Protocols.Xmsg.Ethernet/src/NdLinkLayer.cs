//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Ethernet
{
    /// <summary>
    /// Receives one SINTRAN datagram taken out of an ND link data frame.
    /// </summary>
    /// <param name="payload">
    /// The buffer holding the datagram. It may be reused after the handler returns, so a handler
    /// that retains it MUST copy.
    /// </param>
    /// <param name="length">
    /// The number of valid bytes in <paramref name="payload"/>.
    /// </param>
    public delegate void NdLinkPayloadReceived(byte[] payload, int length);

    /// <summary>
    /// The ND link layer for one peer on an Ethernet segment: sequences outgoing data frames,
    /// acknowledges incoming ones, and hands the SINTRAN datagram up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the Ethernet counterpart of LAPB on HDLC. The framing it drives is documented on
    /// <see cref="NdLinkHeader"/>; this type owns the state that framing needs.
    /// </para>
    /// <para><b>Learned, not assumed</b></para>
    /// <para>
    /// The peer's link id is UNKNOWN in origin - it is neither the node number nor the system
    /// number in the MAC - so it is <b>learned from the first frame received</b> rather than
    /// derived. Deriving it from the node number would be inventing a constant that merely happens
    /// to work, which is the failure mode this project has been bitten by before. Until a frame
    /// arrives, <see cref="HasLearnedPeer"/> is false and outgoing frames use
    /// <see cref="UnknownPeerLinkId"/>.
    /// </para>
    /// <para><b>Acknowledgement rule</b></para>
    /// <para>
    /// Every received data frame is answered with an acknowledgement carrying the received sequence
    /// PLUS ONE - the next expected value. Acknowledgements are never themselves acknowledged.
    /// </para>
    /// <para><b>Send window - ADDED 2026-08-11, and why it was not there before</b></para>
    /// <para>
    /// This class used to send every datagram the moment it was handed one, with no regard for how
    /// many were still unacknowledged. The comment here said the window was "unknown" and that
    /// guessing would be worse than nothing. Measuring settled it - see
    /// <see cref="SendWindow"/> for the counts and for what the flood cost live on D100.
    /// </para>
    /// <para><b>What is still deliberately not implemented</b></para>
    /// <para>
    /// No retransmission and no reject handling. Neither was exercised by any capture: no loss
    /// occurred and no frame kind other than <c>0x20</c> and <c>0x3F</c> was seen. An unrecognised
    /// frame kind is surfaced through <see cref="UnknownFrameKindReceived"/> so it reaches a log
    /// rather than being silently dropped or throwing.
    /// </para>
    /// <para>
    /// The consequence, stated plainly: if the peer's acknowledgement is lost the queue never
    /// drains and this link stops sending. That is observable through
    /// <see cref="QueuedDatagrams"/> and <see cref="DatagramsRefusedQueueFull"/>. It is accepted
    /// deliberately - the alternative in place until now was to flood a machine that cannot keep
    /// up, which is measured to break the conversation outright.
    /// </para>
    /// </remarks>
    public sealed class NdLinkLayer
    {
        /// <summary>
        /// Link id used for the peer before its real one has been learned.
        /// </summary>
        public const ushort UnknownPeerLinkId = 0x0000;

        /// <summary>
        /// First sequence number sent on a freshly opened connection.
        /// </summary>
        /// <remarks>
        /// <para>
        /// MEASURED 2026-08-04 against a live SINTRAN (D100 -> our node 19999). This was 0x01, on
        /// the reasoning that the captured links were already running so the true starting value was
        /// unknown and "any value works because the peer follows what it receives". <b>That
        /// reasoning was wrong.</b> D100 does not follow what it receives: on every fresh connection
        /// its own first data frame is sequence <c>0</c>, and when we opened at <c>1</c> it accepted
        /// the connection, then ignored our reply and re-sent its ReachabilityRequest - consistent
        /// with a receiver holding a frame that arrived one ahead of the sequence it was waiting for.
        /// </para>
        /// <para>
        /// The old value was never contradicted by the captures because every capture began
        /// mid-conversation, where the starting value is invisible. Only opening a connection from
        /// scratch could show it.
        /// </para>
        /// </remarks>
        public const byte InitialSequence = 0x00;

        /// <summary>
        /// How many data frames this node will leave unacknowledged before it queues instead of
        /// sending.
        /// </summary>
        /// <remarks>
        /// <para><b>MEASURED, not specified - and the flood it replaces is measured too</b></para>
        /// <para>
        /// The largest burst a real ND machine has been seen to send before waiting. FOUR is the
        /// widest in the three text captures in <c>DOC\captures\FA-READ-WRITE-2026-08-04\</c>, and
        /// that case is a file READ - the whole answer to one request arriving at once, a short
        /// acknowledgement, the reply, and the two content fragments a content message is always
        /// split into:
        /// </para>
        /// <code>
        /// capture-read.txt  02:29:50.792  D102 -> D100  seq 44  36 bytes   short acknowledgement
        ///                   02:29:50.803                seq 45  52 bytes   reply
        ///                   02:29:50.810                seq 46  622 bytes  content, fragment 1
        ///                   02:29:50.810                seq 47  452 bytes  content, fragment 2
        ///                   02:29:50.827  D100 -> D102  acknowledges up to 44
        ///                   02:29:50.844                acknowledges up to 47
        /// </code>
        /// <para><b>A CAPTURE CANNOT TELL YOU THIS NUMBER - it moved three times in one day</b></para>
        /// <para>
        /// 2, from <c>capture-list-files.txt</c> alone. 4, once all three text captures were read.
        /// 5, once one hub capture was read. 6, once every hub capture was read. Each looked
        /// settled until a wider capture was opened, and each time the reasoning was "this is the
        /// largest a real machine sends".
        /// </para>
        /// <para>
        /// The lesson is not "measure harder". It is that <b>a capture shows what the traffic
        /// NEEDED, never what the protocol ALLOWS</b>. A machine that had six frames to send sends
        /// six; it says nothing about seven. So this value is a FLOOR on the real limit, not the
        /// limit, and no capture can ever establish the limit. What it does establish is the one
        /// thing that matters: a real ND does go this wide, so going this wide cannot flood a peer.
        /// </para>
        /// <para>
        /// The 6 is D100 to D102 in
        /// <c>DOC\captures\ND-TO-ND-WRITE-2026-08-10\readback-proves-content.pcapng</c>, no
        /// emulated node involved. It is the widest across every pcapng under
        /// <c>DOC\captures\</c> with both ends real - eleven captures, swept in one pass rather
        /// than one at a time, which is what should have been done first. Measured two ways that
        /// agree: the Wireshark <c>ndlink</c> dissector's backlog field and <c>decode_hub.py</c>.
        /// </para>
        /// <para>
        /// If the true limit is ever wanted rather than a safe floor, it is in the ENCOS firmware,
        /// not in a capture.
        /// </para>
        /// <para>
        /// The live runs on 2026-08-11 say NOTHING about this value either way: a listing never got
        /// past a backlog of one, so the window was never reached.
        /// </para>
        /// <para>
        /// A window BELOW the real value is not incorrect - the queue simply releases frames one at
        /// a time as acknowledgements arrive, which is why a listing still worked at 2 - but it
        /// serialises exactly the content bursts that carry file data.
        /// </para>
        /// <para><b>What no window cost, live on D100, 2026-08-10 23:19</b></para>
        /// <para>
        /// Our unacknowledged backlog grew and never came down: 3 frames, then 7, then 15, then 33
        /// within three seconds. D100 fell behind, and from that point it re-sent every datagram it
        /// had not seen acknowledged - byte for byte the same frames, its link sequences 5 through
        /// 9, then 10 through 16, over and over. Our file server took each repeat for a new request,
        /// answered it with a fresh session counter and a fresh connection number, and D100 gave up
        /// with SINTRAN error 267 octal, FILE-ACCESS PROTOCOL ERROR. The duplicate requests, the
        /// nine repeated connect letters and the directory walk that never advanced past entry zero
        /// were all one fault seen from higher up.
        /// </para>
        /// <para>
        /// The value is the largest a real ND has ever been seen to use, so it cannot be too
        /// generous by more than whatever the real limit is above it. The acknowledgement frame
        /// carries no credit field - its trailing word is <c>0000</c> on every captured
        /// acknowledgement from both machines - so the peer never tells us a number and this cannot
        /// be negotiated. There IS a separate window NPDU in the protocol
        /// (<see cref="NdNpduType.Window"/>, index 4) but it has never been captured.
        /// </para>
        /// <para>
        /// <c>NdLinkCaptureConformanceTests</c> pins this from both sides: below the widest burst
        /// in the text captures and above <see cref="WidestBurstSeenFromARealMachine"/> both fail.
        /// It can only read the TEXT captures - the hub captures are pcapng and need TCP
        /// reassembly - so the upper bound is carried as a constant with its citation rather than
        /// measured in the test. Teaching that test to read pcapng would close the gap.
        /// </para>
        /// </remarks>
        public const int SendWindow = 6;

        /// <summary>
        /// The widest burst any real ND machine has been observed to send before waiting.
        /// </summary>
        /// <remarks>
        /// D100 to D102 in
        /// <c>DOC\captures\ND-TO-ND-WRITE-2026-08-10\readback-proves-content.pcapng</c>, the widest
        /// across every pcapng under <c>DOC\captures\</c> with both ends real. Measured 2026-08-11
        /// by two independent tools that agree. Nothing may set <see cref="SendWindow"/> above this
        /// without a capture showing a real machine going wider - that is the line between
        /// measuring and inventing. Raising it because a transfer feels slow is inventing.
        /// </remarks>
        public const int WidestBurstSeenFromARealMachine = 6;

        /// <summary>
        /// How many datagrams may wait for the window before this node starts refusing them.
        /// </summary>
        /// <remarks>
        /// A limit rather than an unbounded queue, so a peer that stops acknowledging costs a fixed
        /// amount of memory and shows up as a refusal instead of quietly swallowing the machine. The
        /// number is ours, not the protocol's - nothing on the wire says what it should be.
        /// </remarks>
        public const int MaxQueuedDatagrams = 64;

        /// <summary>
        /// How many frames may be outstanding before the peer has told us where our numbering is.
        /// </summary>
        /// <remarks>
        /// One, deliberately. A peer that has carried state over from a previous session of ours
        /// throws away everything at the wrong sequence, so exactly one frame can be lost that way
        /// and exactly one has to be kept and sent again. Opening at the full
        /// <see cref="SendWindow"/> would lose four. See <c>TakeAcknowledgement</c>.
        /// </remarks>
        public const int UnpositionedWindow = 1;

        private readonly ushort _localLinkId;
        private readonly NdMacAddress _localMac;
        private readonly Action<byte[], int> _sendFrame;

        /// <summary>
        /// Datagrams handed to us while the send window was full, oldest first.
        /// </summary>
        /// <remarks>
        /// Each entry is a copy: the caller's span is only valid for the duration of its call.
        /// </remarks>
        private readonly System.Collections.Generic.Queue<byte[]> _waiting =
            new System.Collections.Generic.Queue<byte[]>();

        private byte[] _frameBuffer = new byte[1600];
        private byte _nextSequence = InitialSequence;

        /// <summary>
        /// The sequence the peer's last acknowledgement said it expects from us next.
        /// </summary>
        private byte _peerNextExpected = InitialSequence;

        /// <summary>
        /// The sequence we expect on the peer's next data frame.
        /// </summary>
        private byte _nextExpectedFromPeer;

        /// <summary>
        /// Whether a data frame has arrived from the peer yet, so its numbering is known.
        /// </summary>
        private bool _haveSeenPeerData;

        /// <summary>
        /// Whether the peer has acknowledged anything yet, so OUR numbering is known to agree.
        /// </summary>
        private bool _havePeerPosition;

        /// <summary>
        /// The one datagram sent before the peer's position was known, kept so it can go again if
        /// the peer turns out to have thrown it away.
        /// </summary>
        private byte[]? _unpositioned;

        /// <summary>
        /// Initialises the link layer.
        /// </summary>
        /// <param name="localSystemNumber">
        /// This node's ND system number, used to build its station address.
        /// </param>
        /// <param name="localLinkId">
        /// This node's link id, placed in the sender field of outgoing frames.
        /// </param>
        /// <param name="sendFrame">
        /// Sends a complete Ethernet frame; called with the buffer and its length.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="sendFrame"/> is null.
        /// </exception>
        public NdLinkLayer(ushort localSystemNumber, ushort localLinkId, Action<byte[], int> sendFrame)
        {
            _localMac = NdMacAddress.FromSystemNumber(localSystemNumber);
            _localLinkId = localLinkId;
            _sendFrame = sendFrame ?? throw new ArgumentNullException(nameof(sendFrame));
            LocalSystemNumber = localSystemNumber;
        }

        /// <summary>
        /// Occurs when a data frame's SINTRAN datagram has been extracted.
        /// </summary>
        public event NdLinkPayloadReceived? PayloadReceived;

        /// <summary>
        /// Occurs when a frame carries a kind other than data or acknowledgement.
        /// </summary>
        public event UnknownFrameKindReceived? OnUnknownFrameKindReceived;

        /// <summary>
        /// Occurs when the peer re-sends a data frame we have already taken.
        /// </summary>
        /// <remarks>
        /// Worth a warning in any log that has one - see <see cref="DuplicateDataFrameReceived"/>
        /// for what it cost to find this by hand the first time.
        /// </remarks>
        public event DuplicateDataFrameReceived? OnDuplicateDataFrameReceived;

        /// <summary>
        /// Reports an acknowledgement that was refused for being behind our acknowledged edge.
        /// </summary>
        /// <param name="acknowledged">
        /// The sequence the peer acknowledged.
        /// </param>
        /// <param name="peerNextExpected">
        /// Where we already believe the peer to be.
        /// </param>
        /// <param name="nextSequence">
        /// The next sequence we would send.
        /// </param>
        /// <param name="queued">
        /// How many datagrams are parked behind the window.
        /// </param>
        public delegate void StaleAcknowledgementReceived(
            byte acknowledged, byte peerNextExpected, byte nextSequence, int queued);

        /// <summary>
        /// Occurs when an acknowledgement is refused for being stale.
        /// </summary>
        /// <remarks>
        /// One is ordinary - a duplicate in flight. A RUN of them alongside a climbing queue is the
        /// deadlock described in <c>TakeAcknowledgement</c>, and is worth acting on.
        /// </remarks>
        public event StaleAcknowledgementReceived? StaleAcknowledgement;

        /// <summary>
        /// Occurs when the peer asks to tear the link down. Carries the raw kind byte, because
        /// there are two disconnect-request types (by user and by network service) and only the
        /// by-network-service one has ever been seen on the wire.
        /// </summary>
        public event DisconnectRequested? OnDisconnectRequested;

        /// <summary>
        /// Gets the number of connection requests received from the peer.
        /// </summary>
        public int ConnectionRequestsReceived { get; private set; }

        /// <summary>
        /// Gets the number of connection confirms this node has sent.
        /// </summary>
        /// <remarks>
        /// If this keeps climbing while the peer goes on repeating its request, the confirm is
        /// being rejected or ignored - which is the signal that
        /// <see cref="ConnectionConfirmKindUnverified"/> or the field layout is wrong.
        /// </remarks>
        public int ConnectionConfirmsSent { get; private set; }

        /// <summary>
        /// Gets the number of disconnect requests received from the peer.
        /// </summary>
        public int DisconnectRequestsReceived { get; private set; }

        /// <summary>
        /// Gets this node's ND system number.
        /// </summary>
        public ushort LocalSystemNumber { get; }

        /// <summary>
        /// Gets this node's station address.
        /// </summary>
        public NdMacAddress LocalMac => _localMac;

        /// <summary>
        /// Gets the peer's link id, once learned.
        /// </summary>
        public ushort PeerLinkId { get; private set; } = UnknownPeerLinkId;

        /// <summary>
        /// Gets the peer's station address, once a frame has arrived from it.
        /// </summary>
        public NdMacAddress PeerMac { get; private set; }

        /// <summary>
        /// Gets a value indicating whether a frame has been received and the peer's identity learned.
        /// </summary>
        public bool HasLearnedPeer { get; private set; }

        /// <summary>
        /// Occurs the first time the peer becomes known, RAISED BEFORE the frame that taught us is
        /// delivered upward.
        /// </summary>
        /// <remarks>
        /// The ordering is the whole point. A caller that waits for <c>HandleFrame</c> to RETURN
        /// before treating the link as usable is too late: the datagram inside that same frame has
        /// already been delivered up and its reply already attempted and refused, because the link
        /// still looked unusable. MEASURED against a live SINTRAN 2026-08-04 - the reply to the
        /// first datagram of a reused connection was dropped exactly this way, which the remote saw
        /// as no answer at all.
        /// </remarks>
        public event PeerLearned? OnPeerLearned;

        /// <summary>
        /// Gets the sequence number that will be used by the next data frame sent.
        /// </summary>
        public byte NextSequence => _nextSequence;

        /// <summary>
        /// Gets the number of data frames received.
        /// </summary>
        public long DataFramesReceived { get; private set; }

        /// <summary>
        /// Gets the number of acknowledgements received.
        /// </summary>
        public long AcknowledgementsReceived { get; private set; }

        /// <summary>
        /// Gets how many data frames this node has sent that the peer has not acknowledged yet.
        /// </summary>
        /// <remarks>
        /// Never more than <see cref="SendWindow"/>. If this sits at the window while
        /// <see cref="QueuedDatagrams"/> climbs, the peer has stopped acknowledging us.
        /// </remarks>
        public int OutstandingFrames
        {
            get { return (_nextSequence - _peerNextExpected + NdLinkHeader.SequenceModulus) % NdLinkHeader.SequenceModulus; }
        }

        /// <summary>
        /// Gets how many frames may be outstanding right now.
        /// </summary>
        /// <remarks>
        /// Exposed because "the send window is full" is not a diagnosis on its own. Full at SIX is a
        /// busy link; full at ONE means the peer has never placed us and the window never grew - a
        /// completely different fault wearing the same words. That distinction cost a live run on
        /// 2026-08-17, when two parked frames were enough to fill it.
        /// </remarks>
        public int Window
        {
            get { return CurrentWindow; }
        }

        /// <summary>
        /// Gets how many datagrams are waiting for room in the send window.
        /// </summary>
        public int QueuedDatagrams
        {
            get { return _waiting.Count; }
        }

        /// <summary>
        /// Gets the number of datagrams refused because the queue behind a full window was itself
        /// full.
        /// </summary>
        /// <remarks>
        /// Anything above zero means this node produced messages faster than the peer would take
        /// them for long enough to fill <see cref="MaxQueuedDatagrams"/>, and those messages were
        /// never sent.
        /// </remarks>
        public long DatagramsRefusedQueueFull { get; private set; }

        /// <summary>
        /// Takes an acknowledgement, learning where the peer thinks our numbering is if this is the
        /// first one on this link.
        /// </summary>
        /// <param name="acknowledged">
        /// The sequence the peer says it expects from us next.
        /// </param>
        /// <remarks>
        /// <para><b>The peer does not start again when we do - MEASURED 2026-08-11</b></para>
        /// <para>
        /// Restarting the runner against a D100 that had NOT had XMSG restarted reproduced this at
        /// once. The previous session had ended with our sequence at 48 and D100 acknowledging 49.
        /// The new one opened at 0, and D100 answered every frame with "I expect 49" and threw all
        /// of them away - the connect letter for a file read among them. Nothing above the link saw
        /// an error; the conversation simply never started.
        /// </para>
        /// <para>
        /// So the peer's position is <b>learned from its first acknowledgement</b>, the same way its
        /// link id is learned rather than derived. An acknowledgement states outright which sequence
        /// it wants next, which is the only evidence available - our own numbering says nothing
        /// about what the other end remembers.
        /// </para>
        /// <para><b>Why not open a fresh connection instead</b></para>
        /// <para>
        /// The protocol has a connection request for this (<c>0x0F</c>, CR - see
        /// <c>DOC\COSMOS-ETHERNET-LINK-CONTROL-FRAMES-2026-08-03.md</c> section 3), and it would be
        /// the tidier answer. It is not used because <b>no answer to a <c>0x0F</c> has ever been
        /// captured</b>, and our own confirm kind byte is admittedly a guess - see
        /// <see cref="ConnectionConfirmKindUnverified"/>. Sending one and hoping would be inventing
        /// behaviour; reading the number the peer already tells us is not.
        /// </para>
        /// <para>
        /// After the first acknowledgement the ordinary forward-only rule applies: a repeated or
        /// reordered acknowledgement carries an older value, and taking it would re-open a window we
        /// have already used, which is how a peer's duplicate would turn into a burst from us.
        /// </para>
        /// </remarks>
        private void TakeAcknowledgement(byte acknowledged)
        {
            if (!_havePeerPosition)
            {
                _havePeerPosition = true;

                // In step already - a peer that really did start fresh. Nothing to correct.
                if (acknowledged == _nextSequence)
                {
                    _peerNextExpected = acknowledged;
                    return;
                }

                // The peer is somewhere else entirely, so the frame we sent was thrown away. Move
                // to where it is waiting and send that frame again. Only ONE frame can be in this
                // position - see UnpositionedWindow - so only one has to be kept and repeated.
                _peerNextExpected = acknowledged;
                _nextSequence = acknowledged;

                byte[]? lost = _unpositioned;
                _unpositioned = null;
                if (lost != null)
                {
                    TransmitDatagram(new ReadOnlySpan<byte>(lost));
                }

                return;
            }

            _unpositioned = null;
            if (IsAtOrAheadOfOurAcknowledgedEdge(acknowledged))
            {
                _peerNextExpected = acknowledged;
                return;
            }

            // A STALE ACKNOWLEDGEMENT, and saying so is the whole point of this branch.
            //
            // Rejecting it is right - it is behind where the peer has already placed us, and moving
            // backwards would resend frames it has. But a RUN of them is the signature of a
            // deadlock, and that is invisible without this line: our window fills, the frames the
            // peer needs in order to move forward are the ones parked behind that window, and it
            // goes on re-acknowledging the last thing it did receive. Neither side is at fault in a
            // way its own logs show.
            //
            // Found 2026-08-17, when three separate-looking failures - a TAD connect, an FA delete
            // and a listing - all turned out to be frames parked behind a window that never opened.
            StaleAcknowledgement?.Invoke(acknowledged, _peerNextExpected, _nextSequence, QueuedDatagrams);
        }

        /// <summary>
        /// Gets a value indicating whether the peer has told us where it thinks our numbering is.
        /// </summary>
        /// <remarks>
        /// False until its first acknowledgement arrives. While it is false only one frame is sent
        /// at a time - see <see cref="UnpositionedWindow"/>.
        /// </remarks>
        public bool HasLearnedPeerPosition
        {
            get { return _havePeerPosition; }
        }

        /// <summary>
        /// Gets the number of data frames the peer has re-sent to us.
        /// </summary>
        /// <remarks>
        /// <b>Anything above zero is a fault, and it is almost always ours.</b> A peer repeats
        /// because it has not seen our acknowledgement - either we are sending far ahead of the
        /// window (see <see cref="SendWindow"/>) or something is holding up our receive path. Check
        /// this before looking for a defect in any layer above.
        /// </remarks>
        public long DuplicateDataFramesReceived { get; private set; }

        /// <summary>
        /// Decides whether an incoming data frame is one the peer has already sent us, and records
        /// where the peer's numbering has reached.
        /// </summary>
        /// <param name="sequence">
        /// The sequence the frame carried.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the frame sits BEHIND what we are waiting for.
        /// </returns>
        /// <remarks>
        /// <para>
        /// The peer's numbering is learned from its first data frame rather than assumed, the same
        /// way its link id is - see the note on <see cref="HasLearnedPeer"/>.
        /// </para>
        /// <para>
        /// "Behind" and "ahead" are separated at HALF the sequence space, which is the only way to
        /// tell them apart in a wrapping counter. Nothing on the wire distinguishes them, so a
        /// frame more than 64 ahead is read as a repeat; on a link that stays within a window of
        /// two, that case cannot arise.
        /// </para>
        /// <para>
        /// A frame AHEAD of what we expect means one went missing. It is let through rather than
        /// dropped: we do not retransmit and neither position is proven, so refusing it would turn
        /// one lost frame into a link that never moves again. It does move the expectation on, so
        /// the gap is not reported over and over.
        /// </para>
        /// </remarks>
        private bool IsRepeatOfAFrameWeAlreadyTook(byte sequence)
        {
            if (!_haveSeenPeerData)
            {
                _haveSeenPeerData = true;
                _nextExpectedFromPeer = NdLinkHeader.NextSequence(sequence);
                return false;
            }

            int ahead = (sequence - _nextExpectedFromPeer + NdLinkHeader.SequenceModulus)
                % NdLinkHeader.SequenceModulus;
            if (ahead >= NdLinkHeader.SequenceModulus / 2)
            {
                return true;
            }

            _nextExpectedFromPeer = NdLinkHeader.NextSequence(sequence);
            return false;
        }

        /// <summary>
        /// Decides whether an acknowledgement sequence moves our window forward rather than back.
        /// </summary>
        /// <param name="acknowledged">
        /// The sequence the peer says it expects from us next.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when it lies between what the peer had already acknowledged and
        /// what we have actually sent.
        /// </returns>
        /// <remarks>
        /// Both edges are compared as distances from the current low edge, so the seven-bit wrap is
        /// handled without special cases. An acknowledgement past what we have sent is nonsense and
        /// is ignored rather than trusted.
        /// </remarks>
        private bool IsAtOrAheadOfOurAcknowledgedEdge(byte acknowledged)
        {
            int toAcknowledged = (acknowledged - _peerNextExpected + NdLinkHeader.SequenceModulus) % NdLinkHeader.SequenceModulus;
            int toSent = (_nextSequence - _peerNextExpected + NdLinkHeader.SequenceModulus) % NdLinkHeader.SequenceModulus;
            return toAcknowledged <= toSent;
        }

        /// <summary>
        /// Sends a SINTRAN datagram as a data frame.
        /// </summary>
        /// <param name="payload">
        /// The datagram bytes.
        /// </param>
        /// <returns>
        /// <see cref="NdSendOutcome.Transmitted"/> when the bytes are on the segment,
        /// <see cref="NdSendOutcome.Queued"/> when they are parked behind a full send window, and
        /// <see cref="NdSendOutcome.Refused"/> when the link would not take them at all.
        /// </returns>
        /// <remarks>
        /// <para>
        /// <see cref="NdSendOutcome.Refused"/> covers three cases: the peer is not yet known,
        /// because a frame addressed to nobody is not worth putting on the segment; the payload is
        /// empty; and the queue behind a full window is itself full, see
        /// <see cref="MaxQueuedDatagrams"/>. A node that must speak first has to be given the peer's
        /// address another way.
        /// </para>
        /// <para>
        /// <b>Queued is not sent.</b> This returned a plain bool until 2026-08-17, and the remark
        /// here already admitted that a true return "no longer means the bytes are on the wire" -
        /// but nothing SAID SO at runtime, so a live TAD connect hung with every log line reporting
        /// a healthy exchange. Distinguishing the two is now the caller's problem to acknowledge
        /// rather than the reader's to remember. <see cref="QueuedDatagrams"/> is the depth.
        /// </para>
        /// </remarks>
        public NdSendOutcome SendDatagram(ReadOnlySpan<byte> payload)
        {
            if (!HasLearnedPeer || payload.Length == 0)
            {
                return NdSendOutcome.Refused;
            }

            // Straight out when the peer has room for it. Everything else waits its turn, because a
            // real ND never runs more than SendWindow frames ahead of its peer's acknowledgements
            // and D100 gives up on a conversation when we do.
            if (OutstandingFrames < CurrentWindow)
            {
                // Until the peer has told us where our numbering is, keep the ONE frame we send, so
                // that if it turns out to have been thrown away it can go again at the right
                // sequence. See TakeAcknowledgement.
                if (!_havePeerPosition)
                {
                    _unpositioned = payload.ToArray();
                }

                TransmitDatagram(payload);
                return NdSendOutcome.Transmitted;
            }

            if (_waiting.Count >= MaxQueuedDatagrams)
            {
                DatagramsRefusedQueueFull++;
                return NdSendOutcome.Refused;
            }

            // PARKED, NOT SENT, and the caller is told which. This used to return the same true as
            // the branch above, which is how a hung terminal came to look like a healthy exchange in
            // every log we had - see NdSendOutcome.
            _waiting.Enqueue(payload.ToArray());
            return NdSendOutcome.Queued;
        }

        /// <summary>
        /// Builds and sends one data frame, advancing the sequence.
        /// </summary>
        /// <param name="payload">
        /// The datagram bytes.
        /// </param>
        private void TransmitDatagram(ReadOnlySpan<byte> payload)
        {
            int required = Ieee8023Frame.PayloadOffset + NdLinkHeader.Length + payload.Length;
            EnsureBuffer(required);

            Span<byte> llcPayload = stackalloc byte[NdLinkHeader.Length];
            // First id field is the DESTINATION's reference, second is our own - see the note on
            // SendConnectionConfirm. The builder's parameters are named the other way round.
            NdLinkHeader.Data(_nextSequence, PeerLinkId, _localLinkId, (ushort)payload.Length).Write(llcPayload);

            int written = BuildFrame(PeerMac, llcPayload, payload);
            _nextSequence = NdLinkHeader.NextSequence(_nextSequence);
            _sendFrame(_frameBuffer, written);
        }

        /// <summary>
        /// Sends as many queued datagrams as the window now allows.
        /// </summary>
        private void DrainWaiting()
        {
            while (_waiting.Count > 0 && OutstandingFrames < CurrentWindow)
            {
                byte[] datagram = _waiting.Dequeue();
                if (!_havePeerPosition)
                {
                    _unpositioned = datagram;
                }

                TransmitDatagram(new ReadOnlySpan<byte>(datagram));
            }
        }

        /// <summary>
        /// Gets how many frames may be outstanding right now.
        /// </summary>
        /// <remarks>
        /// <see cref="SendWindow"/> once the peer's position is known, and
        /// <see cref="UnpositionedWindow"/> before that.
        /// </remarks>
        private int CurrentWindow
        {
            get { return _havePeerPosition ? SendWindow : UnpositionedWindow; }
        }


        /// <summary>
        /// Processes one received Ethernet frame.
        /// </summary>
        /// <param name="frame">
        /// The frame bytes, starting at the destination MAC.
        /// </param>
        /// <param name="length">
        /// The number of valid bytes.
        /// </param>
        /// <returns>
        /// True when the frame was a well-formed ND/COSMOS frame and was processed.
        /// </returns>
        /// <remarks>
        /// A frame sourced from this node's own address is ignored: on a multicast segment a node
        /// hears its own transmissions, and processing them would acknowledge our own data and
        /// corrupt the sequence.
        /// </remarks>
        public bool HandleFrame(byte[] frame, int length)
        {
            if (frame == null || length <= 0)
            {
                return false;
            }

            ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(frame, 0, length);
            if (!Ieee8023Frame.TryParse(span, out NdMacAddress destination, out NdMacAddress source, out int payloadOffset, out int payloadLength))
            {
                return false;
            }

            // Our own frame looped back by the segment.
            if (source.Equals(_localMac))
            {
                return false;
            }

            // Not addressed to us. Only frames whose destination carries the ND vendor prefix are
            // filtered here, so a non-ND destination (including broadcast) still passes.
            //
            // An earlier version of this comment justified that by saying "COSMOS reachability
            // traffic uses broadcast". That is UNSUPPORTED and is not repeated: no captured COSMOS
            // frame has a broadcast destination, and the ENCOS receive path has no broadcast case
            // at all - a broadcast reaches the host only if FF:FF:FF:FF:FF:FF has been registered
            // in the group filter at 0x542C (ETHERNET-II-FEASIBILITY-AND-MODE-WORD-RE-2026-07-25.md
            // section 5). Whether COSMOS ever sends a non-unicast frame is simply UNKNOWN, so the
            // pass-through is left as-is rather than tightened on a guess in either direction.
            //
            // LAYER TRAP, and the likely origin of that wrong comment: an emulated segment run as
            // "--net=udp" is carried over IP multicast 239.3.9.4:3094, so EVERY frame travels by
            // multicast whatever its destination MAC. That is the TRANSPORT being multicast and
            // says nothing about what COSMOS puts in the destination address. Do not read one as
            // evidence for the other.
            //
            // NOTE this does NOT make one instance safe on a shared segment: a frame unicast to us
            // by a DIFFERENT station is accepted here and re-learns the peer below. Binding to one
            // peer is the caller's job - see EthernetLink.
            if (destination.HasNdVendorPrefix && !destination.Equals(_localMac))
            {
                return false;
            }

            if (payloadLength < NdLinkHeader.Length)
            {
                return false;
            }

            if (!NdLinkHeader.TryParse(span.Slice(payloadOffset, payloadLength), out NdLinkHeader header))
            {
                return false;
            }

            // The peer's own reference is in bytes 7-8, which TryParse puts in ReceiverLinkId - the
            // property names are the wrong way round, see the note on SendConnectionConfirm. Reading
            // SenderLinkId here learned OUR OWN reference as the peer's, so every reply addressed
            // D100 as "1" and it answered "DR BY NS reason 1" even after the link was up.
            //
            // A connection request carries zero for the destination and cannot teach us anything, so
            // only learn from frames that actually carry the peer's reference.
            if (header.ReceiverLinkId != 0)
            {
                LearnPeer(source, header.ReceiverLinkId);
            }
            else
            {
                bool firstContact = !HasLearnedPeer;
                PeerMac = source;
                HasLearnedPeer = true;
                if (firstContact)
                {
                    OnPeerLearned?.Invoke();
                }
            }

            if (header.IsAcknowledge)
            {
                AcknowledgementsReceived++;
                TakeAcknowledgement(header.Sequence);
                DrainWaiting();
                return true;
            }

            if (header.IsConnectionRequest)
            {
                ConnectionRequestsReceived++;
                SendConnectionConfirm(header);

                // A CONNECTION REQUEST ENDS THE OLD CONNECTION JUST AS SURELY AS A DISCONNECT DOES,
                // so the send state dies with it - the same three lines as the disconnect path
                // below, for the same reason, and see that comment for the mechanism.
                //
                // MEASURED on the live segment 2026-08-27, and it cost the whole afternoon's build
                // path. The runner restarted while D100 was still mid-run on link 048C, so our send
                // sequence began again at 0 where D100 expected a far higher number. D100 discarded
                // that frame in SILENCE - no acknowledgement and no error:
                //
                //   17:07:16.939  us -> 100  DT seq=00 snd=048C rcv=0001   never acknowledged
                //   17:07:56.642  100-> us   DT seq=45   the same payload again
                //   17:08:36.454  100-> us   DT seq=46   and again
                //   17:09:16.241  100-> us   CR seq=79 snd=0000 rcv=048D   gives up, NEW link
                //
                // D100 rebuilt the link perfectly well. This node confirmed it and then never
                // transmitted again - ZERO data frames in the next fifty-eight minutes, while the
                // runner's own log reported four connect letters "accepted by our transport". They
                // were all queued behind the one frame left outstanding on a connection that no
                // longer existed: outstanding 1, unpositioned window 1, and 1 < 1 is false.
                //
                // On D100 this showed up as "no access to system 19999" and its file server never
                // answering - neither of which is where the fault was.
                _peerNextExpected = _nextSequence;   // nothing is outstanding on a link that is gone
                _havePeerPosition = false;           // the new connection has to place us again
                _unpositioned = null;                // and the frame kept for a resend is stale now
                DrainWaiting();
                return true;
            }

            if (header.IsDisconnectRequest)
            {
                DisconnectRequestsReceived++;

                // THE SEND STATE BELONGS TO THE CONNECTION, so it dies with it. Without this, the
                // frames we had outstanding on the torn-down link stay counted for ever: a peer that
                // disconnects with one of ours unacknowledged leaves OutstandingFrames at 1, the
                // unpositioned window is also 1, and 1 < 1 is false - so every datagram from then on
                // is queued and NOTHING ever leaves this node again, on a link that has meanwhile
                // been rebuilt perfectly well underneath.
                //
                // MEASURED 2026-08-17: that is exactly how a live TAD connect from D100 hung. See
                // DOC/TAD-CONNECT-QUEUED-NOT-SENT-2026-08-17.md.
                //
                // The queue is KEPT. Those datagrams were accepted from callers who were told
                // Queued, not Refused, and the peer reconnects immediately in practice - so they go
                // out on the new connection rather than being dropped on the floor. DrainWaiting
                // respects the unpositioned window of one, so exactly one goes now and the rest
                // follow as the peer acknowledges.
                _peerNextExpected = _nextSequence;   // nothing is outstanding on a link that is gone
                _havePeerPosition = false;           // the new connection has to place us again
                _unpositioned = null;                // and the frame kept for a resend is stale now
                OnDisconnectRequested?.Invoke(header.Kind);
                DrainWaiting();
                return true;
            }

            if (!header.IsData)
            {
                OnUnknownFrameKindReceived?.Invoke(header.Kind);
                return true;
            }

            DataFramesReceived++;

            int datagramOffset = payloadOffset + NdLinkHeader.Length;
            int available = payloadLength - NdLinkHeader.Length;
            int datagramLength = header.PayloadLength <= available ? header.PayloadLength : available;

            // Acknowledge FIRST and unconditionally, including a repeat. A peer repeats precisely
            // because it did not see the acknowledgement, so withholding it here would keep it
            // repeating for ever.
            if (IsRepeatOfAFrameWeAlreadyTook(header.Sequence))
            {
                DuplicateDataFramesReceived++;
                OnDuplicateDataFrameReceived?.Invoke(header.Sequence, _nextExpectedFromPeer);
            }

            SendAcknowledgement(header.Sequence);

            // A REPEAT IS STILL DELIVERED UPWARD, and that is not an oversight.
            //
            // It was dropped here for one afternoon on 2026-08-11, reasoned from what a sequence
            // number is for: a link that hands the same frame up twice is not doing its job, and
            // re-delivery is what let a repeat reach the file server as a fresh request. The
            // comment said outright that this was REASONED, NOT MEASURED.
            //
            // Measuring it disproved it. A live file pull the same afternoon stalled dead after
            // four blocks with 174 repeats, D100 re-sending sequences 86, 87 and 88 ten times each
            // until the transfer died. Our own backlog never exceeded the window, so we were not
            // flooding it - see xmsg-run-F-pull-traced-stalled.log.
            //
            // The reason the drop cannot work: D100's retransmission is driven by the DATAGRAM
            // layer above this one, which is waiting for a subtype-0x03 acknowledgement from the
            // destination port. That acknowledgement is built by the layers above from the
            // datagram itself. Swallow the repeat here and the datagram never reaches them, the
            // acknowledgement is never rebuilt, and the peer's retransmission can never resolve -
            // a recoverable hiccup becomes a permanent stall.
            //
            // The damage the drop was meant to prevent came from the FLOOD, and the send window
            // prevents that at the source. What stays is the counting and the warning, which cost
            // nothing and are what found this.
            if (datagramLength > 0)
            {
                byte[] datagram = new byte[datagramLength];
                Array.Copy(frame, datagramOffset, datagram, 0, datagramLength);
                PayloadReceived?.Invoke(datagram, datagramLength);
            }

            return true;
        }

        /// <summary>
        /// Records the peer's address and link id from a received frame.
        /// </summary>
        /// <param name="source">
        /// The peer's station address.
        /// </param>
        /// <param name="senderLinkId">
        /// The link id the peer put in the sender field.
        /// </param>
        private void LearnPeer(NdMacAddress source, ushort senderLinkId)
        {
            bool first = !HasLearnedPeer;
            PeerMac = source;
            PeerLinkId = senderLinkId;
            HasLearnedPeer = true;
            if (first)
            {
                OnPeerLearned?.Invoke();
            }
        }

        /// <summary>
        /// Sends the acknowledgement for a received data frame.
        /// </summary>
        /// <param name="receivedSequence">
        /// The sequence number being acknowledged.
        /// </param>
        private void SendAcknowledgement(byte receivedSequence)
        {
            EnsureBuffer(Ieee8023Frame.MinimumFrameLength);

            Span<byte> llcPayload = stackalloc byte[NdLinkHeader.Length];
            // Destination reference first, ours second - same correction as the data path.
            //
            // AcknowledgeFor adds one to get the next expected value, and that addition has to wrap
            // in the SEVEN-bit space the wire uses, so 0x7F is acknowledged with 0x00 and never
            // 0x80. Stepping it here keeps the one wrapping rule in one place - see Advance.
            NdLinkHeader.AcknowledgeFor(receivedSequence, PeerLinkId, _localLinkId).Write(llcPayload);

            int written = BuildFrame(PeerMac, llcPayload, ReadOnlySpan<byte>.Empty);
            _sendFrame(_frameBuffer, written);
        }

        /// <summary>
        /// The wire byte this node sends for a connection confirm. CONFIRMED on 2026-08-27 - see
        /// the remarks for the capture that settled it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The high nibble is the NPDU type and CC is type 1, so the top half is solid. The LOW
        /// nibble is a GUESS: every control frame captured so far ends in <c>F</c> - CR
        /// <c>0x0F</c>, AK <c>0x3F</c>, DR <c>0x6F</c> - and only the data frame <c>0x20</c> ends
        /// in <c>0</c>. <c>0x1F</c> follows that pattern. No CC has ever been captured, so this is
        /// a testable proposal, not a fact.
        /// </para>
        /// <para>
        /// How to tell whether it is right: after we answer a CR with this byte, a peer that
        /// accepts it should stop repeating the CR and move on to sending DT. If it keeps
        /// repeating, the byte or the field layout is wrong.
        /// </para>
        /// <para><b>That test was run, and the byte is RIGHT.</b></para>
        /// <para>
        /// Hub capture, 2026-08-27, D100 (08:00:26:64:00:00) to this node (08:00:26:1F:4E:00,
        /// which is 19999 = 0x4E1F little-endian):
        /// </para>
        /// <code>
        /// 17:09:16.241  D100 -> us   ...02 0F 00 79...   CR, sent ONCE
        /// 17:09:16.241  us   -> D100 ...02 1F 00 79...   this byte
        /// 17:09:16.243  D100 -> us   ...02 20 00 00...   DT, 2 ms later
        /// </code>
        /// <para>
        /// D100 sent the CR once, took the confirm, and moved on to data without repeating - which
        /// is exactly the accept case the test above describes. The low nibble F, guessed from the
        /// other control frames all ending in F, is correct.
        /// </para>
        /// <para>
        /// The name is kept as-is so callers do not have to change; only the claim has been
        /// corrected. What the capture does NOT explain is why this node then sent only ONE data
        /// frame in ninety minutes while its own log claimed four connect letters - that is a
        /// separate, still-open fault above this layer.
        /// </para>
        /// </remarks>
        public const byte ConnectionConfirmKindUnverified = 0x1F;

        /// <summary>
        /// Asks the peer to open the link from scratch, so both ends drop whatever they remember
        /// about each other.
        /// </summary>
        /// <param name="destination">
        /// The peer's station address. Needed because this may be sent before any frame has
        /// arrived, so <see cref="PeerMac"/> may not be known yet.
        /// </param>
        /// <returns>
        /// True when the request was handed to the transport.
        /// </returns>
        /// <remarks>
        /// <para><b>What this is for - MEASURED 2026-08-11</b></para>
        /// <para>
        /// On the Ethernet path a peer does NOT forget us when our process restarts. D100 keeps its
        /// link reference and the datagram sequence it expects from us, so a fresh runner opening
        /// at zero is rejected - first in silence, and once we announced ourselves, with an
        /// explicit XENSE (<c>0xFFDE</c>, network sequencing error). Nothing above the link can fix
        /// that: the sequence it objects to is the one it has been remembering.
        /// </para>
        /// <para>
        /// The ONE thing observed to clear it is the link being opened again. In the single
        /// Ethernet run that worked end to end, D100 sent a connection request, we confirmed it,
        /// a reachability exchange followed, and both sides then counted from zero. Every run after
        /// it reused the old link and failed.
        /// </para>
        /// <para><b>Shape, from the captured requests D100 sends us</b></para>
        ///  - kind <c>0x0F</c>, CONFIRMED on the wire.
        ///  - the FIRST id field is the DESTINATION's reference and is zero, because a requester has
        ///    no link yet; the SECOND carries the sender's own. Same order as
        ///    <see cref="SendConnectionConfirm"/>, which is the opposite of what the property names
        ///    suggest.
        ///  - the trailing field carries the SENDER'S OWN system number, not a payload length -
        ///    D100's requests carry <c>0x0064</c> = 100.
        ///  - no payload at all; the 802.3 length is <c>0x000E</c>.
        /// <para><b>ANSWERED, and the answer is NO - MEASURED 2026-08-11</b></para>
        /// <para>
        /// This was written not knowing whether a real ND would answer one of ours at all. It does,
        /// in 51 milliseconds, and the answer is a DISCONNECT REQUEST:
        /// </para>
        /// <code>
        /// 13:40:55.930  us -> D100    connection request (0x0F)
        /// 13:40:55.981  D100 -> us    0B02 6F 00 26 0001 0000 0105   disconnect by network service
        /// </code>
        /// <para>
        /// D100 then went silent and did NOT re-open the link, so the conversation was worse off
        /// than before: the peer had at least been talking to us. So this does not do what it was
        /// built for, and it is left OFF. It is kept, rather than deleted, because it is the only
        /// thing that has ever got an answer out of an ND to a frame of this kind, and the answer
        /// itself is a fact worth having - a real machine treats an unexpected connection request
        /// on an established link as a reason to tear it down.
        /// </para>
        /// <para>
        /// STILL UNKNOWN: whether answering that disconnect request (with a disconnect confirm,
        /// NPDU index 7, whose wire byte has never been captured) and THEN sending a fresh request
        /// would open a clean link. That is the next thing to try if this line is picked up again.
        /// </para>
        /// </remarks>
        public bool SendConnectionRequest(NdMacAddress destination)
        {
            EnsureBuffer(Ieee8023Frame.MinimumFrameLength);

            NdLinkHeader request = new NdLinkHeader(
                (byte)NdLinkFrameKind.ConnectionRequest,
                _nextSequence,
                UnknownPeerLinkId,
                _localLinkId,
                LocalSystemNumber);

            Span<byte> llcPayload = stackalloc byte[NdLinkHeader.Length];
            request.Write(llcPayload);

            int written = BuildFrame(destination, llcPayload, ReadOnlySpan<byte>.Empty);
            _sendFrame(_frameBuffer, written);
            ConnectionRequestsSent++;
            return true;
        }

        /// <summary>
        /// Gets the number of connection requests this node has sent.
        /// </summary>
        public int ConnectionRequestsSent { get; private set; }

        /// <summary>
        /// Answers a connection request so the peer can finish opening the link.
        /// </summary>
        /// <param name="request">
        /// The connection request being answered.
        /// </param>
        /// <remarks>
        /// EVERY field here except the sender link id is UNVERIFIED, because no connection confirm
        /// has been captured. What the captured CR frames DO establish, from both the D100-to-D102
        /// and the D100-to-D19999 traces:
        ///  - The sender link id is 0, because the requester has no link yet.
        ///  - The trailing field carries the SENDER'S OWN system number, not a payload length
        ///    (D100's CR carries <c>0x0064</c> = 100), so we answer with ours.
        ///  - The receiver field is NOT a link id. It steps in lockstep with the sequence number
        ///    (seq 49 with 11553, seq 50 with 11554), so it behaves as a counter or token. We echo
        ///    it back unchanged, on the assumption the requester uses it to match the answer to its
        ///    request.
        /// We put our own link id in the sender field so the peer can learn it, which is the one
        /// part of this frame the protocol clearly needs.
        /// </remarks>
        private void SendConnectionConfirm(NdLinkHeader request)
        {
            EnsureBuffer(Ieee8023Frame.MinimumFrameLength);

            // Field order, from D100's own trace decode of a connection request:
            //     "NPDU out to ND19999 loc 11557 rem 0  CR from ND 100"
            // D100 knows its OWN reference and not ours, and the frame carries 0 in the FIRST id
            // field with 11557 in the SECOND. So the first field is the DESTINATION's reference and
            // the second is the SENDER'S OWN - the opposite of what the property names suggest.
            //
            // The first attempt filled them the other way round and D100 answered every confirm
            // with "DR BY NS reason 1", ten retries then FAILED TRANSMIT. So the confirm must echo
            // the requester's reference back as the destination and carry ours as the source.
            NdLinkHeader confirm = new NdLinkHeader(
                ConnectionConfirmKindUnverified,
                request.Sequence,
                request.ReceiverLinkId,
                _localLinkId,
                LocalSystemNumber);

            Span<byte> llcPayload = stackalloc byte[NdLinkHeader.Length];
            confirm.Write(llcPayload);

            int written = BuildFrame(PeerMac, llcPayload, ReadOnlySpan<byte>.Empty);
            _sendFrame(_frameBuffer, written);
            ConnectionConfirmsSent++;
        }

        /// <summary>
        /// Builds an Ethernet frame from a link header and its datagram into the shared buffer.
        /// </summary>
        /// <param name="destination">
        /// The destination station address.
        /// </param>
        /// <param name="linkHeader">
        /// The already-written 11-byte link header.
        /// </param>
        /// <param name="datagram">
        /// The datagram to place after it; may be empty.
        /// </param>
        /// <returns>
        /// The number of bytes written to the buffer.
        /// </returns>
        private int BuildFrame(NdMacAddress destination, ReadOnlySpan<byte> linkHeader, ReadOnlySpan<byte> datagram)
        {
            // The LLC payload is the link header followed by the datagram; assemble it in place at
            // the frame's payload offset to avoid a second copy.
            int payloadLength = linkHeader.Length + datagram.Length;
            Span<byte> scratch = payloadLength <= 256 ? stackalloc byte[payloadLength] : new byte[payloadLength];
            linkHeader.CopyTo(scratch);
            if (datagram.Length > 0)
            {
                datagram.CopyTo(scratch.Slice(linkHeader.Length));
            }

            return Ieee8023Frame.Build(destination, _localMac, scratch, _frameBuffer);
        }

        /// <summary>
        /// Grows the shared frame buffer when a larger frame is needed.
        /// </summary>
        /// <param name="required">
        /// The number of bytes needed.
        /// </param>
        private void EnsureBuffer(int required)
        {
            if (_frameBuffer.Length < required)
            {
                _frameBuffer = new byte[required];
            }
        }
    }
}
