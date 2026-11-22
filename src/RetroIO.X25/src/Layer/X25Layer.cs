//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

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
        /// Asked, at the DCE end, for the facility field of the DCE Clear Confirmation on a channel whose DTE has just
        /// cleared; null or empty sends the basic confirmation.
        /// </summary>
        /// <remarks>
        /// X.25 5.2.5: "The extended format may be used for DCE clear confirmation packets only in conjunction with the
        /// charging information facility" - the charging answer to a DTE that cleared its own call (6.22). Only the
        /// switch knows what the call cost, so it is asked here, at the moment the confirmation is built, while its view
        /// of the call is still whole. Not asked at the DTE end: a DTE clear confirmation has no extended form.
        /// </remarks>
        public Func<ushort, byte[]?>? ClearConfirmationFacilities { get; set; }

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
        /// Calls that were never answered (T21 expired).
        /// </summary>
        public long CallTimeouts { get; private set; }

        /// <summary>
        /// Incoming calls that were neither accepted nor cleared in time and were cleared by this
        /// engine (T11 expired, X.25 4.1.4 and Table D.1).
        /// </summary>
        public long IncomingCallTimeouts { get; private set; }

        /// <summary>
        /// Reset Requests that were never confirmed through every retransmission (T22).
        /// </summary>
        public long ResetTimeouts { get; private set; }

        /// <summary>
        /// Clear Requests that were never confirmed through every retransmission (T23).
        /// </summary>
        public long ClearTimeouts { get; private set; }

        /// <summary>
        /// Restart Requests that were never confirmed through every retransmission (T20).
        /// </summary>
        public long RestartTimeouts { get; private set; }

        /// <summary>
        /// Diagnostic packets this engine has sent (a DCE only; X.25 5.6).
        /// </summary>
        public long DiagnosticsSent { get; private set; }

        /// <summary>
        /// Diagnostic packets received from the peer.
        /// </summary>
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
            return PlaceCall(
                calledAddress,
                callingAddress,
                callUserData,
                new X25CallParameters(_config.DefaultWindowSize, _config.DefaultWindowSize, _config.DefaultPacketSize, _config.DefaultPacketSize));
        }

        /// <summary>
        /// Places an outgoing call with this layer's default window and packet sizes and the given further facility
        /// elements (encoded as X.25 7.1 has them), and returns its logical channel, or 0 on failure.
        /// </summary>
        /// <param name="calledAddress">Destination X.121 address.</param>
        /// <param name="callingAddress">Origin X.121 address; the configured local address when null or empty.</param>
        /// <param name="callUserData">Optional call user data.</param>
        /// <param name="facilities">Facility elements other than window and packet size, byte for byte.</param>
        /// <returns>The allocated logical channel, or 0 on failure.</returns>
        public ushort PlaceCall(string calledAddress, string? callingAddress, byte[]? callUserData, byte[] facilities)
        {
            return PlaceCall(
                calledAddress,
                callingAddress,
                callUserData,
                new X25CallParameters(_config.DefaultWindowSize, _config.DefaultWindowSize, _config.DefaultPacketSize, _config.DefaultPacketSize, facilities));
        }

        /// <summary>
        /// Places an outgoing call that asks for the given window and packet sizes, and returns its logical channel, or 0
        /// if no channel is free.
        /// </summary>
        /// <remarks>
        /// A switch uses this to pass on what the calling side asked for instead of asking for its own defaults. The Call
        /// Connected that answers states what the call runs with, and <see cref="TryGetCallParameters"/> then returns it.
        /// </remarks>
        /// <param name="calledAddress">
        /// Destination X.121 address.
        /// </param>
        /// <param name="callingAddress">
        /// Origin X.121 address; the configured local address is used when null or empty.
        /// </param>
        /// <param name="callUserData">
        /// Optional call user data.
        /// </param>
        /// <param name="requested">
        /// The window and packet sizes to ask for, "out" being the direction this end sends in.
        /// </param>
        /// <returns>
        /// The allocated logical channel, or 0 on failure.
        /// </returns>
        public ushort PlaceCall(string calledAddress, string? callingAddress, byte[]? callUserData, X25CallParameters requested)
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
                WindowSizeOut = requested.WindowSizeOut,
                WindowSizeIn = requested.WindowSizeIn,
                PacketSizeOut = requested.PacketSizeOut,
                PacketSizeIn = requested.PacketSizeIn,
                OtherFacilities = requested.OtherFacilities,
                NetworkUtilities = requested.NetworkUtilities,
                ClearFacilities = System.Array.Empty<byte>(),
                ClearUtilities = System.Array.Empty<byte>(),
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
            return AcceptCall(lci, null);
        }

        /// <summary>
        /// Accepts an incoming call with the window and packet sizes the call was agreed to run with.
        /// </summary>
        /// <remarks>
        /// A switch uses this to give the caller the values the far end agreed to in its Call Connected (X.25 6.12 and 7.2.2.1:
        /// the answer states what was agreed). Without values, the call runs as it was asked for.
        /// </remarks>
        /// <param name="lci">
        /// Logical channel of the incoming call.
        /// </param>
        /// <param name="agreed">
        /// The values to run with, "out" being the direction this end sends in; null accepts what was asked.
        /// </param>
        /// <returns>
        /// True when the Call Accepted left; false when there was no incoming call on the channel or the link refused it.
        /// </returns>
        public bool AcceptCall(ushort lci, X25CallParameters? agreed)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc) || vc.State != X25VcState.CallIncoming)
            {
                return false;
            }

            // The other facilities of the answer. Only a caller that gives values (a switch, passing on what the far end
            // answered) puts any in: a plain accept must not repeat the caller's requests (reverse charging, say) as if the
            // called side had made them.
            byte[] otherFacilities = System.Array.Empty<byte>();
            byte[] networkUtilities = System.Array.Empty<byte>();
            if (agreed.HasValue)
            {
                networkUtilities = agreed.Value.NetworkUtilities;
                vc.WindowSizeOut = agreed.Value.WindowSizeOut;
                vc.WindowSizeIn = agreed.Value.WindowSizeIn;
                vc.PacketSizeOut = agreed.Value.PacketSizeOut;
                vc.PacketSizeIn = agreed.Value.PacketSizeIn;
                vc.PeerSentFacilities = true;
                otherFacilities = agreed.Value.OtherFacilities;
            }

            // X.25 7.2.2.1: when the caller asked for flow control values the answer states what was
            // agreed. This engine accepts what was asked (the codec has already range-checked it), so
            // the Call Accepted echoes the circuit's sizes in the caller's terms: "from called" is our
            // sending direction, "from calling" is theirs. The other facilities follow them (7.1: X.25
            // facilities first).
            byte[]? facilities = null;
            if (vc.PeerSentFacilities || otherFacilities.Length > 0)
            {
                facilities = new X25Facilities
                {
                    HasPacketSize = vc.PeerSentFacilities,
                    PacketSizeFromCalled = vc.PacketSizeOut,
                    PacketSizeFromCalling = vc.PacketSizeIn,
                    HasWindowSize = vc.PeerSentFacilities,
                    WindowFromCalled = vc.WindowSizeOut,
                    WindowFromCalling = vc.WindowSizeIn,
                    PassThrough = otherFacilities,
                }.Encode();
            }

            // From here the circuit holds what was answered, not what the caller asked for.
            vc.OtherFacilities = otherFacilities;
            vc.NetworkUtilities = networkUtilities;

            // The state moves before the packet goes out, and back if it does not: the peer's first
            // data packet can arrive synchronously inside Send (loopback sink) and must find the
            // circuit in data transfer, not still incoming. If the Call Accepted is refused the call
            // is merely incoming again and the application may try again or clear it.
            vc.State = X25VcState.DataTransfer;
            vc.ResetFlow();
            vc.DataPacketsSent = 0;       // a new call: the counts are for this call (a reset keeps them)
            vc.DataPacketsReceived = 0;
            vc.TimerExpiryMs = 0;
            // Between networks the Call Connected always carries the network utility field (X.75 5.4.1), even empty.
            // The agreed sizes go in the utility field too (X.75 5.1, 5.3.4, 5.3.5), never in the user facility field.
            byte[]? utilities = null;
            if (_config.IsSte)
            {
                // X.75 5.3.6, 5.3.9: neither fast select nor reverse charging in a Call Connected, as utility or facility.
                int rc = FindElement(otherFacilities, ReverseChargingFastSelectCode, out int rcLength);
                if (rc >= 0)
                {
                    otherFacilities = Cut(otherFacilities, rc, rcLength);
                }

                // X.75 5.3.3: the throughput class the called side confirms goes in the utility field, never in the user
                // facility field (added 2026-11-22; before, it stayed a facility).
                MoveElementToUtilities(ThroughputClassCode, 0xFF, ref otherFacilities, ref networkUtilities);

                facilities = otherFacilities.Length > 0 ? new X25Facilities { PassThrough = otherFacilities }.Encode() : null;
                utilities = vc.PeerSentFacilities
                    ? WithUtilityFlowControl(networkUtilities, vc.WindowSizeOut, vc.WindowSizeIn, vc.PacketSizeOut, vc.PacketSizeIn)
                    : networkUtilities;
            }

            if (!Send(X25PacketBuilder.CreateCallAccepted(lci, facilities, networkUtilities: utilities)))
            {
                vc.State = X25VcState.CallIncoming;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns the window and packet sizes a circuit runs with, or asks for while the call is being set up.
        /// </summary>
        /// <remarks>
        /// For an incoming call these are what the caller asked for. For an outgoing call, once it is connected, they are
        /// what the far end agreed to in its Call Connected.
        /// </remarks>
        /// <param name="lci">
        /// Logical channel.
        /// </param>
        /// <param name="parameters">
        /// The values, "out" being the direction this end sends in; default when there is no such circuit.
        /// </param>
        /// <returns>
        /// True when the circuit exists.
        /// </returns>
        public bool TryGetCallParameters(ushort lci, out X25CallParameters parameters)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                parameters = default;
                return false;
            }

            parameters = new X25CallParameters(vc.WindowSizeOut, vc.WindowSizeIn, vc.PacketSizeOut, vc.PacketSizeIn, vc.OtherFacilities, vc.NetworkUtilities);
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

            // X.25 7.3.1.1: the negotiated packet size is the maximum data field. A longer block is a
            // message, not a packet: SendMessage segments it (4.3.4).
            if (data.Length > vc.PacketSizeOut)
            {
                return false;
            }

            // A message accepted earlier is still going out; a packet sent now would overtake its
            // tail and break the complete packet sequence (4.3.5). Queue behind it instead.
            if (vc.PendingSegments.Count > 0)
            {
                return false;
            }

            return SendOnePacket(vc, data, mBit, qBit, dBit);
        }

        /// <summary>
        /// Sends user data of any length as one complete packet sequence (X.25 4.3.5): full packets
        /// with the M bit set, then the remainder with M clear. What the window does not let out now
        /// is queued on the circuit and sent as acknowledgements arrive.
        /// </summary>
        /// <param name="lci">
        /// Logical channel, in data transfer.
        /// </param>
        /// <param name="data">
        /// The message. Empty is refused.
        /// </param>
        /// <param name="qBit">
        /// Qualifier bit, applied to every packet of the sequence (4.3.3).
        /// </param>
        /// <param name="dBit">
        /// Delivery confirmation bit. Carried in every packet, not honoured: this engine's P(R) is a
        /// local window update only (decided 2026-09-06; X.25 4.4.1.4 makes the D-bit procedure
        /// optional for a network to support).
        /// </param>
        /// <returns>
        /// True when the whole message has been accepted - sent or queued. False when the channel is
        /// not in data transfer, the message is empty, or the first packet could not be sent and
        /// nothing was queued (a refused link).
        /// </returns>
        /// <remarks>
        /// The last packet of a message shorter than a multiple of the packet size is a partial
        /// packet with M=0 - a category B packet - so the receiver knows the sequence ended. A
        /// message that is an exact multiple ends with a full packet with M=0, also category B.
        /// </remarks>
        public bool SendMessage(ushort lci, byte[] data, bool qBit = false, bool dBit = false)
        {
            return SendMessage(lci, data, qBit, dBit, moreFollows: false);
        }

        /// <summary>
        /// Sends user data as part of a complete packet sequence (X.25 4.3.5), and says whether the sequence ends with
        /// it or goes on in the next call.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Added 2026-11-21 for a call joined between two lines (RetroIO BUGS 32): the receiving line hands a long
        /// sequence on in pieces (<see cref="X25LayerBase.MessageReceived"/> with "more data"), and each piece must go
        /// out as part of ONE sequence on the other line, not as a sequence of its own. X.25 4.3.4 and 4.3.5: every
        /// packet with the M bit set must be full ("a full data packet", category A), so a piece that leaves the
        /// sequence open must be a whole number of packets of this circuit's size; anything else is refused here and
        /// the caller keeps the rest for the next piece.
        /// </para>
        /// </remarks>
        /// <param name="lci">
        /// Logical channel, in data transfer.
        /// </param>
        /// <param name="data">
        /// The data. Empty is refused.
        /// </param>
        /// <param name="qBit">
        /// Qualifier bit, applied to every packet (4.3.3).
        /// </param>
        /// <param name="dBit">
        /// Delivery confirmation bit, carried in every packet, as <see cref="SendMessage(ushort, byte[], bool, bool)"/>.
        /// </param>
        /// <param name="moreFollows">
        /// True when the sequence goes on after this data: the last packet then carries M = 1, and the length must be a
        /// whole number of packets of the circuit's packet size.
        /// </param>
        /// <returns>
        /// True when the data has been accepted - sent or queued; false when the channel is not in data transfer, the
        /// data is empty, or <paramref name="moreFollows"/> is true and the length is not a whole number of packets.
        /// </returns>
        public bool SendMessage(ushort lci, byte[] data, bool qBit, bool dBit, bool moreFollows)
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

            int size = vc.PacketSizeOut;
            if (moreFollows && data.Length % size != 0)
            {
                return false;
            }

            int offset = 0;
            while (offset < data.Length)
            {
                int take = data.Length - offset < size ? data.Length - offset : size;
                bool more = moreFollows || offset + take < data.Length;
                byte[] segment = new byte[take];
                Array.Copy(data, offset, segment, 0, take);
                vc.PendingSegments.Enqueue(new X25PendingSegment(segment, more, qBit, dBit));
                offset += take;
            }

            DrainPending(vc);
            return true;
        }

        /// <summary>
        /// Says how many packets of a channel are queued and not yet sent, because the window or the peer's busy
        /// condition holds them (<see cref="SendMessage(ushort, byte[], bool, bool, bool)"/>).
        /// </summary>
        /// <remarks>
        /// Added 2026-11-21 so a call joined between two lines can hold back the sending side while the other side's
        /// queue is full, instead of letting the queue grow without end (RetroIO BUGS 32).
        /// </remarks>
        /// <param name="lci">
        /// Logical channel.
        /// </param>
        /// <returns>
        /// The number of queued packets; 0 for a channel that is not in use.
        /// </returns>
        public int PendingPacketCount(ushort lci)
        {
            return _vcs.TryGetValue(lci, out X25Vc? vc) ? vc.PendingSegments.Count : 0;
        }

        /// <summary>
        /// Sends as many queued segments as the window and the peer's busy condition allow, in order.
        /// Called after every acknowledgement and RR.
        /// </summary>
        private void DrainPending(X25Vc vc)
        {
            // With a loopback sink the peer's RR for a segment arrives synchronously, inside
            // SendOnePacket, and lands here again through OnFlowControl. A nested drain would send
            // the next segment and dequeue it, and the outer loop would then dequeue a segment it
            // never sent. So only the outermost call drains; the inner one returns and the outer
            // loop picks up the newly opened window on its next turn.
            if (vc.Draining)
            {
                return;
            }

            vc.Draining = true;
            try
            {
                while (vc.PendingSegments.Count > 0)
                {
                    if (vc.State != X25VcState.DataTransfer && vc.State != X25VcState.PvcReady)
                    {
                        return;
                    }

                    X25PendingSegment next = vc.PendingSegments.Peek();
                    if (!SendOnePacket(vc, next.Data, next.MBit, next.QBit, next.DBit))
                    {
                        return;
                    }

                    // A reset that arrived inside the send has already emptied the queue.
                    if (vc.PendingSegments.Count > 0)
                    {
                        vc.PendingSegments.Dequeue();
                    }
                }
            }
            finally
            {
                vc.Draining = false;
            }
        }

        /// <summary>
        /// The single-packet send: window, busy condition, and P(S) spent only when the packet left.
        /// </summary>
        private bool SendOnePacket(X25Vc vc, byte[] data, bool mBit, bool qBit, bool dBit)
        {
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

            // P(S) is spent only when the packet actually leaves - a refused send that still advanced
            // P(S) would leave a hole the peer must treat as a local procedure error (X.25 4.4.1.3) -
            // but it must be spent BEFORE the sink is called, not after: with a loopback sink the
            // peer's RR for this very packet arrives synchronously inside Send, and an RR with
            // P(R) = ps + 1 against a SendSeq still equal to ps is an invalid P(R). So advance, send,
            // and roll back on refusal.
            byte ps = vc.SendSeq;
            vc.SendSeq = (byte)((ps + 1) & 0x07);
            if (!Send(X25PacketBuilder.CreateDataPacket(vc.Lci, ps, vc.RecvSeq, data, mBit, qBit, dBit)))
            {
                vc.SendSeq = ps;
                return false;
            }

            vc.DataPacketsSent++;
            return true;
        }

        /// <summary>
        /// The data packets sent and received so far on a call (<see cref="X25Vc.DataPacketsSent"/>).
        /// </summary>
        /// <param name="lci">Logical channel.</param>
        /// <param name="sent">Data packets sent.</param>
        /// <param name="received">Data packets received.</param>
        /// <returns>False when there is no call on the channel.</returns>
        public bool TryGetPacketCounts(ushort lci, out long sent, out long received)
        {
            if (_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                sent = vc.DataPacketsSent;
                received = vc.DataPacketsReceived;
                return true;
            }

            sent = 0;
            received = 0;
            return false;
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
            return ClearCall(lci, cause, diagnostic, null);
        }

        /// <summary>
        /// Clears a circuit with a Clear Request that carries a facility field (X.25 5.2.4.2, the extended format).
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
        /// <param name="facilities">
        /// The facility field, byte for byte; null or empty sends the basic format.
        /// </param>
        /// <returns>
        /// True when the Clear Request was sent.
        /// </returns>
        public bool ClearCall(ushort lci, byte cause, byte diagnostic, byte[]? facilities)
        {
            return ClearCall(lci, cause, diagnostic, facilities, null);
        }

        /// <summary>
        /// Clears a circuit as <see cref="ClearCall(ushort, byte, byte, byte[])"/> does; between networks, a refusal names
        /// the transit networks of <paramref name="transitList"/> instead of those of the Call Request it answers.
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
        /// <param name="facilities">
        /// The facility field, byte for byte; null or empty sends the basic format.
        /// </param>
        /// <param name="transitList">
        /// A utility field whose transit network identifications go in the refusal (a switch passes the one the next
        /// network refused with, X.75 5.3.1), or null for the Call Request's own.
        /// </param>
        /// <returns>
        /// True when the Clear Request was sent.
        /// </returns>
        public bool ClearCall(ushort lci, byte cause, byte diagnostic, byte[]? facilities, byte[]? transitList)
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

            // Between networks, X.75 4.2.3.1: a clearing cause not in Table 13 is passed on unchanged or changed to
            // network congestion; this engine changes it, so the far STE never receives a cause X.75 does not have.
            if (_config.IsSte && !IsX75ClearingCause(cause))
            {
                cause = SteClearingNetworkCongestion;
            }

            // The state change stands even when the packet is refused: the application has decided
            // the call is over, and the clear-request timer is what carries that decision to the
            // peer if this attempt did not (retransmission arrives with the T23 work, plan B5).
            // X.75 5.3.1: "A transit network identification is always present for each transit network in ... the clear
            // request packet issued as a direct response to the call request packet", and is "not present in a clear request
            // packet issued either after receipt of the corresponding call connected packet or after transmission of the
            // corresponding call request or call connected packets". A call still incoming here is being refused: its
            // clear carries the transit list of the Call Request it answers, as the destination's Call Connected does
            // (SwitchCore.AnswerTransitList). Added 2026-11-22; before, every clear between networks had an empty field.
            bool directResponse = vc.State == X25VcState.CallIncoming;
            byte[] incomingUtilities = transitList ?? vc.NetworkUtilities;

            vc.State = X25VcState.ClearRequest;
            vc.Cause = cause;
            vc.Diagnostic = diagnostic;
            vc.Retries = 0;
            vc.CallRequestHeld = false;
            vc.TimerExpiryMs = Now() + _config.ClearRequestTimeoutMs;
            // X.75 4.2.3.3.3: in the extended format the network utility length is "always present when the user
            // facility length is present", so between networks a utility field (empty when there is nothing for it) goes
            // with any facilities.
            byte[]? utilities = null;
            if (_config.IsSte)
            {
                utilities = directResponse ? TransitListOf(incomingUtilities) : System.Array.Empty<byte>();
            }
            return Send(X25PacketBuilder.CreateClearRequest(lci, cause, diagnostic, facilities, networkUtilities: utilities));
        }

        /// <summary>
        /// A utility field holding only the transit network identifications of <paramref name="utilities"/>, in order
        /// (X.75 5.3.1); empty when it has none or does not decode.
        /// </summary>
        /// <param name="utilities">
        /// The utility field of the Call Request.
        /// </param>
        /// <returns>
        /// The field for the answer.
        /// </returns>
        private static byte[] TransitListOf(byte[] utilities)
        {
            if (X75Utilities.TryDecode(utilities, out X75Utilities read) != X75UtilityError.None || read.TransitNetworks.Count == 0)
            {
                return System.Array.Empty<byte>();
            }

            X75Utilities answer = new X75Utilities();
            List<string> transit = read.TransitNetworks;
            for (int i = 0; i < transit.Count; i++)
            {
                answer.TransitNetworks.Add(transit[i]);
            }

            return answer.Encode();
        }

        /// <summary>
        /// True when a clearing cause is one X.75 Table 13 lists (read from the PDF page, 2026-11-18): DTE originated
        /// (00000000, and any value with bit 8 set - Note 1), number busy 01, out of order 09, remote procedure error 11,
        /// reverse charging acceptance not subscribed 19, incompatible destination 21, fast select acceptance not
        /// subscribed 29, ship absent 39, invalid facility request 03, access barred 0B, network congestion 05, not
        /// obtainable 0D, RPOA out of order 15 (hexadecimal).
        /// </summary>
        private static bool IsX75ClearingCause(byte cause)
        {
            if (cause == 0x00 || (cause & 0x80) != 0)
            {
                return true;
            }

            switch (cause)
            {
                case 0x01:
                case 0x09:
                case 0x11:
                case 0x19:
                case 0x21:
                case 0x29:
                case 0x39:
                case 0x03:
                case 0x0B:
                case 0x05:
                case 0x0D:
                case 0x15:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Gets the facility field of the Clear Request / Clear Indication that ended the last call on a channel.
        /// </summary>
        /// <param name="lci">
        /// Logical channel.
        /// </param>
        /// <returns>
        /// The field byte for byte; empty when the clear carried none, when the channel is unknown, or when a new call has
        /// started on it since.
        /// </returns>
        public byte[] GetClearFacilities(ushort lci)
        {
            return _vcs.TryGetValue(lci, out X25Vc? vc) ? vc.ClearFacilities : System.Array.Empty<byte>();
        }

        /// <summary>
        /// Gets the X.75 utility field of the clear that ended the last call on a channel (empty off an STE, or when it had
        /// none), for a switch to read the transit list of a refusal (X.75 5.3.1).
        /// </summary>
        /// <param name="lci">
        /// Logical channel.
        /// </param>
        /// <returns>
        /// The field byte for byte, or empty.
        /// </returns>
        public byte[] GetClearUtilities(ushort lci)
        {
            return _vcs.TryGetValue(lci, out X25Vc? vc) ? vc.ClearUtilities : System.Array.Empty<byte>();
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
            // busy condition, it is a stall the peer cannot explain. Set before the send so a
            // synchronous reply sees it, rolled back if the packet did not leave.
            bool before = vc.LocalBusy;
            vc.LocalBusy = busy;
            if (!Send(busy
                ? X25PacketBuilder.CreateRNR(lci, vc.RecvSeq)
                : X25PacketBuilder.CreateRR(lci, vc.RecvSeq)))
            {
                vc.LocalBusy = before;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Sends an Interrupt: 1 to 32 octets that reach the peer ahead of the data queue and outside
        /// the data flow control (X.25 4.4.2).
        /// </summary>
        /// <param name="lci">
        /// Logical channel, in data transfer.
        /// </param>
        /// <param name="data">
        /// 1 to 32 octets.
        /// </param>
        /// <returns>
        /// True when the Interrupt left. False when the channel is not in data transfer, the data
        /// is empty or longer than 32 octets, a previous interrupt is still unconfirmed (4.4.2: only
        /// one outstanding per direction), or the link refused the packet.
        /// </returns>
        public bool SendInterrupt(ushort lci, byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > 32)
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

            if (vc.InterruptOutstanding)
            {
                return false;
            }

            // Marked outstanding before the send: the confirmation can arrive synchronously.
            vc.InterruptOutstanding = true;
            if (!Send(X25PacketBuilder.CreateInterrupt(lci, data)))
            {
                vc.InterruptOutstanding = false;
                return false;
            }

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
            _restartExpiryMs = Now() + RestartTimerMs();
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
            // On a port between networks the call set-up and clearing packets carry the X.75 utility field (X.75 4.2).
            X25PacketInfo info = X25PacketParser.ParsePacket(pkt, _config.IsSte);
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
                case X25PacketType.Interrupt:
                    OnInterrupt(lci, info);
                    break;
                case X25PacketType.InterruptConfirmation:
                    OnInterruptConfirmation(lci);
                    break;
            }
        }

        // ------------------------------------------------------------------ complete packet sequences (X.25 4.3.5)

        /// <summary>
        /// Collects category A packets (full, M=1, D=0) and delivers the whole sequence once the
        /// category B packet that ends it arrives. The Q-bit consistency rule (4.3.3) has already
        /// been applied by the caller, before the packet was acknowledged.
        /// <para>
        /// What X.25 (ITU-T X.25 10/96) says, as read from the text: 4.3.4 lets a DTE use the M bit to
        /// "indicate a sequence of more than one packet"; 4.3.5 defines the complete packet sequence and says
        /// a source DTE's sequence is "always delivered to the destination DTE as a single complete packet
        /// sequence"; Table 4-1 shows the packets arrive with the M bit kept. No clause makes the receiving
        /// DTE join the packets into one block, and none bounds the length of a sequence or names a reset
        /// for a long one. The only reset for size is 4.3.2: one packet whose data field is longer than the
        /// agreed maximum (local procedure error, diagnostic 39). So this method is a convenience: it
        /// joins packets for the consumer, and when the part reaches <see cref="X25Vc.MaxMessagePart"/> it
        /// hands it on with <c>moreData</c> true and carries on. Every packet is also given to
        /// <see cref="X25LayerBase.DataReceived"/> with its own M bit as it arrives.
        /// </para>
        /// </summary>
        private void Reassemble(X25Vc vc, byte[] payload, bool qBit, bool mBit, bool dBit)
        {
            bool categoryA = mBit && !dBit && payload.Length == vc.PacketSizeIn;

            if (categoryA)
            {
                if (vc.Reassembly == null)
                {
                    vc.Reassembly = new System.IO.MemoryStream();
                    vc.ReassemblyQBit = qBit;
                }

                // X.25 4.3.4 and 4.3.5 give a complete packet sequence no upper length and name no reset
                // for a long one. When the part collected so far has reached its size, hand it on with
                // "more follows" and go on collecting; the sequence itself is not cut and nothing is reset.
                if (vc.Reassembly.Length + payload.Length > X25Vc.MaxMessagePart && vc.Reassembly.Length > 0)
                {
                    byte[] part = vc.Reassembly.ToArray();
                    vc.Reassembly.SetLength(0);
                    RaiseMessageReceived(vc.Lci, part, qBit, moreData: true);
                }

                vc.Reassembly.Write(payload, 0, payload.Length);
                return;
            }

            if (vc.Reassembly == null)
            {
                RaiseMessageReceived(vc.Lci, payload, qBit, moreData: false);
                return;
            }

            vc.Reassembly.Write(payload, 0, payload.Length);
            byte[] message = vc.Reassembly.ToArray();
            vc.ClearSequences();
            RaiseMessageReceived(vc.Lci, message, qBit, moreData: false);
        }

        // ------------------------------------------------------------------ interrupts (X.25 4.4.2)

        /// <summary>
        /// An Interrupt from the peer: delivered, then confirmed. The peer may not send another
        /// before this confirmation, so no "second interrupt outstanding" state exists on the
        /// receive side - the confirmation is immediate. An empty or over-long data field is a
        /// procedure error (5.4.2.1: 1 to 32 octets), diagnostic 39 / 38.
        /// </summary>
        private void OnInterrupt(ushort lci, X25PacketInfo info)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                return;
            }

            if (info.InterruptData.Length == 0)
            {
                ResetForProcedureError(vc, X25Diagnostic.PacketTooShort);
                return;
            }

            if (info.InterruptData.Length > 32)
            {
                ResetForProcedureError(vc, X25Diagnostic.PacketTooLong);
                return;
            }

            RaiseInterruptReceived(lci, info.InterruptData);
            Send(X25PacketBuilder.CreateInterruptConfirmation(lci));
        }

        /// <summary>
        /// The peer confirms our Interrupt. One that arrives with none outstanding is Annex E 43,
        /// "Unauthorized interrupt confirmation" - a procedure error (Table C.4).
        /// </summary>
        private void OnInterruptConfirmation(ushort lci)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                return;
            }

            if (!vc.InterruptOutstanding)
            {
                // Between networks it is DISCARD: X.75 Table C.5 (10/96 PDF page 104), interrupt confirmation in i1
                // and i2. Until 2026-11-23 an STE reset the call with # 43, the X.25 rule.
                if (_config.IsSte)
                {
                    Discard();
                    return;
                }

                ResetForProcedureError(vc, X25Diagnostic.UnauthorizedInterruptConfirmation);
                return;
            }

            vc.InterruptOutstanding = false;
            RaiseInterruptConfirmed(lci);
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
                        SendDiagnostic(X25Diagnostic.UnidentifiablePacket, pkt);
                        return false;
                    default:
                        SendDiagnostic(X25Diagnostic.UnassignedLogicalChannel, pkt);
                        return false;
                }
            }

            // Table C.2, column r3 (DCE restart indication): while our Restart Indication is unconfirmed the DCE
            // "will ignore all packets except for restart request and DTE restart confirmation" (3.3.2) - every
            // packet on an assigned channel is DISCARD, a Call Request included. Until 2026-11-17 a call that
            // SINTRAN's COSMOS X.25 sent before its Restart Confirmation was routed on, and SINTRAN cleared it
            // the moment it confirmed: the first call after every switch start failed. An unassigned channel
            // is still Table C.1 (any state), checked below. Annex C gives the DCE's actions only, so a DTE
            // (state r2 for it) is left as it was.
            if (!_config.IsDte && _restartOutstanding && IsAssigned(lci))
            {
                return Discard();
            }

            // Table C.2, last rows: a restart packet on a nonzero channel is ERROR # 41 on that
            // channel; an undefined type on an assigned channel is ERROR # 33.
            if (type == X25PacketType.RestartRequest || type == X25PacketType.RestartConfirmation)
            {
                // Except in p7, our own Clear Request unconfirmed: X.25 Table C-3 (DCE clear indication) and X.75
                // Table C.3 (STE-Y clear request) both give DISCARD there. Until 2026-11-23 it cleared the channel
                // again. A DTE has no Annex C table and keeps the old behaviour.
                if (!_config.IsDte && _vcs.TryGetValue(lci, out X25Vc? clearing) && clearing.State == X25VcState.ClearRequest)
                {
                    return Discard();
                }

                if (SteResetInDataTransfer(lci, X25Diagnostic.RestartWithNonzeroChannel))
                {
                    return false;
                }

                ChannelError(lci, X25Diagnostic.RestartWithNonzeroChannel, pkt);
                return false;
            }

            // Table C.1: a channel outside every configured range is unassigned - DIAG # 36.
            if (!IsAssigned(lci))
            {
                SendDiagnostic(X25Diagnostic.UnassignedLogicalChannel, pkt);
                return false;
            }

            if (type == X25PacketType.Unknown)
            {
                if (SteResetInDataTransfer(lci, X25Diagnostic.UnidentifiablePacket))
                {
                    return false;
                }

                ChannelError(lci, X25Diagnostic.UnidentifiablePacket, pkt);
                return false;
            }

            if (type == X25PacketType.Diagnostic
                || type == X25PacketType.RegistrationRequest || type == X25PacketType.RegistrationConfirmation)
            {
                ChannelError(lci, X25Diagnostic.PacketNotAllowed, pkt);
                return false;
            }

            X25VcState state = _vcs.TryGetValue(lci, out X25Vc? vc) ? vc.State : X25VcState.Ready;

            // Table C.1 NOTE 1: on a PVC only p4 exists and only Table C.4 applies. Its row "Invalid packet type on a
            // permanent virtual circuit" is ERROR (d3) # 35 in d1 and d2 and DISCARD in d3 - a reset, never a clear,
            // because a PVC has no call to clear and nothing that could set it up again.
            if (vc != null && vc.Kind == X25VcKind.Pvc
                && (type == X25PacketType.CallRequest || type == X25PacketType.CallAccepted
                    || type == X25PacketType.ClearRequest || type == X25PacketType.ClearConfirmation))
            {
                if (state == X25VcState.ResetRequest)
                {
                    return Discard();
                }

                ResetForProcedureError(vc, X25Diagnostic.InvalidPacketTypeOnPvc);
                return false;
            }

            // Tables C.3 and C.4, one row per packet family. NORMAL and collision rows let the packet
            // through to its handler; DISCARD drops it; anything else is ERROR with the state's code.
            switch (type)
            {
                case X25PacketType.CallRequest:
                    // NORMAL in p1; collision in p2 (handled); p6/p7 DISCARD; else ERROR.
                    if (state == X25VcState.Ready || state == X25VcState.CallRequest) return true;

                    // p7, our Clear Request unconfirmed: DISCARD for an X.25 DCE (Table C-3), but ERROR (p7) # 26 between
                    // networks (X.75 Table C.3, PDF page 102). Until 2026-11-23 an STE discarded it too.
                    if (state == X25VcState.ClearRequest && !_config.IsSte) return Discard();
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

                    // p1: ERROR # 20 for an X.25 DCE (Table C-3), but DISCARD between networks (X.75 Table C.3). Until
                    // 2026-11-23 an STE cleared the idle channel.
                    if (state == X25VcState.Ready && _config.IsSte) return Discard();
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

        /// <summary>
        /// X.75 Table C.4 (10/96 PDF page 103) for a restart packet on a nonzero channel or an unknown packet type on a
        /// virtual call in data transfer: ERROR (d3) - a RESET with network congestion - in d1, and DISCARD in d3 (our
        /// reset outstanding). Added 2026-11-23; until then an STE cleared the call, as X.25 does through Table C-3.
        /// </summary>
        /// <returns>
        /// True when Table C.4 decided it (reset or discard); false when the caller goes on to Table C.3.
        /// </returns>
        private bool SteResetInDataTransfer(ushort lci, byte diagnostic)
        {
            if (!_config.IsSte || !_vcs.TryGetValue(lci, out X25Vc? vc) || vc.Kind != X25VcKind.Svc)
            {
                return false;
            }

            if (vc.State == X25VcState.ResetRequest)
            {
                Discard();
                return true;
            }

            if (vc.State == X25VcState.DataTransfer)
            {
                ResetForProcedureError(vc, diagnostic);
                return true;
            }

            return false;
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

            // A PVC is never cleared: Table C.4 makes every one of its errors ERROR (d3), a reset, in d1 and d2, and
            // DISCARD in d3 (our reset outstanding). Until 2026-11-16 this cleared it.
            if (vc.Kind == X25VcKind.Pvc)
            {
                if (vc.State != X25VcState.ResetRequest)
                {
                    ResetForProcedureError(vc, diagnostic);
                }

                return;
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
        /// <remarks>
        /// The p-states are states of the INTERFACE, named from the DTE's side: p2 "DTE waiting" is a call the DTE sent,
        /// p3 "DCE waiting" one the DCE sent; p6 "DTE clear request", p7 "DCE clear indication". So a DCE or an STE
        /// (X.75 Table C.3: p2 "STE-X call request", p7 "STE-Y clear request") finds its OWN outgoing call in p3 (# 22)
        /// and its own clear in p7 (# 26), the mirror of a DTE. Until 2026-11-23 every side used the DTE's mapping, so a
        /// network side named the wrong state in diagnostics 21, 22 and 25.
        /// </remarks>
        private byte StateDiagnostic(X25VcState state)
        {
            bool dteSide = _config.IsDte;
            switch (state)
            {
                case X25VcState.Ready: return 20;
                case X25VcState.CallRequest: return dteSide ? (byte)21 : (byte)22;
                case X25VcState.CallIncoming: return dteSide ? (byte)22 : (byte)21;
                case X25VcState.DataTransfer: return 23;
                case X25VcState.PvcReady: return 27;
                case X25VcState.ClearRequest: return dteSide ? (byte)25 : (byte)26;
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

            // X.75 has no Diagnostic packet: its packet types have none, Annex E codes a diagnostic only in a clear,
            // reset or restart, and Table C.1 gives DISCARD for the very cases X.25 answers with DIAG (an unassigned
            // channel, a packet under 2 octets, a bad GFI). So between networks a DIAG entry is a DISCARD. Added
            // 2026-11-23; until then an STE sent X.25 Diagnostic packets across an X.75 link.
            if (_config.IsDte || _config.IsSte)
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
                    case X25VcState.CallIncoming:
                        OnT11Expired(vc, nowMs);
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

            if (!_config.IsDte)
            {
                TickRestartIndication(nowMs);
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
        /// The restart timer for this side: T10 for a DCE (or an STE, which is not a DTE), T20 for a DTE.
        /// </summary>
        private int RestartTimerMs()
        {
            // X.75 Table D.1: T30 is 180 s, the value of RestartTimeoutMs; an STE ran the DCE's 60 s T10 until 2026-11-23.
            return _config.IsDte || _config.IsSte ? _config.RestartTimeoutMs : _config.RestartIndicationTimeoutMs;
        }

        /// <summary>
        /// T10 (r3), the DCE's restart time-out. X.25 (10/96) Table D.1 (text line 16129-16151): T10 = 60 s, started
        /// when the "DCE issues a restart indication", ended when "the restart confirmation or restart request is
        /// received". The first time it runs out the "DCE remains in r3, signals a restart indication (local procedure
        /// error # 52) again, and restarts time-out T10". The second time (line 16241-16243) the "DCE enters the r1
        /// state and may issue a diagnostic packet (# 52)". Until 2026-11-18 a DCE ran the DTE's T20 rule (180 s and
        /// five retransmissions), which Table D.1 does not give a DCE.
        /// </summary>
        private void TickRestartIndication(long nowMs)
        {
            if (_restartRetries == 0)
            {
                // X.75 Table D.1, T30 first expiry: "a restart request packet (network congestion, # 52) again, and
                // restarts time-out T30" - the same rule as T10 with the X.75 cause (00000011, Table 25) and 180 s.
                _restartRetries = 1;
                _restartExpiryMs = nowMs + RestartTimerMs();
                _restartCause = _config.IsSte ? SteRestartingNetworkCongestion : X25Cause.RestartingLocalProcedureError;
                _restartDiagnostic = X25Diagnostic.TimeExpiredRestart;
                Send(X25PacketBuilder.CreateRestartRequest(_restartCause, _restartDiagnostic));
                return;
            }

            _restartOutstanding = false;
            RestartTimeouts++;

            // X.25 5.7 (text line 10625-10633), the diagnostic explanation: "bits 8, 7, 6 and 5 of the first octet
            // contain the general format identifier for the interface" and "bits 4 to 1 of the first octet and bits 8
            // to 1 of the second octet are all 0 for expiration of time-out T10". The GFI is 0001, modulo 8, the only
            // modulo this engine runs (X25Config.Validate).
            // X.75 Table D.2, T30 second expiry: "STE X/Y enters the r1 state" and nothing more - X.75 has no
            // Diagnostic packet. Until 2026-11-23 an STE sent one here.
            if (!_config.IsSte)
            {
                DiagnosticsSent++;
                Send(X25PacketBuilder.CreateDiagnostic(X25Diagnostic.TimeExpiredRestart, new byte[] { 0x10, 0x00 }));
            }

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
        /// T11 (p3), X.25 4.1.4 and Table D.1: an incoming call neither accepted nor cleared within
        /// T11 is "a procedure error from the called DTE" and the call is cleared - at a DCE with a
        /// clear indication, "local procedure error # 49". The cause comes from
        /// <see cref="ProcedureErrorCause"/> (local procedure error at a DCE, 0 at a DTE, see
        /// <see cref="X25Config.IncomingCallTimeoutMs"/>) and the diagnostic is 49, time expired for
        /// incoming call. The application was offered the call, so it is told the call is gone.
        /// </summary>
        private void OnT11Expired(X25Vc vc, long nowMs)
        {
            IncomingCallTimeouts++;
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
            // X.75 Tables D.1 and D.2, T32: the first expiry signals a reset request (network congestion, # 51) again,
            // the second clears a virtual call (network congestion, # 51) or returns a permanent circuit to d1. One
            // retransmission and a fixed packet, not the X.25 retries of the original one. Added 2026-11-23.
            if (_config.IsSte)
            {
                if (vc.Retries == 0)
                {
                    vc.Retries = 1;
                    vc.Cause = SteResettingNetworkCongestion;
                    vc.Diagnostic = X25Diagnostic.TimeExpiredReset;
                    vc.TimerExpiryMs = nowMs + _config.ResetRequestTimeoutMs;
                    Send(X25PacketBuilder.CreateResetRequest(vc.Lci, vc.Cause, vc.Diagnostic));
                    return;
                }

                ResetTimeouts++;
                if (vc.Kind == X25VcKind.Svc)
                {
                    BeginClear(vc, SteClearingNetworkCongestion, X25Diagnostic.TimeExpiredReset, nowMs);
                    ReportCleared(vc, vc.Cause, vc.Diagnostic);
                    return;
                }

                vc.State = X25VcState.PvcReady;
                vc.Retries = 0;
                RaiseReset(vc.Lci, SteResettingNetworkCongestion, X25Diagnostic.TimeExpiredReset);
                return;
            }

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
            // X.75 Tables D.1 and D.2, T33: the first expiry signals a clear request (network congestion, # 50) again,
            // the second returns the channel to p1. One retransmission and a fixed packet. Added 2026-11-23.
            if (_config.IsSte && vc.Retries == 0)
            {
                vc.Retries = 1;
                vc.TimerExpiryMs = nowMs + _config.ClearRequestTimeoutMs;
                Send(X25PacketBuilder.CreateClearRequest(vc.Lci, SteClearingNetworkCongestion, X25Diagnostic.TimeExpiredClear));
                return;
            }

            if (!_config.IsSte && vc.Retries < _config.MaxRetransmissions)
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
            vc.OtherFacilities = System.Array.Empty<byte>();
            vc.NetworkUtilities = System.Array.Empty<byte>();
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
        /// The cause this engine puts in a Clear it originates for a procedure error or a
        /// time-out: "Local procedure error" from a DCE; from a DTE only 0 is allowed (X.25 Table
        /// 5-7 note a) and the diagnostic carries the reason.
        /// </summary>
        /// <remarks>
        /// The clearing value, 00010011, not the resetting one. They differ, and a single constant
        /// used to serve both: every Clear sent from here carried 00000101, which Table 5-7 reads
        /// as network congestion. See <see cref="X25Cause"/>.
        /// </remarks>
        private byte ProcedureErrorCause()
        {
            // Between networks there is no "local procedure error": X.75 Table 13 does not have it, and every ERROR
            // entry of Annex C and every time-out of Annex D clears with "network congestion" (00000101, Table 13).
            if (_config.IsSte)
            {
                return SteClearingNetworkCongestion;
            }

            return _config.IsDte ? X25Cause.DteOriginated : X25Cause.ClearingLocalProcedureError;
        }

        /// <summary>
        /// X.75 Table 13: clearing cause "Network congestion", 00000101.
        /// </summary>
        private const byte SteClearingNetworkCongestion = 0x05;

        /// <summary>
        /// X.75 Table 25: restarting cause "Network congestion", 00000011.
        /// </summary>
        private const byte SteRestartingNetworkCongestion = 0x03;

        /// <summary>
        /// X.75 Table 15: resetting cause "Network congestion", 00000111.
        /// </summary>
        private const byte SteResettingNetworkCongestion = 0x07;

        /// <summary>
        /// X.75 Table C-3 (call request received in the STE's own call request state) and Annex F a) row 40 ("Call
        /// collision is detected on the selected outgoing link"): diagnostic 116.
        /// </summary>
        private const byte SteCallCollisionDiagnostic = 116;

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
            // "From calling" is the direction we send in. The values are the circuit's own: the defaults for a call an
            // application placed, what the calling side asked for for a call a switch relays.
            byte[] facilities = new X25Facilities
            {
                HasPacketSize = true,
                PacketSizeFromCalled = vc.PacketSizeIn,
                PacketSizeFromCalling = vc.PacketSizeOut,
                HasWindowSize = true,
                WindowFromCalled = vc.WindowSizeIn,
                WindowFromCalling = vc.WindowSizeOut,

                // The other facilities after them (7.1: X.25 facilities first): none for a call an application placed,
                // what the calling side asked for for a call a switch relays.
                PassThrough = vc.OtherFacilities,
            }.Encode();

            // T21 is armed before the send: a Call Connected arriving synchronously inside Send
            // cancels it, and arming it afterwards would leave a timer running on a connected call.
            vc.TimerExpiryMs = Now() + _config.CallRequestTimeoutMs;
            // Between networks the Call Request always carries the network utility field (X.75 4.2.1, 5.4.1), and the
            // window and packet sizes go in it, not in the user facility field (X.75 5.1: facilities that "require STE or
            // transit network action are mapped into X.75 utilities and thus are not present in the X.75 facility
            // field"; 5.3.4 and 5.3.5 are those utilities).
            byte[]? utilities = null;
            if (_config.IsSte)
            {
                byte[] userFacilities = vc.OtherFacilities;
                byte[] networkUtilities = vc.NetworkUtilities;
                MoveElementToUtilities(ReverseChargingFastSelectCode, ReverseChargingFastSelectBits, ref userFacilities, ref networkUtilities);
                MoveElementToUtilities(ThroughputClassCode, 0xFF, ref userFacilities, ref networkUtilities);
                facilities = new X25Facilities { PassThrough = userFacilities }.Encode();
                utilities = WithUtilityFlowControl(networkUtilities, vc.WindowSizeIn, vc.WindowSizeOut, vc.PacketSizeIn, vc.PacketSizeOut);
            }

            if (!Send(X25PacketBuilder.CreateCallRequest(vc.Lci, vc.CalledAddress, vc.CallingAddress, facilities, cud,
                    networkUtilities: utilities)))
            {
                vc.TimerExpiryMs = 0;
                return false;
            }

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
                // Between networks, X.75 3.1.4: "both calls shall be cleared. The clearing cause field shall be coded
                // 'Network congestion'." Table C-3 (call request received in the STE's own call request state): ERROR -
                // the received packet is discarded and the channel cleared with network congestion # 116, entering the
                // STE's clear request state. Our own call is lost with it and its owner told. The far STE does the
                // same, so the two Clear Requests cross and 3.1.7 (clear collision) completes both with no
                // confirmation.
                if (_config.IsSte)
                {
                    ProcedureErrors++;
                    ReportCleared(vc, SteClearingNetworkCongestion, SteCallCollisionDiagnostic);
                    BeginClear(vc, SteClearingNetworkCongestion, SteCallCollisionDiagnostic, Now());
                    return;
                }

                if (_config.IsDte)
                {
                    UnexpectedPackets++;
                    return;
                }

                ToReady(vc);
                ReportCleared(vc, X25Cause.ClearingLocalProcedureError, X25Diagnostic.CallCollision);
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

            ReadUtilityFlowControl(info.NetworkUtilities, requested);

            // We are the called DTE: "from called" is what we may send, "from calling" what we receive.
            // The request is accepted as asked (the codec has range-checked it).
            vc.PeerSentFacilities = requested.HasPacketSize || requested.HasWindowSize;
            vc.PacketSizeOut = requested.PacketSizeFromCalled;
            vc.PacketSizeIn = requested.PacketSizeFromCalling;
            vc.WindowSizeOut = requested.WindowFromCalled;
            vc.WindowSizeIn = requested.WindowFromCalling;
            vc.OtherFacilities = _config.IsSte
                ? ElementFromUtilities(ThroughputClassCode, 0xFF, info.NetworkUtilities,
                    ElementFromUtilities(ReverseChargingFastSelectCode, ReverseChargingFastSelectBits, info.NetworkUtilities, requested.PassThrough))
                : requested.PassThrough;
            vc.NetworkUtilities = info.NetworkUtilities;
            vc.ClearFacilities = System.Array.Empty<byte>();
            vc.ClearUtilities = System.Array.Empty<byte>();

            // T11 (Table D.1) runs from the moment the call is offered until it is answered. It is
            // armed BEFORE the offer: an owner that answers inside the event (AcceptCall or ClearCall)
            // stops it there, and one that answers later stops it then. Until 2026-11-17 there was
            // no T11 at all, so a call nobody answered sat in p3 for ever.
            vc.TimerExpiryMs = Now() + _config.IncomingCallTimeoutMs;
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

            ReadUtilityFlowControl(info.NetworkUtilities, agreed);

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

            // X.75 5.3.3, between networks: the confirmed throughput class arrives as utility 02 and reaches the switch as
            // facility 02. "If an STE detects that an explicitly confirmed throughput class value finally applying to the
            // call is higher than the one requested, it should clear the call with an indication of Network congestion"
            // (Table 13: 00000101; not FailCall, whose cause is Invalid facility request). 5.3.3 names no diagnostic; the one used for a window or packet
            // size outside what was asked is used. Only a class asked for explicitly can be compared: the default agreed
            // between Administrations is not known here. Added 2026-11-22.
            if (_config.IsSte)
            {
                agreed.PassThrough = ElementFromUtilities(ThroughputClassCode, 0xFF, info.NetworkUtilities, agreed.PassThrough);
                int askedAt = FindElement(vc.OtherFacilities, ThroughputClassCode, out int askedLength);
                int confirmedAt = FindElement(agreed.PassThrough, ThroughputClassCode, out int confirmedLength);
                if (askedAt >= 0 && askedLength == 2 && confirmedAt >= 0 && confirmedLength == 2)
                {
                    byte asked = vc.OtherFacilities[askedAt + 1];
                    byte confirmed = agreed.PassThrough[confirmedAt + 1];
                    if ((confirmed & 0x0F) > (asked & 0x0F) || (confirmed >> 4) > (asked >> 4))
                    {
                        BeginClear(vc, SteClearingNetworkCongestion, X25Diagnostic.FacilityParameterNotAllowed, Now());
                        ReportCleared(vc, SteClearingNetworkCongestion, X25Diagnostic.FacilityParameterNotAllowed);
                        return;
                    }
                }
            }

            // A Call Connected with no facility says "the defaults apply" (7.2.2.1: a value not
            // indicated is the default), whatever we asked for.
            vc.WindowSizeOut = agreed.WindowFromCalling;
            vc.WindowSizeIn = agreed.WindowFromCalled;
            vc.PacketSizeOut = agreed.PacketSizeFromCalling;
            vc.PacketSizeIn = agreed.PacketSizeFromCalled;
            vc.OtherFacilities = agreed.PassThrough;
            vc.NetworkUtilities = info.NetworkUtilities;
            vc.CallUserData = info.CallUserData;

            vc.State = X25VcState.DataTransfer;
            vc.ResetFlow();
            vc.DataPacketsSent = 0;       // a new call: the counts are for this call (a reset keeps them)
            vc.DataPacketsReceived = 0;
            vc.TimerExpiryMs = 0;
            RaiseCallConnected(lci);
        }

        /// <summary>
        /// Between networks, takes the window and packet sizes from the X.75 utility field (5.3.4, 5.3.5) into the decoded
        /// facilities, so the rest of the engine negotiates them the same way as on an X.25 interface. Does nothing on a
        /// DTE or DCE port, or when the field has no such utility.
        /// </summary>
        private void ReadUtilityFlowControl(byte[] utilities, X25Facilities into)
        {
            if (!_config.IsSte || X75Utilities.TryDecode(utilities, out X75Utilities u) != X75UtilityError.None)
            {
                return;
            }

            if (u.HasWindowSize)
            {
                into.HasWindowSize = true;
                into.WindowFromCalled = u.WindowFromCalled;
                into.WindowFromCalling = u.WindowFromCalling;
            }

            if (u.HasPacketSize)
            {
                // 5.4.3.5: the packet size parameter is the base-2 logarithm of the length in octets.
                into.HasPacketSize = true;
                into.PacketSizeFromCalled = 1 << u.PacketSizeLog2FromCalled;
                into.PacketSizeFromCalling = 1 << u.PacketSizeLog2FromCalling;
            }
        }

        /// <summary>
        /// Returns a utility field with the window size (code 43) and packet size (code 42) utilities set to the given
        /// values, the rest of the field kept. "From called" and "from calling" are the directions of the call.
        /// </summary>
        /// <summary>
        /// X.25 facility code 01 and X.75 utility code 01: reverse charging and fast select (X.25 7.3.6, X.75 5.4.3.6).
        /// </summary>
        private const byte ReverseChargingFastSelectCode = 0x01;

        /// <summary>
        /// The bits X.75 5.4.3.6 assigns in the reverse charging / fast select parameter: bit 8 and bit 7 fast select, bit 1
        /// reverse charging. X.25 (10/96) 7.3.6 gives the same three bits the same meaning and uses bits 6 and 5 for the ICRD
        /// status selection, which X.75 (11/88) leaves "unassigned and set to 0"; those are dropped at the border.
        /// </summary>
        private const byte ReverseChargingFastSelectBits = 0xC1;

        /// <summary>
        /// X.25 facility code 02 and X.75 utility code 02: throughput class (X.25 7.3.2, X.75 5.4.3.3).
        /// </summary>
        private const byte ThroughputClassCode = 0x02;

        /// <summary>
        /// Finds the first element with a given code in a facility or utility field, before the first marker (00). Both
        /// fields use the same element classes (X.25 7.1, X.75 5.2): bits 8 and 7 of the code give one, two or three
        /// parameter octets, or a length octet first.
        /// </summary>
        /// <returns>The index of the element's code octet and its whole length, or -1.</returns>
        private static int FindElement(byte[] field, byte code, out int length)
        {
            int i = 0;
            while (i < field.Length)
            {
                byte here = field[i];
                if (here == 0x00)
                {
                    break;
                }

                switch (here >> 6)
                {
                    case 0: length = 2; break;
                    case 1: length = 3; break;
                    case 2: length = 4; break;
                    default: length = i + 1 < field.Length ? 2 + field[i + 1] : field.Length - i; break;
                }

                if (i + length > field.Length)
                {
                    length = field.Length - i;
                }

                if (here == code)
                {
                    return i;
                }

                i += length;
            }

            length = 0;
            return -1;
        }

        // The field without the element at [at, at + length).
        private static byte[] Cut(byte[] field, int at, int length)
        {
            byte[] kept = new byte[field.Length - length];
            Array.Copy(field, 0, kept, 0, at);
            Array.Copy(field, at + length, kept, at, field.Length - at - length);
            return kept;
        }

        // The field with one two-octet element in front.
        private static byte[] Prepend(byte code, byte parameter, byte[] field)
        {
            byte[] result = new byte[field.Length + 2];
            result[0] = code;
            result[1] = parameter;
            field.CopyTo(result, 2);
            return result;
        }

        /// <summary>
        /// For a Call Request leaving an STE: moves reverse charging / fast select from the X.25 facilities into the X.75
        /// utility field. X.75 5.3.9: "No indication of reverse charging should be present in the user facility field";
        /// 5.3.6 the same for fast select, which is a utility between networks. An element 01 already in the utility field
        /// (carried from the previous network) is replaced, so it is never sent twice (Annex F a) row 15: code repeated).
        /// </summary>
        /// <remarks>
        /// Since 2026-11-22 the same move is made for the throughput class (code 02, X.75 5.3.3: "No indication of throughput
        /// classes should be present in the user facility field of the call request"). X.25 7.3.2 and X.75 5.4.3.3 code it
        /// alike: bits 4-1 the class from the calling side, bits 8-5 from the called side. Until then it went to the next
        /// network in the user facility field (RetroIO BUGS.md, "X.75 utilities not mapped").
        /// </remarks>
        /// <param name="code">
        /// The facility code, which is also the utility code.
        /// </param>
        /// <param name="bits">
        /// The parameter bits X.75 assigns; the rest are cleared.
        /// </param>
        /// <param name="otherFacilities">
        /// The user facilities, without the element afterwards.
        /// </param>
        /// <param name="utilities">
        /// The utility field, with the element afterwards.
        /// </param>
        private static void MoveElementToUtilities(byte code, byte bits, ref byte[] otherFacilities, ref byte[] utilities)
        {
            int inUtilities = FindElement(utilities, code, out int utilityLength);
            if (inUtilities >= 0)
            {
                utilities = Cut(utilities, inUtilities, utilityLength);
            }

            int at = FindElement(otherFacilities, code, out int length);
            if (at < 0 || length != 2)
            {
                return;
            }

            byte parameter = (byte)(otherFacilities[at + 1] & bits);
            otherFacilities = Cut(otherFacilities, at, length);
            if (parameter != 0)
            {
                utilities = Prepend(code, parameter, utilities);
            }
        }

        /// <summary>
        /// For a Call Request arriving at an STE: gives the X.75 reverse charging / fast select utility to the switch as the
        /// X.25 facility 01 it means (same bits, X.25 7.3.6), in front of the other facilities. The utility field keeps it as
        /// well; the next STE replaces it (MoveElementToUtilities).
        /// </summary>
        /// <remarks>
        /// Since 2026-11-22 also used for the throughput class utility (code 02, X.75 5.3.3), given to the switch as X.25
        /// facility 02.
        /// </remarks>
        private static byte[] ElementFromUtilities(byte code, byte bits, byte[] utilities, byte[] otherFacilities)
        {
            int at = FindElement(utilities, code, out int length);
            if (at < 0 || length != 2 || FindElement(otherFacilities, code, out _) >= 0)
            {
                return otherFacilities;
            }

            return Prepend(code, (byte)(utilities[at + 1] & bits), otherFacilities);
        }

        private static byte[] WithUtilityFlowControl(byte[] utilities, byte windowFromCalled, byte windowFromCalling, int packetFromCalled, int packetFromCalling)
        {
            if (X75Utilities.TryDecode(utilities, out X75Utilities u) != X75UtilityError.None)
            {
                throw new InvalidOperationException("the X.75 utility field given for this call does not decode");
            }

            u.HasWindowSize = true;
            u.WindowFromCalled = windowFromCalled;
            u.WindowFromCalling = windowFromCalling;
            u.HasPacketSize = true;
            u.PacketSizeLog2FromCalled = (byte)System.Numerics.BitOperations.Log2((uint)packetFromCalled);
            u.PacketSizeLog2FromCalling = (byte)System.Numerics.BitOperations.Log2((uint)packetFromCalling);
            return u.Encode();
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
            bool windowMoved = info.PR != vc.AckedSeq;
            if (!TryAcknowledge(vc, info.PR))
            {
                ResetForProcedureError(vc, X25Diagnostic.InvalidPR);
                return;
            }

            if (windowMoved)
            {
                DrainPending(vc);
                RaiseWindowOpened(lci);
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

            // 4.3.3: the Q bit must not change inside a complete packet sequence. Annex C lets a
            // network reset on that, and this engine does (diagnostic 83), before the packet is
            // acknowledged or delivered: a sequence whose interpretation changes half-way is not
            // data anyone can use.
            if (vc.Reassembly != null && vc.ReassemblyQBit != info.QBit)
            {
                vc.ClearSequences();
                ResetForProcedureError(vc, X25Diagnostic.InconsistentQBit);
                return;
            }

            vc.RecvSeq = (byte)((info.PS + 1) & 0x07);
            vc.DataPacketsReceived++;

            byte[] payload = new byte[payloadLen];
            if (payloadLen > 0)
            {
                Array.Copy(pkt, header, payload, 0, payloadLen);
            }

            RaiseDataReceived(lci, payload, payloadLen, info.QBit, info.MBit, info.DBit);
            Reassemble(vc, payload, info.QBit, info.MBit, info.DBit);

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

            // X.75 Figure 8: between networks the clear confirmation is the three header octets only.
            byte[]? confirmationFacilities = _config.IsDte || _config.IsSte ? null : ClearConfirmationFacilities?.Invoke(lci);
            Send(X25PacketBuilder.CreateClearConfirmation(lci, confirmationFacilities));
            ToReady(vc);
            vc.CallRequestHeld = false;

            // Kept for whoever is told of the clear (a switch passes it to the other leg, PLAN B1).
            vc.ClearFacilities = info.Facilities ?? System.Array.Empty<byte>();

            // And the utility field between networks: a refusal carries the transit list of the networks past this one
            // (X.75 5.3.1), which a switch passes back to the caller's side. Kept since 2026-11-22.
            vc.ClearUtilities = info.NetworkUtilities ?? System.Array.Empty<byte>();
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

                // A restart confirmation in r1 (nothing to confirm) is ERROR (r3) # 17 for the network side:
                // X.25 Table C-2 for a DCE ("restart indication", local procedure error) and X.75 Table C.2 for an
                // STE ("restarting with network congestion cause", 00000011 in X.75 Table 25). Added 2026-11-22; until
                // then it was counted and dropped on every side. X.25 gives a DTE no table, so a DTE still drops it.
                if (!_config.IsDte)
                {
                    Restart(_config.IsSte ? SteRestartingNetworkCongestion : X25Cause.RestartingLocalProcedureError,
                        X25Diagnostic.UnexpectedRestartConfirmation);
                }

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

            bool windowMoved = pr != vc.AckedSeq;
            bool wasBusy = vc.PeerBusy;
            if (!TryAcknowledge(vc, pr))
            {
                ResetForProcedureError(vc, X25Diagnostic.InvalidPR);
                return;
            }

            vc.PeerBusy = peerBusy;
            if (windowMoved || (wasBusy && !peerBusy))
            {
                // Queued segments go first, so the window they take is not offered to a waiter.
                DrainPending(vc);
                RaiseWindowOpened(lci);
            }
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
            // Between networks: Annex C-4 and C-5 reset "with network congestion cause" (00000111, X.75 Table 15).
            byte cause = _config.IsSte ? SteResettingNetworkCongestion
                : _config.IsDte ? (byte)0x00 : X25Cause.ResettingLocalProcedureError;
            vc.State = X25VcState.ResetRequest;
            vc.Cause = cause;
            vc.Diagnostic = diagnostic;
            vc.Retries = 0;
            vc.TimerExpiryMs = Now() + _config.ResetRequestTimeoutMs;
            ProcedureErrors++;
            Send(X25PacketBuilder.CreateResetRequest(vc.Lci, cause, diagnostic));
        }

        /// <summary>
        /// Ends one circuit because the transport that carried it is gone, without sending anything.
        /// </summary>
        /// <param name="lci">
        /// The logical channel of the circuit.
        /// </param>
        /// <param name="cause">
        /// The clearing cause reported to the application.
        /// </param>
        /// <param name="diagnostic">
        /// The diagnostic reported to the application.
        /// </param>
        /// <returns>
        /// True if the circuit existed and was live; false if there was nothing to end.
        /// </returns>
        /// <remarks>
        /// A restart (<see cref="FreeSwitchedCircuits"/>) ends every circuit on the interface. This ends
        /// one, for a transport that has a connection for each circuit (RFC 1613 section 5): when one
        /// connection drops, the other circuits on the interface are still up and must not be touched.
        /// Nothing is written, because there is nobody to write to. A switched circuit is cleared and
        /// the application told once; a permanent circuit is reset, as a restart would reset it.
        /// </remarks>
        public bool AbortCircuit(ushort lci, byte cause, byte diagnostic)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                return false;
            }

            if (vc.Kind == X25VcKind.Svc)
            {
                bool wasLive = vc.State != X25VcState.Ready;
                ToReady(vc);
                if (wasLive)
                {
                    ReportCleared(vc, cause, diagnostic);
                }

                return wasLive;
            }

            vc.State = X25VcState.PvcReady;
            vc.TimerExpiryMs = 0;
            vc.OutOfOrder = false;
            vc.ResetFlow();
            RaiseReset(vc.Lci, cause, diagnostic);
            return true;
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

            // X.75 3.1.4: between networks the direction is "agreed bilaterally", so it is the port's setting.
            bool fromHighest = _config.IsSte ? _config.SearchFromHighest : _config.IsDte;
            if (fromHighest)
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
