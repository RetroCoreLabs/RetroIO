using System;
using System.Collections.Generic;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.PacketLayer
{
    /// <summary>
    /// The X.25 packet layer for one link: it owns a table of virtual circuits and runs the ITU-T X.25
    /// call/data/clear/reset/restart state machine over them, raising up-events for its applications and
    /// sending packets down through an injected sink. Packet encoding/decoding is delegated to the
    /// stateless codec; this class holds only session/circuit state.
    /// </summary>
    public sealed class X25Layer : X25LayerBase
    {
        private readonly X25Config _config;
        private readonly Dictionary<ushort, X25Vc> _vcs = new Dictionary<ushort, X25Vc>();
        // Our Restart Request, from sending it until the peer confirms it or sends its own (X.25
        // 4.1.2): the T20 deadline, how often it has been retransmitted, and what it said.
        private bool _restartOutstanding;
        private long _restartExpiryMs;
        private int _restartRetries;
        private byte _restartCause;
        private byte _restartDiagnostic;

        /// <summary>
        /// Creates an X.25 layer over a packet sink with the given configuration.
        /// </summary>
        /// <param name="sink">
        /// The link below, called to frame and transmit packets.
        /// </param>
        /// <param name="config">
        /// Addressing, channel range, defaults, and timer durations.
        /// </param>
        public X25Layer(IX25PacketSink sink, X25Config config)
            : base(sink)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _config.Validate();
        }

        /// <summary>
        /// Declares a permanent virtual circuit on a logical channel outside the virtual call range.
        /// </summary>
        /// <param name="lci">
        /// The channel. X.25 Annex A: permanent virtual circuits take the lowest channels, below every
        /// virtual call range; here it must simply not be 0 and not inside the configured call range.
        /// </param>
        /// <param name="windowSize">
        /// Window in both directions (1..7).
        /// </param>
        /// <param name="packetSize">
        /// Maximum data field in both directions (a power of two, 16..4096).
        /// </param>
        /// <returns>
        /// The circuit, in flow control ready state (d1) with zeroed sequence numbers.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The channel is 0, lies in the virtual call range, or the sizes are outside the clauses.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// A circuit already exists on the channel.
        /// </exception>
        /// <remarks>
        /// A permanent virtual circuit is never set up or cleared (X.25 4.3, 4.4.3): it exists from
        /// configuration, it is reset to recover, and a restart resets it (4.1.1). Until 2026-09-06
        /// the engine had the PVC kind but no way to create one.
        /// </remarks>
        public X25Vc AddPvc(ushort lci, byte windowSize, int packetSize)
        {
            int first = _config.LciBase == 0 ? 1 : _config.LciBase;
            int last = _config.LciBase + _config.LciCount - 1;
            if (lci == 0 || (lci >= first && lci <= last))
            {
                throw new ArgumentOutOfRangeException(nameof(lci), lci,
                    "a permanent virtual circuit must not use channel 0 or a channel in the virtual call range " + first + ".." + last);
            }

            if (windowSize < 1 || windowSize > X25Facilities.MaxWindowModulo8)
            {
                throw new ArgumentOutOfRangeException(nameof(windowSize), windowSize, "X.25 7.3.1.2: the window is 1..7 with modulo 8");
            }

            bool powerOfTwo = packetSize >= 16 && packetSize <= 4096 && (packetSize & (packetSize - 1)) == 0;
            if (!powerOfTwo)
            {
                throw new ArgumentOutOfRangeException(nameof(packetSize), packetSize, "X.25 7.3.1.1: the packet size is a power of two from 16 to 4096");
            }

            if (_vcs.ContainsKey(lci))
            {
                throw new InvalidOperationException("a circuit already exists on logical channel " + lci);
            }

            X25Vc vc = new X25Vc(lci, X25VcKind.Pvc)
            {
                State = X25VcState.PvcReady,
                WindowSizeIn = windowSize,
                WindowSizeOut = windowSize,
                PacketSizeIn = packetSize,
                PacketSizeOut = packetSize,
            };
            _vcs[lci] = vc;
            return vc;
        }

        /// <summary>
        /// The virtual circuits currently tracked, keyed by logical channel.
        /// </summary>
        public IReadOnlyDictionary<ushort, X25Vc> Circuits => _vcs;

        /// <summary>
        /// How many times the peer broke a rule badly enough to reset a circuit (invalid P(S) or
        /// P(R), a REJ we do not support, ...).
        /// </summary>
        public long ProcedureErrors { get; private set; }

        /// <summary>
        /// How many packets arrived in a state where they mean nothing and were dropped.
        /// </summary>
        public long UnexpectedPackets { get; private set; }

        /// <summary>Calls that were never answered (T21 expired).</summary>
        public long CallTimeouts { get; private set; }

        /// <summary>Reset Requests that were never confirmed through every retransmission (T22).</summary>
        public long ResetTimeouts { get; private set; }

        /// <summary>Clear Requests that were never confirmed through every retransmission (T23).</summary>
        public long ClearTimeouts { get; private set; }

        /// <summary>Restart Requests that were never confirmed through every retransmission (T20).</summary>
        public long RestartTimeouts { get; private set; }

        /// <summary>Diagnostic packets this engine has sent (a DCE only; X.25 5.6).</summary>
        public long DiagnosticsSent { get; private set; }

        /// <summary>Diagnostic packets received from the peer.</summary>
        public long DiagnosticsReceived { get; private set; }

        /// <summary>
        /// Places an outgoing call and returns its logical channel, or 0 if no channel is free.
        /// </summary>
        /// <param name="calledAddress">
        /// Destination X.121 address.
        /// </param>
        /// <param name="callingAddress">
        /// Origin X.121 address; the configured local address is used when null or empty.
        /// </param>
        /// <param name="callUserData">
        /// Optional call user data.
        /// </param>
        /// <returns>
        /// The allocated logical channel, or 0 on failure.
        /// </returns>
        public ushort PlaceCall(string calledAddress, string? callingAddress = null, byte[]? callUserData = null)
        {
            ushort lci = AllocateLci();
            if (lci == 0)
            {
                return 0;
            }

            string calling = string.IsNullOrEmpty(callingAddress) ? _config.LocalAddress : callingAddress!;
            X25Vc vc = new X25Vc(lci, X25VcKind.Svc)
            {
                State = X25VcState.CallRequest,
                CalledAddress = calledAddress,
                CallingAddress = calling,
                CallUserData = callUserData ?? Array.Empty<byte>(),
                WindowSizeOut = _config.DefaultWindowSize,
                WindowSizeIn = _config.DefaultWindowSize,
                PacketSizeOut = _config.DefaultPacketSize,
                PacketSizeIn = _config.DefaultPacketSize,
                TimerExpiryMs = Now() + _config.CallRequestTimeoutMs
            };
            _vcs[lci] = vc;

            // X.25 4.1.1: in state r2 (our Restart Request unanswered) no call set-up packet may be
            // sent. The call is accepted from the application and held; it goes out the moment the
            // restart completes, or is reported cleared if the restart never does.
            if (_restartOutstanding)
            {
                vc.CallRequestHeld = true;
                vc.TimerExpiryMs = 0;
                return lci;
            }

            if (!SendCallRequest(vc))
            {
                // The link did not take it: no call exists, and the channel is free again.
                _vcs.Remove(lci);
                return 0;
            }

            return lci;
        }

        /// <summary>
        /// Accepts an incoming call, moving the circuit to data transfer.
        /// </summary>
        /// <param name="lci">
        /// Logical channel of the incoming call.
        /// </param>
        /// <returns>
        /// True when the Call Accepted left; false when there was no incoming call on the channel or
        /// the link refused the packet (the call is then still incoming).
        /// </returns>
        public bool AcceptCall(ushort lci)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc) || vc.State != X25VcState.CallIncoming)
            {
                return false;
            }

            // X.25 7.2.2.1: when the caller asked for flow control values the answer states what was
            // agreed. This engine accepts what was asked (the codec has already range-checked it), so
            // the Call Accepted echoes the circuit's sizes in the caller's terms: "from called" is our
            // sending direction, "from calling" is theirs.
            byte[]? facilities = null;
            if (vc.PeerSentFacilities)
            {
                facilities = new X25Facilities
                {
                    HasPacketSize = true,
                    PacketSizeFromCalled = vc.PacketSizeOut,
                    PacketSizeFromCalling = vc.PacketSizeIn,
                    HasWindowSize = true,
                    WindowFromCalled = vc.WindowSizeOut,
                    WindowFromCalling = vc.WindowSizeIn,
                }.Encode();
            }

            // Send first: if the Call Accepted does not leave, the call is still merely incoming and
            // the application may try again or clear it.
            if (!Send(X25PacketBuilder.CreateCallAccepted(lci, facilities)))
            {
                return false;
            }

            vc.State = X25VcState.DataTransfer;
            vc.ResetFlow();
            vc.TimerExpiryMs = 0;
            return true;
        }

        /// <summary>
        /// Sends a data packet on an established circuit, subject to peer-busy and the send window.
        /// </summary>
        /// <param name="lci">
        /// Logical channel.
        /// </param>
        /// <param name="data">
        /// User data to send.
        /// </param>
        /// <param name="qBit">
        /// Qualifier bit.
        /// </param>
        /// <param name="mBit">
        /// More-data bit.
        /// </param>
        /// <param name="dBit">
        /// Delivery-confirmation bit.
        /// </param>
        /// <returns>
        /// True if the packet was sent.
        /// </returns>
        public bool SendData(ushort lci, byte[] data, bool qBit = false, bool mBit = false, bool dBit = false)
        {
            if (data == null || data.Length == 0)
            {
                return false;
            }

            if (!_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                return false;
            }

            if (vc.State != X25VcState.DataTransfer && vc.State != X25VcState.PvcReady)
            {
                return false;
            }

            if (vc.PeerBusy)
            {
                return false;
            }

            // X.25 7.3.1.1: the negotiated packet size is the maximum data field. Longer sends are
            // refused here until M-bit segmentation splits them (plan B11).
            if (data.Length > vc.PacketSizeOut)
            {
                return false;
            }

            // Enforce the outbound window: outstanding = (P(S) - last acknowledged) modulo 8.
            int outstanding = (vc.SendSeq - vc.AckedSeq) & 0x07;
            if (outstanding >= vc.WindowSizeOut)
            {
                return false;
            }

            // P(S) is spent only when the packet actually leaves. A refused send that still advanced
            // P(S) would leave a hole the peer must treat as a local procedure error (X.25 4.4.1.3).
            byte ps = vc.SendSeq;
            if (!Send(X25PacketBuilder.CreateDataPacket(lci, ps, vc.RecvSeq, data, mBit, qBit, dBit)))
            {
                return false;
            }

            vc.SendSeq = (byte)((ps + 1) & 0x07);
            return true;
        }

        /// <summary>
        /// Clears a circuit.
        /// </summary>
        /// <param name="lci">
        /// Logical channel.
        /// </param>
        /// <param name="cause">
        /// Clearing cause code.
        /// </param>
        /// <param name="diagnostic">
        /// Diagnostic code.
        /// </param>
        public bool ClearCall(ushort lci, byte cause = 0, byte diagnostic = 0)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                return false;
            }

            // X.25 4.3: a permanent virtual circuit has no clearing procedure; it is reset instead.
            // The C emulator accepted this and left the PVC permanently dead (audit F-02-14).
            if (vc.Kind == X25VcKind.Pvc)
            {
                return false;
            }

            cause = ApplyCauseRule(cause);

            // The state change stands even when the packet is refused: the application has decided
            // the call is over, and the clear-request timer is what carries that decision to the
            // peer if this attempt did not (retransmission arrives with the T23 work, plan B5).
            vc.State = X25VcState.ClearRequest;
            vc.Cause = cause;
            vc.Diagnostic = diagnostic;
            vc.Retries = 0;
            vc.CallRequestHeld = false;
            vc.TimerExpiryMs = Now() + _config.ClearRequestTimeoutMs;
            return Send(X25PacketBuilder.CreateClearRequest(lci, cause, diagnostic));
        }

        /// <summary>
        /// Resets a circuit, re-initialising its sequence state without clearing the call.
        /// </summary>
        /// <param name="lci">
        /// Logical channel.
        /// </param>
        /// <param name="cause">
        /// Reset cause code.
        /// </param>
        /// <param name="diagnostic">
        /// Diagnostic code.
        /// </param>
        public bool ResetCall(ushort lci, byte cause = 0, byte diagnostic = 0)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                return false;
            }

            cause = ApplyCauseRule(cause);

            if (vc.State != X25VcState.DataTransfer && vc.State != X25VcState.PvcReady)
            {
                return false;
            }

            // As for ClearCall: the requesting state stands and the timer carries it (plan B5).
            vc.State = X25VcState.ResetRequest;
            vc.Cause = cause;
            vc.Diagnostic = diagnostic;
            vc.Retries = 0;
            vc.TimerExpiryMs = Now() + _config.ResetRequestTimeoutMs;
            return Send(X25PacketBuilder.CreateResetRequest(lci, cause, diagnostic));
        }

        /// <summary>
        /// Signals or clears our local receive-not-ready condition to the peer.
        /// </summary>
        /// <param name="lci">
        /// Logical channel.
        /// </param>
        /// <param name="busy">
        /// True to signal receive-not-ready, false to signal receive-ready.
        /// </param>
        public bool SetBusy(ushort lci, bool busy)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                return false;
            }

            // The flag follows the packet: a busy condition the peer was never told about is not a
            // busy condition, it is a stall the peer cannot explain.
            if (!Send(busy
                ? X25PacketBuilder.CreateRNR(lci, vc.RecvSeq)
                : X25PacketBuilder.CreateRR(lci, vc.RecvSeq)))
            {
                return false;
            }

            vc.LocalBusy = busy;
            return true;
        }

        /// <summary>
        /// Restarts the link: frees all switched circuits and notifies the peer.
        /// </summary>
        /// <param name="cause">
        /// Restart cause code.
        /// </param>
        /// <param name="diagnostic">
        /// Diagnostic code.
        /// </param>
        public bool Restart(byte cause = 0, byte diagnostic = 0)
        {
            cause = ApplyCauseRule(cause);
            // The local circuits are freed whether or not the packet leaves: a restart is the
            // application saying "forget everything", and that must hold locally. Telling the peer
            // is what the T20 retransmission will guarantee (plan B5); until then a refused Restart
            // Request is counted and reported.
            FreeSwitchedCircuits(cause, diagnostic);
            _restartOutstanding = true;
            _restartRetries = 0;
            _restartCause = cause;
            _restartDiagnostic = diagnostic;
            _restartExpiryMs = Now() + _config.RestartTimeoutMs;
            return Send(X25PacketBuilder.CreateRestartRequest(cause, diagnostic));
        }

        /// <summary>
        /// Feeds a received X.25 packet up from the link, advancing the state machine and raising events.
        /// </summary>
        /// <param name="packet">
        /// The complete received packet bytes.
        /// </param>
        public void ProcessPacket(ReadOnlySpan<byte> packet)
        {
            // Annex C Table C.1: a packet shorter than the header is DIAG # 38. (The Diagnostic
            // packet needs three octets of the offender to quote; here there are not three.)
            if (packet.Length < 3)
            {
                SendDiagnostic(X25Diagnostic.PacketTooShort, packet);
                return;
            }

            byte[] pkt = packet.ToArray();
            X25PacketInfo info = X25PacketParser.ParsePacket(pkt);
            ushort lci = info.LCN;

            if (!Admit(info, lci, pkt))
            {
                return;
            }

            switch (info.PacketType)
            {
                case X25PacketType.CallRequest:
                    OnCallRequest(lci, info);
                    break;
                case X25PacketType.CallAccepted:
                    OnCallAccepted(lci, info);
                    break;
                case X25PacketType.Data:
                    OnData(lci, info, pkt);
                    break;
                case X25PacketType.ClearRequest:
                    OnClearRequest(lci, info);
                    break;
                case X25PacketType.ClearConfirmation:
                    OnClearConfirmation(lci);
                    break;
                case X25PacketType.ResetRequest:
                    OnResetRequest(lci, info);
                    break;
                case X25PacketType.ResetConfirmation:
                    OnResetConfirmation(lci);
                    break;
                case X25PacketType.RestartRequest:
                    OnRestartRequest(info);
                    break;
                case X25PacketType.RestartConfirmation:
                    OnRestartConfirmation();
                    break;
                case X25PacketType.RR:
                    OnFlowControl(lci, info.PR, peerBusy: false);
                    break;
                case X25PacketType.RNR:
                    OnFlowControl(lci, info.PR, peerBusy: true);
                    break;
                case X25PacketType.REJ:
                    OnReject(lci, info.PR);
                    break;
                case X25PacketType.Diagnostic:
                    DiagnosticsReceived++;
                    RaiseDiagnosticReceived(info.DiagnosticCode, info.DiagnosticInfo);
                    break;
            }
        }

        // ------------------------------------------------------------------ Annex C: what may arrive when
        //
        // Until 2026-09-06 a packet that made no sense where it arrived - data on a channel with no
        // call, a Call Connected in data transfer, anything at all on channel 0 - was silently
        // dropped, and the peer, which had done something wrong, never heard about it. X.25 Annex C
        // says exactly what each case draws: DIAG # x (discard; a DCE also sends a Diagnostic packet
        // with code x, 5.6), DISCARD, or ERROR # x (discard and clear or reset the channel with
        // "local procedure error" and diagnostic x). The state codes are Annex E 20-29: "packet
        // type invalid for state" p1..p7, d1..d3.

        /// <summary>
        /// Applies Annex C Tables C.1-C.4 to a received packet before it is dispatched.
        /// </summary>
        /// <returns>
        /// True when the packet may be processed; false when it has been discarded, with whatever
        /// Diagnostic, Clear or Reset the tables demand already sent.
        /// </returns>
        private bool Admit(X25PacketInfo info, ushort lci, byte[] pkt)
        {
            X25PacketType type = info.PacketType;

            // Table C.1: an invalid GFI. Modulo 128 is not supported by this engine yet (plan R18).
            if (info.Modulo128 || (pkt[0] & 0x30) == 0)
            {
                SendDiagnostic(X25Diagnostic.InvalidGfi, pkt);
                return false;
            }

            // Table C.2: channel 0 carries restart and diagnostic packets and nothing else.
            if (lci == 0)
            {
                switch (type)
                {
                    case X25PacketType.RestartRequest:
                    case X25PacketType.RestartConfirmation:
                    case X25PacketType.Diagnostic:
                        return true;
                    case X25PacketType.Unknown:
                        SendDiagnostic(X25Diagnostic.PacketNotAllowed, pkt);
                        return false;
                    default:
                        SendDiagnostic(X25Diagnostic.UnassignedLogicalChannel, pkt);
                        return false;
                }
            }

            // Table C.2, last rows: a restart packet on a nonzero channel is ERROR # 41 on that
            // channel; an undefined type on an assigned channel is ERROR # 33.
            if (type == X25PacketType.RestartRequest || type == X25PacketType.RestartConfirmation)
            {
                ChannelError(lci, X25Diagnostic.RestartWithNonzeroChannel, pkt);
                return false;
            }

            // Table C.1: a channel outside every configured range is unassigned - DIAG # 36.
            if (!IsAssigned(lci))
            {
                SendDiagnostic(X25Diagnostic.UnassignedLogicalChannel, pkt);
                return false;
            }

            if (type == X25PacketType.Unknown || type == X25PacketType.Diagnostic
                || type == X25PacketType.RegistrationRequest || type == X25PacketType.RegistrationConfirmation)
            {
                ChannelError(lci, X25Diagnostic.PacketNotAllowed, pkt);
                return false;
            }

            X25VcState state = _vcs.TryGetValue(lci, out X25Vc? vc) ? vc.State : X25VcState.Ready;

            // Tables C.3 and C.4, one row per packet family. NORMAL and collision rows let the packet
            // through to its handler; DISCARD drops it; anything else is ERROR with the state's code.
            switch (type)
            {
                case X25PacketType.CallRequest:
                    // NORMAL in p1; collision in p2 (handled); p6/p7 DISCARD; else ERROR.
                    if (state == X25VcState.Ready || state == X25VcState.CallRequest) return true;
                    if (state == X25VcState.ClearRequest) return Discard();
                    return Error(lci, state, pkt);

                case X25PacketType.CallAccepted:
                    if (state == X25VcState.CallRequest) return true;
                    if (state == X25VcState.ClearRequest) return Discard();
                    return Error(lci, state, pkt);

                case X25PacketType.ClearRequest:
                    // NORMAL in every state (Table C.3: clear request row).
                    return true;

                case X25PacketType.ClearConfirmation:
                    if (state == X25VcState.ClearRequest) return true;
                    return Error(lci, state, pkt);

                case X25PacketType.ResetConfirmation:
                    if (state == X25VcState.ResetRequest) return true;
                    if (state == X25VcState.DataTransfer || state == X25VcState.PvcReady)
                    {
                        // Table C.4, d1: a reset confirmation is ERROR (d3) # 27 - a reset, not a clear.
                        ResetForProcedureError(vc!, X25Diagnostic.PacketTypeInvalidForD1);
                        return false;
                    }

                    if (state == X25VcState.ClearRequest) return Discard();
                    return Error(lci, state, pkt);

                default:
                    // Data, interrupt, reset request, RR, RNR, REJ: the data-transfer family.
                    // Table C.3: NORMAL in p4 (Table C.4 then decides by d-state), DISCARD in p6/p7,
                    // ERROR elsewhere. Table C.4: in d2 (reset request) data and flow control are
                    // DISCARD, and reset request is the collision, which the handler completes.
                    if (state == X25VcState.DataTransfer || state == X25VcState.PvcReady) return true;
                    if (state == X25VcState.ResetRequest) return type == X25PacketType.ResetRequest || Discard();
                    if (state == X25VcState.ClearRequest) return Discard();
                    return Error(lci, state, pkt);
            }
        }

        private bool Discard()
        {
            UnexpectedPackets++;
            return false;
        }

        /// <summary>
        /// Table C.3 ERROR (p7) # x: the channel is cleared with "local procedure error" and the
        /// diagnostic that names the state the packet was invalid for.
        /// </summary>
        private bool Error(ushort lci, X25VcState state, byte[] pkt)
        {
            ChannelError(lci, StateDiagnostic(state), pkt);
            return false;
        }

        private void ChannelError(ushort lci, byte diagnostic, byte[] pkt)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                if (!IsAssigned(lci))
                {
                    SendDiagnostic(diagnostic, pkt);
                    return;
                }

                vc = new X25Vc(lci, X25VcKind.Svc);
                _vcs[lci] = vc;
            }

            ProcedureErrors++;
            bool wasLive = vc.State != X25VcState.Ready;
            BeginClear(vc, ProcedureErrorCause(), diagnostic, Now());
            if (wasLive)
            {
                ReportCleared(vc, vc.Cause, vc.Diagnostic);
            }
            else
            {
                vc.ClearReported = true;
            }
        }

        /// <summary>
        /// Annex E 20-29: "packet type invalid for state" p1..p7 and d1..d3, from this engine's
        /// circuit states.
        /// </summary>
        private static byte StateDiagnostic(X25VcState state)
        {
            switch (state)
            {
                case X25VcState.Ready: return 20;
                case X25VcState.CallRequest: return 21;
                case X25VcState.CallIncoming: return 22;
                case X25VcState.DataTransfer: return 23;
                case X25VcState.PvcReady: return 27;
                case X25VcState.ClearRequest: return 25;
                case X25VcState.ResetRequest: return 28;
                default: return X25Diagnostic.PacketTypeInvalid;
            }
        }

        /// <summary>
        /// DIAG # x: the packet is discarded; a DCE also sends a Diagnostic packet (X.25 5.6, "the
        /// diagnostic packet is transmitted by the DCE") quoting the first three octets of the
        /// offender (5.6.2). A DTE has no such packet to send and only counts.
        /// </summary>
        private void SendDiagnostic(byte diagnostic, ReadOnlySpan<byte> offender)
        {
            UnexpectedPackets++;
            if (_config.IsDte)
            {
                return;
            }

            int n = offender.Length < 3 ? offender.Length : 3;
            byte[] explanation = offender.Slice(0, n).ToArray();
            DiagnosticsSent++;
            Send(X25PacketBuilder.CreateDiagnostic(diagnostic, explanation));
        }

        /// <summary>
        /// True when the channel lies in the configured virtual call range or is a known PVC.
        /// </summary>
        private bool IsAssigned(ushort lci)
        {
            int first = _config.LciBase == 0 ? 1 : _config.LciBase;
            int last = _config.LciBase + _config.LciCount - 1;
            if (lci >= first && lci <= last)
            {
                return true;
            }

            return _vcs.TryGetValue(lci, out X25Vc? vc) && vc.Kind == X25VcKind.Pvc;
        }

        /// <summary>
        /// Sweeps per-circuit timers, forcing overdue call/clear/reset requests to complete.
        /// </summary>
        /// <param name="nowMs">
        /// The current absolute time in milliseconds.
        /// </param>
        public void Tick(long nowMs)
        {
            TickRestart(nowMs);

            ushort[] keys = SnapshotKeys();
            for (int i = 0; i < keys.Length; i++)
            {
                if (!_vcs.TryGetValue(keys[i], out X25Vc? vc))
                {
                    continue;
                }

                if (vc.TimerExpiryMs == 0 || nowMs < vc.TimerExpiryMs)
                {
                    continue;
                }

                vc.TimerExpiryMs = 0;
                switch (vc.State)
                {
                    case X25VcState.CallRequest:
                        OnT21Expired(vc, nowMs);
                        break;
                    case X25VcState.ResetRequest:
                        OnT22Expired(vc, nowMs);
                        break;
                    case X25VcState.ClearRequest:
                        OnT23Expired(vc, nowMs);
                        break;
                }
            }
        }

        // ------------------------------------------------------------------ time-limits (X.25 Table D.2)
        //
        // Until 2026-09-06 every expiry silently flipped the state to what the peer would have
        // produced had it answered: T21 went to Ready with no Clear Request (the peer kept the
        // channel in p3), T22 went back to data transfer as if confirmed, T23 gave up after one
        // attempt, and there was no T20 at all. Table D.2 says what each expiry is for.

        /// <summary>
        /// T20 (r2): "To retransmit the restart request". After the retries: note 1, "recovery
        /// decisions should be taken at higher layers" - the owner is told through RestartTimedOut
        /// and every call held for the restart is reported cleared.
        /// </summary>
        private void TickRestart(long nowMs)
        {
            if (!_restartOutstanding || nowMs < _restartExpiryMs)
            {
                return;
            }

            if (_restartRetries < _config.MaxRetransmissions)
            {
                _restartRetries++;
                _restartExpiryMs = nowMs + _config.RestartTimeoutMs;
                Send(X25PacketBuilder.CreateRestartRequest(_restartCause, _restartDiagnostic));
                return;
            }

            _restartOutstanding = false;
            RestartTimeouts++;
            FailHeldCalls(X25Diagnostic.TimeExpiredRestart);
            RaiseRestartTimedOut();
        }

        /// <summary>
        /// T21 (p2): "To transmit a clear request". The application hears now that its call failed
        /// (diagnostic 49, time expired); the channel itself is busy clearing until the peer confirms
        /// or T23 gives up, and is not offered to a new call before that.
        /// </summary>
        private void OnT21Expired(X25Vc vc, long nowMs)
        {
            CallTimeouts++;
            BeginClear(vc, ProcedureErrorCause(), X25Diagnostic.TimeExpiredCall, nowMs);
            ReportCleared(vc, vc.Cause, vc.Diagnostic);
        }

        /// <summary>
        /// T22 (d2): "For virtual calls, to retransmit the reset request or to transmit a clear
        /// request. For permanent virtual circuits, to retransmit the reset request." After the
        /// retries a virtual call is cleared (diagnostic 51); a permanent virtual circuit is marked
        /// out of order (note 2) and keeps asking, because a reset or a restart is its only way back.
        /// </summary>
        private void OnT22Expired(X25Vc vc, long nowMs)
        {
            if (vc.Retries < _config.MaxRetransmissions)
            {
                vc.Retries++;
                vc.TimerExpiryMs = nowMs + _config.ResetRequestTimeoutMs;
                Send(X25PacketBuilder.CreateResetRequest(vc.Lci, vc.Cause, vc.Diagnostic));
                return;
            }

            ResetTimeouts++;
            if (vc.Kind == X25VcKind.Svc)
            {
                BeginClear(vc, ProcedureErrorCause(), X25Diagnostic.TimeExpiredReset, nowMs);
                ReportCleared(vc, vc.Cause, vc.Diagnostic);
                return;
            }

            if (!vc.OutOfOrder)
            {
                vc.OutOfOrder = true;
                RaiseReset(vc.Lci, vc.Cause, X25Diagnostic.TimeExpiredReset);
            }

            vc.Retries = 0;
            vc.TimerExpiryMs = nowMs + _config.ResetRequestTimeoutMs;
            Send(X25PacketBuilder.CreateResetRequest(vc.Lci, vc.Cause, vc.Diagnostic));
        }

        /// <summary>
        /// T23 (p6): "To retransmit the clear request". After the retries the channel is taken back
        /// locally (note 2: considered out of order) and the application, if it has not heard yet,
        /// is told with diagnostic 50.
        /// </summary>
        private void OnT23Expired(X25Vc vc, long nowMs)
        {
            if (vc.Retries < _config.MaxRetransmissions)
            {
                vc.Retries++;
                vc.TimerExpiryMs = nowMs + _config.ClearRequestTimeoutMs;
                Send(X25PacketBuilder.CreateClearRequest(vc.Lci, vc.Cause, vc.Diagnostic));
                return;
            }

            ClearTimeouts++;
            byte cause = vc.Cause;
            ToReady(vc);
            ReportCleared(vc, cause, X25Diagnostic.TimeExpiredClear);
        }

        /// <summary>
        /// Puts a circuit into p6 with a Clear Request on the wire and T23 running.
        /// </summary>
        private void BeginClear(X25Vc vc, byte cause, byte diagnostic, long nowMs)
        {
            vc.State = X25VcState.ClearRequest;
            vc.Cause = cause;
            vc.Diagnostic = diagnostic;
            vc.Retries = 0;
            vc.CallRequestHeld = false;
            vc.TimerExpiryMs = nowMs + _config.ClearRequestTimeoutMs;
            Send(X25PacketBuilder.CreateClearRequest(vc.Lci, cause, diagnostic));
        }

        /// <summary>
        /// Returns a virtual call's channel to p1 with nothing left over from the call: addresses,
        /// user data, facility flags, timers, retries, sequence numbers and busy flags are all reset,
        /// so the next call on the channel starts clean (the C emulator carried a previous call's
        /// facilities and user data into the next one, audit F-01-06 / F-02-09).
        /// </summary>
        private static void ToReady(X25Vc vc)
        {
            vc.State = X25VcState.Ready;
            vc.TimerExpiryMs = 0;
            vc.Retries = 0;
            vc.CallRequestHeld = false;
            vc.OutOfOrder = false;
            vc.PeerSentFacilities = false;
            vc.CalledAddress = string.Empty;
            vc.CallingAddress = string.Empty;
            vc.CallUserData = Array.Empty<byte>();
            vc.ResetFlow();
        }

        /// <summary>
        /// Tells the application that a call is over - once per call, however many steps the ending
        /// took.
        /// </summary>
        private void ReportCleared(X25Vc vc, byte cause, byte diagnostic)
        {
            if (vc.ClearReported)
            {
                return;
            }

            vc.ClearReported = true;
            RaiseCleared(vc.Lci, cause, diagnostic);
        }

        /// <summary>
        /// X.25 Tables 5-7, 5-8 and 5-9, note a: a DTE's clearing, resetting or restarting cause is
        /// 00000000 ("DTE originated"), or any value with bit 8 set when the network allows it. The
        /// other codes (out of order, local procedure error, ...) are the DCE's vocabulary; a DTE that
        /// sends them is lying about who it is. An application's DCE-style cause is therefore turned
        /// into 0 here; the diagnostic still carries the reason. A DCE sends what it is given.
        /// </summary>
        private byte ApplyCauseRule(byte cause)
        {
            if (_config.IsDte && cause != 0 && (cause & 0x80) == 0)
            {
                return X25Cause.DteOriginated;
            }

            return cause;
        }

        /// <summary>
        /// The cause this engine puts in a Clear or Reset it originates for a procedure error or a
        /// time-out: "Local procedure error" from a DCE; from a DTE only 0 is allowed (X.25 Table
        /// 5-7 / 5-8 note a) and the diagnostic carries the reason.
        /// </summary>
        private byte ProcedureErrorCause()
        {
            return _config.IsDte ? X25Cause.DteOriginated : X25Cause.LocalProcedureError;
        }

        /// <summary>
        /// Sends the Call Request for a circuit in p2 and arms T21.
        /// </summary>
        private bool SendCallRequest(X25Vc vc)
        {
            vc.CallRequestHeld = false;
            vc.ClearReported = false;
            byte[]? cud = vc.CallUserData.Length == 0 ? null : vc.CallUserData;

            // The flow control request goes in every Call Request. RFC 1613 6.1 makes it a MUST over
            // XOT; over plain X.25 it is how the peer learns what we want instead of silently applying
            // its own defaults (7.2.2.1). Until 2026-09-06 the facility length octet was always zero.
            byte[] facilities = X25Facilities.ForRequest(_config.DefaultWindowSize, _config.DefaultPacketSize).Encode();
            if (!Send(X25PacketBuilder.CreateCallRequest(vc.Lci, vc.CalledAddress, vc.CallingAddress, facilities, cud)))
            {
                return false;
            }

            vc.TimerExpiryMs = Now() + _config.CallRequestTimeoutMs;
            return true;
        }

        /// <summary>
        /// Sends every Call Request that was held for an outstanding restart (X.25 4.1.1).
        /// </summary>
        private void SendHeldCalls()
        {
            ushort[] keys = SnapshotKeys();
            for (int i = 0; i < keys.Length; i++)
            {
                if (_vcs.TryGetValue(keys[i], out X25Vc? vc) && vc.CallRequestHeld && vc.State == X25VcState.CallRequest)
                {
                    if (!SendCallRequest(vc))
                    {
                        ToReady(vc);
                        ReportCleared(vc, ProcedureErrorCause(), X25Diagnostic.None);
                    }
                }
            }
        }

        /// <summary>
        /// Reports every held Call Request cleared because the restart it waited for failed.
        /// </summary>
        private void FailHeldCalls(byte diagnostic)
        {
            ushort[] keys = SnapshotKeys();
            for (int i = 0; i < keys.Length; i++)
            {
                if (_vcs.TryGetValue(keys[i], out X25Vc? vc) && vc.CallRequestHeld)
                {
                    ToReady(vc);
                    ReportCleared(vc, ProcedureErrorCause(), diagnostic);
                }
            }
        }

        private void OnCallRequest(ushort lci, X25PacketInfo info)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                vc = new X25Vc(lci, X25VcKind.Svc);
                _vcs[lci] = vc;
            }

            // Call collision, X.25 4.1.6: "The DCE will proceed with the call request and cancel the
            // incoming call." So when this engine is the DTE and its own Call Request is out on this
            // channel, the incoming call is the one that dies: drop it and wait for Call Connected.
            // When this engine is the DCE, the peer's Call Request wins: our outgoing call (which was
            // an Incoming Call toward the peer) is cancelled and its application told, and the
            // peer's call is offered as usual. Until 2026-09-06 the incoming call simply overwrote
            // whatever was on the channel, and the application's own call vanished without a word.
            if (vc.State == X25VcState.CallRequest)
            {
                if (_config.IsDte)
                {
                    UnexpectedPackets++;
                    return;
                }

                ToReady(vc);
                ReportCleared(vc, X25Cause.LocalProcedureError, X25Diagnostic.CallCollision);
            }

            vc.State = X25VcState.CallIncoming;
            vc.ClearReported = false;
            vc.Retries = 0;
            vc.CalledAddress = info.CalledAddress;
            vc.CallingAddress = info.CallingAddress;
            vc.WindowSizeOut = _config.DefaultWindowSize;
            vc.WindowSizeIn = _config.DefaultWindowSize;
            vc.PacketSizeOut = _config.DefaultPacketSize;
            vc.PacketSizeIn = _config.DefaultPacketSize;
            vc.CallUserData = info.CallUserData;

            // A Call Request whose lengths do not fit its body, or whose facility field breaks clause
            // 7, is cleared and never offered to the application (Annex E 69 / 66). Until 2026-09-06
            // the leftover octets became call user data and the call was offered as normal.
            if (info.ParseError != null)
            {
                RejectCall(vc, X25Diagnostic.InvalidFacilityLength);
                return;
            }

            X25FacilityError facilityError = X25Facilities.TryDecode(info.Facilities, out X25Facilities requested);
            if (facilityError != X25FacilityError.None)
            {
                RejectCall(vc, FacilityDiagnostic(facilityError));
                return;
            }

            // We are the called DTE: "from called" is what we may send, "from calling" what we receive.
            // The request is accepted as asked (the codec has range-checked it).
            vc.PeerSentFacilities = requested.HasPacketSize || requested.HasWindowSize;
            vc.PacketSizeOut = requested.PacketSizeFromCalled;
            vc.PacketSizeIn = requested.PacketSizeFromCalling;
            vc.WindowSizeOut = requested.WindowFromCalled;
            vc.WindowSizeIn = requested.WindowFromCalling;

            RaiseIncomingCall(lci, info.CalledAddress, info.CallingAddress, info.CallUserData);
        }

        private void OnCallAccepted(ushort lci, X25PacketInfo info)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc) || vc.State != X25VcState.CallRequest)
            {
                return;
            }

            // X.25 7.2.2.1 / 7.3.1: the Call Connected carries the values the call will run with.
            // Until 2026-09-06 the body was never looked at and the circuit kept its own request, so
            // a caller that asked for window 7 and was granted 2 sent seven packets into a window of
            // two - a P(S) outside the window at the peer, which 4.4.1.3 makes a procedure error.
            if (info.ParseError != null)
            {
                FailCall(vc, X25Diagnostic.InvalidFacilityLength);
                return;
            }

            X25FacilityError facilityError = X25Facilities.TryDecode(info.Facilities, out X25Facilities agreed);
            if (facilityError != X25FacilityError.None)
            {
                FailCall(vc, FacilityDiagnostic(facilityError));
                return;
            }

            // 6.12 Table 6-2 (flow control parameter negotiation): with W(requested) >= 2 the Call
            // Connected may indicate 2..W(requested); with W(requested) = 1 it may indicate 1 or 2;
            // P likewise between 128 and the request. That is "between the default and the request,
            // inclusive". Anything outside is a facility parameter not allowed. We are the calling
            // DTE: "from calling" is our sending direction.
            if (agreed.HasWindowSize
                && (!Between(agreed.WindowFromCalling, X25Facilities.DefaultWindowSize, vc.WindowSizeOut)
                    || !Between(agreed.WindowFromCalled, X25Facilities.DefaultWindowSize, vc.WindowSizeIn)))
            {
                FailCall(vc, X25Diagnostic.FacilityParameterNotAllowed);
                return;
            }

            if (agreed.HasPacketSize
                && (!Between(agreed.PacketSizeFromCalling, X25Facilities.DefaultPacketSize, vc.PacketSizeOut)
                    || !Between(agreed.PacketSizeFromCalled, X25Facilities.DefaultPacketSize, vc.PacketSizeIn)))
            {
                FailCall(vc, X25Diagnostic.FacilityParameterNotAllowed);
                return;
            }

            // A Call Connected with no facility says "the defaults apply" (7.2.2.1: a value not
            // indicated is the default), whatever we asked for.
            vc.WindowSizeOut = agreed.WindowFromCalling;
            vc.WindowSizeIn = agreed.WindowFromCalled;
            vc.PacketSizeOut = agreed.PacketSizeFromCalling;
            vc.PacketSizeIn = agreed.PacketSizeFromCalled;
            vc.CallUserData = info.CallUserData;

            vc.State = X25VcState.DataTransfer;
            vc.ResetFlow();
            vc.TimerExpiryMs = 0;
            RaiseCallConnected(lci);
        }

        private static bool Between(int value, int a, int b)
        {
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            return value >= lo && value <= hi;
        }

        /// <summary>
        /// Maps a facility decode error to the Annex E diagnostic that names it.
        /// </summary>
        private static byte FacilityDiagnostic(X25FacilityError error)
        {
            switch (error)
            {
                case X25FacilityError.Truncated: return X25Diagnostic.InvalidFacilityLength;
                case X25FacilityError.RepeatedCode: return X25Diagnostic.DuplicateFacility;
                default: return X25Diagnostic.FacilityParameterNotAllowed;
            }
        }

        /// <summary>
        /// Clears an incoming call that was never offered to the application: Clear Request with the
        /// invalid-facility cause (from a DCE) or 0 (from a DTE), T23 running, and no Cleared event
        /// because nobody above ever heard of the call.
        /// </summary>
        private void RejectCall(X25Vc vc, byte diagnostic)
        {
            byte cause = _config.IsDte ? X25Cause.DteOriginated : X25Cause.InvalidFacilityRequest;
            BeginClear(vc, cause, diagnostic, Now());
            vc.ClearReported = true;
            ProcedureErrors++;
        }

        /// <summary>
        /// Clears an outgoing call whose Call Connected was unacceptable and tells the caller.
        /// </summary>
        private void FailCall(X25Vc vc, byte diagnostic)
        {
            byte cause = _config.IsDte ? X25Cause.DteOriginated : X25Cause.InvalidFacilityRequest;
            BeginClear(vc, cause, diagnostic, Now());
            ProcedureErrors++;
            ReportCleared(vc, cause, diagnostic);
        }

        private void OnData(ushort lci, X25PacketInfo info, byte[] pkt)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                return;
            }

            if (vc.State != X25VcState.DataTransfer && vc.State != X25VcState.PvcReady)
            {
                return;
            }

            // The piggybacked P(R) must acknowledge something we actually sent (R2); a P(R) outside
            // [last acknowledged .. next to send] is a local procedure error, diagnostic 2.
            if (!TryAcknowledge(vc, info.PR))
            {
                ResetForProcedureError(vc, X25Diagnostic.InvalidPR);
                return;
            }

            // X.25 4.4.1.3: "A received data packet containing a P(S) that is out of sequence [i.e.
            // there is a duplicate or a gap in the P(S) numbering] ... is considered by the DCE as a
            // local procedure error. The DCE will reset the virtual call ... The DTE should follow
            // the same procedure." Diagnostic 1, "Invalid P(S)" (Annex E). Until 2026-09-06 this
            // engine took whatever P(S) arrived and set RecvSeq past it - a lost packet went unnoticed
            // and a duplicate was delivered twice.
            if (info.PS != vc.RecvSeq)
            {
                ResetForProcedureError(vc, X25Diagnostic.InvalidPS);
                return;
            }

            int header = info.Modulo128 ? 4 : 3;
            int payloadLen = pkt.Length > header ? pkt.Length - header : 0;

            // Annex E 39 "Packet too long": a data field longer than the negotiated maximum is a
            // local procedure error. Checked before P(S) moves, so the offending packet is not
            // acknowledged.
            if (payloadLen > vc.PacketSizeIn)
            {
                ResetForProcedureError(vc, X25Diagnostic.PacketTooLong);
                return;
            }

            vc.RecvSeq = (byte)((info.PS + 1) & 0x07);
            byte[] payload = new byte[payloadLen];
            if (payloadLen > 0)
            {
                Array.Copy(pkt, header, payload, 0, payloadLen);
            }

            RaiseDataReceived(lci, payload, payloadLen, info.QBit, info.MBit, info.DBit);

            // Acknowledge the received data (receive-ready, unless we are locally busy).
            // A refused acknowledgement is counted, not rolled back: the data has been delivered and
            // P(R) has moved; the next packet in this direction carries the newer P(R).
            Send(vc.LocalBusy
                ? X25PacketBuilder.CreateRNR(lci, vc.RecvSeq)
                : X25PacketBuilder.CreateRR(lci, vc.RecvSeq));
        }

        private void OnClearRequest(ushort lci, X25PacketInfo info)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                return;
            }

            // Clear collision, X.25 4.1.9: both sides cleared at once; "the DCE will consider that
            // the clearing is completed. The DCE will not expect a DTE clear confirmation packet and
            // will not transfer a DCE clear confirmation packet." The channel is free; the
            // application asked for this clear and is told it is complete, once. Until 2026-09-06 a
            // Clear Confirmation the peer did not expect went out and the application was told its
            // own call had been cleared as though by the peer.
            if (vc.State == X25VcState.ClearRequest)
            {
                ToReady(vc);
                ReportCleared(vc, vc.Cause, vc.Diagnostic);
                return;
            }

            Send(X25PacketBuilder.CreateClearConfirmation(lci));
            ToReady(vc);
            vc.CallRequestHeld = false;
            ReportCleared(vc, info.ClearingCause, info.DiagnosticCode);
        }

        private void OnClearConfirmation(ushort lci)
        {
            if (_vcs.TryGetValue(lci, out X25Vc? vc) && vc.State == X25VcState.ClearRequest)
            {
                ToReady(vc);

                // Cleared means "this channel is now free" whichever side started the clearing; an
                // application that asked to clear learns here that the peer has confirmed it.
                ReportCleared(vc, vc.Cause, vc.Diagnostic);
            }
        }

        private void OnResetRequest(ushort lci, X25PacketInfo info)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                return;
            }

            // Reset collision, X.25 4.4.3.3: "the DCE will consider that the reset is completed.
            // The DCE will not expect a DTE reset confirmation packet and will not transfer a DCE
            // reset confirmation packet." Both sides go straight to d1. Until 2026-09-06 the
            // colliding Reset was dropped and the circuit sat in d2 until T22 expired - 180 s of
            // dead circuit, the C emulator's F-02-04 exactly.
            if (vc.State == X25VcState.ResetRequest)
            {
                vc.State = vc.Kind == X25VcKind.Pvc ? X25VcState.PvcReady : X25VcState.DataTransfer;
                vc.TimerExpiryMs = 0;
                vc.ResetFlow();
                RaiseReset(lci, vc.Cause, vc.Diagnostic);
                return;
            }

            if (vc.State != X25VcState.DataTransfer && vc.State != X25VcState.PvcReady)
            {
                return;
            }

            Send(X25PacketBuilder.CreateResetConfirmation(lci));
            vc.ResetFlow();
            RaiseReset(lci, info.ResetCause, info.DiagnosticCode);
        }

        private void OnResetConfirmation(ushort lci)
        {
            if (_vcs.TryGetValue(lci, out X25Vc? vc) && vc.State == X25VcState.ResetRequest)
            {
                vc.State = vc.Kind == X25VcKind.Pvc ? X25VcState.PvcReady : X25VcState.DataTransfer;
                vc.TimerExpiryMs = 0;
                vc.OutOfOrder = false;
                vc.ResetFlow();

                // The application learns that the circuit was reset when the reset is complete - it
                // needs to know for a reset it asked for as much as for one the engine raised on a
                // procedure error, because in both cases everything in flight is gone (X.25 4.4.3.1).
                RaiseReset(lci, vc.Cause, vc.Diagnostic);
            }
        }

        private void OnRestartRequest(X25PacketInfo info)
        {
            // Restart collision, X.25 4.1.2: "the DCE will consider that the restart is completed.
            // The DCE will not expect a DTE restart confirmation packet and will not transfer a DCE
            // restart confirmation packet." Our own restart is thereby complete too.
            if (_restartOutstanding)
            {
                _restartOutstanding = false;
                FreeSwitchedCircuits(info.RestartCause, info.DiagnosticCode);
                RaiseRestartOccurred(info.RestartCause, info.DiagnosticCode);
                SendHeldCalls();
                return;
            }

            Send(X25PacketBuilder.CreateRestartConfirmation());
            FreeSwitchedCircuits(info.RestartCause, info.DiagnosticCode);
            RaiseRestartOccurred(info.RestartCause, info.DiagnosticCode);
        }

        private void OnRestartConfirmation()
        {
            if (!_restartOutstanding)
            {
                UnexpectedPackets++;
                return;
            }

            _restartOutstanding = false;
            SendHeldCalls();
        }

        /// <summary>
        /// True while our Restart Request has not been answered (X.25 state r2).
        /// </summary>
        public bool RestartOutstanding => _restartOutstanding;

        private void OnFlowControl(ushort lci, byte pr, bool peerBusy)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                return;
            }

            // RR and RNR mean something only in the flow-control states of data transfer (X.25
            // Annex B). Anywhere else they are dropped and counted here; the Table D-1 reaction
            // (a Diagnostic or a Clear with "packet type invalid for state pN") arrives with the
            // protocol-error work (plan B8, R17). Until 2026-09-06 an RNR in call-request state set
            // PeerBusy on a circuit that was not even connected.
            if (vc.State != X25VcState.DataTransfer && vc.State != X25VcState.PvcReady)
            {
                UnexpectedPackets++;
                return;
            }

            if (!TryAcknowledge(vc, pr))
            {
                ResetForProcedureError(vc, X25Diagnostic.InvalidPR);
                return;
            }

            vc.PeerBusy = peerBusy;
        }

        /// <summary>
        /// Handles a received REJ. This engine has no packet retransmission facility (X.25 6.x,
        /// "Packet retransmission" is an optional user facility), so a REJ is a local procedure
        /// error with diagnostic 21, "Reject not subscribed to" (Annex E). Decided 2026-09-06 (audit
        /// decision D9, option A): reset, never send REJ ourselves.
        /// </summary>
        private void OnReject(ushort lci, byte pr)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                return;
            }

            if (vc.State != X25VcState.DataTransfer && vc.State != X25VcState.PvcReady)
            {
                UnexpectedPackets++;
                return;
            }

            ResetForProcedureError(vc, X25Diagnostic.RejectNotSubscribed);
        }

        /// <summary>
        /// Applies a received P(R) if it lies in the window, else reports it invalid.
        /// </summary>
        /// <remarks>
        /// A valid P(R) acknowledges packets we have sent and not yet had acknowledged: it lies in
        /// [AckedSeq .. SendSeq] inclusive (modulo 8). Anything else acknowledges a packet that was
        /// never sent, and X.25 treats that as a local procedure error with diagnostic 2 ("Invalid
        /// P(R)", Annex E; the range wording is in the packet retransmission facility clause, which
        /// uses the same window arithmetic). Until 2026-09-06 any P(R) was stored unchecked, and one
        /// outside the window made (SendSeq - AckedSeq) look like a full window for ever: a healthy
        /// circuit that could never send again.
        /// </remarks>
        private static bool TryAcknowledge(X25Vc vc, byte pr)
        {
            int outstanding = (vc.SendSeq - vc.AckedSeq) & 0x07;
            int advance = (pr - vc.AckedSeq) & 0x07;
            if (advance > outstanding)
            {
                return false;
            }

            vc.AckedSeq = pr;
            return true;
        }

        /// <summary>
        /// Starts the reset procedure on a circuit because the peer broke a rule.
        /// </summary>
        /// <param name="vc">
        /// The circuit.
        /// </param>
        /// <param name="diagnostic">
        /// The Annex E diagnostic that says which rule.
        /// </param>
        /// <remarks>
        /// The resetting cause is "Local procedure error" (00000101, Table 5-8) when this engine is
        /// the DCE. A DTE has no such cause available to it: Table 5-8 note a) allows a DTE only
        /// 00000000 ("DTE originated") or a value with bit 8 set, so a DTE sends 0 and lets the
        /// diagnostic carry the reason. The circuit enters d2 (reset request) and T22 is armed; the
        /// application is told when the reset completes.
        /// </remarks>
        private void ResetForProcedureError(X25Vc vc, byte diagnostic)
        {
            byte cause = _config.IsDte ? (byte)0x00 : X25Cause.LocalProcedureError;
            vc.State = X25VcState.ResetRequest;
            vc.Cause = cause;
            vc.Diagnostic = diagnostic;
            vc.Retries = 0;
            vc.TimerExpiryMs = Now() + _config.ResetRequestTimeoutMs;
            ProcedureErrors++;
            Send(X25PacketBuilder.CreateResetRequest(vc.Lci, cause, diagnostic));
        }

        /// <summary>
        /// Applies a restart to every circuit: X.25 4.1 - a restart clears every virtual call and
        /// resets every permanent virtual circuit. The application is told per circuit (a Cleared for
        /// each call that was up or being set up, a Reset for each PVC) and then once for the restart
        /// itself, so a holder of several calls does not have to walk the table to find out what
        /// happened. A Call Request held for our own outstanding restart is not a call yet and is
        /// left alone.
        /// </summary>
        private void FreeSwitchedCircuits(byte cause, byte diagnostic)
        {
            ushort[] keys = SnapshotKeys();
            for (int i = 0; i < keys.Length; i++)
            {
                if (!_vcs.TryGetValue(keys[i], out X25Vc? vc))
                {
                    continue;
                }

                if (vc.Kind == X25VcKind.Svc)
                {
                    if (vc.CallRequestHeld)
                    {
                        continue;
                    }

                    bool wasLive = vc.State != X25VcState.Ready;
                    ToReady(vc);
                    if (wasLive)
                    {
                        ReportCleared(vc, cause, diagnostic);
                    }
                }
                else
                {
                    vc.State = X25VcState.PvcReady;
                    vc.TimerExpiryMs = 0;
                    vc.OutOfOrder = false;
                    vc.ResetFlow();
                    RaiseReset(vc.Lci, cause, diagnostic);
                }
            }
        }

        /// <summary>
        /// Picks a free logical channel for an outgoing call.
        /// </summary>
        /// <remarks>
        /// X.25 Annex A, note 6: "In order to minimize the risk of call collision, the DTE search
        /// algorithm is suggested to start with the highest numbered" channel in the two-way range,
        /// while the DCE starts with the lowest. So a DTE walks down from the top of its range and a
        /// DCE walks up from the base, and a DTE and a DCE built from this same code only collide
        /// when the range is nearly full. Until 2026-09-06 both walked upward with a rotating
        /// cursor, so two stations calling each other at once collided on the first call (the C
        /// emulator's F-02-02 twin). Channel 0 is never used for a virtual call.
        /// </remarks>
        private ushort AllocateLci()
        {
            int first = _config.LciBase == 0 ? 1 : _config.LciBase;
            int last = _config.LciBase + _config.LciCount - 1;
            if (last < first)
            {
                return 0;
            }

            if (_config.IsDte)
            {
                for (int candidate = last; candidate >= first; candidate--)
                {
                    if (IsFree((ushort)candidate))
                    {
                        return (ushort)candidate;
                    }
                }
            }
            else
            {
                for (int candidate = first; candidate <= last; candidate++)
                {
                    if (IsFree((ushort)candidate))
                    {
                        return (ushort)candidate;
                    }
                }
            }

            return 0;
        }

        private bool IsFree(ushort lci)
        {
            return !_vcs.TryGetValue(lci, out X25Vc? vc) || vc.State == X25VcState.Ready;
        }

        private ushort[] SnapshotKeys()
        {
            ushort[] keys = new ushort[_vcs.Count];
            _vcs.Keys.CopyTo(keys, 0);
            return keys;
        }

        private static long Now()
        {
            return Environment.TickCount64;
        }
    }
}
