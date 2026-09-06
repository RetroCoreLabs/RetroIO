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
        private ushort _nextLci;

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
            _nextLci = config.LciBase == 0 ? (ushort)1 : config.LciBase;
        }

        /// <summary>
        /// The virtual circuits currently tracked, keyed by logical channel.
        /// </summary>
        public IReadOnlyDictionary<ushort, X25Vc> Circuits => _vcs;

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
                case X25PacketType.RR:
                    OnFlowControl(lci, info.PR, peerBusy: false);
                    break;
                case X25PacketType.RNR:
                    OnFlowControl(lci, info.PR, peerBusy: true);
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

            // The peer's P(R) acknowledges our sends; advance our receive sequence over the delivered data.
            vc.AckedSeq = info.PR;
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
            }
        }

        private void OnResetRequest(ushort lci, X25PacketInfo info)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc))
            {
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
                vc.State = X25VcState.DataTransfer;
                vc.TimerExpiryMs = 0;
                vc.ResetFlow();
            }
        }

        private void OnRestartRequest(X25PacketInfo info)
        {
            Send(X25PacketBuilder.CreateRestartConfirmation());
            FreeSwitchedCircuits();
            RaiseRestartOccurred(info.RestartCause, info.DiagnosticCode);
        }

        private void OnFlowControl(ushort lci, byte pr, bool peerBusy)
        {
            if (_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                vc.AckedSeq = pr;
                vc.PeerBusy = peerBusy;
            }
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

        private ushort AllocateLci()
        {
            ushort limit = (ushort)(_config.LciBase + _config.LciCount);
            for (int guard = 0; guard < _config.LciCount; guard++)
            {
                ushort candidate = _nextLci;
                _nextLci++;
                if (_nextLci >= limit || _nextLci == 0)
                {
                    _nextLci = _config.LciBase == 0 ? (ushort)1 : _config.LciBase;
                }

                if (!_vcs.TryGetValue(candidate, out X25Vc? vc) || vc.State == X25VcState.Ready)
                {
                    return candidate;
                }
            }

            return 0;
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
