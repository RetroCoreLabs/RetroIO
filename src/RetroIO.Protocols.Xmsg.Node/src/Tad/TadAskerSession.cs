//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

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

        /// <summary>
        /// Size of the 7IAM system-name field, measured in record 3618 (element 0x0A, length 0x21).
        /// </summary>
        private const int IamSystemNameSize = 33;

        /// <summary>
        /// Size of the 7IAM user-name field, measured in record 3618 (element 0x10, length 0x11).
        /// </summary>
        private const int IamUserNameSize = 17;

        /// <summary>
        /// Length of the whole 7IAM chain: 28 00 | 0A 21 + 33 | 00 pad | 0B 02 + 2 | 10 11 + 17 | FF 00.
        /// Record 3618 carries 0x3F = 63.
        /// </summary>
        private const int IamChainLength = 2 + 2 + IamSystemNameSize + 1 + 4 + 2 + IamUserNameSize + 2;

        private bool _terminalSetupSent;

        /// <summary>
        /// The largest BDAT payload (the TAD length byte).
        /// </summary>
        private const int MaxBdat = 255;

        /// <summary>
        /// The host's break strategy from its BMMX (opcode 0x04): 0 every character ends a piece of
        /// input, 1 control characters below 32 and DEL (SINTRAN's default, used until a BMMX
        /// comes), negative none. Source: SINTRAN Monitor Calls, MON BRKM / InputString. Same rule
        /// as libxmsg <c>tad/tad_asker.c</c> (e961a02).
        /// </summary>
        private int _breakStrategy = 1;

        /// <summary>
        /// The BMMX BRKMAX word: for strategies 3 and up the most bytes one BDAT may carry (MON BRKM,
        /// "maximum number of characters before break"); 0 means no limit.
        /// </summary>
        private int _breakMax;

        /// <summary>
        /// A strategy 7 table of all ones (every character breaks). A table of all zeros breaks on
        /// none; a mixed table needs the bit order, which is not known (BUGS.md).
        /// </summary>
        private bool _breakTableAll;

        /// <summary>
        /// The host has granted input (RFI) and the grant is not used yet.
        /// </summary>
        private bool _inputGranted;

        /// <summary>
        /// Typed bytes waiting for the next grant, oldest first.
        /// </summary>
        private byte[] _pending = new byte[1024];

        /// <summary>
        /// The number of bytes in <see cref="_pending"/>.
        /// </summary>
        private int _pendingLength;
        private string _ownSystemName;
        private string _userName = "SYSTEM";

        /// <summary>
        /// Occurs when the host sends terminal text (BDAT), high bit stripped, for display.
        /// </summary>
        public event TadTerminalTextHandler? TerminalText;

        /// <summary>
        /// Occurs with a human-readable diagnostic of what the driver decided.
        /// </summary>
        public event XmsgLogHandler? Log;

        /// <summary>
        /// Occurs when the host sends its 0xFD session notice (XMCSM SessionNotify from the host's
        /// *TADADM port). The notice does NOT end the session; the owner decides when its user is done
        /// and then calls <see cref="Hangup"/>.
        /// </summary>
        public event Action? SessionNoticeReceived;

        /// <summary>
        /// Gets how many 0xFD session notices the host has sent on this session so far.
        /// </summary>
        public int SessionNoticeCount { get; private set; }

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

            // "D" + our system number in decimal is the form the real asker sends (record 3618:
            // system 102 says "D102"; live 2026-11-09 a C asker on 19999 said "D19999").
            _ownSystemName = "D" + clientNode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Gets or sets our own system name, sent in the 7IAM answer to the host's 7WHO. TADA prints it
        /// in its SYSTEM column. Default: "D" + our system number (for example "D100").
        /// </summary>
        /// <remarks>
        /// At most 32 characters: the field is 33 bytes and the name is followed by <c>'</c>.
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// The name is empty, not printable ASCII, or longer than 32 characters.
        /// </exception>
        public string OwnSystemName
        {
            get => _ownSystemName;
            set => _ownSystemName = CheckName(value, IamSystemNameSize - 1, nameof(OwnSystemName));
        }

        /// <summary>
        /// Gets or sets our terminal number, sent in the 7IAM answer (element 0x0B). TADA prints it in
        /// its TERMNO column. Default: 1 (record 3618 carries 0001).
        /// </summary>
        public ushort TerminalNumber { get; set; } = 1;

        /// <summary>
        /// Gets or sets the user name on our side, sent in the 7IAM answer (element 0x10). TADA prints
        /// it in its USER column.
        /// </summary>
        /// <remarks>
        /// The default "SYSTEM" is a LABEL, not a measured value for this session: it is the name the
        /// real asker sent in record 3618, but this class does not know who is logged in, so the owner
        /// should set it. At most 16 characters: the field is 17 bytes and the name is followed by
        /// <c>'</c>.
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// The name is empty, not printable ASCII, or longer than 16 characters.
        /// </exception>
        public string UserName
        {
            get => _userName;
            set => _userName = CheckName(value, IamUserNameSize - 1, nameof(UserName));
        }

        /// <summary>
        /// Gets how many 7WHO questions this session has answered with 7IAM.
        /// </summary>
        public int WhoAnswerCount { get; private set; }

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

            if (HasOpcode(frame, TadOp.Who))
            {
                // The host asks who we are (TADA does, for each remote terminal it lists, and waits for
                // the answer - without one it hangs). Answer to the port that asked, from the port that
                // was asked; the asking port is a fresh *TADADM port on each TADA run. The frame is a
                // question, not terminal content, so no DUMM and no other driving frame.
                WhoAnswerCount++;
                Log?.Invoke($"[asker] 7WHO from port 0x{frame.SubHeader.SourcePort:X4} -> 7IAM");
                outgoing.Add(_client.BuildIam(BuildIamChain(), frame.SubHeader.DestinationPort, frame.SubHeader.SourcePort));
                return outgoing;
            }
            RenderBdat(frame);

            uint controlService = frame.XmcsmWithBodyWord;
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
                // 0xFD session notice from the host's *TADADM port. It does NOT end the session, so
                // it is acked (above) and reported, and no DCON is sent here.
                // Measured in pcap-decode-sample.json (record = list index), asker node 100 port 02E3,
                // host 102, session port 0313, *TADADM port 0156: record 1324 login "OK", 1326 the "@"
                // prompt, 1327 the first FD from 0156, and then the session goes on - records
                // 1329-1337 more host text on the session port, the user typed more (asker records
                // 1246-1250, "log"), 1337 "--EXIT--", 1341 a second FD, and the asker's last frame is
                // DCON to session port 0313 (record 1260). Asker 103/0245 shows the same: OK, FD, more
                // session, EXIT, FD, then DCON. Sessions 100/0288, 100/0291, 100/0298 and 102/0288:
                // OK, EXIT, FD, then DCON. Both FDs of a session are byte-identical apart from Flags1,
                // so nothing in the frame says which one is "the end". Live, 2026-11-09, a RetroCore
                // D100 host sent FD about 3 s after connect, once in the middle of a LIST-FILES
                // listing; answering it with DCON killed a working session.
                // The real asker hangs up when its USER is done (after LOGOUT, or when the person
                // disconnects); the host's FD then confirms the end. That is Hangup().
                SessionNoticeCount++;
                Log?.Invoke("[asker] 0xFD session notice received (no DCON; the owner hangs up)");
                SessionNoticeReceived?.Invoke();
            }

            // 0x00080000 (host 0x20 answering our ESCA): ack only, no driving frame.
            return outgoing;
        }

        /// <summary>
        /// Queues typed text for the host and returns the letter that may go now.
        /// </summary>
        /// <remarks>
        /// One BDAT carries typed bytes up to and including the first break character of the host's
        /// break setting, and only after the host has granted input (RFI); the rest goes with later
        /// grants, from <see cref="OnReceive"/>. MEASURED 2026-11-10 on the RetroServer hub: in
        /// S3-CONFIG D100 sends BMMX strategy 0 and the real D102 asker sends the TDV key
        /// ESC [ 4 8 _ as five one-byte BDATs, each after its own RFI; the whole key in one letter
        /// is refused with BEL. At the command prompt (strategy 1) a line goes whole up to its CR.
        /// S3-CONFIG's screen letter first sets strategy 7 with BRKMAX 1 and a table of all ones.
        /// BRKMAX limits one BDAT for strategies 3 and up. Strategies 2 to 6, 8 and a mixed 7 table
        /// are not implemented: they are logged and input is cut only at BRKMAX (BUGS.md).
        /// </remarks>
        /// <param name="text">
        /// The typed text, as 7-bit ASCII; bit 7 carries even parity unless <see cref="KeystrokeParity"/> is off.
        /// </param>
        /// <returns>
        /// The BDAT frame to transmit now, or an empty list when the host has not granted input.
        /// </returns>
        public IReadOnlyList<XmsgFrame> SendLine(string text)
        {
            byte[] ascii = System.Text.Encoding.ASCII.GetBytes(text ?? string.Empty);
            if (_pendingLength + ascii.Length > _pending.Length)
            {
                Array.Resize(ref _pending, Math.Max(_pending.Length * 2, _pendingLength + ascii.Length));
            }

            Array.Copy(ascii, 0, _pending, _pendingLength, ascii.Length);
            _pendingLength += ascii.Length;
            List<XmsgFrame> outgoing = new List<XmsgFrame>(1);
            SendPendingIfGranted(outgoing);
            return outgoing;
        }

        /// <summary>
        /// Gets or sets whether typed bytes go out with even parity in bit 7. True by default, as the
        /// real asker sends them. MEASURED: "sys" + CR goes as F3 F9 F3 8D (pcap-decode-sample.json),
        /// and D102's CONNECT-TO sends the TDV key ESC [ 4 8 _ as 1B DB B4 B8 5F (RetroServer hub,
        /// 2026-11-10). Same rule as libxmsg <c>keystroke_parity</c>. Set false to send bytes as given.
        /// </summary>
        public bool KeystrokeParity { get; set; } = true;

        /// <summary>
        /// Gets the number of typed bytes still waiting for a grant from the host.
        /// </summary>
        public int PendingInputLength => _pendingLength;

        /// <summary>
        /// Gets the host's break strategy as last set by its BMMX (1 until one comes).
        /// </summary>
        public int BreakStrategy => _breakStrategy;

        /// <summary>
        /// Builds the hang-up: a DCON to the host's session port. Call it when the user is done (after
        /// LOGOUT, or when the person disconnects); the host's 0xFD then confirms the end.
        /// </summary>
        /// <returns>
        /// The DCON frame to transmit.
        /// </returns>
        public IReadOnlyList<XmsgFrame> Hangup()
        {
            Log?.Invoke("[asker] hang-up -> DCON");
            return new List<XmsgFrame> { _client.BuildDcon() };
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
            TakeBreakSetting(frame);
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
                _inputGranted = true;
                SendPendingIfGranted(outgoing);
            }
        }

        /// <summary>
        /// Sends one BDAT of waiting input when the host has granted input.
        /// </summary>
        /// <param name="outgoing">
        /// The list to append the frame to.
        /// </param>
        private void SendPendingIfGranted(List<XmsgFrame> outgoing)
        {
            if (!_inputGranted || _pendingLength == 0)
            {
                return;
            }

            int take = Math.Min(_pendingLength, MaxBdat);
            if (_breakStrategy >= 3 && _breakMax != 0 && take > _breakMax)
            {
                take = _breakMax;
            }

            for (int i = 0; i < take; i++)
            {
                if (IsBreakCharacter(_pending[i]))
                {
                    take = i + 1;
                    break;
                }
            }

            Span<byte> letter = stackalloc byte[MaxBdat];
            for (int i = 0; i < take; i++)
            {
                letter[i] = KeystrokeParity ? EvenParity(_pending[i]) : _pending[i];
            }

            outgoing.Add(_client.BuildInput(letter.Slice(0, take)));
            Array.Copy(_pending, take, _pending, 0, _pendingLength - take);
            _pendingLength -= take;
            _inputGranted = false; // one BDAT per grant
        }

        /// <summary>
        /// Gives a 7-bit character even parity in bit 7 (bit 7 set when the low seven bits hold an odd
        /// number of ones), as libxmsg <c>tad_even_parity</c> does.
        /// </summary>
        /// <param name="c">
        /// The typed byte; its bit 7 is replaced.
        /// </param>
        /// <returns>
        /// The byte with even parity.
        /// </returns>
        private static byte EvenParity(byte c)
        {
            int c7 = c & 0x7F;
            int ones = System.Numerics.BitOperations.PopCount((uint)c7);
            return (byte)((ones & 1) != 0 ? c7 | 0x80 : c7);
        }

        /// <summary>
        /// Tells whether a typed character ends a piece of input under the host's break setting.
        /// </summary>
        /// <param name="c">
        /// The typed byte.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the BDAT must end after this byte.
        /// </returns>
        private bool IsBreakCharacter(byte c)
        {
            int c7 = c & 0x7F;
            switch (_breakStrategy)
            {
                case 0:
                    return true;
                case 1:
                    return c7 < 32 || c7 == 127;
                case 7:
                    return _breakTableAll;
                default:
                    return false; // negative: none; 2..6, 8 and a mixed 7 table: not implemented
            }
        }

        /// <summary>
        /// Takes the host's break setting from the BMMX messages of a frame (strategy byte, BRKMAX word).
        /// </summary>
        /// <param name="frame">
        /// The received frame.
        /// </param>
        private void TakeBreakSetting(XmsgFrame frame)
        {
            if (frame.Tad == null)
            {
                return;
            }

            IReadOnlyList<TadMessage> messages = frame.Tad.Messages;
            for (int i = 0; i < messages.Count; i++)
            {
                if ((TadOp)messages[i].Opcode != TadOp.Bmmx || messages[i].Data.Length < 1)
                {
                    continue;
                }

                byte[] data = messages[i].Data;
                int strategy = (sbyte)data[0];
                _breakMax = data.Length >= 3 ? (data[1] << 8) | data[2] : 0;
                if (strategy == 9)
                {
                    continue; // MON BRKM: 9 changes only BRKMAX; the strategy in force stays
                }

                _breakStrategy = strategy;
                _breakTableAll = false;
                bool readable = strategy <= 1;
                if (strategy == 7 && data.Length >= 19)
                {
                    // Only all ones or all zeros can be read: they need no bit order.
                    bool ones = true;
                    bool zeros = true;
                    for (int j = 3; j < 19; j++)
                    {
                        ones &= data[j] == 0xFF;
                        zeros &= data[j] == 0x00;
                    }

                    _breakTableAll = ones;
                    readable = ones || zeros;
                }

                if (!readable)
                {
                    Log?.Invoke($"[asker] host break strategy {strategy} is not implemented; input is cut only at BRKMAX {_breakMax}");
                }
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
        /// Builds the 7IAM chain as record 3618 lays it out (63 bytes):
        /// <c>28 00 | 0A 21 + 33 bytes system name | 00 | 0B 02 + terminal (big-endian) | 10 11 + 17 bytes user name | FF 00</c>.
        /// </summary>
        /// <remarks>
        /// The single 00 after the system name is the word-alignment pad a SINTRAN host puts before an
        /// element that would start at an odd offset (TAD-Message-Formats 2.1); it is in the capture.
        /// Same layout as <c>build_iam</c> in libxmsg <c>tad/tad_asker.c</c>, verified live there.
        /// </remarks>
        /// <returns>
        /// The packed chain, terminator included.
        /// </returns>
        private byte[] BuildIamChain()
        {
            byte[] c = new byte[IamChainLength];
            int n = 0;
            c[n++] = (byte)TadOp.Iam;
            c[n++] = 0;
            c[n++] = (byte)TadOp.IamSystemName;
            c[n++] = IamSystemNameSize;
            PutName(c.AsSpan(n, IamSystemNameSize), _ownSystemName);
            n += IamSystemNameSize;
            c[n++] = 0; // alignment pad
            c[n++] = (byte)TadOp.Lun;
            c[n++] = 2;
            c[n++] = (byte)(TerminalNumber >> 8);
            c[n++] = (byte)TerminalNumber;
            c[n++] = (byte)TadOp.IamUserName;
            c[n++] = IamUserNameSize;
            PutName(c.AsSpan(n, IamUserNameSize), _userName);
            n += IamUserNameSize;
            c[n++] = 0xFF; // chain terminator
            c[n] = 0;
            return c;
        }

        /// <summary>
        /// Writes a name into a fixed SINTRAN string field: the text, <c>'</c>, then zeros (the array
        /// is new, so the zeros are already there).
        /// </summary>
        /// <param name="field">
        /// The field to fill.
        /// </param>
        /// <param name="name">
        /// The name, already checked to leave room for the <c>'</c>.
        /// </param>
        private static void PutName(Span<byte> field, string name)
        {
            for (int i = 0; i < name.Length; i++)
            {
                field[i] = (byte)name[i];
            }

            field[name.Length] = (byte)'\'';
        }

        /// <summary>
        /// Checks a name for a fixed 7IAM field: not empty, printable ASCII, and short enough to leave
        /// room for the closing <c>'</c>.
        /// </summary>
        /// <param name="value">
        /// The name.
        /// </param>
        /// <param name="maxLength">
        /// The longest allowed name.
        /// </param>
        /// <param name="paramName">
        /// The property name, for the exception.
        /// </param>
        /// <returns>
        /// The name.
        /// </returns>
        private static string CheckName(string value, int maxLength, string paramName)
        {
            if (string.IsNullOrEmpty(value) || value.Length > maxLength)
            {
                throw new ArgumentException($"must be 1 to {maxLength} characters", paramName);
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] < 0x20 || value[i] > 0x7E)
                {
                    throw new ArgumentException("must be printable ASCII", paramName);
                }
            }

            return value;
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
