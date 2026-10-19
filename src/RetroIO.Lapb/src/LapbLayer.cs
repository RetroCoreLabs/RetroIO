//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Hdlc;

namespace RetroIO.Protocols.Xmsg.Live
{
    /// <summary>
    /// A driven, modulo-8 LAPB Asynchronous Balanced Mode link state machine conforming to the
    /// authoritative ND LAPB spec (sections 3-7): balanced bring-up, a full transmit window with
    /// go-back-N, cumulative acknowledgement, RNR flow control, FRMR error reporting, and the
    /// T1/T3/N2 timers.
    /// </summary>
    /// <remarks>
    /// <para><b>Time model</b></para>
    /// Time is injected as an opaque monotonic value through the clocked methods
    /// (<see cref="Connect(long)"/>, <see cref="OnFrameReceived(LapbFrame, long)"/>,
    /// <see cref="SendInformation(ReadOnlySpan{byte}, long)"/>, <see cref="Tick(long)"/>). The link
    /// never reads a wall clock, so replay and unit tests are fully deterministic. The timer
    /// durations in <see cref="LapbOptions"/> are expressed in this same unit.
    /// <para><b>Payload independence (spec 1.1)</b></para>
    /// The information field is opaque: this layer never parses it and holds no X.25/XMSG type. It
    /// delivers received I-frame payloads through <see cref="OnInformation"/> and accepts payloads to
    /// send through <see cref="SendInformation(ReadOnlySpan{byte}, long)"/>.
    /// <para><b>No sequence adoption (spec 3.2)</b></para>
    /// On every SABM (our SABM acknowledged by UA, or any SABM received including mid-session), the
    /// sequence variables are hard-zeroed and the retransmit queue is cleared. The link NEVER
    /// initialises V(R)/V(S) from a peer frame's N(S)/N(R).
    /// </remarks>
    public sealed class LapbLayer
    {
        /// <summary>
        /// LAPB address for link-management frames: SABM, UA, DISC, DM, FRMR (spec 2.1).
        /// </summary>
        public const byte AddressLinkSetup = 0x01;

        /// <summary>
        /// Base LAPB address for data-transfer frames: I, RR, RNR, REJ (spec 2.1).
        /// </summary>
        /// <remarks>
        /// ND extension (capture-VERIFIED across 230+ data I-frames / 378 RRs, both directions):
        /// address bit <see cref="AddressOddLengthBit"/> (<c>0x80</c>) is an ODD-INFO-LENGTH marker -
        /// the address is <c>0x89</c> when the frame's information field has an odd byte count and
        /// <c>0x09</c> when it is even. It is NOT the ITU-T command/response bit (standard LAPB has no
        /// length signalling in the address). S/U frames always carry the 2-byte node number (even), so
        /// they are always <c>0x09</c> / <c>0x01</c>. INFERRED mechanism: the ND-100 HDLC DMA moves
        /// 16-bit words, so the receiver needs to know whether the final word of the info field holds
        /// one or two valid bytes; a wrong flag off-by-ones the reconstructed length, the FCS check
        /// lands on the wrong bytes, and 100 silently discards the frame as a line error (V(R) frozen).
        /// That silent discard was the login username-accepted stall and the Problem-A odd-length hangs.
        /// See XMSG-PROTOCOL.md section 3.1 / 3.6 and LAPB-REQUIREMENTS.md A1b.
        /// </remarks>
        public const byte AddressData = 0x09;

        /// <summary>
        /// The ND odd-info-length marker bit OR-ed into the data-transfer address when the information
        /// field byte count is odd (<c>0x09</c> becomes <c>0x89</c>). See <see cref="AddressData"/>.
        /// </summary>
        public const byte AddressOddLengthBit = 0x80;

        /// <summary>
        /// ITU-T X.25 2.4.2 address A, single link operation: on commands from the DCE and responses
        /// from the DTE. Coded 11000000 in transmission order, which is 0x03 in a byte.
        /// </summary>
        public const byte AddressItuA = 0x03;

        /// <summary>
        /// ITU-T X.25 2.4.2 address B, single link operation: on commands from the DTE and responses
        /// from the DCE. Coded 10000000 in transmission order, which is 0x01 in a byte.
        /// </summary>
        public const byte AddressItuB = 0x01;

        /// <summary>
        /// Maximum information-field length; a longer received I-field is FRMR reason Y (spec 2.3.2).
        /// </summary>
        /// <remarks>
        /// <para><b>RAISED 2026-08-05 from 312, which would have rejected real traffic</b></para>
        /// Requirement A5 in <c>DOC\LAPB-REQUIREMENTS.md</c> says 312, and the same document names
        /// the recorded captures as its source of truth. Measured over all 33 of them - 3673
        /// information frames:
        /// <code>
        /// largest info field    622
        /// over the stated 312   452
        /// size bands   0:3041  100:132  200:48  400:226  600:226
        /// </code>
        /// The 226/226 pairing is the point: those are the fragment pairs of a segmented message -
        /// a 622-byte first fragment and its continuation - and they occur in exactly the four
        /// transfer captures and nowhere else. Every other class of traffic (file access, listing,
        /// TAD, routing) tops out at 292, well inside the old limit.
        /// <para><b>So 312 held for everything except file CONTENT</b></para>
        /// Which is the one thing that has to be fragmented in the first place. At 312 we could
        /// SEND a file and not receive one: all 452 of those frames would have been answered with
        /// an FRMR instead of being delivered.
        /// <para><b>Why this exact value, and why it is not a guess</b></para>
        /// It is DERIVED, not observed. A first fragment carries the full 28-byte addressing head
        /// and a fixed <c>SintranMessageFragment.FirstFragmentBodyLength</c> of body, so
        /// 594 + 28 = 622 is the largest frame the fragmentation scheme can produce - which is
        /// exactly the largest ever recorded. A continuation is shorter (it carries no sub-header),
        /// and an unfragmented frame is shorter still by definition.
        /// <para><b>RESOLVED 2026-08-07: the spec's 312 is in WORDS</b></para>
        /// ND's own generation-variable file <c>XMSG-SYS-DEF-L.SYMB</c> defines
        /// <code>
        /// X4FSO, Maximum frame size in words (output)________________(old: 312):312
        /// </code>
        /// 312 WORDS is 624 bytes. So the number in the LAPB spec is almost certainly this
        /// variable, and reading it as 312 BYTES is what made 452 recorded frames look illegal.
        /// Under the word reading there was never a conflict: the largest frame ever recorded is
        /// 622 bytes, comfortably inside 624, and the derived value above needs no change.
        /// <para>
        /// CONFIRMED ON THE LIVE MACHINE 2026-08-07: <c>LIST-GENERATION-VARIABLES</c> on D100
        /// reports a SECOND frame-size variable that the generation file does not carry -
        /// <c>X3FSZ, Maximum frame size in words (input) = 312</c>, the counterpart to
        /// <c>X4FSO</c> (output). Two independent limits, both stated in WORDS, both 312, one per
        /// direction. There is no reading left in which a bare "312" is a byte count.
        /// </para>
        /// <para>
        /// One question remains and it is now the only one: both variables say "frame", while
        /// requirement A5 bounds the INFORMATION FIELD. If they bound the whole frame the info
        /// limit is 624 less address, control and FCS - about 620, under the 622 observed. So
        /// either they bound the info field, or the LAPB number is a different limit sharing the
        /// value. Settling it needs the ND LAPB spec read against these two, not another capture.
        /// </para>
        /// <para>
        /// The spec at <c>docs/lapb-nd-spec.md</c> in the X25Emulator repo still says a bare 312
        /// with no unit. Measured by <c>LapbInformationLengthTests</c>.
        /// </para>
        /// </remarks>
        /// <remarks>
        /// <para>
        /// 622 = 594 + 14 + 14: the largest SINTRAN first-fragment body plus the SINTRAN header and
        /// the XMSG sub-header that precede it. That is where the number comes from and why it is
        /// this number on an ND network.
        /// </para>
        /// <para>
        /// It is the DEFAULT, not a constant of the protocol. N1 is a link parameter (X.25 2.3.2:
        /// an information field longer than the agreed maximum is FRMR reason Y), so a link that is
        /// not carrying SINTRAN traffic sets its own through <see cref="LapbOptions.MaxInformationLength"/>.
        /// Until 2026-09-06 this was computed from the XMSG message layout, which meant the data
        /// link layer could not be built without it.
        /// </para>
        /// </remarks>
        public const int MaxInformationLength = 622;

        // Unnumbered control base patterns (P/F bit 0x10 cleared), spec 2.2.3.
        // The U-frame bases, S-frame nibbles and the poll/final bit USED TO BE DECLARED HERE, and
        // again as bare literals in RetroIO.Protocols.Xmsg.Hdlc\LapbFrame.cs - the parse side. Two independent copies
        // of the same wire numbers is how a builder and a parser drift apart, so they now live once
        // in LapbControl, in the lower project that both halves reference.
        private const byte SabmBase = LapbControl.SabmBase;
        private const byte DiscBase = LapbControl.DiscBase;
        private const byte UaBase = LapbControl.UaBase;
        private const byte DmBase = LapbControl.DmBase;
        private const byte FrmrBase = LapbControl.FrmrBase;

        private const byte RrNibble = LapbControl.RrNibble;
        private const byte RnrNibble = LapbControl.RnrNibble;
        private const byte RejNibble = LapbControl.RejNibble;

        private const byte PollFinalBit = LapbControl.PollFinalBit;

        // FRMR reason bits (spec 2.3.3), OR-combined into the diagnostic's third byte. Typed as
        // LapbFrmrReason in LapbControl.cs; these aliases keep the existing call sites readable.
        // The gap at 0x02 is reason X, which we neither send nor recognise - see that enum.
        private const byte FrmrReasonW = (byte)LapbFrmrReason.ControlFieldInvalid;
        private const byte FrmrReasonY = (byte)LapbFrmrReason.InformationFieldTooLong;
        private const byte FrmrReasonZ = (byte)LapbFrmrReason.ReceiveSequenceInvalid;

        private readonly ushort _ownNode;
        private readonly LapbOptions _options;

        private LapbLayerState _state;

        // Sequence variables, modulo 8 (spec 4.1).
        // The TWO HALVES of spec 3.1: "the link is UP when BOTH directions have completed a
        // SABM -> UA exchange". Tracked as halves rather than as the state table's single "notify
        // up" action because the table's cells assume SABM_SENT is left exactly one way, and real
        // traffic interleaves: the peer's SABM can move us to CONNECTED before the UA answering
        // OUR SABM has arrived, and that UA then lands in CONNECTED where the table has no cell.
        //
        // _ourSabmAcked starts TRUE and is cleared by Connect(). That is not a fiddle: a PASSIVE
        // station that never sends a SABM has no outstanding direction to complete, which is the
        // table's "rx SABM in DISCONNECTED -> notify up" case. Only having a SABM in flight makes
        // our half something to wait for.
        private bool _ourSabmAcked = true;
        private bool _peerSabmAnswered;

        private int _sendVariable;      // V(S): next I-frame to send
        private int _receiveVariable;   // V(R): next in-sequence I-frame expected
        private int _acknowledgeVariable; // V(A): oldest unacknowledged I-frame

        // Transmit window (spec 4.2): the payload sent (or queued to be sent) with each N(S), indexed
        // by sequence number. A slot is "outstanding" while its index is in [V(A), V(S)). Storing the
        // payload (not the framed body) lets a retransmission carry the CURRENT N(R) as its ack.
        private readonly byte[]?[] _txInfo = new byte[]?[8];

        // Payloads accepted from the upper layer that do not yet fit the window (spec 4.2).
        private readonly Queue<byte[]> _pending = new Queue<byte[]>();

        // Flow-control / reject flags within CONNECTED (spec 6.1).
        private bool _peerBusy;         // set by RNR, cleared by RR/REJ
        private bool _rejectCondition;  // one REJ per detected gap (spec 4.5)

        // Event counters for the link statistics. They count real events only and are never reset
        // by a re-connect: they describe the life of this layer object.
        private long _retransmissions;
        private long _rejectionsSent;
        private long _rejectionsReceived;
        private long _t1Timeouts;
        private long _t2Timeouts;

        // T2 (X.25 2.4.9.2): true while the acknowledgement of a received I frame is being held back.
        // The held acknowledgement is simply the current V(R), so one RR (or the N(R) of an outgoing
        // I frame) covers every frame received while it was held. The deadline is set when the FIRST
        // frame is held and is not pushed out by later ones: T2 bounds how long any frame waits.
        private bool _ackPending;
        private long _t2Deadline;

        // The physical neighbour id learned from the peer's SABM/UA/RR info (spec 3.3), read-only and
        // NEVER a routing key. -1 until learned.
        private int _peerNode = -1;

        // Timers and retry counter (spec 5). Deadlines are in the injected-clock unit.
        private bool _t1Running;
        private long _t1Deadline;
        private bool _t3Running;
        private long _t3Deadline;
        private int _retry;

        // The last FRMR diagnostic sent, retained so FRMR_SENT can resend it (spec 5.1 / 6.3).
        private byte[]? _lastFrmrInfo;

        /// <summary>
        /// Represents the method that transmits a LAPB frame body for HDLC encoding.
        /// </summary>
        /// <param name="lapbBody">
        /// The unstuffed LAPB body (address, control and information, without FCS).
        /// </param>
        public delegate void LapbTransmit(byte[] lapbBody);

        /// <summary>
        /// Represents the method that receives an in-order information field.
        /// </summary>
        /// <param name="info">
        /// The information field of the received I-frame (opaque upper-layer payload).
        /// </param>
        public delegate void InformationReceived(ReadOnlyMemory<byte> info);

        /// <summary>
        /// Occurs when a LAPB frame body must be transmitted.
        /// </summary>
        public event LapbTransmit? OnTransmit;

        /// <summary>
        /// Occurs when an in-order information field is delivered to the upper layer.
        /// </summary>
        public event InformationReceived? OnInformation;

        /// <summary>
        /// Represents the method that is told the link has failed and was NOT re-established.
        /// </summary>
        /// <param name="reason">
        /// A short human-readable description of what failed, suitable for a log line.
        /// </param>
        public delegate void LinkFailed(string reason);

        /// <summary>
        /// Occurs when N2 is exhausted and the link is declared dead without a re-establish.
        /// </summary>
        /// <remarks>
        /// Raised only when <see cref="LapbOptions.ReestablishOnLinkFailure"/> is <c>false</c> (the
        /// default). The caller should end its run and send a DISC rather than reconnecting: the
        /// SABM of a reconnect is what kills a real XMSG gateway.
        /// </remarks>
        public event LinkFailed? OnLinkFailure;

        /// <summary>
        /// Initialises a new link for a given node number.
        /// </summary>
        /// <param name="ownNode">
        /// This node's number, stamped as the info field of link-management and supervisory frames.
        /// </param>
        /// <param name="options">
        /// The timer and window configuration; defaults to <see cref="LapbOptions.Default"/>.
        /// </param>
        public LapbLayer(ushort ownNode, LapbOptions? options = null)
        {
            _ownNode = ownNode;
            _options = options ?? LapbOptions.Default;
            _state = LapbLayerState.Disconnected;
        }

        /// <summary>
        /// Gets this node's number.
        /// </summary>
        public ushort OwnNode
        {
            get { return _ownNode; }
        }

        /// <summary>
        /// Gets the physical neighbour id learned from the peer, or -1 if not yet learned.
        /// </summary>
        /// <remarks>
        /// Read-only and NEVER used as a routing key (spec 3.3): on relayed traffic the logical
        /// source differs from the LAPB neighbour.
        /// </remarks>
        public int PeerNode
        {
            get { return _peerNode; }
        }

        /// <summary>
        /// Gets the current link state.
        /// </summary>
        public LapbLayerState State
        {
            get { return _state; }
        }

        /// <summary>
        /// Gets a value indicating whether the upper layer has been told the link is usable.
        /// </summary>
        /// <remarks>
        /// <para>This is NOT the same as <see cref="State"/> being Connected</para>
        /// <para>
        /// The spec's state table (section 8) carries "notify up" as a SEPARATE action from the
        /// state transition, and the two do not coincide. Entering CONNECTED from SABM_SENT by
        /// receiving the PEER's SABM is written <c>send UA; reset -> CONNECTED</c> with NO
        /// notify up; only <c>rx UA</c> in SABM_SENT is <c>reset; notify up -> CONNECTED</c>.
        /// Section 3.1 says the same thing in prose: the link is up when BOTH directions have
        /// completed a SABM -> UA exchange.
        /// </para>
        /// <para><b>Why it matters, measured 2026-08-19</b></para>
        /// <para>
        /// This layer used to expose only <see cref="State"/>, and the adapter above mapped
        /// Connected straight to an active link. On a run where RetroCore flushed a queued backlog,
        /// we answered the peer's SABM at T+8ms, were reported up, and the upper layer sent an
        /// I-frame at T+21ms - while the UA for OUR OWN SABM did not arrive until T+201ms. A later
        /// SABM then reset V(S) to 0, the peer's legitimate N(R)=1 fell outside [0,0], and section
        /// 4.3 forced us to FRMR the peer's own answer. Every individual rule was obeyed; the frame
        /// was simply sent too early.
        /// </para>
        /// </remarks>
        /// <remarks>
        /// <para><b>Guarded by the state as well as the flag</b></para>
        /// <para>
        /// <c>_notifiedUp</c> records the table's "notify up" action; ANDing it with CONNECTED
        /// means no path that leaves CONNECTED can strand a stale <c>true</c> here. That is
        /// deliberate belt-and-braces: the state is left in several places, and a fix that depended
        /// on every one of them remembering to clear a flag would be one edit away from this same
        /// class of bug returning.
        /// </para>
        /// </remarks>
        public bool IsUp
        {
            get
            {
                return _ourSabmAcked
                    && _peerSabmAnswered
                    && _state == LapbLayerState.Connected;
            }
        }

        /// <summary>
        /// Gets the send sequence variable V(S) (modulo 8).
        /// </summary>
        public int SendVariable
        {
            get { return _sendVariable; }
        }

        /// <summary>
        /// Gets the receive sequence variable V(R) (modulo 8).
        /// </summary>
        public int ReceiveVariable
        {
            get { return _receiveVariable; }
        }

        /// <summary>
        /// Gets the acknowledge sequence variable V(A) (modulo 8): the oldest unacknowledged I-frame.
        /// </summary>
        public int AcknowledgeVariable
        {
            get { return _acknowledgeVariable; }
        }

        /// <summary>
        /// Gets a value indicating whether the peer has signalled busy (RNR); new I-frames are held.
        /// </summary>
        public bool PeerBusy
        {
            get { return _peerBusy; }
        }

        /// <summary>
        /// Gets a value indicating whether a reject condition is set (one REJ sent per gap).
        /// </summary>
        public bool RejectCondition
        {
            get { return _rejectCondition; }
        }

        /// <summary>
        /// Gets the number of I-frames sent again after a REJ from the peer (go-back-N). Each resent
        /// frame counts once.
        /// </summary>
        public long Retransmissions
        {
            get { return _retransmissions; }
        }

        /// <summary>
        /// Gets the number of REJ frames this layer sent. One is sent per gap, not per frame (spec 4.5).
        /// </summary>
        public long RejectionsSent
        {
            get { return _rejectionsSent; }
        }

        /// <summary>
        /// Gets the number of REJ frames received from the peer while connected.
        /// </summary>
        public long RejectionsReceived
        {
            get { return _rejectionsReceived; }
        }

        /// <summary>
        /// Gets the number of times T2 ran out: the acknowledgement of a received I frame had been
        /// held for the whole of T2 (X.25 2.4.9.2) with no outgoing I frame to carry it, so this
        /// layer had to send the RR itself. Stays zero when <see cref="LapbOptions.T2"/> is zero,
        /// and an acknowledgement that went out inside an I frame is not counted.
        /// </summary>
        public long T2Timeouts
        {
            get { return _t2Timeouts; }
        }

        /// <summary>
        /// Gets the number of times T1 ran out.
        /// </summary>
        public long T1Timeouts
        {
            get { return _t1Timeouts; }
        }

        /// <summary>
        /// Gets the number of unacknowledged I-frames currently outstanding, <c>(V(S) - V(A)) mod 8</c>.
        /// </summary>
        public int Outstanding
        {
            get { return (_sendVariable - _acknowledgeVariable) & 0x07; }
        }

        /// <summary>
        /// Initiates the link by transmitting our SABM (P=1) carrying this node's number.
        /// </summary>
        /// <param name="currentTicks">
        /// The current injected clock value, used to arm the T1 timer.
        /// </param>
        /// <remarks>
        /// Balanced bring-up (spec 3.1): each station sends its own SABM on start; the peer's SABM is
        /// answered with UA in <see cref="OnFrameReceived(LapbFrame, long)"/>.
        /// </remarks>
        public void Connect(long currentTicks)
        {
            // A SABM is about to go out, so our direction is now something to wait for. Only the UA
            // answering it clears this again. The peer's half is untouched: if it already answered
            // our earlier SABM, that exchange still stands.
            _ourSabmAcked = false;
            Reset();
            _retry = 0;
            _state = LapbLayerState.SabmSent;
            SendUnnumbered(SabmBase, includeNode: true, pollFinal: true);
            StartT1(currentTicks);
        }

        /// <summary>
        /// Drops the link to disconnected without sending anything, discarding whatever was queued.
        /// </summary>
        /// <remarks>
        /// For the case where the transport under the link has gone away: the peer cannot be reached,
        /// so a DISC would be a frame into a hole, and holding V(S)/V(R) from the dead connection
        /// would make the next one start mid-sequence. X.25 has no name for this because the
        /// Recommendation assumes the physical layer stays put; a TCP connection does not.
        /// </remarks>
        public void ForceDisconnected()
        {
            Reset();
            _state = LapbLayerState.Disconnected;
        }

        /// <summary>
        /// Requests an orderly disconnect by transmitting DISC (P=1).
        /// </summary>
        /// <param name="currentTicks">
        /// The current injected clock value, used to arm the T1 timer.
        /// </param>
        public void Disconnect(long currentTicks)
        {
            if (_state == LapbLayerState.Disconnected || _state == LapbLayerState.DiscSent)
            {
                return;
            }

            _retry = 0;
            _state = LapbLayerState.DiscSent;
            SendUnnumbered(DiscBase, includeNode: true, pollFinal: true);
            StartT1(currentTicks);
        }

        /// <summary>
        /// Processes one received, FCS-valid LAPB frame and updates the link accordingly.
        /// </summary>
        /// <param name="frame">
        /// The received frame, already de-framed and FCS-checked.
        /// </param>
        /// <param name="currentTicks">
        /// The current injected clock value; frame arrival (re)starts or stops the timers.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="frame"/> is null.
        /// </exception>
        public void OnFrameReceived(LapbFrame frame, long currentTicks)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (frame.Kind == LapbFrameKind.Unnumbered)
            {
                HandleUnnumbered(frame, currentTicks);
                return;
            }

            // I- and S-frames are only processed in CONNECTED (spec 6.3). In DISCONNECTED they draw a
            // DM; in SABM_SENT / DISC_SENT they are ignored; in FRMR_SENT they trigger a FRMR resend.
            switch (_state)
            {
                case LapbLayerState.Disconnected:
                    SendUnnumbered(DmBase, includeNode: true, pollFinal: frame.PollFinal);
                    return;

                case LapbLayerState.SabmSent:
                case LapbLayerState.DiscSent:
                    return;

                case LapbLayerState.FrmrSent:
                    ResendFrmr(currentTicks);
                    return;
            }

            if (frame.Kind == LapbFrameKind.Information)
            {
                HandleInformation(frame, currentTicks);
            }
            else
            {
                HandleSupervisory(frame, currentTicks);
            }
        }

        /// <summary>
        /// Queues an information field for transmission and sends it within the window.
        /// </summary>
        /// <param name="info">
        /// The information field to send (opaque upper-layer payload).
        /// </param>
        /// <param name="currentTicks">
        /// The current injected clock value, used to arm the T1 timer.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the link is not <see cref="LapbLayerState.Connected"/>.
        /// </exception>
        /// <remarks>
        /// The payload is copied into the link's own storage before returning, so the caller's buffer
        /// (which may be pooled or reused) is free once the call returns. If the window is full or the
        /// peer is busy the frame waits in the pending queue and is sent as the window opens (spec 4.2).
        /// </remarks>
        public void SendInformation(ReadOnlySpan<byte> info, long currentTicks)
        {
            if (_state != LapbLayerState.Connected)
            {
                throw new InvalidOperationException("Cannot send information before the link is connected.");
            }

            _pending.Enqueue(info.ToArray());
            TransmitPending(currentTicks);
        }

        /// <summary>
        /// Advances the injected clock, driving the T1 (retransmission/poll) and T3 (keepalive) timers.
        /// </summary>
        /// <param name="currentTicks">
        /// The current injected clock value.
        /// </param>
        /// <returns>
        /// <c>true</c> when a timer fired and emitted a frame or a state change; otherwise <c>false</c>.
        /// </returns>
        public bool Tick(long currentTicks)
        {
            bool acted = false;

            // T2 (X.25 2.4.9.2): the acknowledgement has been held for the whole of T2 and no I frame
            // went out to carry it, so send the RR now. EmitSupervisory clears the pending flag.
            if (_ackPending && currentTicks >= _t2Deadline && _state == LapbLayerState.Connected)
            {
                _t2Timeouts++;
                EmitSupervisory(RrNibble, pollFinal: false, isCommand: false);
                acted = true;
            }

            if (_t1Running && currentTicks >= _t1Deadline)
            {
                acted = OnT1Expiry(currentTicks);
                if (acted && _state == LapbLayerState.Disconnected)
                {
                    return true;
                }
            }

            if (_t3Running && currentTicks >= _t3Deadline && _state == LapbLayerState.Connected)
            {
                // Idle keepalive (spec 5.2): send ONE RR(P=1) poll and STOP T3. Do NOT start T1 here.
                // Starting T1 turned an idle poll that the peer IGNORES into a 3-second RR re-poll cascade
                // that then hit N2 and needlessly RE-ESTABLISHED an alive idle link (the RR flood + SABM
                // churn observed live). T3 re-arms only when the peer actually answers (AcknowledgeThrough
                // restarts it), so an ignored poll simply leaves the link silent - matching the proven
                // legacy behaviour. T1 remains armed for retransmitting outstanding I-frames (real data);
                // that path (SendInformation/TransmitPending) is untouched, so the login-retransmit fix
                // still holds.
                EmitSupervisory(RrNibble, pollFinal: true);
                _retry = 0;
                StopT3();
                acted = true;
            }

            return acted;
        }

        /// <summary>
        /// Emits a Receiver-Ready keepalive when the link is connected (legacy idle-driver hook).
        /// </summary>
        /// <remarks>
        /// Retained for the current adapter loop. The conformant keepalive is the T3 timer in
        /// <see cref="Tick(long)"/>; this sends an RR carrying the current V(R) so an idle peer keeps
        /// the link in RUN.
        /// </remarks>
        public void SendKeepalive()
        {
            if (_state == LapbLayerState.Connected)
            {
                EmitSupervisory(RrNibble, pollFinal: false);
            }
        }

        /// <summary>
        /// Handles a received unnumbered frame (SABM / UA / DISC / DM / FRMR).
        /// </summary>
        /// <param name="frame">
        /// The received unnumbered frame.
        /// </param>
        /// <param name="currentTicks">
        /// The current injected clock value.
        /// </param>
        private void HandleUnnumbered(LapbFrame frame, long currentTicks)
        {
            switch (frame.UnnumberedKind)
            {
                case LapbUnnumberedKind.Sabm:
                    // rx SABM in ANY state (spec 6.3): answer UA (F=1) with our node, HARD-RESET the
                    // sequence state and queue (spec 3.2, NO adoption), and enter CONNECTED. A SABM
                    // received mid-session is a legitimate reset, NOT an error (the highest-risk ND
                    // deviation): stay connected.
                    CaptureNode(frame.Info);

                    // "notify up" PER THE STATE TABLE, which differs by the state we came FROM:
                    //
                    //   DISCONNECTED -> send UA; reset; NOTIFY UP -> CONNECTED
                    //   SABM_SENT    -> send UA; reset;           -> CONNECTED   (NO notify up)
                    //   CONNECTED    -> send UA; reset (mid-session, STAY UP)    (no change)
                    //   DISC_SENT    -> send UA; reset;           -> CONNECTED   (NO notify up)
                    //   FRMR_SENT    -> send UA; reset; NOTIFY UP -> CONNECTED
                    //
                    // The SABM_SENT cell is the one that matters and it is the one we had wrong:
                    // answering the peer's SABM does NOT mean our own SABM has been acknowledged,
                    // so the upper layer must not be told it may send. See IsUp for the
                    // measurement that this cost.
                    //
                    // This completes the PEER's half only. Ours is _ourSabmAcked, and if a SABM of
                    // ours is still in flight it stays false until its UA arrives.
                    _peerSabmAnswered = true;

                    Reset();
                    _state = LapbLayerState.Connected;
                    StartT3(currentTicks);
                    StopT1();

                    // The UA goes out only now, with the state already settled. It used to be sent
                    // first, before Reset() and before CONNECTED: on a real link the peer's next
                    // frame arrives milliseconds later and nobody noticed, but on an in-process link
                    // it arrives INSIDE this send, finds the station still disconnected, and is
                    // answered with a DM - both ends then tear the link down they had just built.
                    // Found 2026-09-06 wiring two stations back to back through a codec bridge.
                    SendUnnumbered(UaBase, includeNode: true, pollFinal: true);

                    // Push the peer to the RUN phase: it advances only once it receives an RR from us
                    // (spec 3.4 idle keepalive; live-necessary against the ND kernel).
                    EmitSupervisory(RrNibble, pollFinal: false);
                    break;

                case LapbUnnumberedKind.Ua:
                    CaptureNode(frame.Info);   // UA carries the peer's node number (spec 2.3.1)

                    // OUR half completes here whatever state we are in, and "whatever state" is the
                    // point. MEASURED 2026-08-19: the peer's SABM moved us to CONNECTED at T+8 ms
                    // and the UA answering our own SABM did not arrive until T+201 ms - by which
                    // time we were no longer in SABM_SENT. Honouring it only there would leave the
                    // link permanently half-up and never usable, which is how the first version of
                    // this fix failed its own tests.
                    _ourSabmAcked = true;

                    if (_state == LapbLayerState.SabmSent)
                    {
                        // Our SABM was accepted: reset and go CONNECTED (spec 3.2). Our half was
                        // already recorded above, before this state test, precisely because the UA
                        // does not always arrive here.
                        Reset();
                        _state = LapbLayerState.Connected;
                        StartT3(currentTicks);
                        StopT1();
                        EmitSupervisory(RrNibble, pollFinal: false);
                    }
                    else if (_state == LapbLayerState.DiscSent)
                    {
                        _state = LapbLayerState.Disconnected;
                        StopAllTimers();
                    }

                    break;

                case LapbUnnumberedKind.Disc:
                    CaptureNode(frame.Info);   // DISC may carry the peer's node number (spec 2.3.1)
                    if (_state == LapbLayerState.Connected || _state == LapbLayerState.FrmrSent)
                    {
                        SendUnnumbered(UaBase, includeNode: true, pollFinal: frame.PollFinal);
                        _state = LapbLayerState.Disconnected;
                        StopAllTimers();
                    }
                    else
                    {
                        SendUnnumbered(DmBase, includeNode: true, pollFinal: frame.PollFinal);
                    }

                    break;

                case LapbUnnumberedKind.Dm:
                    CaptureNode(frame.Info);   // DM may carry the peer's node number (spec 2.3.1)
                    // A DM tears the link down whether it answers our SABM (fail) or arrives while
                    // connected (spec 6.3).
                    if (_state == LapbLayerState.SabmSent || _state == LapbLayerState.Connected)
                    {
                        _state = LapbLayerState.Disconnected;
                        StopAllTimers();
                    }

                    break;

                case LapbUnnumberedKind.Frmr:
                    // The peer rejected one of our frames: re-establish by sending a fresh SABM.
                    if (_state == LapbLayerState.Connected)
                    {
                        Reset();
                        SendUnnumbered(SabmBase, includeNode: true, pollFinal: true);
                        _retry = 0;
                        _state = LapbLayerState.SabmSent;
                        StartT1(currentTicks);
                    }

                    break;
            }
        }

        /// <summary>
        /// Handles a received supervisory frame (RR / RNR / REJ) while connected.
        /// </summary>
        /// <param name="frame">
        /// The received supervisory frame.
        /// </param>
        /// <param name="currentTicks">
        /// The current injected clock value.
        /// </param>
        private void HandleSupervisory(LapbFrame frame, long currentTicks)
        {
            CaptureNode(frame.Info);

            if (!AcknowledgeThrough(frame.ReceiveSequence, frame.Control, currentTicks))
            {
                return;   // invalid N(R): FRMR(Z) sent, now FRMR_SENT
            }

            switch (frame.SupervisoryKind)
            {
                case LapbSupervisoryKind.ReceiveReady:
                    _peerBusy = false;
                    break;

                case LapbSupervisoryKind.ReceiveNotReady:
                    _peerBusy = true;
                    break;

                case LapbSupervisoryKind.Reject:
                    _rejectionsReceived++;
                    _peerBusy = false;
                    GoBackN(frame.ReceiveSequence, currentTicks);
                    break;

                default:
                    // An unknown S-subtype is an unimplemented control field: FRMR reason W (spec 2.3.3).
                    SendFrmr(frame.Control, FrmrReasonW, frame.PollFinal, currentTicks, ReceivedIsResponse(frame.Address));
                    return;
            }

            if (frame.PollFinal && !ReceivedIsResponse(frame.Address))
            {
                // A COMMAND with P=1 must be answered with a response carrying F=1 and the current
                // N(R) (X.25 2.4.5.9, ND spec 4.8). A RESPONSE with F=1 is the answer to a poll of
                // ours and must not be answered again.
                //
                // The ND dialect cannot tell the two apart - it has no command/response bit in the
                // address - so there this answers anything with P/F set, as it always has. In ITU
                // mode the address says which it is (2.4.2), and without that test two stations
                // answer each other's F=1 for ever: found 2026-09-06 as a stack overflow the moment
                // two ITU stations were wired back to back over a synchronous loopback.
                EmitSupervisory(RrNibble, pollFinal: true, isCommand: false);
            }

            if (!_peerBusy)
            {
                TransmitPending(currentTicks);
            }
        }

        /// <summary>
        /// Handles a received information frame: delivers in-order info, or rejects a gap once.
        /// </summary>
        /// <param name="frame">
        /// The received information frame.
        /// </param>
        /// <param name="currentTicks">
        /// The current injected clock value.
        /// </param>
        private void HandleInformation(LapbFrame frame, long currentTicks)
        {
            if (!AcknowledgeThrough(frame.ReceiveSequence, frame.Control, currentTicks))
            {
                return;   // invalid piggybacked N(R): FRMR(Z) sent
            }

            if (frame.Info.Length > _options.MaxInformationLength)
            {
                // Over-long I-field: FRMR reason Y (spec 2.3.2 / 2.3.3).
                SendFrmr(frame.Control, FrmrReasonY, frame.PollFinal, currentTicks, ReceivedIsResponse(frame.Address));
                return;
            }

            int ns = frame.SendSequence;
            if (ns == _receiveVariable)
            {
                // In-sequence: deliver, advance V(R), clear any reject condition, acknowledge (4.4).
                _receiveVariable = (_receiveVariable + 1) & 0x07;
                _rejectCondition = false;
                OnInformation?.Invoke(frame.Info);

                if (_options.T2 > 0 && !frame.PollFinal)
                {
                    // X.25 2.4.6.2 a) ii): with P=0 the acknowledgement may wait for the next outgoing
                    // I frame (its N(R) acknowledges) or go in an RR. Hold it for at most T2 (2.4.9.2).
                    // A frame with P=1 is never held: 2.4.6.2 a) i) requires the F=1 response at once.
                    if (!_ackPending)
                    {
                        _ackPending = true;
                        _t2Deadline = currentTicks + _options.T2;
                    }
                }
                else
                {
                    EmitSupervisory(RrNibble, pollFinal: frame.PollFinal, isCommand: false);
                }
            }
            else if (IsDuplicate(ns))
            {
                // Already-accepted frame retransmitted: discard, re-acknowledge current V(R) (4.5).
                EmitSupervisory(RrNibble, pollFinal: frame.PollFinal, isCommand: false);
            }
            else
            {
                // Gap: send exactly one REJ and latch the reject condition (4.5), avoiding a REJ storm.
                if (!_rejectCondition)
                {
                    EmitSupervisory(RejNibble, pollFinal: frame.PollFinal, isCommand: false);
                    _rejectionsSent++;
                    _rejectCondition = true;
                }
            }

            if (!_peerBusy)
            {
                TransmitPending(currentTicks);
            }
        }

        /// <summary>
        /// Applies a cumulative acknowledgement up to (but not including) N(R), validating its range.
        /// </summary>
        /// <param name="receiveSequence">
        /// The N(R) carried by the received frame.
        /// </param>
        /// <param name="rejectedControl">
        /// The control byte of the received frame, echoed into a FRMR diagnostic on an invalid N(R).
        /// </param>
        /// <param name="currentTicks">
        /// The current injected clock value, used to restart or stop the timers.
        /// </param>
        /// <returns>
        /// <c>true</c> when N(R) was valid and applied; <c>false</c> when it was out of range and a
        /// FRMR (reason Z) was sent instead.
        /// </returns>
        private bool AcknowledgeThrough(int receiveSequence, byte rejectedControl, long currentTicks)
        {
            // N(R) must lie in [V(A), V(S)]: (N(R) - V(A)) mod 8 must not exceed the outstanding count
            // (spec 4.3). Anything beyond that acknowledges a frame we never sent.
            int outstanding = (_sendVariable - _acknowledgeVariable) & 0x07;
            if (((receiveSequence - _acknowledgeVariable) & 0x07) > outstanding)
            {
                SendFrmr(rejectedControl, FrmrReasonZ, pollFinal: false, currentTicks);
                return false;
            }

            // Release every acknowledged frame's buffer and advance V(A).
            while (_acknowledgeVariable != receiveSequence)
            {
                _txInfo[_acknowledgeVariable] = null;
                _acknowledgeVariable = (_acknowledgeVariable + 1) & 0x07;
            }

            if (_acknowledgeVariable == _sendVariable)
            {
                // Everything acknowledged: stop the retransmission timer, start the idle keepalive.
                StopT1();
                StartT3(currentTicks);
            }
            else
            {
                RestartT1(currentTicks);
            }

            _retry = 0;
            return true;
        }

        /// <summary>
        /// Retransmits all outstanding I-frames from N(R) onward in response to a REJ (go-back-N).
        /// </summary>
        /// <param name="receiveSequence">
        /// The N(R) the peer rejected from; V(S) is rewound here and the frames replayed in order.
        /// </param>
        /// <param name="currentTicks">
        /// The current injected clock value, used to arm the T1 timer.
        /// </param>
        private void GoBackN(int receiveSequence, long currentTicks)
        {
            // AcknowledgeThrough has already advanced V(A) to N(R); the outstanding frames are
            // N(R)..(V(S)-1). Rewind V(S) to N(R) and resend them, each carrying the current V(R) as
            // its acknowledgement (spec 4.6).
            int resendUpTo = _sendVariable;
            _sendVariable = receiveSequence;
            while (_sendVariable != resendUpTo)
            {
                byte[]? payload = _txInfo[_sendVariable];
                if (payload != null)
                {
                    EmitInformation(_sendVariable, payload);
                    _retransmissions++;
                }

                _sendVariable = (_sendVariable + 1) & 0x07;
            }

            StartT1(currentTicks);
        }

        /// <summary>
        /// Sends as many pending I-frames as the window and peer-busy state allow.
        /// </summary>
        /// <param name="currentTicks">
        /// The current injected clock value, used to arm the T1 timer on the first send.
        /// </param>
        private void TransmitPending(long currentTicks)
        {
            // Send while connected, the peer is not busy, the window has room, and work is queued (4.2).
            while (_state == LapbLayerState.Connected
                && !_peerBusy
                && ((_sendVariable - _acknowledgeVariable) & 0x07) < _options.WindowSize
                && _pending.Count > 0)
            {
                byte[] payload = _pending.Dequeue();
                int sequence = _sendVariable;
                _txInfo[sequence] = payload;

                // V(S) advances BEFORE the frame goes out, not after. On a real link the peer's
                // acknowledgement arrives later, so either order looks the same; on an in-process
                // link - a codec bridge, a loopback - the answer comes back INSIDE the transmit
                // call, and an N(R) that acknowledges a frame V(S) has not counted yet reads as
                // invalid. Both stations then FRMR each other for ever. Found 2026-09-06 wiring two
                // stations back to back; the X.25 engine had the same fault in its own send path.
                _sendVariable = (sequence + 1) & 0x07;
                EmitInformation(sequence, payload);

                // An I-frame is now outstanding: stop the idle keepalive and start T1 if it was idle
                // (spec 5.1: "start T1 when an I-frame is sent and the queue was previously empty").
                StopT3();
                if (!_t1Running)
                {
                    StartT1(currentTicks);
                }
            }
        }

        /// <summary>
        /// Records the peer's node number (physical neighbour id) from a link/supervisory info field.
        /// </summary>
        /// <param name="info">
        /// The frame's information field; the first two bytes are the big-endian node number if present.
        /// </param>
        private void CaptureNode(ReadOnlyMemory<byte> info)
        {
            // ITU frames carry no node number (X.25 2.3.4): there is nothing to capture, and reading
            // two bytes of whatever followed would invent a neighbour id.
            if (IsItu)
            {
                return;
            }

            // Spec 2.3.1 / 3.3: SABM/UA/RR carry the sender's node number big-endian. Store it read-only
            // as the neighbour id; it is NEVER a routing key.
            ReadOnlySpan<byte> span = info.Span;
            if (span.Length >= 2)
            {
                _peerNode = (span[0] << 8) | span[1];
            }
        }

        /// <summary>
        /// Determines whether an out-of-sequence N(S) is a retransmitted duplicate rather than a gap.
        /// </summary>
        /// <param name="sendSequence">
        /// The received frame's N(S), known to differ from V(R).
        /// </param>
        /// <returns>
        /// <c>true</c> when N(S) is behind V(R) (already accepted); <c>false</c> when it is ahead (a gap).
        /// </returns>
        private bool IsDuplicate(int sendSequence)
        {
            // A gap is the peer running ahead of V(R) by 1..(k-1); at or beyond k round the modulo-8
            // circle the frame is already behind V(R), i.e. a retransmitted duplicate (spec 4.5). With
            // the maximum window k=7 this makes the common "one behind" retransmit (ahead == 7) a
            // duplicate that draws an RR, never a spurious REJ.
            int ahead = (sendSequence - _receiveVariable) & 0x07;
            return ahead >= _options.WindowSize;
        }

        /// <summary>
        /// Handles a T1 expiry: retry, or re-establish/fail on exceeding N2 (spec 5.1).
        /// </summary>
        /// <param name="currentTicks">
        /// The current injected clock value, used to restart T1.
        /// </param>
        /// <returns>
        /// <c>true</c> when the expiry produced a frame or a state change.
        /// </returns>
        private bool OnT1Expiry(long currentTicks)
        {
            _t1Timeouts++;
            _retry++;
            if (_retry > _options.N2)
            {
                // Link failure (spec 5.1 step 1): from CONNECTED or SABM_SENT, auto re-establish with a
                // fresh SABM; otherwise drop to DISCONNECTED.
                //
                // THE RE-ESTABLISH IS OFF BY DEFAULT AND THAT IS DELIBERATE. It is correct LAPB and it
                // KILLS A REAL XMSG GATEWAY. Measured twice on D100, 2026-08-21, with the console error
                // device logged so both ends carry wall-clock stamps:
                //
                //   23:38:51.392 [TX] a=0x01 SABM state=SabmSent  -> 23:38:51.539 XMSG CODE 27  (147 ms)
                //   23:58:35.837 [TX] a=0x01 SABM state=Connected -> 23:58:36.038 XMSG CODE 27  (201 ms)
                //
                // Code 27 is XXNER, "network gateway error", at PHYSICAL ADDRESS 141204 both times. In
                // the first the peer had COMPLETED a transfer 30 seconds earlier, so it was healthy and
                // nothing else had touched it.
                //
                // The second one is also what this branch looked like from our side: a 133KB push, the
                // peer silent from 23:58:02, fourteen RR polls over 33 seconds, then this code reset a
                // link it still believed was Connected. Losing the transfer is cheap; taking XMSG down
                // costs XMSG, COSPO, the file server, TADAD and XMFIDO, each by hand.
                //
                // So by default we STOP and SAY SO, and the caller ends its run with a DISC. See
                // LapbOptions.ReestablishOnLinkFailure for the opt-in.
                StopT3();
                if (_options.ReestablishOnLinkFailure &&
                    (_state == LapbLayerState.Connected || _state == LapbLayerState.SabmSent))
                {
                    Reset();
                    SendUnnumbered(SabmBase, includeNode: true, pollFinal: true);
                    _retry = 0;
                    _state = LapbLayerState.SabmSent;
                    StartT1(currentTicks);
                }
                else
                {
                    // The reason is built BEFORE the state is cleared - the outstanding-frame count is
                    // the single most useful number here, and it is what says "the peer stopped
                    // acknowledging" rather than "the peer never answered at all".
                    string reason = "LAPB link failure: N2 (" + _options.N2.ToString() +
                        ") exhausted in state " + _state.ToString() +
                        " with " + Outstanding.ToString() + " unacknowledged frame(s)" +
                        (_options.ReestablishOnLinkFailure ? "." : "; NOT re-establishing (a SABM kills the peer's XMSG gateway).");

                    LapbLayerState failedFrom = _state;
                    _state = LapbLayerState.Disconnected;
                    StopT1();

                    // Only announce a failure for a link that was actually carrying something. A
                    // DISC_SENT or FRMR_SENT timeout ending in DISCONNECTED is an ordinary teardown.
                    if (failedFrom == LapbLayerState.Connected || failedFrom == LapbLayerState.SabmSent)
                    {
                        OnLinkFailure?.Invoke(reason);
                    }
                }

                return true;
            }

            switch (_state)
            {
                case LapbLayerState.Connected:
                    // Poll-first recovery (spec 5.1): RR with P=1; the peer's N(R)/REJ drives go-back-N.
                    EmitSupervisory(RrNibble, pollFinal: true);
                    RestartT1(currentTicks);
                    return true;

                case LapbLayerState.SabmSent:
                    SendUnnumbered(SabmBase, includeNode: true, pollFinal: true);
                    RestartT1(currentTicks);
                    return true;

                case LapbLayerState.DiscSent:
                    SendUnnumbered(DiscBase, includeNode: true, pollFinal: true);
                    RestartT1(currentTicks);
                    return true;

                case LapbLayerState.FrmrSent:
                    ResendFrmr(currentTicks);
                    return true;

                default:
                    StopT1();
                    return false;
            }
        }

        /// <summary>
        /// Builds and sends a FRMR carrying the 3-byte diagnostic, then enters FRMR_SENT.
        /// </summary>
        /// <param name="rejectedControl">
        /// The control byte of the frame that caused the reject.
        /// </param>
        /// <param name="reasonBits">
        /// The OR-combined FRMR reason bits (W / Y / Z).
        /// </param>
        /// <param name="pollFinal">
        /// Whether to set the final bit on the FRMR.
        /// </param>
        /// <param name="currentTicks">
        /// The current injected clock value, used to arm the T1 timer.
        /// </param>
        /// <param name="rejectedWasResponse">
        /// True when the frame being rejected arrived as a response; sets the C/R bit of the FRMR
        /// diagnostic in ITU mode (X.25 2.3.4.10).
        /// </param>
        private void SendFrmr(byte rejectedControl, byte reasonBits, bool pollFinal, long currentTicks, bool rejectedWasResponse = false)
        {
            // Diagnostic (spec 2.3.3): rejected control; V(S)/V(R) with bit4 = 0; reason nibble.
            // X.25 2.3.4.10: V(S) in bits 2-4, the C/R bit in bit 5 (1 when the rejected frame was a
            // response), V(R) in bits 6-8. The ND dialect leaves the C/R bit clear;
            // in ITU mode it says which it was.
            byte sequences = (byte)(((_sendVariable << 1) & 0x0E) | ((_receiveVariable << 5) & 0xE0));
            if (IsItu && rejectedWasResponse)
            {
                sequences |= 0x10;
            }

            byte[] info = new byte[] { rejectedControl, sequences, reasonBits };
            _lastFrmrInfo = info;

            byte control = (byte)(FrmrBase | (pollFinal ? PollFinalBit : 0));
            byte[] body = new byte[2 + info.Length];
            body[0] = AddressFor(linkManagement: true, isCommand: false, infoLength: info.Length);
            body[1] = control;
            body[2] = info[0];
            body[3] = info[1];
            body[4] = info[2];
            OnTransmit?.Invoke(body);

            _state = LapbLayerState.FrmrSent;
            StartT1(currentTicks);
        }

        /// <summary>
        /// Resends the last FRMR diagnostic (F=1) while in FRMR_SENT and restarts T1.
        /// </summary>
        /// <param name="currentTicks">
        /// The current injected clock value, used to restart T1.
        /// </param>
        private void ResendFrmr(long currentTicks)
        {
            if (_lastFrmrInfo == null)
            {
                return;
            }

            byte[] body = new byte[2 + _lastFrmrInfo.Length];
            body[0] = AddressFor(linkManagement: true, isCommand: false, infoLength: _lastFrmrInfo.Length);
            body[1] = (byte)(FrmrBase | PollFinalBit);
            body[2] = _lastFrmrInfo[0];
            body[3] = _lastFrmrInfo[1];
            body[4] = _lastFrmrInfo[2];
            OnTransmit?.Invoke(body);
            RestartT1(currentTicks);
        }

        /// <summary>
        /// True when this link speaks ITU-T LAPB rather than the ND dialect.
        /// </summary>
        private bool IsItu => _options.Variant == LapbVariant.Itu;

        /// <summary>
        /// True when a frame we received was a response rather than a command, judged by its address
        /// (X.25 2.4.2). Meaningless in the ND dialect, which does not distinguish, so it answers
        /// false there and the FRMR C/R bit stays clear as it always has.
        /// </summary>
        /// <param name="address">
        /// The received frame's address byte.
        /// </param>
        private bool ReceivedIsResponse(byte address)
        {
            if (!IsItu)
            {
                return false;
            }

            return _options.IsDte ? address == AddressItuB : address == AddressItuA;
        }

        /// <summary>
        /// The address byte for a frame we are about to send.
        /// </summary>
        /// <param name="linkManagement">
        /// True for SABM, UA, DISC, DM and FRMR; false for I and the supervisory frames.
        /// </param>
        /// <param name="isCommand">
        /// True for a command frame, false for a response.
        /// </param>
        /// <param name="infoLength">
        /// Length of the information field, for the ND odd-length marker.
        /// </param>
        /// <remarks>
        /// The two dialects address on different grounds. ITU (2.4.2) addresses by DIRECTION: a
        /// command carries the address of the station it is sent to, a response the address of the
        /// station sending it, so a DTE commands with B and answers with A. ND addresses by FRAME
        /// FAMILY: link management is 0x01, data transfer 0x09, with 0x80 added when the information
        /// field has an odd byte count because the ND-100's word DMA needs to know (see
        /// <see cref="AddressData"/>). The two agree on nothing except the value 0x01.
        /// </remarks>
        private byte AddressFor(bool linkManagement, bool isCommand, int infoLength)
        {
            if (IsItu)
            {
                bool useA = _options.IsDte ? !isCommand : isCommand;
                return useA ? AddressItuA : AddressItuB;
            }

            if (linkManagement)
            {
                return AddressLinkSetup;
            }

            return (byte)(AddressData | (((infoLength & 1) != 0) ? AddressOddLengthBit : 0));
        }

        /// <summary>
        /// Emits an information frame carrying the given sequence number, the current V(R), and payload.
        /// </summary>
        /// <param name="sendSequence">
        /// The N(S) to stamp (bits 1..3 of the control byte).
        /// </param>
        /// <param name="payload">
        /// The information field bytes.
        /// </param>
        private void EmitInformation(int sendSequence, byte[] payload)
        {
            // I-frame control (spec 2.2.1): N(R) in bits 5..7, P=0, N(S) in bits 1..3, bit 0 = 0.
            byte control = (byte)((_receiveVariable << 5) | (sendSequence << 1));

            // N(R) = V(R) in this frame acknowledges everything received so far, so a held
            // acknowledgement is delivered and T2 is cancelled without counting (X.25 2.4.6.2 a) ii)).
            _ackPending = false;
            byte[] body = new byte[2 + payload.Length];
            // ND odd-info-length address marker (capture-VERIFIED): 0x89 for an odd-length info field,
            // 0x09 for even. 100 silently discards an I-frame whose address parity disagrees with its
            // length (word-DMA off-by-one), so this MUST be set per frame - see AddressData.
            body[0] = AddressFor(linkManagement: false, isCommand: true, infoLength: payload.Length);
            body[1] = control;
            Array.Copy(payload, 0, body, 2, payload.Length);
            OnTransmit?.Invoke(body);
        }

        /// <summary>
        /// Emits a supervisory frame (RR / RNR / REJ) carrying the current V(R) and our node number.
        /// </summary>
        /// <param name="subtypeNibble">
        /// The subtype low nibble: RR <c>0x1</c>, RNR <c>0x5</c>, REJ <c>0x9</c>.
        /// </param>
        /// <param name="pollFinal">
        /// Whether to set the poll/final bit.
        /// </param>
        /// <param name="isCommand">
        /// True when this frame is a command (our own poll or a plain acknowledgement), false when
        /// it answers a poll. Decides the ITU address; ignored by the ND dialect.
        /// </param>
        private void EmitSupervisory(byte subtypeNibble, bool pollFinal, bool isCommand = true)
        {
            // S-frame control (spec 2.2.2): N(R) in bits 5..7, P/F, then the subtype low nibble.
            byte control = (byte)((_receiveVariable << 5) | (pollFinal ? PollFinalBit : 0) | subtypeNibble);

            // Any RR, RNR or REJ also carries the current V(R), so it settles a held acknowledgement.
            _ackPending = false;

            // ITU 2.3.4: a supervisory frame has no information field. The ND dialect puts the
            // sender's node number there, which is how a neighbour is identified on that network.
            byte[] body = IsItu ? new byte[2] : new byte[4];
            body[0] = AddressFor(linkManagement: false, isCommand: isCommand, infoLength: 0);
            body[1] = control;
            if (!IsItu)
            {
                body[2] = (byte)((_ownNode >> 8) & 0xFF);
                body[3] = (byte)(_ownNode & 0xFF);
            }

            OnTransmit?.Invoke(body);
        }

        /// <summary>
        /// Emits an unnumbered frame (SABM / UA / DISC / DM), optionally stamping our node number.
        /// </summary>
        /// <param name="controlBase">
        /// The unnumbered control base pattern (P/F bit cleared).
        /// </param>
        /// <param name="includeNode">
        /// Whether to append our 2-byte node number as the info field (spec 2.3.1).
        /// </param>
        /// <param name="pollFinal">
        /// Whether to set the poll/final bit.
        /// </param>
        private void SendUnnumbered(byte controlBase, bool includeNode, bool pollFinal)
        {
            byte control = (byte)(controlBase | (pollFinal ? PollFinalBit : 0));

            // SABM and DISC are commands, UA and DM are responses (X.25 2.3.4). The ND dialect does
            // not distinguish, so this only decides the address in ITU mode.
            bool isCommand = controlBase == SabmBase || controlBase == DiscBase;

            // ITU 2.3.4: no information field on SABM, UA, DISC or DM. The node number is the ND
            // dialect's way of naming the neighbour (spec 2.3.1) and must not go on an X.25 link.
            bool withNode = includeNode && !IsItu;
            byte[] body = withNode ? new byte[4] : new byte[2];
            body[0] = AddressFor(linkManagement: true, isCommand: isCommand, infoLength: 0);
            body[1] = control;
            if (withNode)
            {
                body[2] = (byte)((_ownNode >> 8) & 0xFF);
                body[3] = (byte)(_ownNode & 0xFF);
            }

            OnTransmit?.Invoke(body);
        }

        /// <summary>
        /// Hard-resets the sequence variables, retransmit queue and flow flags (spec 3.2).
        /// </summary>
        private void Reset()
        {
            _sendVariable = 0;
            _receiveVariable = 0;
            _acknowledgeVariable = 0;
            for (int i = 0; i < _txInfo.Length; i++)
            {
                _txInfo[i] = null;
            }

            // _pending IS cleared here, and whether it SHOULD be is an OPEN QUESTION - see below.
            //
            // The spec defines the word precisely (section 8): "reset" = set V(S)=V(A)=V(R)=0 and
            // clear the RETRANSMIT queue. The retransmit queue is _txInfo above. _pending holds the
            // upper layer's work not yet transmitted, which carries no sequence number, so a strict
            // reading says a reset cannot have invalidated it and it should survive.
            //
            // IT IS LEFT ALONE FOR NOW BECAUSE REMOVING IT CHANGES THE WIRE. Dropping the Clear()
            // makes SeamParityTests differ from the legacy composition at byte 26: a payload that
            // used to be discarded is now transmitted. That may well be the correct behaviour, but
            // it is a behaviour change to the bytes we put on a live link and it is NOT what caused
            // the 2026-08-19 failure - in that capture the connect letter WAS transmitted (I ns=0
            // is in the log); what the reset discarded was its retransmit record, which 3.2
            // requires. Decide it deliberately, against the parity corpus, not as a side-effect of
            // fixing the link-up gating.
            _pending.Clear();
            _peerBusy = false;
            _rejectCondition = false;
            _ackPending = false;   // V(R) is zero again: nothing received is waiting to be acknowledged
        }

        /// <summary>
        /// Starts the T1 retransmission / poll timer from the given clock value.
        /// </summary>
        /// <param name="currentTicks">
        /// The current injected clock value.
        /// </param>
        private void StartT1(long currentTicks)
        {
            _t1Running = true;
            _t1Deadline = currentTicks + _options.T1;
        }

        /// <summary>
        /// Restarts the T1 timer (equivalent to a fresh start) from the given clock value.
        /// </summary>
        /// <param name="currentTicks">
        /// The current injected clock value.
        /// </param>
        private void RestartT1(long currentTicks)
        {
            StartT1(currentTicks);
        }

        /// <summary>
        /// Stops the T1 timer.
        /// </summary>
        private void StopT1()
        {
            _t1Running = false;
        }

        /// <summary>
        /// Starts the T3 idle keepalive timer from the given clock value.
        /// </summary>
        /// <param name="currentTicks">
        /// The current injected clock value.
        /// </param>
        private void StartT3(long currentTicks)
        {
            _t3Running = true;
            _t3Deadline = currentTicks + _options.T3;
        }

        /// <summary>
        /// Stops the T3 timer.
        /// </summary>
        private void StopT3()
        {
            _t3Running = false;
        }

        /// <summary>
        /// Stops both timers (used on disconnect).
        /// </summary>
        private void StopAllTimers()
        {
            _t1Running = false;
            _t3Running = false;
            _ackPending = false;   // T2 stops with the link
        }
    }
}
