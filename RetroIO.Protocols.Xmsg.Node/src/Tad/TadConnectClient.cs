using System;

using RetroIO.Protocols.Xmsg.Node;
using RetroIO.Protocols.Xmsg.Packet;

namespace RetroIO.Protocols.Xmsg.Node.Tad
{
    /// <summary>
    /// The connect-to CLIENT (asker) side of a TAD terminal session: builds the frames that drive a
    /// connect-to against a <see cref="TadTerminalResponder"/> - the directory letter, the
    /// session-setup, the terminal-setup negotiation, and the typed keystroke lines - and tracks its
    /// own outgoing datagram sequence. The mirror of the server-side responder.
    /// </summary>
    /// <remarks>
    /// <para><b>Envelope model</b></para>
    /// Every frame is stamped with the shared seed model (<see cref="XmsgEnvelope"/>): the Counter and
    /// channel are derived from the link seed plus this client's own Flags1 sequence, exactly as the
    /// responder derives its own. Both nodes share the seed, so an in-memory pairing of a client and a
    /// responder is a coherent conversation without a real machine.
    /// <para><b>TAD content</b></para>
    /// All TAD chains are built through <see cref="TadMessageBuilder"/> (typed, word-aligned) - no
    /// hand-built byte arrays. The concrete negotiation values (TMOD flags, terminal type, OPSV
    /// version) are OBSERVED from a single capture and will be refined as the TAD spec firms up; they
    /// are isolated here so that refinement is a local change.
    /// </remarks>
    public sealed class TadConnectClient
    {
        private const uint XroutSetupControlService = (uint)XmcsmService.XsletLetter;   // XSLET directory letter
        private const uint SessionSetupControlService = (uint)XmcsmService.SessionSetup; // 06/1B/1C/FF negotiation chain
        private const uint TerminalDataControlService = (uint)XmcsmService.TerminalData; // DC/TAD terminal data

        private readonly ushort _clientNode;
        private readonly ushort _serverNode;
        private readonly ushort _clientPort;
        private readonly byte _seed;
        private readonly IResponderSequenceStore _sequenceStore;

        private ushort _flags1;
        private ushort _serverSessionPort;

        /// <summary>
        /// Initialises the client for a client/server node pair.
        /// </summary>
        /// <param name="clientNode">
        /// This client's node number (for example 100).
        /// </param>
        /// <param name="serverNode">
        /// The server's node number (for example 102).
        /// </param>
        /// <param name="clientPort">
        /// The client's session-source port (its allocated TAD port on the wire).
        /// </param>
        /// <param name="seed">
        /// The shared link seed (100-102 = <c>0x14</c>, 100-103 = <c>0x13</c>); the host learns the
        /// same value from the connect frame this client builds.
        /// </param>
        /// <param name="sequenceStore">
        /// Persists our outgoing datagram sequence (Flags1) per host node across restarts, so we resume
        /// in step with the host's persistent expected-from-us instead of resetting to 0 (which the host
        /// silently drops as behind-sequence). When null, a non-persisting store starts every host at 0.
        /// </param>
        public TadConnectClient(ushort clientNode, ushort serverNode, ushort clientPort, byte seed, IResponderSequenceStore? sequenceStore = null)
        {
            _clientNode = clientNode;
            _serverNode = serverNode;
            _clientPort = clientPort;
            _seed = seed;
            _sequenceStore = sequenceStore ?? new NullResponderSequenceStore();
            // Resume our sequence where our previous frames to this host left off.
            _flags1 = _sequenceStore.LoadNextFlags1(serverNode);
            // Default until learned from the host's first terminal frame; the host does not validate
            // the dest port.
            _serverSessionPort = 0x0211;

            // The session ACK parameters are fixed by the connect frame (Flags1 = the resumed value,
            // Flags2 0x0400, XMCSM 0x04000041): channel = connect-channel + 4, counter = connect-counter
            // + 0x0A. Computed here so the asker seeds its receiver identically to the proven responder.
            byte connectCounter = XmsgEnvelope.ComputeCounter(seed, _flags1, 0x0400);
            SintranProtocolId connectChannel = XmsgEnvelope.DeriveChannel(seed, _flags1, 0x0400, XroutSetupControlService);
            AckChannel = (SintranProtocolId)(byte)((byte)connectChannel + 4);
            InitialAckCounter = (byte)(connectCounter + 0x0A);
        }

        /// <summary>
        /// Gets the per-session ACK channel (connect-channel + 4) the asker acks host frames on.
        /// </summary>
        public SintranProtocolId AckChannel { get; }

        /// <summary>
        /// Gets the initial per-direction ACK counter (connect-counter + 0x0A) for the session.
        /// </summary>
        public byte InitialAckCounter { get; }

        /// <summary>
        /// Records that the host ACKed one of our frames (its 0x03 ACK echoes the Flags1 it received),
        /// persisting the next sequence as <c>ackedFlags1 + 1</c> so a restart resumes in step.
        /// </summary>
        /// <param name="ackedFlags1">
        /// The Flags1 the host's ACK echoes (the frame it confirmed receiving).
        /// </param>
        public void ConfirmDelivered(ushort ackedFlags1)
        {
            // 0xFFFF is the reachability broadcast marker, never a real data sequence.
            if (ackedFlags1 == 0xFFFF)
            {
                return;
            }

            ushort next = (ushort)(ackedFlags1 + 1);
            ushort current = _sequenceStore.LoadNextFlags1(_serverNode);
            if (next > current)
            {
                _sequenceStore.SaveNextFlags1(_serverNode, next);
            }
        }

        /// <summary>
        /// Gets the next Flags1 (datagram sequence) this client will send.
        /// </summary>
        public ushort NextFlags1
        {
            get { return _flags1; }
        }

        /// <summary>
        /// Builds the connect-to request: an XSLET directory letter naming the remote (opens the session).
        /// </summary>
        /// <param name="remoteName">
        /// The target remote name (for example <c>"D102"</c>).
        /// </param>
        /// <returns>
        /// The connect-request frame.
        /// </returns>
        public XmsgFrame BuildConnect(string remoteName)
        {
            byte[] letter = TadSession.BuildDirectoryLetterBody(remoteName);
            // Connect letter: XMCSM XSLET (frame-class 0x0400 is its high half); role 0xE4 =
            // WaitForTransfer|WakeOnStatus|HighPriority|RoutedLetter; frame-flags Setup 0x86.
            return Assemble(controlService: XroutSetupControlService,
                frameFlags: (byte)XmsgFrameFlags.Setup,
                role: (byte)(XmsgSendOptions.WaitForTransfer | XmsgSendOptions.WakeOnStatus
                    | XmsgSendOptions.HighPriority | XmsgSendOptions.RoutedLetter),
                sourcePort: _clientPort, destinationPort: 0x0000, payload: letter);
        }

        /// <summary>
        /// Builds the session-setup frame (the 06/1B/1C/FF negotiation chain) sent after the accept.
        /// </summary>
        /// <returns>
        /// The session-setup frame.
        /// </returns>
        public XmsgFrame BuildSessionSetup()
        {
            byte[] tad = new TadMessageBuilder()
                .Raw((TadOp)0x06, ReadOnlySpan<byte>.Empty)
                .Raw((TadOp)0x1B, ReadOnlySpan<byte>.Empty)
                .Raw((TadOp)0x1C, new byte[] { 0x00 })
                .Raw((TadOp)0xFF, ReadOnlySpan<byte>.Empty)
                .Build();
            // Asker data role 0x84 = WaitForTransfer|RoutedLetter; frame-flags Setup 0x86.
            return Assemble(controlService: SessionSetupControlService,
                frameFlags: (byte)XmsgFrameFlags.Setup,
                role: (byte)(XmsgSendOptions.WaitForTransfer | XmsgSendOptions.RoutedLetter),
                sourcePort: _clientPort, destinationPort: 0x0156, payload: tad);
        }

        /// <summary>
        /// Builds the terminal-setup frame (TMOD/TTYP/DESC/OPSV) that triggers the server's MOTD burst.
        /// </summary>
        /// <returns>
        /// The terminal-setup frame.
        /// </returns>
        public XmsgFrame BuildTerminalSetup()
        {
            // OBSERVED negotiation values (single capture): TMOD 0x08, TTYP 0x0000, DESC 0x1B,
            // OPSV L(0x4C)/0x01/0x04. Refined later as the TAD spec firms up.
            byte[] tad = new TadMessageBuilder()
                .Tmod(0x08)
                .Ttyp(0x0000)
                .Desc(0x1B)
                .Opsv(0x4C, 0x01, 0x04)
                .Build();
            // Asker data role 0x84 = WaitForTransfer|RoutedLetter; frame-flags Setup 0x86.
            return Assemble(controlService: TerminalDataControlService,
                frameFlags: (byte)XmsgFrameFlags.Setup,
                role: (byte)(XmsgSendOptions.WaitForTransfer | XmsgSendOptions.RoutedLetter),
                sourcePort: _clientPort, destinationPort: _serverSessionPort, payload: tad);
        }

        /// <summary>
        /// Builds a keystroke line as a BDAT terminal-input frame.
        /// </summary>
        /// <param name="text">
        /// The line the user typed (for example <c>"help"</c>). Sent as 7-bit ASCII; the server strips
        /// the parity bit either way.
        /// </param>
        /// <returns>
        /// The terminal-input frame.
        /// </returns>
        public XmsgFrame BuildInput(string text)
        {
            byte[] tad = new TadMessageBuilder().BdatText(text).Build();
            // Asker data role 0x84 = WaitForTransfer|RoutedLetter; frame-flags DataA 0x96.
            return Assemble(controlService: TerminalDataControlService,
                frameFlags: (byte)XmsgFrameFlags.DataA,
                role: (byte)(XmsgSendOptions.WaitForTransfer | XmsgSendOptions.RoutedLetter),
                sourcePort: _clientPort, destinationPort: _serverSessionPort, payload: tad);
        }

        /// <summary>
        /// Builds an ESCA (escape) control frame - sent once with the terminal-setup chain (spec 22.4).
        /// </summary>
        /// <returns>
        /// The ESCA frame.
        /// </returns>
        public XmsgFrame BuildEsca()
        {
            return BuildControl(TadOp.Esca, controlService: (uint)XmcsmService.BareTadControl,
                role: (byte)(XmsgSendOptions.WaitForTransfer | XmsgSendOptions.Bounce | XmsgSendOptions.RoutedLetter));
        }

        /// <summary>
        /// Builds a RECO (reset-confirm) control frame - the answer to each host RESE (spec 22.4).
        /// </summary>
        /// <returns>
        /// The RECO frame.
        /// </returns>
        public XmsgFrame BuildReco()
        {
            return BuildControl(TadOp.Reco, controlService: TerminalDataControlService,
                role: (byte)(XmsgSendOptions.WaitForTransfer | XmsgSendOptions.Bounce | XmsgSendOptions.RoutedLetter));
        }

        /// <summary>
        /// Builds a CERS (escape-response) control frame - sent after each consumed host burst / CESC
        /// transition (spec 22.6).
        /// </summary>
        /// <returns>
        /// The CERS frame.
        /// </returns>
        public XmsgFrame BuildCers()
        {
            return BuildControl(TadOp.Cers, controlService: TerminalDataControlService,
                role: (byte)(XmsgSendOptions.WaitForTransfer | XmsgSendOptions.Bounce | XmsgSendOptions.RoutedLetter));
        }

        /// <summary>
        /// Builds a bare single-opcode control frame carrying ANY opcode, including one
        /// <see cref="TadOp"/> does not name.
        /// </summary>
        /// <remarks>
        /// The typed builders above cover the ops a real client sends during a session. This is the
        /// way to drive the ops it sends only when a program is running - ISRQ, NWRE, TREP - and the
        /// way to present a peer with something it cannot decode, which is what a real TAD answers
        /// with a REJE (see <see cref="TadRejectPolicy"/>).
        /// </remarks>
        /// <param name="opcode">
        /// The opcode byte to place at the head of the chain.
        /// </param>
        /// <returns>
        /// The assembled control frame.
        /// </returns>
        public XmsgFrame BuildBareControl(byte opcode)
        {
            return BuildControl((TadOp)opcode, controlService: TerminalDataControlService,
                role: (byte)(XmsgSendOptions.WaitForTransfer | XmsgSendOptions.Bounce | XmsgSendOptions.RoutedLetter));
        }

        /// <summary>
        /// Builds a DUMM (dummy keepalive) control frame - sent when idle (spec 22.6).
        /// </summary>
        /// <returns>
        /// The DUMM frame.
        /// </returns>
        public XmsgFrame BuildDumm()
        {
            return BuildControl(TadOp.Dumm, controlService: TerminalDataControlService,
                role: (byte)(XmsgSendOptions.WaitForTransfer | XmsgSendOptions.Bounce | XmsgSendOptions.RoutedLetter));
        }

        /// <summary>
        /// Builds a DCON (disconnect) control frame - sent after the host's 0xFD to end the session
        /// (spec 22.7).
        /// </summary>
        /// <returns>
        /// The DCON frame.
        /// </returns>
        public XmsgFrame BuildDcon()
        {
            return BuildControl(TadOp.Dcon, controlService: (uint)XmcsmService.BareTadControl,
                role: (byte)(XmsgSendOptions.WaitForTransfer | XmsgSendOptions.Bounce | XmsgSendOptions.RoutedLetter));
        }

        /// <summary>
        /// Builds a single-message control frame (one empty TAD message).
        /// </summary>
        /// <param name="opcode">
        /// The TAD opcode.
        /// </param>
        /// <param name="controlService">
        /// The XMCSM control/service word (its high half is the frame-class).
        /// </param>
        /// <param name="role">
        /// The sub-header role byte.
        /// </param>
        /// <returns>
        /// The assembled control frame.
        /// </returns>
        private XmsgFrame BuildControl(TadOp opcode, uint controlService, byte role)
        {
            byte[] tad = new TadMessageBuilder().Raw(opcode, ReadOnlySpan<byte>.Empty).Build();
            // Frame-flags per frame class (GOD LLM correction, spec 22.6): the bare-TAD control class
            // (ESCA, DCON) rides ControlBare 0x82; the terminal-data control class (RECO, CERS, DUMM)
            // rides DataA 0x96. The old code emitted 0x96 for ESCA/DCON too, which did NOT byte-match
            // the captures - fixed here.
            byte frameFlags = controlService == (uint)XmcsmService.BareTadControl
                ? (byte)XmsgFrameFlags.ControlBare
                : (byte)XmsgFrameFlags.DataA;
            return Assemble(controlService, frameFlags, role,
                sourcePort: _clientPort, destinationPort: _serverSessionPort, payload: tad);
        }

        /// <summary>
        /// Learns session state from a frame the server sent (currently the server's session port,
        /// taken from the source port of its terminal-data frames).
        /// </summary>
        /// <param name="frame">
        /// A frame received from the server.
        /// </param>
        public void NoteServerFrame(XmsgFrame frame)
        {
            if (frame?.SubHeader == null)
            {
                return;
            }

            // The server's terminal-data frames (XMCSM 0x01080000) carry its session port as the source.
            if (frame.ControlService == TerminalDataControlService && frame.SubHeader.SourcePort != 0)
            {
                _serverSessionPort = frame.SubHeader.SourcePort;
            }
        }

        /// <summary>
        /// Assembles one client frame with the shared seed-model envelope, then advances Flags1.
        /// </summary>
        /// <param name="controlService">
        /// The XMCSM control/service word (its high half is the derived frame-class).
        /// </param>
        /// <param name="frameFlags">
        /// The sub-header frame-flags byte.
        /// </param>
        /// <param name="role">
        /// The sub-header role byte.
        /// </param>
        /// <param name="sourcePort">
        /// Our source port.
        /// </param>
        /// <param name="destinationPort">
        /// The server destination port.
        /// </param>
        /// <param name="payload">
        /// The trailer payload (letter body or TAD chain).
        /// </param>
        /// <returns>
        /// The assembled frame.
        /// </returns>
        private XmsgFrame Assemble(
            uint controlService, byte frameFlags, byte role,
            ushort sourcePort, ushort destinationPort, byte[] payload)
        {
            // On data frames the SINTRAN Flags2 frame-class IS the top 16 bits of the XMCSM word
            // (VERIFIED empirically, 601/601 data frames). Derive it here so it is never a separate
            // hand-entered value. See XMSG-PROTOCOL.md section 18.4.
            ushort frameClass = (ushort)(controlService >> 16);

            ushort f1 = _flags1;
            byte counter = XmsgEnvelope.ComputeCounter(_seed, f1, frameClass);
            SintranProtocolId channel = XmsgEnvelope.DeriveChannel(_seed, f1, frameClass, controlService);
            _flags1 = (ushort)(f1 + 1);

            XmsgFrame frame = new XmsgFrame();
            frame.Header.Marker1 = SintranHeader.Marker1Value;
            frame.Header.Marker2 = SintranHeader.Marker2Normal;
            frame.Header.PacketType = 0x00;
            frame.Header.Subtype = SintranPacketSubtype.Data;
            frame.Header.DestinationNode = _serverNode;
            frame.Header.SourceNode = _clientNode;
            frame.Header.Flags1 = f1;
            frame.Header.Flags2 = frameClass;

            // WORD 6 IS COMPUTED - CORRECTED 2026-08-06.
            //
            // This was the worst of the four. It did not merely carry word 6 through, it DERIVED it
            // from XmsgEnvelope.ComputeCounter and XmsgEnvelope.DeriveChannel - the fitted
            // seed/channel model, which XmsgEnvelope's own remarks mark SUPERSEDED and describe as
            // the wrong explanation. That model reproduces the low byte by construction (the seed is
            // learned from a received frame) and anchors the high byte at a constant, which is why
            // it looked right for years and then produced a wrong word 6 the moment a node number
            // above 255 appeared.
            //
            // Word 6 is a ones-complement checksum over words 0-5. A wrong one kills D100 with
            // XMSG ERROR CODE 24.
            XmsgEnvelope.StampChecksum(frame.Header);

            XmsgSubHeader sub = new XmsgSubHeader();
            sub.FrameFlags = frameFlags;
            sub.Role = role;
            sub.DestinationSystem = _serverNode;
            sub.DestinationPort = destinationPort;
            sub.SourceSystem = _clientNode;
            sub.SourcePort = sourcePort;
            // XMCSM is ONE word at wire 26-27; the rest of the old 32-bit "control service",
            // the pad and the length byte are the first four bytes of the MESSAGE BODY at wire
            // 28-31. ComposeBody splits them through the single compatibility facade.
            ushort xmcsm;
            byte[] body = XmsgDataFields.ComposeBody(controlService, payload, out xmcsm);
            sub.Xmcsm = xmcsm;

            frame.SubHeader = sub;
            frame.TrailingBytes = body;
            frame.ClearRawBytes();
            return frame;
        }
    }
}
