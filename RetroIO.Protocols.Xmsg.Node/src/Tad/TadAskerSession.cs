using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Packet;
using RetroIO.Protocols.Xmsg.SubProtocol;

namespace RetroIO.Protocols.Xmsg.Node.Tad
{
    /// <summary>
    /// The reactive connect-to CLIENT (asker) driver: sends the connect letter, then reacts to each
    /// host frame per the ND TAD wire spec (TAD-Message-Formats.md section 22.13) - acking every host
    /// data frame and driving the handshake (session-setup -> terminal-setup -> RECO -> banner -> login).
    /// Renders the host's BDAT terminal text; the caller supplies typed lines.
    /// </summary>
    /// <remarks>
    /// The per-session ACK parameters mirror the proven responder: the ACK rides the connect channel
    /// + 4 and its trailing counter seeds at connect-counter + 0x0A, decrementing (a bad ACK counter
    /// or channel crashes machine 100 with XXPER, so this reuses the value proven live).
    /// </remarks>
    public sealed class TadAskerSession
    {
        private readonly TadConnectClient _client;
        private readonly SecureDatagramReceiver _ackReceiver;
        private readonly SintranProtocolId _ackChannel;
        private readonly string _targetName;

        private bool _terminalSetupSent;

        /// <summary>
        /// Occurs when the host sends terminal text (BDAT), high bit stripped, for display.
        /// </summary>
        public event TadTerminalTextHandler? TerminalText;

        /// <summary>
        /// Occurs with a human-readable diagnostic of what the driver decided.
        /// </summary>
        public event XmsgLogHandler? Log;

        /// <summary>
        /// Initialises the asker session.
        /// </summary>
        /// <param name="clientNode">
        /// This client's node number (the asker).
        /// </param>
        /// <param name="serverNode">
        /// The host's node number.
        /// </param>
        /// <param name="clientPort">
        /// The client's session-source port.
        /// </param>
        /// <param name="seed">
        /// The shared link seed (100-102 = 0x14, 100-103 = 0x13).
        /// </param>
        /// <param name="targetName">
        /// The remote name to connect to (for example "D100").
        /// </param>
        /// <param name="sequenceStore">
        /// Persists the client's outgoing datagram sequence per host node across restarts (null = a
        /// non-persisting store starting at 0).
        /// </param>
        public TadAskerSession(ushort clientNode, ushort serverNode, ushort clientPort, byte seed, string targetName, IResponderSequenceStore? sequenceStore = null)
        {
            _client = new TadConnectClient(clientNode, serverNode, clientPort, seed, sequenceStore);
            _targetName = targetName ?? throw new ArgumentNullException(nameof(targetName));

            // The client fixes the per-session ACK parameters (channel = connect-channel + 4, counter =
            // connect-counter + 0x0A) from the resumed connect Flags1; seed the receiver identically.
            _ackChannel = _client.AckChannel;
            _ackReceiver = new SecureDatagramReceiver(_client.InitialAckCounter);
        }

        /// <summary>
        /// Starts the session: the connect letter to the host's port 0.
        /// </summary>
        /// <returns>
        /// The frames to transmit (the connect letter).
        /// </returns>
        public IReadOnlyList<XmsgFrame> Start()
        {
            Log?.Invoke($"[asker] connect-to {_targetName}");
            return new List<XmsgFrame> { _client.BuildConnect(_targetName) };
        }

        /// <summary>
        /// Reacts to one frame received from the host: acks it (if data), renders any BDAT, and drives
        /// the next step of the handshake.
        /// </summary>
        /// <param name="frame">
        /// The decoded frame received from the host.
        /// </param>
        /// <returns>
        /// The frames to transmit in response (ACK first, then any driving frames).
        /// </returns>
        public IReadOnlyList<XmsgFrame> OnReceive(XmsgFrame frame)
        {
            List<XmsgFrame> outgoing = new List<XmsgFrame>();
            if (frame?.SubHeader == null || frame.Header == null)
            {
                return outgoing;
            }

            // A host ACK of one of our frames needs no reply, but its echoed Flags1 advances our
            // persisted sequence so a restart resumes in step with the host's expected-from-us.
            if (frame.Header.Subtype == SintranPacketSubtype.Ack)
            {
                _client.ConfirmDelivered(frame.Header.Flags1);
                return outgoing;
            }

            if (frame.Header.Subtype != SintranPacketSubtype.Data)
            {
                return outgoing;
            }

            _client.NoteServerFrame(frame);

            // Ack every host data frame (spec 22.5), then render and drive.
            outgoing.Add(_ackReceiver.ReceiveDataFrame(frame, _ackChannel));
            RenderBdat(frame);

            uint controlService = frame.ControlService;
            if (controlService == (uint)XmcsmService.XsletLetter)
            {
                // ACCEPT (from host system port) -> send the session-setup to port 342.
                Log?.Invoke("[asker] accept received -> session-setup");
                outgoing.Add(_client.BuildSessionSetup());
            }
            else if (controlService == (uint)XmcsmService.SessionSetup)
            {
                // PORT-ASSIGN -> the terminal port is learned from the host's first 0x0108 frame (its DUMM).
                Log?.Invoke("[asker] port-assign received -> awaiting terminal DUMM");
            }
            else if (controlService == (uint)XmcsmService.TerminalData)
            {
                DriveTerminalPhase(frame, outgoing);
            }
            else if (controlService == (uint)XmcsmService.SessionNotify)
            {
                // 0xFD session-state notification -> end the session with DCON.
                Log?.Invoke("[asker] 0xFD received -> DCON");
                outgoing.Add(_client.BuildDcon());
            }

            // 0x00080000 (host 0x20 answering our ESCA): ack only, no driving frame.
            return outgoing;
        }

        /// <summary>
        /// Builds a typed keystroke line to send to the host (the user pressed Enter).
        /// </summary>
        /// <param name="text">
        /// The line the user typed.
        /// </param>
        /// <returns>
        /// The BDAT frame to transmit.
        /// </returns>
        public IReadOnlyList<XmsgFrame> SendLine(string text)
        {
            return new List<XmsgFrame> { _client.BuildInput(text ?? string.Empty) };
        }

        /// <summary>
        /// Drives the terminal phase: RESE->RECO, the priming DUMM->terminal-setup, and CERS after a
        /// host burst that grants input (ends with RFI).
        /// </summary>
        /// <param name="frame">
        /// The received terminal-data frame.
        /// </param>
        /// <param name="outgoing">
        /// The list to append response frames to.
        /// </param>
        private void DriveTerminalPhase(XmsgFrame frame, List<XmsgFrame> outgoing)
        {
            if (HasOpcode(frame, TadOp.Rese))
            {
                Log?.Invoke("[asker] RESE -> RECO");
                outgoing.Add(_client.BuildReco());
                return;
            }

            if (!_terminalSetupSent && HasOpcode(frame, TadOp.Dumm))
            {
                // The host's priming DUMM is the first frame on the fresh terminal port: now send the
                // terminal-setup negotiation and the ESCA (spec 22.4 steps 5-7).
                _terminalSetupSent = true;
                Log?.Invoke("[asker] host DUMM -> terminal-setup + ESCA");
                outgoing.Add(_client.BuildTerminalSetup());
                outgoing.Add(_client.BuildEsca());
                return;
            }

            if (HasOpcode(frame, TadOp.Rfi))
            {
                // A host burst that grants input: answer with CERS (spec 22.6). The user may now type.
                Log?.Invoke("[asker] host burst (RFI) -> CERS; input granted");
                outgoing.Add(_client.BuildCers());
            }
        }

        /// <summary>
        /// Raises <see cref="TerminalText"/> for the concatenated BDAT text of a frame (high bit stripped).
        /// </summary>
        /// <param name="frame">
        /// The received frame.
        /// </param>
        private void RenderBdat(XmsgFrame frame)
        {
            if (frame.Tad == null || TerminalText == null)
            {
                return;
            }

            IReadOnlyList<TadMessage> messages = frame.Tad.Messages;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < messages.Count; i++)
            {
                if ((TadOp)messages[i].Opcode != TadOp.Bdat)
                {
                    continue;
                }

                byte[] data = messages[i].Data;
                for (int j = 0; j < data.Length; j++)
                {
                    sb.Append((char)(data[j] & 0x7F));
                }
            }

            if (sb.Length > 0)
            {
                TerminalText?.Invoke(sb.ToString());
            }
        }

        /// <summary>
        /// Returns true when the frame's TAD chain contains a message with the given opcode.
        /// </summary>
        /// <param name="frame">
        /// The frame to inspect.
        /// </param>
        /// <param name="opcode">
        /// The TAD opcode to look for.
        /// </param>
        /// <returns>
        /// True when present.
        /// </returns>
        private static bool HasOpcode(XmsgFrame frame, TadOp opcode)
        {
            if (frame.Tad == null)
            {
                return false;
            }

            IReadOnlyList<TadMessage> messages = frame.Tad.Messages;
            for (int i = 0; i < messages.Count; i++)
            {
                if ((TadOp)messages[i].Opcode == opcode)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
