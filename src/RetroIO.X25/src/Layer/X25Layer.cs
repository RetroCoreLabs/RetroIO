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
        // True from our Restart Request until the peer confirms it or sends its own (X.25 4.1.2).
        private bool _restartOutstanding;

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
                TimerExpiryMs = Now() + _config.CallRequestTimeoutMs
            };
            _vcs[lci] = vc;

            if (!Send(X25PacketBuilder.CreateCallRequest(lci, calledAddress, calling, null, callUserData)))
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

            // Send first: if the Call Accepted does not leave, the call is still merely incoming and
            // the application may try again or clear it.
            if (!Send(X25PacketBuilder.CreateCallAccepted(lci)))
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

            // The state change stands even when the packet is refused: the application has decided
            // the call is over, and the clear-request timer is what carries that decision to the
            // peer if this attempt did not (retransmission arrives with the T23 work, plan B5).
            vc.State = X25VcState.ClearRequest;
            vc.Cause = cause;
            vc.Diagnostic = diagnostic;
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

            if (vc.State != X25VcState.DataTransfer && vc.State != X25VcState.PvcReady)
            {
                return false;
            }

            // As for ClearCall: the requesting state stands and the timer carries it (plan B5).
            vc.State = X25VcState.ResetRequest;
            vc.Cause = cause;
            vc.Diagnostic = diagnostic;
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
            // The local circuits are freed whether or not the packet leaves: a restart is the
            // application saying "forget everything", and that must hold locally. Telling the peer
            // is what the T20 retransmission will guarantee (plan B5); until then a refused Restart
            // Request is counted and reported.
            FreeSwitchedCircuits();
            _restartOutstanding = true;
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
            if (packet.Length < 3)
            {
                return;
            }

            byte[] pkt = packet.ToArray();
            X25PacketInfo info = X25PacketParser.ParsePacket(pkt);
            ushort lci = info.LCN;

            switch (info.PacketType)
            {
                case X25PacketType.CallRequest:
                    OnCallRequest(lci, info);
                    break;
                case X25PacketType.CallAccepted:
                    OnCallAccepted(lci);
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
                default:
                    // Supervisory/other packets need no state change in this layer.
                    break;
            }
        }

        /// <summary>
        /// Sweeps per-circuit timers, forcing overdue call/clear/reset requests to complete.
        /// </summary>
        /// <param name="nowMs">
        /// The current absolute time in milliseconds.
        /// </param>
        public void Tick(long nowMs)
        {
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
                if (vc.State == X25VcState.CallRequest)
                {
                    // Call not answered in time: clear it locally and notify the application.
                    vc.State = X25VcState.Ready;
                    RaiseCleared(vc.Lci, vc.Cause, vc.Diagnostic);
                }
                else if (vc.State == X25VcState.ClearRequest)
                {
                    vc.State = X25VcState.Ready;
                }
                else if (vc.State == X25VcState.ResetRequest)
                {
                    vc.State = X25VcState.DataTransfer;
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

                vc.State = X25VcState.Ready;
                vc.TimerExpiryMs = 0;
                RaiseCleared(lci, X25Cause.LocalProcedureError, X25Diagnostic.CallCollision);
            }

            vc.State = X25VcState.CallIncoming;
            vc.CalledAddress = info.CalledAddress;
            vc.CallingAddress = info.CallingAddress;
            vc.WindowSizeOut = _config.DefaultWindowSize;
            vc.WindowSizeIn = _config.DefaultWindowSize;
            RaiseIncomingCall(lci, info.CalledAddress, info.CallingAddress, Array.Empty<byte>());
        }

        private void OnCallAccepted(ushort lci)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc) || vc.State != X25VcState.CallRequest)
            {
                return;
            }

            vc.State = X25VcState.DataTransfer;
            vc.ResetFlow();
            vc.TimerExpiryMs = 0;
            RaiseCallConnected(lci);
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

            vc.RecvSeq = (byte)((info.PS + 1) & 0x07);

            int header = info.Modulo128 ? 4 : 3;
            int payloadLen = pkt.Length > header ? pkt.Length - header : 0;
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
                vc.State = X25VcState.Ready;
                vc.TimerExpiryMs = 0;
                RaiseCleared(lci, vc.Cause, vc.Diagnostic);
                return;
            }

            Send(X25PacketBuilder.CreateClearConfirmation(lci));
            vc.State = X25VcState.Ready;
            vc.TimerExpiryMs = 0;
            RaiseCleared(lci, info.ClearingCause, info.DiagnosticCode);
        }

        private void OnClearConfirmation(ushort lci)
        {
            if (_vcs.TryGetValue(lci, out X25Vc? vc) && vc.State == X25VcState.ClearRequest)
            {
                vc.State = X25VcState.Ready;
                vc.TimerExpiryMs = 0;

                // Cleared means "this channel is now free" whichever side started the clearing; an
                // application that asked to clear learns here that the peer has confirmed it.
                RaiseCleared(lci, vc.Cause, vc.Diagnostic);
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
                FreeSwitchedCircuits();
                RaiseRestartOccurred(info.RestartCause, info.DiagnosticCode);
                return;
            }

            Send(X25PacketBuilder.CreateRestartConfirmation());
            FreeSwitchedCircuits();
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
            vc.TimerExpiryMs = Now() + _config.ResetRequestTimeoutMs;
            ProcedureErrors++;
            Send(X25PacketBuilder.CreateResetRequest(vc.Lci, cause, diagnostic));
        }

        private void FreeSwitchedCircuits()
        {
            ushort[] keys = SnapshotKeys();
            for (int i = 0; i < keys.Length; i++)
            {
                if (_vcs.TryGetValue(keys[i], out X25Vc? vc) && vc.Kind == X25VcKind.Svc)
                {
                    vc.State = X25VcState.Ready;
                    vc.TimerExpiryMs = 0;
                    vc.ResetFlow();
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
