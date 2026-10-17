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
    /// An event-driven TAD connect-to session state machine built on top of the decoded
    /// XMSG frame model. It tracks the session phase and the negotiated terminal parameters
    /// as frames are observed, and (client side) builds the frames that DRIVE the connect-to.
    /// </summary>
    /// <remarks>
    /// <para><b>Provenance - read this first</b></para>
    /// This layer is the most inference-heavy in the stack: it is reconstructed almost
    /// entirely from ONE captured connect-to scenario
    /// (<c>new-conn-to-102-from-100.pcapng</c>, cross-checked with
    /// <c>conn-to-102-from103-via100.pcapng</c>). The phase model is INFERRED; every value the
    /// builders emit that was seen exactly once is OBSERVED (single capture), not VERIFIED.
    /// The opcode meanings are VERIFIED against <c>TAD/TAD-Message-Formats.md</c>, but the
    /// concrete envelope bytes (counters, flags, ports) come only from capture and are supplied
    /// through a <see cref="TadFrameContext"/> so the machine never fabricates them.
    /// <para><b>Determinism</b></para>
    /// The session holds no wall-clock. Any timing (RFI credit, retransmit) belongs to the
    /// injected-clock <c>LapbLayer</c> beneath it; this layer is purely event-driven.
    /// (Plain-text, not a cref: <c>LapbLayer</c> lives in the replaceable RetroIO.Protocols.Xmsg.Live half, which
    /// this portable RetroIO.Protocols.Xmsg.Node type does not reference.)
    /// <para><b>Honesty about the server</b></para>
    /// Only one scenario is captured, so a genuinely general correct server is impossible from
    /// this evidence. For faithful server behaviour on the captured input, use
    /// <see cref="TadReplayServer"/>, which replays recorded responses and refuses to fabricate
    /// for inputs it never saw. The server-side builders here only produce the specific control
    /// frames proven byte-identical against the capture.
    /// </remarks>
    public sealed class TadSession
    {
        /// <summary>
        /// The XMCSM control/service word of the XROUT XSLET setup frame (send a letter to
        /// the terminal directory service).
        /// </summary>
        /// <remarks>
        /// OBSERVED (single capture): both captured connect-to scenarios open with
        /// XMCSM <c>0x04000041</c> whose low byte <c>0x41</c> = XSLET (XMSG-PROTOCOL.md 9,
        /// TAD-Message-Formats.md). The high bytes <c>0x040000</c> are carried verbatim.
        /// </remarks>
        public const uint XroutSetupControlService = (uint)XmcsmService.XsletLetter;

        // OBSERVED (single capture): the fixed prefix of the XSLET letter body that precedes
        // the target remote name. Bytes: serial 0xFF, service 0x07, then 0x2A and the ASCII
        // service name "TADADM" and a 0x00. The trailing target name is appended as a string
        // parameter (see BuildDirectoryLetterBody). This whole prefix is a template lifted
        // from the single capture - its internal structure is only partly understood (the
        // 0xFF/0x07 header and the 0x2A separator are not decoded), which is why it is treated
        // as an opaque OBSERVED template rather than synthesised field by field.
        private static readonly byte[] DirectoryLetterPrefix =
        {
            0xFF, 0x07, 0x2A, 0x54, 0x41, 0x44, 0x41, 0x44, 0x4D, 0x00,
        };

        // OBSERVED (single capture): the target remote name is carried as ND standard-message
        // string parameter number 2 - type byte 0xFE (= -2, string param 2), then a 1-byte
        // length, then the ASCII name (e.g. "D102" -> FE 04 44 31 30 32).
        private const byte DirectoryNameParameterType = 0xFE;

        private readonly TadSessionRole _role;
        private readonly TadNegotiatedParameters _parameters;
        private readonly List<TadSessionState> _phaseHistory;

        private TadSessionState _state;

        /// <summary>
        /// Raised when the session moves from one phase to another.
        /// </summary>
        /// <param name="previous">
        /// The phase the session was in before the transition.
        /// </param>
        /// <param name="current">
        /// The phase the session is in after the transition.
        /// </param>
        public delegate void PhaseChanged(TadSessionState previous, TadSessionState current);

        /// <summary>
        /// Occurs when the session phase changes.
        /// </summary>
        public event PhaseChanged? OnPhaseChanged;

        /// <summary>
        /// Initialises a new session for a given role.
        /// </summary>
        /// <param name="role">
        /// Whether this session drives the connect-to (client) or answers it (server).
        /// </param>
        public TadSession(TadSessionRole role)
        {
            _role = role;
            _parameters = new TadNegotiatedParameters();
            _phaseHistory = new List<TadSessionState>();
            _state = TadSessionState.Idle;
            _phaseHistory.Add(TadSessionState.Idle);
        }

        /// <summary>
        /// Gets the role this session plays.
        /// </summary>
        public TadSessionRole Role
        {
            get { return _role; }
        }

        /// <summary>
        /// Gets the current session phase.
        /// </summary>
        public TadSessionState State
        {
            get { return _state; }
        }

        /// <summary>
        /// Gets the terminal parameters negotiated so far on this session.
        /// </summary>
        public TadNegotiatedParameters Parameters
        {
            get { return _parameters; }
        }

        /// <summary>
        /// Gets the ordered list of phases this session has entered, starting with
        /// <see cref="TadSessionState.Idle"/>.
        /// </summary>
        public IReadOnlyList<TadSessionState> PhaseHistory
        {
            get { return _phaseHistory; }
        }

        /// <summary>
        /// Observes a decoded XMSG data frame and advances the session phase and negotiated
        /// parameters accordingly.
        /// </summary>
        /// <param name="frame">
        /// A decoded frame (typically produced by <see cref="XmsgFrame.Parse(ReadOnlySpan{byte})"/>,
        /// exactly as the <c>LiveNode</c> receive loop produces them). Non-data frames
        /// and frames without TAD/letter content are ignored.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="frame"/> is null.
        /// </exception>
        public void Observe(XmsgFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            // An XROUT letter (XMCSM low byte 0x41, decoded into Body) opens the connect-to.
            // OBSERVED: this is the XSLET name-resolution step (XMSG-PROTOCOL.md 9).
            if (frame.Body != null)
            {
                EnterXroutSetup();
                return;
            }

            SubProtocol.TadChain? chain = frame.Tad;
            if (chain == null)
            {
                return;
            }

            // Capture negotiated parameters and derive the phase from the opcodes present.
            IReadOnlyList<TadMessage> messages = chain.Messages;
            for (int i = 0; i < messages.Count; i++)
            {
                ApplyMessage(messages[i]);
            }
        }

        /// <summary>
        /// Builds the XROUT XSLET setup frame that resolves a remote name to the terminal
        /// directory service (the first client frame of a connect-to).
        /// </summary>
        /// <param name="context">
        /// The envelope context (addressing and sub-header field values) for the frame. The
        /// XMLEN field is overwritten with the computed letter length.
        /// </param>
        /// <param name="remoteName">
        /// The target remote name (for example <c>"D102"</c>) resolved via DEF-REMOTE / XSDRN.
        /// </param>
        /// <returns>
        /// A data frame whose trailer is the XSLET directory letter naming
        /// <paramref name="remoteName"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="context"/> or <paramref name="remoteName"/> is null.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when called on a <see cref="TadSessionRole.Server"/> session - only the
        /// client drives the setup letter.
        /// </exception>
        public XmsgFrame BuildXroutSetupFrame(TadFrameContext context, string remoteName)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (remoteName == null)
            {
                throw new ArgumentNullException(nameof(remoteName));
            }

            if (_role != TadSessionRole.Client)
            {
                throw new InvalidOperationException("Only a client session drives the XROUT setup letter.");
            }

            byte[] letter = BuildDirectoryLetterBody(remoteName);
            return AssembleDataFrame(context, letter);
        }

        /// <summary>
        /// Builds the XSLET directory-letter body naming a target remote.
        /// </summary>
        /// <param name="remoteName">
        /// The target remote name (ASCII).
        /// </param>
        /// <returns>
        /// The letter body: the OBSERVED fixed prefix, then a string parameter carrying
        /// <paramref name="remoteName"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="remoteName"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="remoteName"/> is empty or longer than 255 bytes.
        /// </exception>
        public static byte[] BuildDirectoryLetterBody(string remoteName)
        {
            if (remoteName == null)
            {
                throw new ArgumentNullException(nameof(remoteName));
            }

            byte[] nameBytes = Encoding.ASCII.GetBytes(remoteName);
            if (nameBytes.Length == 0 || nameBytes.Length > 255)
            {
                throw new ArgumentException("Remote name must be 1-255 ASCII bytes.", nameof(remoteName));
            }

            // Layout: [prefix][0xFE][len][name]. OBSERVED (single capture) - see the prefix
            // and parameter-type field notes above.
            byte[] body = new byte[DirectoryLetterPrefix.Length + 2 + nameBytes.Length];
            int cursor = 0;
            for (int i = 0; i < DirectoryLetterPrefix.Length; i++)
            {
                body[cursor++] = DirectoryLetterPrefix[i];
            }

            body[cursor++] = DirectoryNameParameterType;
            body[cursor++] = (byte)nameBytes.Length;
            for (int i = 0; i < nameBytes.Length; i++)
            {
                body[cursor++] = nameBytes[i];
            }

            return body;
        }

        /// <summary>
        /// Builds a single-message TAD control/negotiation data frame (for example RESE, RECO
        /// or DCON).
        /// </summary>
        /// <param name="context">
        /// The envelope context; its XMLEN is overwritten with the trailer length.
        /// </param>
        /// <param name="opcode">
        /// The TAD opcode byte (see <see cref="TadOp"/>).
        /// </param>
        /// <param name="data">
        /// The message data bytes (empty for a zero-count control message).
        /// </param>
        /// <returns>
        /// A data frame whose trailer is the single TAD message.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="context"/> is null.
        /// </exception>
        public XmsgFrame BuildControlFrame(TadFrameContext context, TadOp opcode, ReadOnlySpan<byte> data)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            byte[] trailer = TadChainWriter.SingleMessage((byte)opcode, data);
            return AssembleDataFrame(context, trailer);
        }

        /// <summary>
        /// Assembles a data (subtype <c>0x0E</c>) frame from an envelope context and a trailer.
        /// </summary>
        /// <param name="context">
        /// The envelope context.
        /// </param>
        /// <param name="trailer">
        /// The trailer bytes to place after the XMSG sub-header (a TAD chain or a letter body).
        /// </param>
        /// <returns>
        /// A freshly built <see cref="XmsgFrame"/> with no retained raw bytes, so
        /// <see cref="XmsgFrame.ToArray"/> serialises from the structured fields.
        /// </returns>
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

            // WORD 6 IS COMPUTED, NOT CARRIED - CORRECTED 2026-08-06.
            //
            // This used to set Header.ProtocolId from context.ProtocolId and Header.Counter from
            // context.Counter. Those two properties are compatibility views over the checksum's HIGH
            // and LOW bytes, so between them they FABRICATED word 6 out of whatever the caller had
            // put in the context - measured as 0xDD55 where the carved checksum is 0x9008.
            //
            // Word 6 is a ones-complement checksum over words 0-5, carved from the kernel and
            // confirmed on 3595/3595 captured frames. A fabricated one kills D100 with XMSG ERROR
            // CODE 24; the identical defect on the file-access path cost most of a day. It survived
            // here because D100 does not validate word 6 on every subtype, and because nodes 100 and
            // 103 are small enough that the sum's high half contributes nothing - the same accident
            // that hid it on the FA path until node 19999.
            //
            // context.ProtocolId and context.Counter are left alone: they are still meaningful to
            // callers that read them, they simply no longer decide what goes on the wire.
            //
            // NOT YET VERIFIED LIVE against a TAD session - see TadHeaderChecksumTests.
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

            // OBSERVED: XMLEN equals the trailer byte length on 100% of captured data frames
            // (it is the low byte of the user-data length). Derived, not supplied.

            frame.SubHeader = sub;
            frame.TrailingBytes = body;
            frame.ClearRawBytes();
            return frame;
        }

        /// <summary>
        /// Applies one observed TAD message: captures any negotiated parameter it carries and
        /// advances the phase if the opcode signals a phase change.
        /// </summary>
        /// <param name="message">
        /// The decoded TAD message.
        /// </param>
        private void ApplyMessage(TadMessage message)
        {
            byte[] data = message.Data;
            switch ((TadOp)message.Opcode)
            {
                case TadOp.Tmod:
                    if (data.Length >= 1)
                    {
                        _parameters.TerminalMode = data[0];
                    }

                    EnterNegotiating();
                    break;

                case TadOp.Ttyp:
                    if (data.Length >= 2)
                    {
                        // VERIFIED layout: 16-bit big-endian id (TAD-Message-Formats.md 4.2).
                        _parameters.TerminalType = (ushort)((data[0] << 8) | data[1]);
                    }

                    EnterNegotiating();
                    break;

                case TadOp.Desc:
                    if (data.Length >= 1)
                    {
                        _parameters.EscapeCharacter = data[0];
                    }

                    EnterNegotiating();
                    break;

                case TadOp.Eckm:
                    if (data.Length >= 1)
                    {
                        _parameters.EchoStrategy = data[0];
                    }

                    EnterNegotiating();
                    break;

                case TadOp.Opsv:
                    if (data.Length >= 1)
                    {
                        byte[] copy = new byte[data.Length];
                        Array.Copy(data, copy, data.Length);
                        _parameters.OsVersion = copy;
                    }

                    EnterNegotiating();
                    break;

                case TadOp.Bmmx:
                    EnterNegotiating();
                    break;

                case TadOp.Bdat:
                case TadOp.Rfi:
                    EnterDataPhase();
                    break;

                case TadOp.Rese:
                    Transition(TadSessionState.Resetting);
                    break;

                case TadOp.Dcon:
                    Transition(TadSessionState.Disconnected);
                    break;

                default:
                    // Opcodes without a phase effect (ESCA, CERS, RECO, DUMM, SYCN and the
                    // undocumented 0x06/0x1B/0x1C/0x20/0x21/0x60 seen in the capture) do not
                    // move the phase on their own. They are catalogued in TAD-MISSING.md.
                    break;
            }
        }

        /// <summary>
        /// Enters the XROUT setup phase if still idle.
        /// </summary>
        private void EnterXroutSetup()
        {
            if (_state == TadSessionState.Idle)
            {
                Transition(TadSessionState.XroutSetup);
            }
        }

        /// <summary>
        /// Enters the negotiating phase from the idle or XROUT-setup phases.
        /// </summary>
        private void EnterNegotiating()
        {
            // INFERRED boundary: config opcodes seen while still setting up mean negotiation
            // has begun. Config opcodes that recur during the data phase (rare) do not pull
            // the session backwards.
            if (_state == TadSessionState.Idle || _state == TadSessionState.XroutSetup)
            {
                Transition(TadSessionState.Negotiating);
            }
        }

        /// <summary>
        /// Enters the data phase from setup, negotiation or a completed reset.
        /// </summary>
        private void EnterDataPhase()
        {
            if (_state == TadSessionState.XroutSetup
                || _state == TadSessionState.Negotiating
                || _state == TadSessionState.Resetting)
            {
                Transition(TadSessionState.DataPhase);
            }
        }

        /// <summary>
        /// Moves to a new phase and raises <see cref="OnPhaseChanged"/> if it differs.
        /// </summary>
        /// <param name="next">
        /// The phase to move to.
        /// </param>
        private void Transition(TadSessionState next)
        {
            if (_state == next)
            {
                return;
            }

            TadSessionState previous = _state;
            _state = next;
            _phaseHistory.Add(next);
            OnPhaseChanged?.Invoke(previous, next);
        }
    }
}
