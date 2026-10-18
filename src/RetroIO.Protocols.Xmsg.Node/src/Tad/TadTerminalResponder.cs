//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;

using RetroIO.Protocols.Xmsg.Packet;
using RetroIO.Protocols.Xmsg.SubProtocol;

namespace RetroIO.Protocols.Xmsg.Node.Tad
{
    /// <summary>
    /// The remote-machine simulation: answers an incoming <c>connect-to</c> (SYSTEM-TAD) session
    /// and drives the <see cref="TadTerminalMenu"/> terminal over it. This is the SERVER/answering
    /// side (the role our node 103 plays when machine 100 connects to it), the mirror of the
    /// client-oriented <see cref="TadSession"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Provenance - what is VERIFIED vs INFERRED.</b></para>
    /// The frame shapes are modelled on the captured <c>102 -> 100</c> responder side of
    /// <c>2026-04-09-0025-tad-connect-to-d102-from-d100.pcapng</c> (connect-accept: proto <c>0xD8</c>, role <c>0x40</c>,
    /// <c>XMCSM 0x04000041</c>, param trailer <c>01 02 0000 02 02 000A</c>, replying from the
    /// TADADM well-known port 2 = wire <c>0x0156</c>). Those field VALUES are VERIFIED from the
    /// capture. What is INFERRED (and may need live tuning) is the transport SEQUENCING for OUR
    /// direction: we ECHO the request's Flags1 and counter (the pattern proven to work for the
    /// stateless list-route reply), because the responder's own independent sequence base is not
    /// recoverable from a one-sided view. Every such choice is commented at its use site.
    /// </remarks>
    public sealed class TadTerminalResponder
    {
        // The XMSG SYSTEM-TAD / directory-service control word (XSLET letter to TADADM),
        // VERIFIED as the connect dispatch value in every conn-to capture.
        private const uint SystemTadControlService = (uint)XmcsmService.XsletLetter;

        // The TADADM well-known service port. VERIFIED: logical port 2 with the fixed low-7
        // component 0x56 -> wire value 0x0156 (342), seen on systems 100/102/103 alike.
        private const ushort TadAdminWirePort = 0x0156;

        // TAD opcode for terminal character data (BDAT). VERIFIED from TadOpcodes / captures.
        private const byte BdatOpcode = 0x01;

        private readonly ushort _nodeNumber;
        private readonly TadTerminalMenu _menu;
        private readonly Func<DateTime> _clock;

        // Persists our outgoing datagram sequence per remote node across process restarts, so it
        // stays in step with 100's persistent expected-from-us (XSRSQ). Default = non-persisting
        // (starts every remote at 0x0000); the live runner injects a file-backed store.
        private readonly IResponderSequenceStore _sequenceStore;

        // Our dynamically-allocated session port (logical port << 7 | random low-7). Assigned on
        // connect. INFERRED starting value; only the (logical<<7)|random LAYOUT is VERIFIED.
        private ushort _sessionWirePort;

        // The remote (machine 100) endpoint learned from the connect request.
        private ushort _clientSystem;
        private ushort _clientPort;

        // Session/connect metadata captured for the "stat" command (a diagnostic the operator can type).
        // The connect-letter strings are parsed from the connect trailer; the terminal parameters come
        // from 100's TMOD/TTYP/DESC/OPSV negotiation chain (captured in OnTerminalSetup).
        private bool _negotiationSeen;
        private ushort _terminalType;                        // TTYP (0x0D)
        private byte _terminalMode;                          // TMOD (0x0C)
        private byte _escapeChar;                            // DESC (0x0F)
        private byte[] _osVersion = Array.Empty<byte>();     // OPSV (0x1F)
        private string _connectService = string.Empty;       // "*TADADM" from the connect letter
        private string _connectTargetName = string.Empty;    // "D102" from the connect letter

        // The per-session secure-ACK channel BASE: the 0xDE channel anchor. CAPTURE-CORRECTED
        // (multiple-connect session 2): the ACK base is DE regardless of the connect epoch/channel;
        // it steps DOWN (DE->DD->DC) only as the ACK counter wraps (SecureDatagramReceiver._wraps).
        // The earlier "connect-channel + 4" rule only coincided with DE because every prior capture
        // connected on DA (epoch 0); a D9 (epoch-1) reconnect ACKed on DE too, not DD - and our DD
        // ACK crashed 100 at PERF_CONNCT. Set on connect; DE is also the sane default.
        private SintranProtocolId _ackChannel = (SintranProtocolId)XmsgEnvelope.ChannelAnchor;

        private bool _connected;

        /// <summary>
        /// When true, the (currently blocked) terminal bring-up frames are sent after the
        /// port-assign. Default false: sending them crashes 100's XMSG because the session
        /// channels/counters are per-session-allocated and not yet reconstructable. Left as a
        /// switch so the replay can be re-enabled once a bidirectional live capture is available.
        /// </summary>
        public bool SendTerminalBringup { get; set; }

        /// <summary>
        /// Optional sink for one-line diagnostic messages (the live runner points this at the console).
        /// Used to make the per-link outgoing-Flags1 lifecycle loudly visible: the value continued on a
        /// connect, and - critically - when a peer ReachabilityRequest resets the store to 0x0000.
        /// </summary>
        public XmsgLogHandler? Log { get; set; }

        /// <summary>
        /// Initialises the responder for a given node with a clock (injected for deterministic
        /// tests; the live runner passes <c>() => DateTime.Now</c>).
        /// </summary>
        /// <param name="nodeNumber">
        /// This node's number (for example 103).
        /// </param>
        /// <param name="clock">
        /// Supplies the current time for the MOTD and the Time/Date commands.
        /// </param>
        /// <param name="sequenceStore">
        /// Persists our outgoing datagram sequence per remote node across restarts. When null, a
        /// non-persisting store is used (every remote starts at 0x0000) - correct for tests and for
        /// a first-ever contact, but a live node against a long-running peer should pass a
        /// <see cref="FileResponderSequenceStore"/> so it does not fall behind the peer's XSRSQ.
        /// </param>
        public TadTerminalResponder(ushort nodeNumber, Func<DateTime> clock, IResponderSequenceStore? sequenceStore = null)
        {
            _nodeNumber = nodeNumber;
            _menu = new TadTerminalMenu();
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _sequenceStore = sequenceStore ?? new NullResponderSequenceStore();
        }

        /// <summary>
        /// Gets a value indicating whether a terminal session is currently established.
        /// </summary>
        public bool IsConnected
        {
            get { return _connected; }
        }

        /// <summary>
        /// Returns true when a frame carries a DCON (disconnect indication, TAD opcode 0x09) - 100's
        /// graceful teardown, e.g. its 1-minute "TAD not logged in" idle timeout.
        /// </summary>
        /// <param name="frame">
        /// The incoming data frame.
        /// </param>
        /// <returns>
        /// True when this frame disconnects the session.
        /// </returns>
        public bool IsDisconnect(XmsgFrame frame)
        {
            if (!_connected || frame == null || frame.Tad == null)
            {
                return false;
            }

            IReadOnlyList<TadMessage> messages = frame.Tad.Messages;
            for (int i = 0; i < messages.Count; i++)
            {
                if ((TadOp)messages[i].Opcode == TadOp.Dcon)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Closes the current session (on a received DCON), so the next connect starts cleanly.
        /// </summary>
        public void CloseSession()
        {
            _connected = false;
            _motdSent = false;
        }

        /// <summary>
        /// Records that 100 ACKed one of our frames (its subtype-0x03 ACK echoes the Flags1 it
        /// received), so the persisted next-sequence is <c>ackedFlags1 + 1</c>. Persisting on ACK -
        /// rather than on send - guarantees the saved value never runs ahead of what 100 actually
        /// received; the previous drift (saving on send, over-counting an un-received reply) is what
        /// made a later connect XENSE-reject our accept.
        /// </summary>
        /// <param name="remoteNode">
        /// The node that ACKed (source of the 0x03 frame), e.g. 100.
        /// </param>
        /// <param name="ackedFlags1">
        /// The Flags1 the ACK echoes (the frame 100 confirmed receiving).
        /// </param>
        public void ConfirmDelivered(ushort remoteNode, ushort ackedFlags1)
        {
            // 0xFFFF is the reachability broadcast marker, never a real data sequence - ignore it.
            if (ackedFlags1 == 0xFFFF)
            {
                return;
            }

            ushort next = (ushort)(ackedFlags1 + 1);
            // ACKs arrive in order (LAPB); advance only forward so a stray/duplicate ACK never rewinds.
            ushort current = _sequenceStore.LoadNextFlags1(remoteNode);
            if (next > current)
            {
                _sequenceStore.SaveNextFlags1(remoteNode, next);
            }
        }

        /// <summary>
        /// Resets our persisted outgoing datagram sequence for a remote node back to 0x0000. Called
        /// when that node signals an XMSG (re)start - a ReachabilityRequest - which zeroes its
        /// per-node-pair expected-from-us. LIVE-VERIFIED signal: after an XMSG restart 100 sends a
        /// ReachabilityRequest and its subsequent connect arrives at Flags1 0x0000; a bare HDLC link
        /// restart does NOT send one and 100 continues its sequence. Resetting here keeps our sequence
        /// in step across 100's restarts without any manual state-file surgery.
        /// </summary>
        /// <param name="remoteNode">
        /// The node that (re)started, e.g. 100.
        /// </param>
        public void ResetSequence(ushort remoteNode)
        {
            _sequenceStore.SaveNextFlags1(remoteNode, 0x0000);
            // Loud, greppable marker so a later reconnect issue is easy to trace: this is the ONLY
            // event (besides first-ever contact) that zeroes our per-link outgoing sequence.
            Log?.Invoke($"[responder] ReachabilityRequest from node {remoteNode}: peer XMSG RESTARTED -> outgoing Flags1 store RESET to 0x0000");
        }

        /// <summary>
        /// True while we have sent the accept but not yet seen the session-setup - i.e. we can still
        /// resync the accept's sequence in response to a XENSE reject.
        /// </summary>
        public bool CanResyncAccept
        {
            get { return _connected && !_sessionSetupSeen && _connectFrame != null; }
        }

        /// <summary>
        /// Recovers from a XENSE reject of our accept (our Flags1 was AHEAD of 100's expected-from-us):
        /// steps the accept's sequence DOWN by one, corrects the persisted value, and rebuilds the
        /// accept so it can be re-sent. Stepping down one per XENSE converges on 100's exact expected
        /// value (at which point 100 answers with the session-setup instead of another XENSE) with no
        /// manual restart or file surgery. This is the learn-from-100 recovery for a drifted sequence.
        /// </summary>
        /// <returns>
        /// The rebuilt accept at the next-lower sequence, or null when not resyncable.
        /// </returns>
        public XmsgFrame? ResyncAcceptDown()
        {
            if (!CanResyncAccept)
            {
                return null;
            }

            _acceptFlags1 = (ushort)(_acceptFlags1 - 1);
            _respFlags1 = _acceptFlags1;
            // Correct the stored value too, so a later restart uses the recovered base.
            _sequenceStore.SaveNextFlags1(_clientSystem, _acceptFlags1);
            return BuildConnectAccept(_connectFrame!);
        }

        /// <summary>
        /// Gets the per-session secure-ACK channel (Protocol-ID) that every subtype-<c>0x03</c>
        /// delivery ACK for this session must ride: connect-channel + 4. Learned from the
        /// connect frame in <see cref="OnConnect"/>; before a connect it is the TAD default
        /// (<c>0xDD</c>). See the field comment for the capture provenance.
        /// </summary>
        public SintranProtocolId AckChannel
        {
            get { return _ackChannel; }
        }

        /// <summary>
        /// Returns true when the frame is a SYSTEM-TAD connect request addressed to this node
        /// (the <c>*TADADM</c> letter that opens a <c>connect-to</c>).
        /// </summary>
        /// <param name="frame">
        /// The decoded incoming data frame.
        /// </param>
        /// <returns>
        /// True when this is a connect request we should answer.
        /// </returns>
        public static bool IsConnectRequest(XmsgFrame frame)
        {
            return frame != null
                && frame.SubHeader != null
                && frame.XmcsmWithBodyWord == SystemTadControlService
                // The connect letter is a routed XROUT letter (XFROU set). The old "(role & 0x0F) == 4 =
                // asker" reading was wrong (bit 0x04 is XFROU "routed", not "asker" - the host's own 0xFD
                // notify rides role 0x54 which also sets it); the XsletLetter XMCSM above is what actually
                // identifies the connect. See XMSG-PROTOCOL.md section 18.4 / XmsgSendOptions.
                && (frame.SubHeader.Role & (byte)XmsgSendOptions.RoutedLetter) != 0;
        }

        /// <summary>
        /// Handles an incoming connect request: allocates a session port and produces the
        /// connect-accept plus the terminal greeting (MOTD + menu + prompt) as BDAT.
        /// </summary>
        /// <param name="request">
        /// The connect-request frame.
        /// </param>
        /// <returns>
        /// The frames to transmit, in order.
        /// </returns>
        public IReadOnlyList<XmsgFrame> OnConnect(XmsgFrame request)
        {
            List<XmsgFrame> outgoing = new List<XmsgFrame>();

            // Learn the client endpoint from the request's SOURCE fields.
            _clientSystem = request.SubHeader!.SourceSystem;
            _clientPort = request.SubHeader.SourcePort;

            // Capture the connect-letter strings (service "*TADADM" and target name e.g. "D102") for
            // the "stat" command; reset the terminal negotiation (a new session re-negotiates it).
            ExtractConnectStrings(request);
            _negotiationSeen = false;

            // The secure-ACK channel base is the 0xDE channel ANCHOR - NOT connect-channel + 4.
            // CAPTURE-CORRECTED (multiple-connect session 2): the reconnect's connect rode D9 (epoch 1)
            // yet BOTH sides' handshake ACKs rode DE (frames 126/131/140/146: DE 07/06/04/02), not
            // D9+4=DD. The old "+4" only ever equalled DE because every earlier capture connected on DA
            // (epoch 0, DA+4=DE); a D9 reconnect exposed it as wrong, and our DD ACK crashed 100 at
            // PERF_CONNCT. The channel then steps DOWN from DE only as the ACK COUNTER wraps
            // (SecureDatagramReceiver._wraps): DE -> DD -> DC, decoupled from the connect epoch.
            _ackChannel = (SintranProtocolId)XmsgEnvelope.ChannelAnchor;

            // Allocate our session port: logical port 4 with a fixed low-7 for now. The random
            // part is deliberately FIXED (not random) so the wire bytes are reproducible while we
            // iterate live; the magic-number model only requires the (logical<<7)|low7 layout,
            // which this satisfies (4<<7 | 0x11 = 0x0211). [INFERRED value; VERIFIED layout.]
            // Fixed port word, kept deliberately. Its SHAPE is correct - port number 4 in the
            // high nine bits, 0x11 in the low seven - and a responder may answer on any
            // well-formed port word, which is VERIFIED live with this exact value. A real kernel
            // would draw the low seven bits from ZRAND instead; XmsgPortWordAllocator does that
            // if we ever want the traffic to look kernel-minted, but switching is a live-behaviour
            // change and buys fidelity, not function.
            _sessionWirePort = (ushort)((4 << 7) | 0x11);
            _connected = true;
            _motdSent = false;
            // A fresh session starts at the username prompt (the banner ends with SYCN 0002 + "ENTER ").
            // We only assert the logged-in state (SYCN 000A) after a valid SYSTEM/SYSTEM login.
            _loginPhase = LoginPhase.Username;
            _loginFaults = 0;
            _pendingUsername = string.Empty;

            // LEARN the per-link seed from 100's connect frame: seed = (Counter + Flags1 + (Flags2 &
            // 0xFF)) & 0xFF (100-102 = 0x14). Every frame we originate is then fully determined by
            // seed + our own Flags1 via the VERIFIED envelope arithmetic (XmsgEnvelope): the Counter
            // is (seed - (Flags2&0xFF) - Flags1) and the channel is 0xDE - (XMCSM>>24) - epoch. Our
            // OWN outgoing datagram sequence starts at 0x0000 (a fresh direction) and advances +1 per
            // frame across accept, port-assign and all terminal-data - one sequence, epoch 0, so
            // terminal data rides 0xDD (NOT DB: DB was an epoch-2 artifact of a high running sequence).
            _seed = XmsgEnvelope.LearnSeed(
                request.Header.Flags1, (byte)(request.Header.Checksum & 0x00FF), request.Header.Flags2);
            // CONTINUE our per-remote-node outgoing Flags1 from the persisted store (the store advances on
            // 100's 0x03 ACKs = 100's expected-from-us / XSRSQ). GOD-LLM S6a MEASUREMENT: continuation is
            // right and its values are capture-legal at any epoch/channel - "echo (accept F1 == connect
            // F1) crashes" was DISPROVEN (it happens in 6 captures, all accepted). The counters are fully
            // independent (deltas +/-0x35 exist); the connect neither carries nor resets XSRSQ.
            // CONTINUE our per-remote-node outgoing Flags1 from the persisted store, ALWAYS - the S7
            // capture answer (multiple-connect-100-to102-and-then-reboot). Real 102 runs ONE free-running
            // per-direction counter across every connect/disconnect: session 1 letters 0x0004/0x0005,
            // session 1 data climbs 0x0006..0x0015, session 2 (reconnect, NO restart) letters continue at
            // 0x0016/0x0017 (epoch-1, channel D9). Nothing resets at session end; the counter zeroes ONLY
            // on a real peer restart (then the connect arrives at 0x0000, ctr seed). So there is NO
            // per-connect reset and NO wrap-boundary guard: the earlier fall-back-to-0x0000 was the wrong
            // fix (it PRODUCED the reset-to-0 stall) and the class-0x0400-letter-at-ctr-0xFF shape simply
            // never arises under continuation (session 2's letters landed on ctr 0xFE/0xFD, not 0xFF). The
            // envelope math for every epoch is proven by EnvelopeConformanceTests (753 frames, 0 mismatch).
            _respFlags1 = _sequenceStore.LoadNextFlags1(_clientSystem);
            Log?.Invoke($"[responder] connect from node {_clientSystem}: continuing outgoing Flags1 at 0x{_respFlags1:X4} (persisted; no per-connect reset)");

            // Retain the connect and the accept's sequence so a XENSE (accept ahead of 100's expected)
            // can be recovered by stepping the accept down (ResyncAcceptDown), no restart needed.
            _connectFrame = request;
            _acceptFlags1 = _respFlags1;
            _sessionSetupSeen = false;

            // The captured 102 responder handshake is, in order:
            //   1. connect-accept  (proto D8, role 40, XMCSM 04000041, param trailer)
            //   2. port-assign     (proto D8, role 40, XMCSM 04000000, TAD 0x07 = our session
            //                       endpoint, so 100 learns where to send terminal data)
            // Only AFTER these does 100 drive terminal negotiation (TMOD/TTYP/...). Sending the
            // greeting immediately (on a port 100 has not been told about) makes the emulator drop
            // the link, so the greeting is deferred until negotiation is observed. [Learned live.]
            // ISOLATION STEP: send ONLY the byte-verified connect-accept for now. The port-assign
            // and terminal-setup frames are deferred until we confirm the accept alone is
            // structurally accepted by 100 (previous multi-frame attempts crashed XMSG with XXPER,
            // so we add one verified frame at a time).
            outgoing.Add(BuildConnectAccept(request));

            return outgoing;
        }

        /// The XMSG control word 100 uses for the session-setup / port-negotiation frames
        /// (the TAD 06/1B/1C/FF chain and our port-assign reply). VERIFIED from captures.
        private const uint SessionSetupControlService = (uint)XmcsmService.SessionSetup;

        /// <summary>
        /// Returns true when the frame is the session-setup that follows an accepted connect
        /// (XMCSM <c>0x04000000</c> carrying the <c>06/1B/1C/FF</c> negotiation chain).
        /// </summary>
        public bool IsSessionSetup(XmsgFrame frame)
        {
            return _connected
                && frame != null
                && frame.SubHeader != null
                && frame.XmcsmWithBodyWord == SessionSetupControlService;
        }

        /// <summary>
        /// Handles the session-setup frame by replying with the port-assignment: a TAD <c>0x07</c>
        /// message carrying our session endpoint (system + session port) so 100 learns where to
        /// send terminal data, followed by the OPSV/0B/15/FF options copied from the capture.
        /// </summary>
        /// <remarks>
        /// The frame ECHOES the session-setup's proto / Flags1 / counter (the same reply pattern
        /// the accept uses - VERIFIED from 2026-04-09-0100-tad-connect-to-d102-from-d103-via-d100, where the responder's
        /// port-assign echoed the asker's session-setup transport fields). The 24-byte trailer is
        /// the captured 102 trailer with the system byte and session-port bytes substituted for
        /// ours; the remaining option bytes are copied verbatim (not yet decoded).
        /// </remarks>
        /// <param name="request">
        /// The session-setup frame.
        /// </param>
        /// <returns>
        /// The port-assignment frame.
        /// </returns>
        public IReadOnlyList<XmsgFrame> OnSessionSetup(XmsgFrame request)
        {
            List<XmsgFrame> outgoing = new List<XmsgFrame>();

            // 100 sent the session-setup, so our accept was accepted: stop any accept resync.
            _sessionSetupSeen = true;

            // Captured 102 trailer (24 bytes):
            //   00 | 07 05 [00 00 66 03 13] | 1F 03 4C 00 00 | 00 | 0B 02 03 00 | 15 02 01 08 | FF 00
            // where 0x07's data = 00 00 <system> <sessionPortHi> <sessionPortLo>. Substitute ours.
            byte sysByte = (byte)_nodeNumber;
            byte portHi = (byte)(_sessionWirePort >> 8);
            byte portLo = (byte)(_sessionWirePort & 0xFF);
            byte[] trailer = new byte[]
            {
                0x00,
                0x07, 0x05, 0x00, 0x00, sysByte, portHi, portLo,
                0x1F, 0x03, 0x4C, 0x00, 0x00,
                0x00,
                0x0B, 0x02, 0x03, 0x00,
                0x15, 0x02, 0x01, 0x08,
                0xFF, 0x00,
            };

            // Port-assign continues our sequence (Flags1 0x0001), a CONTROL-class frame: seed model
            // gives Counter = seed - 0x0001 and channel DA. Same bytes the echo scheme produced.
            outgoing.Add(BuildResponderFrame(
                request,
                controlService: SessionSetupControlService,
                frameFlags: (byte)XmsgFrameFlags.Setup,
                role: (byte)XmsgSendOptions.WakeOnStatus,
                sourcePort: TadAdminWirePort,
                payload: trailer));

            // POST-PORT-ASSIGN BRING-UP.
            // 100 waits for our session-data burst before it drives TMOD/TTYP -> MOTD (the beep). The
            // channel and Counter are COMPUTED from the verified seed model (XmsgEnvelope): for a fresh
            // responder (epoch 0) a terminal-data frame (XMCSM 0x01080000) rides 0xDD with Counter
            // (seed - 8 - Flags1). NOT DB - DB was an epoch-2 artifact of a high running sequence, and
            // the earlier DC/DD crashes were a wrong COUNTER (fixed-Base), not a wrong channel.
            if (SendTerminalBringup)
            {
                // First terminal-data frame (DUMM): Flags1 0x0002 (continues the sequence), Flags2
                // 0x0108 -> Counter 0x0A, channel DD. Built via the TAD API. ISOLATION: emit only the
                // DUMM; 100's response tells us whether to send the rest (ctrl 0x20, RESE, RESE, MOTD).
                byte[] dumm = new TadMessageBuilder().Dumm().Build();
                outgoing.Add(BuildResponderFrame(
                    request,
                    controlService: TerminalDataControlService,
                    frameFlags: (byte)XmsgFrameFlags.DataB,
                    role: (byte)XmsgSendOptions.None,
                    sourcePort: _sessionWirePort,
                    payload: dumm));
            }

            return outgoing;
        }

        /// <summary>
        /// XMCSM control/service word for a TAD terminal-data frame. VERIFIED from captures.
        /// </summary>
        private const uint TerminalDataControlService = (uint)XmcsmService.TerminalData;

        /// <summary>
        /// The per-link seed byte, learned from 100's connect frame (see <see cref="OnConnect"/>).
        /// With our own <see cref="_respFlags1"/> it fully determines every frame we originate.
        /// </summary>
        private byte _seed;

        /// <summary>
        /// The responder's own outgoing datagram sequence to 100, starting at 0x0000 (a fresh
        /// direction) and advancing +1 per frame across accept, port-assign and terminal-data - one
        /// sequence, which is what 100 validates for in-order delivery (out-of-order = XENSE).
        /// </summary>
        private ushort _respFlags1;

        /// <summary>
        /// The connect frame, retained so the accept can be rebuilt during a XENSE resync.
        /// </summary>
        private XmsgFrame? _connectFrame;

        /// <summary>
        /// The Flags1 our accept currently uses; stepped down on each XENSE resync.
        /// </summary>
        private ushort _acceptFlags1;

        /// <summary>
        /// True once 100 sent the session-setup (accept confirmed) - resync stops here.
        /// </summary>
        private bool _sessionSetupSeen;

        /// <summary>
        /// The login phases the terminal walks before the command loop is reachable.
        /// </summary>
        private enum LoginPhase
        {
            /// <summary>
            /// Awaiting the username line (the banner's "ENTER " prompt, SYCN 0002).
            /// </summary>
            Username,

            /// <summary>
            /// Awaiting the password line (no-echo, ECKM FF).
            /// </summary>
            Password,

            /// <summary>
            /// A valid SYSTEM/SYSTEM login completed (SYCN 000A asserted).
            /// </summary>
            LoggedIn,
        }

        // The expected credentials (case-insensitive). Both username and password are "SYSTEM".
        private const string ExpectedUser = "SYSTEM";
        private const string ExpectedPassword = "SYSTEM";

        // The maximum wrong-credential attempts before "BYE HACKER!" and disconnect.
        private const int MaxLoginFaults = 3;

        private LoginPhase _loginPhase;
        private int _loginFaults;
        private string _pendingUsername = string.Empty;

        /// <summary>
        /// True once we have sent the MOTD, so we do not re-send it on repeated setup frames.
        /// </summary>
        private bool _motdSent;

        /// <summary>
        /// The MOTD frame's TAD payload, VERIFIED from conn-to-d102 frame 62: BMMX / ECKM / a BDAT
        /// banner (date, "SINTRAN III - VSX/500", "--- RETROCORE EMULATED ID:102 ---") / SYCN / a BDAT
        /// "ENTER " prompt / RFI. Copied verbatim (the "ID:102" already matches our node number).
        /// </summary>
        private static readonly byte[] MotdPayload = Convert.FromHexString(
            "0004030100000003010101600D0A2032322E32372E32322020202020203820415052494C202020313939380D0A"
            + "2053494E5452414E20494949202D205653582F353030204C0D0A2D2D2D20524554524F434F524520454D554C4154"
            + "4544204C2049443A313032202D2D2D0D0A1302000201080D0A454E544552200200");

        /// <summary>
        /// Returns true when the frame is 100's terminal-setup (it carries a TMOD message), i.e. the
        /// negotiation 100 sends after our DUMM. Answering it with the MOTD burst is the last step.
        /// </summary>
        /// <param name="frame">
        /// The incoming data frame.
        /// </param>
        /// <returns>
        /// True when this is the terminal-setup that should trigger the MOTD.
        /// </returns>
        public bool IsTerminalSetup(XmsgFrame frame)
        {
            if (!_connected || _motdSent || frame == null || frame.Tad == null)
            {
                return false;
            }

            IReadOnlyList<TadMessage> messages = frame.Tad.Messages;
            for (int i = 0; i < messages.Count; i++)
            {
                if ((TadOp)messages[i].Opcode == TadOp.Tmod)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Answers 100's terminal-setup with the responder burst that produces the login screen, each
        /// frame CONTINUING our one datagram sequence (so the channels/counters are the seed-model
        /// values): control <c>0x20</c>, RESE, RESE, then the MOTD. VERIFIED shapes from conn-to-d102
        /// frames 57/58/60/62.
        /// </summary>
        /// <param name="request">
        /// The terminal-setup (TMOD) frame; its source is 100's session endpoint.
        /// </param>
        /// <returns>
        /// The burst frames, in transmit order.
        /// </returns>
        public IReadOnlyList<XmsgFrame> OnTerminalSetup(XmsgFrame request)
        {
            List<XmsgFrame> outgoing = new List<XmsgFrame>();

            // Capture 100's terminal parameters (TMOD/TTYP/DESC/OPSV) for the "stat" command.
            CaptureNegotiation(request);

            // control 0x20 (XMCSM 0x00080000): TAD opcode 0x20, empty. Seed model -> channel DE at epoch 0.
            outgoing.Add(BuildResponderFrame(
                request, controlService: (uint)XmcsmService.BareTadControl, frameFlags: (byte)XmsgFrameFlags.Setup, role: (byte)XmsgSendOptions.None,
                sourcePort: _sessionWirePort, payload: new TadMessageBuilder().Raw((TadOp)0x20, ReadOnlySpan<byte>.Empty).Build()));

            // RESE, RESE (XMCSM 0x01080000): TAD RESE, empty. Channel DD at epoch 0.
            outgoing.Add(BuildResponderFrame(
                request, controlService: TerminalDataControlService, frameFlags: (byte)XmsgFrameFlags.DataA, role: (byte)XmsgSendOptions.None,
                sourcePort: _sessionWirePort, payload: new TadMessageBuilder().Rese().Build()));
            outgoing.Add(BuildResponderFrame(
                request, controlService: TerminalDataControlService, frameFlags: (byte)XmsgFrameFlags.DataB, role: (byte)XmsgSendOptions.None,
                sourcePort: _sessionWirePort, payload: new TadMessageBuilder().Rese().Build()));

            // MOTD (XMCSM 0x01080000): the banner + ENTER prompt chain. Channel DD at epoch 0.
            outgoing.Add(BuildResponderFrame(
                request, controlService: TerminalDataControlService, frameFlags: (byte)XmsgFrameFlags.DataA, role: (byte)XmsgSendOptions.None,
                sourcePort: _sessionWirePort, payload: MotdPayload));

            _motdSent = true;
            return outgoing;
        }

        /// <summary>
        /// Builds one frame we originate for the session using the VERIFIED envelope seed model
        /// (<see cref="XmsgEnvelope"/>): the Counter is <c>(seed - (Flags2 AND 0xFF) - Flags1)</c> and
        /// the channel is <c>0xDE - (XMCSM>>24) - epoch</c>, with Flags1 the next value of our own
        /// single outgoing datagram sequence. Then advances the sequence. Control-class frames
        /// (Flags2 0x0400) land on DA at epoch 0; terminal-data frames (Flags2 0x0108) on DD at epoch 0.
        /// </summary>
        /// <param name="request">
        /// The triggering frame (source addressing = 100's endpoint).
        /// </param>
        /// <param name="controlService">
        /// The XMCSM control/service word (its high half is the derived frame-class).
        /// </param>
        /// <param name="frameFlags">
        /// The sub-header frame-flags byte for this frame type.
        /// </param>
        /// <param name="role">
        /// The sub-header role byte (0x40 setup, 0x00 data-phase).
        /// </param>
        /// <param name="sourcePort">
        /// Our source port (TADADM for control, session port for data).
        /// </param>
        /// <param name="payload">
        /// The trailer payload bytes (param blocks or TAD chain).
        /// </param>
        /// <returns>
        /// The assembled frame on the derived channel with the computed counter.
        /// </returns>
        private XmsgFrame BuildResponderFrame(
            XmsgFrame request,
            uint controlService,
            byte frameFlags,
            byte role,
            ushort sourcePort,
            byte[] payload)
        {
            // On data frames the SINTRAN Flags2 frame-class IS the top 16 bits of the XMCSM word
            // (VERIFIED empirically, 601/601 data frames). Derive it here so it is never a separate
            // hand-entered value that could disagree. See XMSG-PROTOCOL.md section 18.4.
            ushort frameClass = (ushort)(controlService >> 16);

            ushort f1 = _respFlags1;
            // Counter and channel from the verified seed model - NOT a fixed Base (that fixed-Base
            // Counter was the cause of the XXPER crashes on the terminal-data frames).
            byte ctr = XmsgEnvelope.ComputeCounter(_seed, f1, frameClass);
            SintranProtocolId channel = XmsgEnvelope.DeriveChannel(_seed, f1, frameClass, controlService);

            // Advance our in-memory sequence for the NEXT frame this session. We do NOT persist here:
            // persisting on SEND over-counts frames 100 never received (e.g. a reply whose LAPB frame
            // was not acked), so the saved value drifts AHEAD of 100's expected-from-us and the next
            // connect is XENSE-rejected. Persistence is driven by 100's ACKs instead (ConfirmDelivered).
            _respFlags1 = (ushort)(f1 + 1);

            TadFrameContext ctx = new TadFrameContext
            {
                DestinationNode = request.Header.SourceNode,        // 100
                SourceNode = _nodeNumber,                           // us
                DatagramSequence = f1,
                FrameClass = frameClass,
                ProtocolId = channel,                              // DERIVED, not canned
                Counter = ctr,
                FrameFlags = frameFlags,
                Role = role,
                DestinationSystem = _clientSystem,                   // 100 (learned at connect)
                DestinationPort = request.SubHeader!.SourcePort,     // 100's port on the triggering frame
                SourceSystem = _nodeNumber,
                SourcePort = sourcePort,
                ControlService = controlService,
            };

            return AssembleDataFrame(ctx, payload);
        }

        /// <summary>
        /// Handles a terminal-data frame (the user's typed line) during an active session and
        /// produces the menu response. Returns an empty list when the frame is not terminal input.
        /// </summary>
        /// <param name="frame">
        /// The incoming data frame.
        /// </param>
        /// <param name="disconnect">
        /// Set true when the session should close after the response.
        /// </param>
        /// <returns>
        /// The response frames.
        /// </returns>
        public IReadOnlyList<XmsgFrame> OnTerminalInput(XmsgFrame frame, out bool disconnect)
        {
            disconnect = false;
            List<XmsgFrame> outgoing = new List<XmsgFrame>();

            if (!_connected || frame.SubHeader == null)
            {
                return outgoing;
            }

            // Only a frame carrying a BDAT is a typed line. The client also sends control frames that
            // are NOT input and must not drive the login/menu state machine: a 7CERS (opcode 0x21)
            // answers each of our CESC transitions (spec 22.15 frames 3/6a), and 7DUMM keepalives
            // arrive when idle. Treating a CERS as a line would, in WAIT_PASSWORD, become an empty
            // (wrong) password and break the login. Ignore any frame with no BDAT (the layer still
            // ACKs it).
            if (!HasBdat(frame))
            {
                return outgoing;
            }

            // Extract the typed line (BDAT), high bit stripped, surrounding whitespace/CR trimmed.
            string line = ExtractBdatText(frame).Trim();

            switch (_loginPhase)
            {
                case LoginPhase.Username:
                    // Remember the username and ALWAYS ask for the password next, with echo OFF
                    // (ECKM FF) so the password is not shown while typed. We never reveal whether the
                    // username was valid - the verdict comes only after the password (spec 21). No
                    // logged-in state is asserted here.
                    _pendingUsername = line;
                    outgoing.Add(BuildTerminalChain(frame, new TadMessageBuilder()
                        .BdatText("\r\n").Sycn(SycnState.UsernameAccepted).Cesc(CescState.EscapeDisabled).Build()));
                    outgoing.Add(BuildTerminalChain(frame, new TadMessageBuilder()
                        .BdatText("PASSWORD: ").Eckm(EchoStrategy.NoEcho).Rfi().Build()));
                    _loginPhase = LoginPhase.Password;
                    break;

                case LoginPhase.Password:
                    bool valid =
                        string.Equals(_pendingUsername, ExpectedUser, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(line, ExpectedPassword, StringComparison.OrdinalIgnoreCase);
                    if (valid)
                    {
                        // ONLY NOW, after a valid SYSTEM/SYSTEM login: restore echo, confirm "OK", and
                        // assert the logged-in state (SYCN 000A) with the command prompt. SYCN 000A is
                        // what cancels 100's 1-minute "not logged in" idle disconnect.
                        _loginFaults = 0;
                        _loginPhase = LoginPhase.LoggedIn;
                        outgoing.Add(BuildTerminalChain(frame, new TadMessageBuilder()
                            .BdatText("\r\n").Eckm(EchoStrategy.LocalEcho).BdatText("OK")
                            .Sycn(SycnState.PasswordAccepted).Cesc(CescState.EscapeEnabled).Build()));
                        outgoing.Add(BuildTerminalChain(frame, new TadMessageBuilder()
                            .BdatText("\r\n").Sycn(SycnState.LoggedIn).BdatText("# ").Rfi().Build()));
                    }
                    else if (_loginFaults + 1 >= MaxLoginFaults)
                    {
                        // Third strike: taunt and tear the session down (0xFD -> asker DCON). Never a
                        // logged-in state.
                        _loginFaults++;
                        outgoing.Add(BuildTerminalChain(frame, new TadMessageBuilder()
                            .BdatText("\r\nBYE HACKER!\r\n").Sycn(SycnState.LoggedOut).Build()));
                        outgoing.Add(BuildFdNotification(frame));
                        disconnect = true;
                    }
                    else
                    {
                        // Wrong credentials: restore echo, report the failure, and go back to the
                        // username prompt (SYCN 0002 = waiting for username). No logged-in state.
                        _loginFaults++;
                        _loginPhase = LoginPhase.Username;
                        outgoing.Add(BuildTerminalChain(frame, new TadMessageBuilder()
                            .BdatText("\r\nInvalid user/password").Eckm(EchoStrategy.LocalEcho)
                            .Sycn(SycnState.WaitingForUsername).BdatText("\r\nENTER ").Rfi().Build()));
                    }

                    break;

                default:
                    // Logged-in command loop. Intercept "stat" here - only the responder holds the
                    // captured session/negotiation metadata; the pure menu cannot. Everything else
                    // is dispatched to the menu.
                    if (string.Equals(line, "stat", StringComparison.OrdinalIgnoreCase))
                    {
                        EmitMenuReply(frame, outgoing, BuildStatReport());
                        break;
                    }

                    TadMenuResult result = _menu.Handle(line, _clock());
                    switch (result.Mode)
                    {
                        case TadDisconnectMode.Ladder:
                            // The idle-timer teardown (opt-in): the FULL five-frame ladder + 0xFD, WITHOUT
                            // a host DCON, so 100 holds the TAD until its "not logged in" idle timer fires
                            // (~1 minute) - the capture-faithful behaviour. The ladder is:
                            //   1. BDAT(farewell) + CESC 00           (class 0x0108)
                            //   2. BMMX 000000 + ECKM 00 + CESC 00    (class 0x0108) line-discipline restore
                            //   3. BDAT("\r\n--EXIT--\r\n") + SYCN 000B (class 0x0108) the LoggedOut signal
                            //   4. CESC 01                            (class 0x0108)
                            //   5. 0xFD                               (class 0x0006) the session notification
                            AppendTeardownLadder(frame, outgoing, result.Output);
                            disconnect = true;
                            break;

                        case TadDisconnectMode.LadderThenDcon:
                            // The DEFAULT disconnect (menu 4): the full ladder + 0xFD, then a host-initiated
                            // DCON (host data-phase role 0x00). LIVE-VERIFIED 2026-07-05 vs real 100: 100
                            // prints "-- DISCONNECTED BY TAD --" IMMEDIATELY, no idle wait. (Live 5/6/7 runs
                            // proved the DCON alone suffices and its role byte is irrelevant; we keep the
                            // ladder for a clean line-discipline restore.)
                            AppendTeardownLadder(frame, outgoing, result.Output);
                            outgoing.Add(BuildDconIndication(frame));
                            disconnect = true;
                            break;

                        default:
                            // Re-assert the logged-in state after every command (spec 21) and grant input.
                            // Long output (e.g. the full menu / "help") can exceed a single BDAT's 255-byte
                            // count, so split it across frames rather than throwing. See EmitMenuReply.
                            EmitMenuReply(frame, outgoing, result.Output);
                            break;
                    }

                    break;
            }

            return outgoing;
        }

        /// <summary>
        /// Emits a logged-in command reply, splitting output longer than one BDAT (255-byte count)
        /// across several terminal-data frames so long text (for example the full menu / "help")
        /// never overflows the single-byte length and throws.
        /// </summary>
        /// <remarks>
        /// Every chunk CONTINUES the datagram sequence; only the FINAL frame re-asserts the logged-in
        /// state and grants input (SYCN 000A + RFI), matching the proven single-BDAT reply shape - the
        /// intermediate frames carry output only, so input credit is issued once, after all output.
        /// An empty reply still emits one frame (empty BDAT + SYCN + RFI) so input is always granted.
        /// </remarks>
        /// <param name="frame">
        /// The triggering input frame (its source is 100's session endpoint).
        /// </param>
        /// <param name="outgoing">
        /// The response list to append the reply frames to.
        /// </param>
        /// <param name="text">
        /// The full command output text.
        /// </param>
        private void EmitMenuReply(XmsgFrame frame, List<XmsgFrame> outgoing, string text)
        {
            // Chunk well under the 255-byte BDAT limit and on an even boundary, so no inter-message
            // word-alignment pad is needed inside a chunk; 240 also leaves room for the trailing
            // SYCN/RFI on the final frame.
            const int ChunkSize = 240;
            string body = text ?? string.Empty;

            int offset = 0;
            // All but the final chunk are bare output frames (no SYCN, no RFI).
            while (body.Length - offset > ChunkSize)
            {
                string piece = body.Substring(offset, ChunkSize);
                outgoing.Add(BuildTerminalChain(frame, new TadMessageBuilder().BdatText(piece).Build()));
                offset += ChunkSize;
            }

            // The final chunk (possibly the only one, possibly empty) carries the state + input credit.
            string tail = body.Substring(offset);
            outgoing.Add(BuildTerminalChain(frame, new TadMessageBuilder()
                .BdatText(tail).Sycn(SycnState.LoggedIn).Rfi().Build()));
        }

        /// <summary>
        /// Parses the connect letter's trailer for the two ASCII strings it carries - the service name
        /// (for example <c>*TADADM</c>) and the target system name (for example <c>D102</c>) - for the
        /// "stat" command.
        /// </summary>
        /// <param name="request">
        /// The connect-request frame.
        /// </param>
        private void ExtractConnectStrings(XmsgFrame request)
        {
            _connectService = string.Empty;
            _connectTargetName = string.Empty;

            byte[] trailer = request.TrailingBytes;
            if (trailer == null)
            {
                return;
            }

            // Collect maximal printable-ASCII runs (length >= 2). The connect letter contains exactly
            // two: the "*"-prefixed service and the target system name. The +1 flushes the final run.
            StringBuilder run = new StringBuilder();
            for (int i = 0; i <= trailer.Length; i++)
            {
                byte b = i < trailer.Length ? trailer[i] : (byte)0x00;
                if (b >= 0x20 && b <= 0x7E)
                {
                    run.Append((char)b);
                    continue;
                }

                if (run.Length >= 2)
                {
                    string s = run.ToString();
                    if (s[0] == '*' && _connectService.Length == 0)
                    {
                        _connectService = s;
                    }
                    else if (_connectTargetName.Length == 0)
                    {
                        _connectTargetName = s;
                    }
                }

                run.Clear();
            }
        }

        /// <summary>
        /// Captures 100's terminal parameters from the negotiation chain (TMOD / TTYP / DESC / OPSV) so
        /// the "stat" command can display them.
        /// </summary>
        /// <param name="request">
        /// The terminal-setup frame carrying the negotiation chain.
        /// </param>
        private void CaptureNegotiation(XmsgFrame request)
        {
            if (request.Tad == null)
            {
                return;
            }

            IReadOnlyList<TadMessage> messages = request.Tad.Messages;
            for (int i = 0; i < messages.Count; i++)
            {
                TadMessage m = messages[i];
                byte[] d = m.Data;
                switch (m.Opcode)
                {
                    case 0x0C:                                   // TMOD - terminal mode
                        if (d.Length >= 1)
                        {
                            _terminalMode = d[0];
                        }

                        break;
                    case 0x0D:                                   // TTYP - terminal type (2-byte)
                        if (d.Length >= 2)
                        {
                            _terminalType = (ushort)((d[0] << 8) | d[1]);
                        }

                        break;
                    case 0x0F:                                   // DESC - escape character
                        if (d.Length >= 1)
                        {
                            _escapeChar = d[0];
                        }

                        break;
                    case 0x1F:                                   // OPSV - host OS version
                        _osVersion = (byte[])d.Clone();
                        break;
                }
            }

            _negotiationSeen = true;
        }

        /// <summary>
        /// Builds the "stat" report: the connect-letter metadata and the negotiated terminal
        /// parameters, each with a short explanation so the operator can read it without the spec.
        /// </summary>
        /// <returns>
        /// The multi-line report text.
        /// </returns>
        private string BuildStatReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("\r\n--- TAD SESSION STATUS ---\r\n\r\n");

            sb.Append("Connect letter (XMCSM 04000041):\r\n");
            sb.Append("  From node    : ").Append(_clientSystem)
              .Append("  ->  this node ").Append(_nodeNumber)
              .Append(" (D").Append(_nodeNumber).Append(")\r\n");
            sb.Append("  Service      : ")
              .Append(_connectService.Length != 0 ? _connectService : "(none)").Append("\r\n");
            sb.Append("  Target name  : ")
              .Append(_connectTargetName.Length != 0 ? _connectTargetName : "(none)").Append("\r\n");
            sb.Append("  Client port  : 0x").Append(_clientPort.ToString("X4"))
              .Append("  (logical ").Append(_clientPort >> 7)
              .Append(", incarnation ").Append(_clientPort & 0x7F).Append(")\r\n");
            sb.Append("  Link seed    : 0x").Append(_seed.ToString("X2"))
              .Append("  (per-link envelope constant)\r\n\r\n");

            if (_negotiationSeen)
            {
                sb.Append("Terminal negotiation (sent by your connect-to):\r\n");
                sb.Append("  Terminal type: ").Append(_terminalType)
                  .Append("  (octal ").Append(Convert.ToString(_terminalType, 8))
                  .Append(", hex 0x").Append(_terminalType.ToString("X4")).Append(")   [TTYP]\r\n");
                sb.Append("  Terminal mode: ").Append(_terminalMode)
                  .Append("  (0x").Append(_terminalMode.ToString("X2")).Append(")   [TMOD]\r\n");
                sb.Append("  Escape char  : ").Append(_escapeChar)
                  .Append("  (octal ").Append(Convert.ToString(_escapeChar, 8))
                  .Append(_escapeChar == 0x1B ? ", ESC" : string.Empty).Append(")   [DESC]\r\n");
                sb.Append("  Host OS ver  : ").Append(FormatHexBytes(_osVersion)).Append("   [OPSV]\r\n");
            }
            else
            {
                sb.Append("Terminal negotiation: not yet received.\r\n");
            }

            sb.Append("\r\nWhat these mean:\r\n");
            sb.Append("  TTYP - terminal type number your machine advertises (shown in decimal).\r\n");
            sb.Append("  TMOD - terminal mode / line-discipline flags.\r\n");
            sb.Append("  DESC - the escape (local) character code.\r\n");
            sb.Append("  OPSV - the operating-system version of the calling machine.\r\n");
            sb.Append("  Your login NAME is not sent as metadata - it travels as ordinary\r\n");
            sb.Append("  keystrokes; the connect itself is machine-level (node ")
              .Append(_clientSystem).Append(").\r\n");

            return sb.ToString();
        }

        /// <summary>
        /// Formats a byte array as space-separated two-digit hex (for the OPSV OS-version display).
        /// </summary>
        /// <param name="bytes">
        /// The bytes to format.
        /// </param>
        /// <returns>
        /// The hex string, or <c>(none)</c> when empty.
        /// </returns>
        private static string FormatHexBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return "(none)";
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i != 0)
                {
                    sb.Append(' ');
                }

                sb.Append(bytes[i].ToString("X2"));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Appends the capture-faithful five-frame host teardown ladder (spec 22.7): farewell +
        /// CESC 00; BMMX/ECKM/CESC line-discipline restore; "--EXIT--" + SYCN 000B (LoggedOut);
        /// CESC 01; then the 0xFD session notification. Shared by menu choices 4, 5 and 7.
        /// </summary>
        /// <param name="frame">
        /// The triggering input frame (addressing source).
        /// </param>
        /// <param name="outgoing">
        /// The response list to append to.
        /// </param>
        /// <param name="farewellText">
        /// The goodbye text for the first ladder frame.
        /// </param>
        private void AppendTeardownLadder(XmsgFrame frame, List<XmsgFrame> outgoing, string farewellText)
        {
            outgoing.Add(BuildTerminalChain(frame, new TadMessageBuilder()
                .BdatText(farewellText).Cesc(CescState.EscapeDisabled).Build()));
            outgoing.Add(BuildTerminalChain(frame, new TadMessageBuilder()
                .Bmmx(0x00, 0x0000).Eckm(EchoStrategy.Teardown).Cesc(CescState.EscapeDisabled).Build()));
            outgoing.Add(BuildTerminalChain(frame, new TadMessageBuilder()
                .BdatText("\r\n--EXIT--\r\n").Sycn(SycnState.LoggedOut).Build()));
            outgoing.Add(BuildTerminalChain(frame, new TadMessageBuilder()
                .Cesc(CescState.EscapeEnabled).Build()));
            outgoing.Add(BuildFdNotification(frame));
        }

        /// <summary>
        /// Builds a HOST-initiated DCON indication (TAD <c>0x09</c>, class <c>0x00080000</c>, from the
        /// session/terminal port) - the LIVE-VERIFIED instant-disconnect trigger.
        /// </summary>
        /// <remarks>
        /// LIVE-VERIFIED 2026-07-05 vs real 100: a host DCON makes the client print
        /// "-- DISCONNECTED BY TAD --" immediately, with no idle wait. The 5/6/7 experiments proved the
        /// role byte is irrelevant (host data-phase role <c>0x00</c> and the asker-style <c>0x94</c> both
        /// worked), so we use the host-natural role <c>0x00</c>. The DCON rides class 0x0008 with
        /// frameFlags 0x82, mirroring every captured (client) DCON.
        /// </remarks>
        /// <param name="request">
        /// The triggering frame (addressing source).
        /// </param>
        /// <returns>
        /// The DCON frame.
        /// </returns>
        private XmsgFrame BuildDconIndication(XmsgFrame request)
        {
            byte[] tad = new TadMessageBuilder().Raw(TadOp.Dcon, ReadOnlySpan<byte>.Empty).Build();
            return BuildResponderFrame(
                request,
                controlService: (uint)XmcsmService.BareTadControl,
                frameFlags: (byte)XmsgFrameFlags.ControlBare,
                role: (byte)XmsgSendOptions.None,
                sourcePort: _sessionWirePort,
                payload: tad);
        }

        /// <summary>
        /// Builds the connect-accept frame (proto <c>0xD8</c>, role <c>0x40</c>, the SYSTEM-TAD
        /// param trailer) that a real responder sends to acknowledge the connect.
        /// </summary>
        private XmsgFrame BuildConnectAccept(XmsgFrame request)
        {
            // Param trailer: two parameter blocks, 01 02 <p1> and 02 02 <p2>.
            //
            // IT IS NOT A CONSTANT. Measured 2026-08-17 across nine archived connect captures, two
            // values occur:
            //
            //     01 02 0000  02 02 000A     (p1=0,  p2=10)
            //     01 02 0001  02 02 0009     (p1=1,  p2=9)
            //
            // The first guess - that it identifies the responding machine - is REFUTED: D100 sends
            // BOTH. It answers 0/10 in 2026-04-08-2339-tad-session-d100-to-d102-31-minutes, 2026-04-09-0058-list-systems-on-d103 and 2026-04-09-0052-list-routing-on-d102, and 1/9 in
            // ethernet-conn-to-D100-from-102-WORKING and ALLTEST-fa-connectto. So it tracks live
            // state, not identity.
            //
            // BOTH OBSERVED PAIRS SUM TO TEN, which fits a resource count - sessions in use against
            // sessions free, out of ten. That is an inference from two distinct samples, NOT a
            // decode: a third value would settle it, and 10 may just as easily be a coincidence of
            // these captures.
            //
            // We emit 0/10 unconditionally. If the sum-to-ten reading is right, that is a lie the
            // moment we hold one session - which is exactly the state D100 saw when it re-sent its
            // connect and refused us on 2026-08-17. UNPROVEN as the cause, and the next thing to
            // test on the machine.
            byte[] trailer = new byte[] { 0x01, 0x02, 0x00, 0x00, 0x02, 0x02, 0x00, 0x0A };

            // The accept is the FIRST frame of our own sequence (Flags1 0x0000), a CONTROL-class frame
            // (Flags2 0x0400): the seed model gives Counter = seed - 0x0000 = 0x14 and channel DA
            // (0xDE - 4 - epoch0). This equals the value 100 accepts - the old echo scheme produced
            // the SAME bytes for the accept because 100's connect was itself Flags1 0x0000 / Counter
            // seed; the model just makes accept, port-assign and terminal-data one coherent sequence.
            return BuildResponderFrame(
                request,
                controlService: SystemTadControlService,
                frameFlags: (byte)XmsgFrameFlags.Setup,
                role: (byte)XmsgSendOptions.WakeOnStatus,
                sourcePort: TadAdminWirePort,
                payload: trailer);
        }

        /// <summary>
        /// Wraps a pre-built TAD chain in a terminal-data frame (class <c>0x0108</c>, XMCSM
        /// <c>0x01080000</c>) on the session port - the carrier for the login / prompt bursts.
        /// </summary>
        /// <param name="request">
        /// The triggering frame (its source is 100's session endpoint).
        /// </param>
        /// <param name="tadChain">
        /// The TAD chain to carry (built with <see cref="TadMessageBuilder"/>).
        /// </param>
        /// <returns>
        /// The terminal-data frame.
        /// </returns>
        private XmsgFrame BuildTerminalChain(XmsgFrame request, byte[] tadChain)
        {
            return BuildResponderFrame(
                request,
                controlService: TerminalDataControlService,
                frameFlags: (byte)XmsgFrameFlags.DataA,
                role: (byte)XmsgSendOptions.None,
                sourcePort: _sessionWirePort,
                payload: tadChain);
        }

        /// <summary>
        /// Builds the <c>0xFD</c> session-state notification that tells the asker to disconnect (spec
        /// 22.7): TAD opcode <c>0xFD</c>, the <c>0x00060000</c> control class, sent from the system
        /// port 342. The asker answers it with a DCON.
        /// </summary>
        /// <param name="request">
        /// The triggering frame (its source addressing is 100's endpoint).
        /// </param>
        /// <returns>
        /// The 0xFD notification frame.
        /// </returns>
        private XmsgFrame BuildFdNotification(XmsgFrame request)
        {
            // TAD 0xFD, empty. Class 0x0006 (XMCSM 0x00060000) -> channel 0xDE at epoch 0; role 0x54,
            // frameFlags 0x82 (observed values for the 0xFD frame, spec 22.6); from the TADADM port 342.
            byte[] tad = new TadMessageBuilder().Raw((TadOp)0xFD, ReadOnlySpan<byte>.Empty).Build();
            return BuildResponderFrame(
                request,
                controlService: (uint)XmcsmService.SessionNotify,
                frameFlags: (byte)XmsgFrameFlags.ControlBare,
                role: (byte)(XmsgSendOptions.WakeOnStatus | XmsgSendOptions.Bounce | XmsgSendOptions.RoutedLetter),
                sourcePort: TadAdminWirePort,
                payload: tad);
        }

        /// <summary>
        /// Builds a terminal-text frame carrying <paramref name="text"/> as a BDAT TAD message on
        /// the session port. Long text is truncated to a single 255-byte BDAT for now (the menu
        /// text is well under that; chunking is a later refinement). [INFERRED envelope; iterate.]
        /// </summary>
        private XmsgFrame BuildTerminalText(XmsgFrame request, string text, bool readyForInput)
        {
            // Build via the typed API so the chain is WORD-ALIGNED: an odd-length BDAT is followed by
            // a 0x00 pad so the RFI starts on an even offset. Without that pad, odd-length replies (the
            // menu, the Echo reply) put the RFI on an odd byte, 100's word-oriented reader mis-parsed
            // it, the input credit was lost, and the terminal hung. Even-length replies happened to
            // work because their RFI was already word-aligned.
            TadMessageBuilder builder = new TadMessageBuilder().BdatText(text);
            if (readyForInput)
            {
                // RFI (ready-for-input): the flow-control credit that lets 100 send the next line.
                // The MOTD carried one, which is why the first ENTER worked; each reply must renew it.
                builder.Rfi();
            }

            byte[] payload = builder.Build();

            // Route terminal text through the SAME seed model as every other frame we originate:
            // CONTINUE our own Flags1 (not echo the incoming BDAT's), and derive Counter and channel.
            // The old echo scheme here reused 100's Flags1 (e.g. the MOTD's 0x0006), producing a
            // DUPLICATE datagram that crashed 100's XMSG (XXPER) on the first keystroke. This is not a
            // new format - it is the identical BuildResponderFrame path that got the MOTD accepted.
            return BuildResponderFrame(
                request,
                controlService: TerminalDataControlService,
                frameFlags: (byte)XmsgFrameFlags.DataA,
                role: (byte)XmsgSendOptions.None,
                sourcePort: _sessionWirePort,
                payload: payload);
        }

        /// <summary>
        /// Returns true when the frame's TAD chain carries at least one BDAT (terminal-data) message -
        /// i.e. it is a typed line rather than a bare control frame (CERS / DUMM).
        /// </summary>
        /// <param name="frame">
        /// The incoming frame.
        /// </param>
        /// <returns>
        /// True when a BDAT message is present.
        /// </returns>
        private static bool HasBdat(XmsgFrame frame)
        {
            if (frame.Tad == null)
            {
                return false;
            }

            IReadOnlyList<TadMessage> messages = frame.Tad.Messages;
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Opcode == BdatOpcode)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reads the concatenated ASCII text of every BDAT message in a frame's TAD chain.
        /// </summary>
        private static string ExtractBdatText(XmsgFrame frame)
        {
            if (frame.Tad == null)
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder();
            IReadOnlyList<TadMessage> messages = frame.Tad.Messages;
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Opcode == BdatOpcode)
                {
                    byte[] data = messages[i].Data;
                    for (int j = 0; j < data.Length; j++)
                    {
                        // Strip the high (parity) bit terminals sometimes set, and keep printable
                        // characters; CR/LF are handled by the menu's Trim.
                        byte b = (byte)(data[j] & 0x7F);
                        sb.Append((char)b);
                    }
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Assembles a subtype-<c>0x0E</c> data frame from an envelope context and trailer bytes
        /// (mirror of <see cref="TadSession"/>'s private assembler, duplicated here so the
        /// responder is self-contained).
        /// </summary>
        private static XmsgFrame AssembleDataFrame(TadFrameContext context, byte[] trailer)
        {
            XmsgFrame frame = new XmsgFrame();
            frame.Header.Marker1 = SintranHeader.Marker1Value;
            frame.Header.Marker2 = SintranHeader.Marker2Normal;
            frame.Header.PacketType = 0x00;
            frame.Header.Subtype = SintranPacketSubtype.Data;
            frame.Header.DestinationNode = context.DestinationNode;
            frame.Header.SourceNode = context.SourceNode;
            frame.Header.Flags1 = context.DatagramSequence;
            frame.Header.Flags2 = context.FrameClass;

            // WORD 6 IS COMPUTED - CORRECTED 2026-08-06, same as TadSession.
            //
            // ProtocolId and Counter are compatibility views over the checksum's HIGH and LOW bytes,
            // so setting both FABRICATED word 6 out of the context. Word 6 is a ones-complement
            // checksum over words 0-5, confirmed on 3595/3595 captured frames, and a wrong one kills
            // D100 with XMSG ERROR CODE 24.
            //
            // This method is a copy of TadSession.AssembleDataFrame - the comment above it says so -
            // which is why it carried the identical defect. That duplication is worth removing, but
            // not inside a checksum fix.
            XmsgEnvelope.StampChecksum(frame.Header);

            XmsgSubHeader sub = new XmsgSubHeader();
            sub.FrameFlags = context.FrameFlags;
            sub.Role = context.Role;
            sub.DestinationSystem = context.DestinationSystem;
            sub.DestinationPort = context.DestinationPort;
            sub.SourceSystem = context.SourceSystem;
            sub.SourcePort = context.SourcePort;
            // XMCSM is ONE word at wire 26-27; the rest of the old 32-bit "control service",
            // the pad and the length byte are the first four bytes of the MESSAGE BODY at wire
            // 28-31. ComposeBody splits them through the single compatibility facade.
            ushort xmcsm;
            byte[] body = XmsgDataFields.ComposeBody(context.ControlService, trailer, out xmcsm);
            sub.Xmcsm = xmcsm;

            frame.SubHeader = sub;
            frame.TrailingBytes = body;
            frame.ClearRawBytes();
            return frame;
        }
    }
}
