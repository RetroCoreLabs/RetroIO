//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Threading;
using RetroIO.Protocols.X25.PacketLayer;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Switch
{
    /// <summary>
    /// The switching engine: it watches every attached port, routes incoming calls to an egress port,
    /// stitches the two circuits together, forwards data in both directions, and tears the pair down on
    /// clear — maintaining counters, accounts, and Call Detail Records as it goes.
    /// </summary>
    public sealed class SwitchCore
    {
        /// <summary>
        /// Clear cause: call cleared by the originating DTE / normal teardown.
        /// </summary>
        public const byte CauseDteOriginated = 0x00;

        /// <summary>
        /// Clear cause: destination address is not obtainable (no route).
        /// </summary>
        public const byte CauseNotObtainable = 0x0D;

        /// <summary>
        /// Clear cause: the circuit is out of order (X.25 Table 5-7).
        /// </summary>
        public const byte CauseOutOfOrder = 0x09;

        /// <summary>
        /// Clearing cause "Invalid facility request" (X.25 Table 5-7), used for an NUI no account has (Annex C row 15).
        /// </summary>
        public const byte CauseInvalidFacilityRequest = 0x03;

        /// <summary>
        /// Clearing cause "Local procedure error" (X.25 Table 5-7), used for a missing NUI a port requires (Annex C row 16).
        /// </summary>
        public const byte CauseLocalProcedureError = 0x13;

        /// <summary>
        /// Diagnostic 84, "NUI problem" (X.25 Annex E).
        /// </summary>
        public const byte DiagnosticNuiProblem = 84;

        /// <summary>
        /// Reset cause: network congestion (X.25 Table 5-8). Sent to both legs when a switch has
        /// to give up on what it was holding for a circuit.
        /// </summary>
        /// <remarks>
        /// Table 5-8 bits 0000 0111 = 0x07. Until 2026-11-16 this was 0x05, the CLEARING cause for congestion (Table 5-7);
        /// in a Reset 0x05 means "Local procedure error", so the DTE was told the wrong reason.
        /// </remarks>
        public const byte ResetCauseNetworkCongestion = 0x07;

        /// <summary>
        /// Reset cause "Out of order" (X.25 Table 5-8, PVCs only): sent to the far end of each PVC when an interface goes
        /// out of order (4.6.3).
        /// </summary>
        public const byte ResetCauseOutOfOrder = 0x01;

        /// <summary>
        /// Reset cause "Remote DTE operational" (X.25 Table 5-8, PVCs only): sent to the far end of each PVC when the
        /// interface is back (4.6.5).
        /// </summary>
        public const byte ResetCauseRemoteDteOperational = 0x09;

        /// <summary>
        /// How many packets a switch will hold for one direction of one circuit before it treats
        /// the leg as beyond help.
        /// </summary>
        /// <remarks>
        /// It should never be reached. The leg that is sending gets RNR as soon as the first packet
        /// is held (X.25 4.4.2), so at most one window of packets can already be in flight behind
        /// it. Reaching this depth means the RNR was ignored, and the answer is a reset rather than
        /// growing the queue: a switch that buffers without limit turns one stuck circuit into a
        /// memory problem for every circuit.
        /// </remarks>
        public const int MaxQueuedPacketsPerDirection = 64;

        private readonly SwitchNode _node;

        /// <summary>
        /// Raised when a routed circuit is established.
        /// </summary>
        public event CallSetupHandler? CallSetup;

        /// <summary>
        /// Raised when a circuit is cleared.
        /// </summary>
        public event CallClearHandler? CallClear;

        /// <summary>
        /// Raised when a torn-down circuit produces a Call Detail Record.
        /// </summary>
        public event CdrHandler? CdrRecorded;

        /// <summary>
        /// The reserved management address. Incoming calls to this address are handed to
        /// <see cref="ManagementCall"/> instead of being routed. Null or empty disables it.
        /// </summary>
        public string? ManagementAddress { get; set; }

        /// <summary>
        /// Raised when a call arrives for <see cref="ManagementAddress"/>. The handler owns accepting the
        /// call and running the management session on it.
        /// </summary>
        public event PortIncomingCall? ManagementCall;

        /// <summary>
        /// Creates the engine over a switch node and wires all currently attached ports.
        /// </summary>
        /// <param name="node">
        /// The switch runtime state to operate on.
        /// </param>
        public SwitchCore(SwitchNode node)
        {
            _node = node ?? throw new ArgumentNullException(nameof(node));
            for (int i = 0; i < _node.Ports.Count; i++)
            {
                Wire(_node.Ports[i]);
            }

            JoinPvcs();

            // The periodic PVC record: checked once a second, only when there is a PVC to bill and a period set.
            if (_node.Config.Pvcs.Count > 0 && _node.Config.PvcRecordMinutes > 0)
            {
                _pvcRecordTimer = new Timer(_ => WritePvcRecordsDue(), null, 1000, 1000);
            }
        }

        /// <summary>Fires <see cref="WritePvcRecordsDue"/> once a second; held so it is not collected.</summary>
        private readonly Timer? _pvcRecordTimer;

        private readonly object _pvcRecordGate = new object();

        /// <summary>
        /// Creates every configured PVC on its two ports and joins them (X.25 4.2: no call set-up; both ends are in data
        /// transfer from the start). A PVC is marked by <see cref="VcStitch.HopCount"/> -1 and is never destroyed.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// A port cannot hold a PVC, or the PVC's account is not configured. Refused rather than skipped: a PVC that is
        /// silently not there is the failure nobody notices.
        /// </exception>
        private void JoinPvcs()
        {
            List<PvcConfig> pvcs = _node.Config.Pvcs;
            for (int i = 0; i < pvcs.Count; i++)
            {
                PvcConfig pvc = pvcs[i];
                ISwitchPort? a = _node.GetPort(pvc.SourcePort);
                ISwitchPort? b = _node.GetPort(pvc.DestPort);
                if (a == null || b == null || !a.AddPvc(pvc.SourceLci) || !b.AddPvc(pvc.DestLci))
                {
                    throw new InvalidOperationException("PVC " + pvc.SourcePort + "/" + pvc.SourceLci + " <-> "
                        + pvc.DestPort + "/" + pvc.DestLci + ": a port cannot hold a permanent virtual circuit");
                }

                SwitchAccount? account = null;
                if (pvc.Account.Length > 0)
                {
                    IReadOnlyList<SwitchAccount> accounts = _node.Accounts;
                    for (int k = 0; k < accounts.Count; k++)
                    {
                        if (accounts[k].Name == pvc.Account)
                        {
                            account = accounts[k];
                            break;
                        }
                    }

                    if (account == null)
                    {
                        throw new InvalidOperationException("PVC " + pvc.SourcePort + "/" + pvc.SourceLci
                            + ": account " + pvc.Account + " is not configured");
                    }
                }

                VcStitch stitch = _node.CreateStitch(a, pvc.SourceLci, b, pvc.DestLci);
                stitch.HopCount = -1;
                stitch.StartTimeMs = NowMs();
                stitch.ConnectedTimeMs = stitch.StartTimeMs;
                stitch.StartWallSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                stitch.PayingAccount = account;

                // An end on an XOT initiator port is carried only once the RFC 1613 6.5 setup has been answered 0x00;
                // the port starts that exchange now and repeats it when it fails. A responder end waits to be asked.
                StartXotSetup(a, pvc.SourceLci, pvc, stitch);
                StartXotSetup(b, pvc.DestLci, pvc, stitch);
            }
        }

        private static void StartXotSetup(ISwitchPort port, ushort lci, PvcConfig pvc, VcStitch stitch)
        {
            if (port is not XotSwitchPort xot)
            {
                return;
            }

            if (xot.Config.XotPvcInitiator)
            {
                xot.StartPvcSetup(lci, pvc.RemoteHost, pvc.RemoteInterface, pvc.RemoteLci);
                return;
            }

            // As responder, the destination of this PVC is the port its other end is on. It is "not up" (RFC 1613 6.5,
            // 0x14) while that port is out of order - the same mark X.25 4.6 resets the far end by. The XOT port itself
            // having no connection is normal before a setup and does not count.
            Func<ushort, bool>? before = xot.PvcDestinationUp;
            xot.PvcDestinationUp = asked =>
            {
                if (asked == lci)
                {
                    ISwitchPort? down = stitch.PvcDownPort;
                    return down == null || ReferenceEquals(down, xot);
                }

                return before == null || before(asked);
            };
        }

        // A permanent circuit (JoinPvcs): never cleared, only reset.
        private static bool IsPvc(VcStitch stitch)
        {
            return stitch.HopCount == -1;
        }

        /// <summary>
        /// Subscribes the engine to a port's events. Call this for any port attached after construction.
        /// </summary>
        /// <param name="port">
        /// The port to wire.
        /// </param>
        public void Wire(ISwitchPort port)
        {
            port.IncomingCall += OnIncomingCall;
            port.CallConnected += OnCallConnected;
            port.DataReceived += OnDataReceived;
            port.CallCleared += OnCallCleared;
            port.CircuitReset += OnCircuitReset;
            port.InterruptReceived += OnInterruptReceived;
            port.ReadyToSend += OnReadyToSend;
            port.StateChanged += OnPortStateChanged;

            // The charging answer to a DTE that clears its own call goes in the DCE Clear Confirmation, which the port's
            // packet layer builds; it asks here for the facility field (X.25 5.2.5, 6.22).
            port.ClearConfirmationFacilities = lci => ChargingForConfirmation(port, lci);
        }

        /// <summary>
        /// The clock the switch reads, in milliseconds: call start, connect and end times and so call duration. The
        /// default is <see cref="Environment.TickCount64"/>; a test sets its own so durations can be checked exactly.
        /// </summary>
        public Func<long> ClockMs { get; set; } = () => Environment.TickCount64;

        private long NowMs()
        {
            return ClockMs();
        }

        /// <summary>Facility code of the reverse charging element (X.25 Table 7-2, 7.3.6).</summary>
        private const byte ReverseChargingCode = 0x01;

        /// <summary>Facility code of the charging information request (X.25 Table 7-2, 7.3.8.1).</summary>
        private const byte ChargingRequestCode = 0x04;

        /// <summary>Facility code of the call duration answer (X.25 Table 7-2, 7.3.8.4).</summary>
        private const byte CallDurationCode = 0xC1;

        /// <summary>Facility code of the segment count answer (X.25 Table 7-2, 7.3.8.3).</summary>
        private const byte SegmentCountCode = 0xC2;

        /// <summary>Facility code of the monetary unit answer (X.25 Table 7-2, 7.3.8.2).</summary>
        private const byte MonetaryUnitCode = 0xC5;

        // A class A element whose parameter has bit 1 set: reverse charging requested (7.3.6), charging information
        // requested (7.3.8.1).
        private static bool FacilityBitOneSet(X25CallParameters parameters, byte code)
        {
            return parameters.TryGetFacility(code, out byte[] p) && p.Length == 1 && (p[0] & 0x01) != 0;
        }

        /// <summary>
        /// The charging answer for one end of a call, as a facility field (X.25 7.3.8.3 and 7.3.8.4, one tariff period).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Segment count, C2 08: four BCD octets of segments sent to the DTE, then four of segments received from it,
        /// lowest digit in bits 4 to 1 of the last octet. Call duration, C1 04: days, hours, minutes and seconds, one BCD
        /// octet each. One tariff period, because this switch has one tariff: which period goes where "is a national
        /// matter" (7.3.8.3 NOTE). Monetary unit is "for further study" (7.3.8.2) and is not given.
        /// </para>
        /// <para>
        /// A count too large for eight digits is given as 99999999, a duration of 100 days or more as 99 days 23:59:59:
        /// the field has no room for more, and the largest value it can say is closer than a wrapped one.
        /// </para>
        /// </remarks>
        private byte[] ChargingAnswer(VcStitch stitch, bool toCaller)
        {
            stitch.SegmentsForward.Close();
            stitch.SegmentsReverse.Close();

            ulong sentToDte = toCaller ? stitch.SegmentsReverse.Segments : stitch.SegmentsForward.Segments;
            ulong receivedFromDte = toCaller ? stitch.SegmentsForward.Segments : stitch.SegmentsReverse.Segments;

            long seconds = stitch.ConnectedTimeMs == 0 ? 0 : Math.Max(0, NowMs() - stitch.ConnectedTimeMs) / 1000;
            long days = seconds / 86400;
            if (days > 99)
            {
                days = 99;
                seconds = 99L * 86400 + 86399;
            }

            byte[] field = new byte[2 + 8 + 2 + 4];
            field[0] = SegmentCountCode;
            field[1] = 8;
            WriteBcd(field.AsSpan(2, 4), sentToDte);
            WriteBcd(field.AsSpan(6, 4), receivedFromDte);
            field[10] = CallDurationCode;
            field[11] = 4;
            WriteBcd(field.AsSpan(12, 1), (ulong)days);
            WriteBcd(field.AsSpan(13, 1), (ulong)(seconds / 3600 % 24));
            WriteBcd(field.AsSpan(14, 1), (ulong)(seconds / 60 % 60));
            WriteBcd(field.AsSpan(15, 1), (ulong)(seconds % 60));
            return field;
        }

        // value as BCD, two digits an octet, the lowest digit in bits 4 to 1 of the last octet; capped at all nines.
        private static void WriteBcd(Span<byte> into, ulong value)
        {
            ulong max = 1;
            for (int i = 0; i < into.Length * 2; i++)
            {
                max *= 10;
            }

            if (value >= max)
            {
                value = max - 1;
            }

            for (int i = into.Length - 1; i >= 0; i--)
            {
                byte low = (byte)(value % 10);
                value /= 10;
                byte high = (byte)(value % 10);
                value /= 10;
                into[i] = (byte)((high << 4) | low);
            }
        }

        /// <summary>
        /// The facility field for a Clear Indication to one end: what the other end's clear carried, with this switch's
        /// charging answer in place of any charging elements from further along when this end is owed one.
        /// </summary>
        /// <remarks>
        /// The charging answer belongs to the DCE of the DTE to be charged (6.22). A charging element that came from
        /// another switch was that switch's answer to its own DTE, not to this one, so it is dropped before this switch's
        /// own is added; with no answer owed here, the field goes on as it came (PLAN B1).
        /// </remarks>
        private byte[] ClearFacilitiesFor(VcStitch stitch, bool toCaller, byte[] passed)
        {
            bool owed = toCaller ? stitch.ChargingToCaller : stitch.ChargingToCallee;
            if (!owed)
            {
                return passed;
            }

            byte[] answer = ChargingAnswer(stitch, toCaller);
            byte[] kept = WithoutChargingAnswers(passed, out int keptLength);
            byte[] field = new byte[keptLength + answer.Length];
            Array.Copy(kept, field, keptLength);
            answer.CopyTo(field, keptLength);
            return field;
        }

        // The facility field without its C1, C2 and C5 elements before the first marker (X.25 7.1 class rule).
        private static byte[] WithoutChargingAnswers(byte[] field, out int length)
        {
            byte[] kept = new byte[field.Length];
            length = 0;
            int i = 0;
            while (i < field.Length)
            {
                byte code = field[i];
                if (code == 0x00)
                {
                    break;
                }

                int elementLength;
                switch (code >> 6)
                {
                    case 0: elementLength = 2; break;
                    case 1: elementLength = 3; break;
                    case 2: elementLength = 4; break;
                    default: elementLength = i + 1 < field.Length ? 2 + field[i + 1] : field.Length - i; break;
                }

                if (i + elementLength > field.Length)
                {
                    elementLength = field.Length - i;
                }

                if (code != CallDurationCode && code != SegmentCountCode && code != MonetaryUnitCode)
                {
                    Array.Copy(field, i, kept, length, elementLength);
                    length += elementLength;
                }

                i += elementLength;
            }

            Array.Copy(field, i, kept, length, field.Length - i);
            length += field.Length - i;
            return kept;
        }

        // Asked by a port's packet layer when its DTE clears: the charging answer, if that DTE is owed one.
        private byte[]? ChargingForConfirmation(ISwitchPort port, ushort lci)
        {
            VcStitch? stitch = _node.FindIngress(port, lci);
            if (stitch != null)
            {
                return stitch.ChargingToCaller ? ChargingAnswer(stitch, toCaller: true) : null;
            }

            stitch = _node.FindEgress(port, lci);
            if (stitch != null)
            {
                return stitch.ChargingToCallee ? ChargingAnswer(stitch, toCaller: false) : null;
            }

            return null;
        }

        private void OnIncomingCall(ISwitchPort inPort, ushort inLci, string calledAddress, string callingAddress, byte[] callUserData)
        {
            inPort.Counters.IncrementCallsIn();

            // Calls to the reserved management address are handed to the management surface, not routed.
            if (!string.IsNullOrEmpty(ManagementAddress) && calledAddress == ManagementAddress)
            {
                ManagementCall?.Invoke(inPort, inLci, calledAddress, callingAddress, callUserData);
                return;
            }

            // Who pays (X.25 6.21). Annex C, Table C.1: an NUI no account has is "Invalid facility request", diagnostic
            // 84 (row 15); none on a port that expects one is "Local procedure error", diagnostic 84 (row 16). Checked
            // before routing, so a refused call is never offered to the far end.
            bool haveParameters = inPort.TryGetCallParameters(inLci, out X25CallParameters asked);
            SwitchAccount? paying;
            string payingNui = string.Empty;
            if (haveParameters && asked.TryGetNuiSelection(out byte[] nui))
            {
                paying = FindAccountByNui(nui);
                if (paying == null)
                {
                    inPort.ClearCall(inLci, CauseInvalidFacilityRequest, DiagnosticNuiProblem);
                    return;
                }

                payingNui = paying.Nui;
            }
            else if (inPort.Config.DefaultNui.Length > 0)
            {
                // The loader has already refused a default that names no account.
                paying = FindAccountByNui(System.Text.Encoding.ASCII.GetBytes(inPort.Config.DefaultNui));
                payingNui = inPort.Config.DefaultNui;
            }
            else if (inPort.Config.NuiRequired)
            {
                inPort.ClearCall(inLci, CauseLocalProcedureError, DiagnosticNuiProblem);
                return;
            }
            else
            {
                paying = FindAccount(callingAddress);
            }

            // Resolve an egress port for the dialled address; never route back to the ingress port.
            if (!_node.Config.Routing.TryLookup(calledAddress, inPort.Id, out int outPortId))
            {
                inPort.ClearCall(inLci, CauseNotObtainable, 0);
                return;
            }

            ISwitchPort? outPort = _node.GetPort(outPortId);
            if (outPort == null)
            {
                inPort.ClearCall(inLci, CauseNotObtainable, 0);
                return;
            }

            // Place the onward call; a zero LCI means the egress port could not initiate it.
            // X.25 6.9 and 6.12: the window and packet size of a call are negotiated end to end. The onward Call Request
            // asks for what the caller asked for (the directions swapped: what the caller sends, the callee receives),
            // limited to the largest packet the egress port is set up for. Asking for the switch's own defaults here made
            // the caller think it had the sizes it asked for while the callee ran with others, and the first big packet
            // reset the call.
            ushort outLci;
            if (haveParameters)
            {
                // The NUI says who pays this network; it never goes on to the called DTE (X.25 6.21).
                X25CallParameters toAsk = asked.Swapped().WithPacketSizeAtMost(outPort.Config.MaxPacketSize).WithoutNuiSelection()
                    .WithoutChargingRequest();   // Table 7-2: not in an Incoming Call; this DCE answers it (6.22)
                outLci = outPort.PlaceCall(calledAddress, callingAddress, callUserData, toAsk);
            }
            else
            {
                outLci = outPort.PlaceCall(calledAddress, callingAddress, callUserData);
            }

            if (outLci == 0)
            {
                inPort.ClearCall(inLci, CauseNotObtainable, 0);
                return;
            }

            outPort.Counters.IncrementCallsOut();

            VcStitch stitch = _node.CreateStitch(inPort, inLci, outPort, outLci);
            stitch.CallingAddress = callingAddress;
            stitch.CalledAddress = calledAddress;
            stitch.StartTimeMs = NowMs();
            stitch.StartWallSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            stitch.HopCount = 0;
            stitch.PayingAccount = paying;
            stitch.PayingNui = payingNui;

            // Who is owed a charging answer (6.22): the DTE to be charged, if it asked (7.3.8.1) and its DCE is this
            // switch, which is so on an access port. The caller pays unless it asked for reverse charging (6.18); then
            // the called DTE pays, and whether it asked is known only from its Call Accepted (OnCallConnected).
            // A port that subscribes to the facility (6.22, "agreed for a period of time") gets the answer without asking.
            bool callerAsked = false;
            if (haveParameters)
            {
                stitch.ReverseCharging = FacilityBitOneSet(asked, ReverseChargingCode);
                callerAsked = FacilityBitOneSet(asked, ChargingRequestCode);
            }
            stitch.ChargingToCaller = !stitch.ReverseCharging
                && inPort.Config.Type == PortType.Access
                && (callerAsked || inPort.Config.ChargingInfo);

            // The incoming call is NOT accepted here. X.25 4.1: a Call Accepted says the circuit is
            // established end to end, and at this point only the onward Call Request has gone out.
            // Confirming now tells the caller the call is up while the far end may still refuse it,
            // and anything it sends in between has nowhere to go. The accept happens in
            // OnCallConnected, when the far end has answered.
            stitch.CalledAccount = FindAccount(calledAddress);
            AccountForCallSetup(stitch);
            CallSetup?.Invoke(callingAddress, calledAddress, inPort.Id, outPort.Id);
        }

        private void OnCallConnected(ISwitchPort port, ushort lci)
        {
            // The onward leg was accepted by the far end, so now the incoming call can be
            // confirmed. The stitch was created when the call was routed.
            VcStitch? stitch = _node.FindEgress(port, lci);
            if (stitch == null)
            {
                return;
            }

            // The called DTE may name a NUI in its Call Accepted (X.25 6.21.1, 6.21.3). "If the DCE determines that the
            // network user identifier is invalid ... it will clear the call as described in Annex C": Annex E, b) Call
            // accepted packet, row 13, "Invalid network user identifier" - Invalid facility request, diagnostic 84. The call
            // never connected, so it is charged nothing. Only an access port is the called DTE's own DCE; on a trunk the NUI
            // belongs to the next switch.
            if (stitch.OutPort.Config.Type == PortType.Access
                && stitch.OutPort.TryGetCallParameters(stitch.OutLci, out X25CallParameters answered)
                && answered.TryGetNuiSelection(out byte[] calleeNui))
            {
                SwitchAccount? named = FindAccountByNui(calleeNui);
                if (named == null)
                {
                    ClearStitchBothWays(stitch, CauseInvalidFacilityRequest, DiagnosticNuiProblem);
                    return;
                }

                // A known one says who the called DTE is, for billing (6.21.1: information "for billing ... on a per
                // call basis"), as the caller's NUI does for the caller: it replaces the account found by the called
                // address. No data has crossed yet (none can before the Call Connected), so only "calls up now" moves.
                if (!ReferenceEquals(named, stitch.CalledAccount))
                {
                    if (stitch.CalledAccount != null)
                    {
                        stitch.CalledAccount.CallsActive--;
                    }

                    named.CallsActive++;
                    stitch.CalledAccount = named;
                }
            }

            stitch.Active = true;
            stitch.ConnectedTimeMs = NowMs();

            // The Call Accepted to the caller states what the far end agreed to in its Call Connected (X.25 7.2.2.1), with
            // the directions swapped back.
            if (stitch.OutPort.TryGetCallParameters(stitch.OutLci, out X25CallParameters agreed))
            {
                // With reverse charging the called DTE pays (6.18), and it may ask for the charging answer in its Call
                // Accepted (6.22), or subscribe to it on its port.
                stitch.ChargingToCallee = stitch.ReverseCharging
                    && stitch.OutPort.Config.Type == PortType.Access
                    && (FacilityBitOneSet(agreed, ChargingRequestCode) || stitch.OutPort.Config.ChargingInfo);

                // The called DTE's NUI (6.21.3) does not go back to the caller either (6.21).
                // Nor the called DTE's charging request: Table 7-2 does not allow it in a Call Connected.
                stitch.InPort.AcceptCall(stitch.InLci, agreed.Swapped().WithoutNuiSelection().WithoutChargingRequest());
            }
            else
            {
                stitch.InPort.AcceptCall(stitch.InLci);
            }
        }

        private void OnDataReceived(ISwitchPort port, ushort lci, byte[] data, int length, bool qBit, bool mBit, bool dBit)
        {
            port.Counters.IncrementPacketsIn();

            // Forward ingress -> egress.
            VcStitch? ingress = _node.FindIngress(port, lci);
            if (ingress != null)
            {
                if (PvcOutOfOrder(ingress, port, lci))
                {
                    return;
                }

                if (ingress.IngressFullSize == 0)
                {
                    ingress.IngressFullSize = FullSize(port, lci);
                }

                // Counted as it arrives: the segments the network accepted from the calling DTE (D.12, 7.3.8.3).
                lock (ingress)
                {
                    ingress.SegmentsForward.Add(length, mBit, dBit, ingress.IngressFullSize);
                    CreditTraffic(ingress.PayingAccount, ingress.CalledAccount, length, ingress.SegmentsForward.TakeNew());
                }

                Forward(ingress, toEgress: true, data, length, qBit, mBit, dBit);
                return;
            }

            // Forward egress -> ingress.
            VcStitch? egress = _node.FindEgress(port, lci);
            if (egress != null)
            {
                if (PvcOutOfOrder(egress, port, lci))
                {
                    return;
                }

                if (egress.EgressFullSize == 0)
                {
                    egress.EgressFullSize = FullSize(port, lci);
                }

                lock (egress)
                {
                    egress.SegmentsReverse.Add(length, mBit, dBit, egress.EgressFullSize);
                    CreditTraffic(egress.CalledAccount, egress.PayingAccount, length, egress.SegmentsReverse.TakeNew());
                }

                Forward(egress, toEgress: false, data, length, qBit, mBit, dBit);
            }
        }

        /// <summary>
        /// Credits one data packet to the accounts as it passes, so the read-out moves while a call is up (the same as
        /// x25emu): the sender's account sent it, the receiver's received it. Segments are credited when their message
        /// ends, since a part-used segment is not known until then (D.12).
        /// </summary>
        private static void CreditTraffic(SwitchAccount? from, SwitchAccount? to, int length, ulong segments)
        {
            if (from != null)
            {
                from.PacketsSent++;
                from.BytesSent += (ulong)length;
                from.SegmentsSent += segments;
            }

            if (to != null)
            {
                to.PacketsReceived++;
                to.BytesReceived += (ulong)length;
                to.SegmentsReceived += segments;
            }
        }

        // The maximum user data field of a packet arriving on this leg: its agreed receive size (X.25 6.12), the same
        // size the packet layer uses to tell a category A packet (4.3.5).
        private static int FullSize(ISwitchPort port, ushort lci)
        {
            // The channel carried the packet, so it is known. The X.25 default (6.12, 128) is for a channel cleared between
            // the packet arriving and this read; the call is ending then, and its count is closed at the clear anyway.
            return port.TryGetCallParameters(lci, out X25CallParameters p) ? p.PacketSizeIn : RetroIO.Protocols.X25.Models.X25Facilities.DefaultPacketSize;
        }

        /// <summary>
        /// Passes one packet across a stitch, holding it if the far leg will not take it yet.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Order is what matters here. Anything already queued for this direction has to go first,
        /// so a packet is only offered to the port when the queue is empty; otherwise it goes on
        /// the end. X.25 gives no way to say "this one is out of order", and a receiver that sees a
        /// gap in P(S) resets the circuit (4.4.1.3).
        /// </para>
        /// <para>
        /// A leg that refuses for flow control is not in trouble: the packet waits, and the leg the
        /// data came from is told RNR so the sender stops (4.4.2). A leg that fails outright is,
        /// and the whole stitch is cleared rather than left holding data for a circuit that will
        /// never take it.
        /// </para>
        /// </remarks>
        /// <param name="stitch">
        /// The stitch the packet is crossing.
        /// </param>
        /// <param name="toEgress">
        /// True when the packet came in on the ingress leg and goes out of the egress leg.
        /// </param>
        /// <param name="data">
        /// Buffer holding the packet's user data.
        /// </param>
        /// <param name="length">
        /// How many bytes of <paramref name="data"/> are the packet.
        /// </param>
        /// <param name="qBit">
        /// The qualifier bit.
        /// </param>
        /// <param name="mBit">
        /// The more-data bit.
        /// </param>
        /// <param name="dBit">
        /// The delivery-confirmation bit.
        /// </param>
        private void Forward(VcStitch stitch, bool toEgress, byte[] data, int length, bool qBit, bool mBit, bool dBit)
        {
            Queue<QueuedPacket> queue = toEgress ? stitch.ForwardQueue : stitch.ReverseQueue;

            if (queue.Count == 0)
            {
                PortSendResult result = SendAcross(stitch, toEgress, data.AsSpan(0, length), qBit, mBit, dBit);
                if (result == PortSendResult.Sent)
                {
                    CountForwarded(stitch, toEgress, length);
                    return;
                }

                if (result == PortSendResult.Failed)
                {
                    ClearStitchBothWays(stitch, CauseOutOfOrder, 0);
                    return;
                }
            }

            if (queue.Count >= MaxQueuedPacketsPerDirection)
            {
                // The sender did not stop when it was told to. Reset both legs: the sequence
                // numbers go back to zero on each and what we are holding is discarded, which is
                // exactly what a reset means (X.25 4.4).
                ResetStitchBothWays(stitch, ResetCauseNetworkCongestion, 0);
                return;
            }

            byte[] held = new byte[length];
            Buffer.BlockCopy(data, 0, held, 0, length);
            queue.Enqueue(new QueuedPacket(held, qBit, mBit, dBit));
            HoldSender(stitch, toEgress, busy: true);
        }

        /// <summary>
        /// Hands a packet to the leg it is going out of.
        /// </summary>
        /// <param name="stitch">
        /// The stitch being crossed.
        /// </param>
        /// <param name="toEgress">
        /// True to send out of the egress leg.
        /// </param>
        /// <param name="data">
        /// The packet's user data.
        /// </param>
        /// <param name="qBit">
        /// The qualifier bit.
        /// </param>
        /// <param name="mBit">
        /// The more-data bit.
        /// </param>
        /// <param name="dBit">
        /// The delivery-confirmation bit.
        /// </param>
        /// <returns>
        /// What the port said.
        /// </returns>
        private static PortSendResult SendAcross(VcStitch stitch, bool toEgress, ReadOnlySpan<byte> data, bool qBit, bool mBit, bool dBit)
        {
            return toEgress
                ? stitch.OutPort.SendData(stitch.OutLci, data, qBit, mBit, dBit)
                : stitch.InPort.SendData(stitch.InLci, data, qBit, mBit, dBit);
        }

        /// <summary>
        /// Counts a packet that actually left, on the port it left by and on the stitch.
        /// </summary>
        /// <remarks>
        /// Only packets that left are counted. Counting on the attempt is how a switch reports
        /// traffic it dropped, and it hides the drop from whoever reads the counters.
        /// </remarks>
        /// <param name="stitch">
        /// The stitch the packet crossed.
        /// </param>
        /// <param name="toEgress">
        /// True when it went out of the egress leg.
        /// </param>
        /// <param name="length">
        /// How many bytes it carried.
        /// </param>
        private static void CountForwarded(VcStitch stitch, bool toEgress, int length)
        {
            // Under the stitch's lock, as the PVC period line reads and zeroes these (EndPvcPeriod). Nothing in here
            // calls a port, so the lock cannot wait on a port's lock.
            lock (stitch)
            {
                if (toEgress)
                {
                    stitch.OutPort.Counters.IncrementPacketsOut();
                    stitch.PacketsForward++;
                    stitch.BytesForward += (ulong)length;
                }
                else
                {
                    stitch.InPort.Counters.IncrementPacketsOut();
                    stitch.PacketsReverse++;
                    stitch.BytesReverse += (ulong)length;
                }
            }
        }

        /// <summary>
        /// Tells the leg the data is coming from whether it may keep sending (X.25 4.4.2).
        /// </summary>
        /// <param name="stitch">
        /// The stitch being crossed.
        /// </param>
        /// <param name="toEgress">
        /// True when the data is flowing toward the egress leg, so the ingress leg is the sender.
        /// </param>
        /// <param name="busy">
        /// True to send RNR, false to send RR.
        /// </param>
        private static void HoldSender(VcStitch stitch, bool toEgress, bool busy)
        {
            if (toEgress)
            {
                if (stitch.IngressHeldBusy == busy)
                {
                    return;
                }

                stitch.IngressHeldBusy = busy;
                stitch.InPort.SetLocalBusy(stitch.InLci, busy);
                return;
            }

            if (stitch.EgressHeldBusy == busy)
            {
                return;
            }

            stitch.EgressHeldBusy = busy;
            stitch.OutPort.SetLocalBusy(stitch.OutLci, busy);
        }

        private void OnReadyToSend(ISwitchPort port, ushort lci)
        {
            // The leg that can send again is the one data goes OUT of, so a stitch whose egress leg
            // just opened drains its forward queue, and one whose ingress leg opened drains its
            // reverse queue.
            VcStitch? stitch = _node.FindEgress(port, lci);
            if (stitch != null)
            {
                Drain(stitch, toEgress: true);
                return;
            }

            stitch = _node.FindIngress(port, lci);
            if (stitch != null)
            {
                Drain(stitch, toEgress: false);
            }
        }

        /// <summary>
        /// Sends what is queued for one direction, stopping at the first packet the leg will not
        /// take. When the queue empties, the sender is released from RNR.
        /// </summary>
        /// <param name="stitch">
        /// The stitch to drain.
        /// </param>
        /// <param name="toEgress">
        /// True to drain the forward queue.
        /// </param>
        private void Drain(VcStitch stitch, bool toEgress)
        {
            Queue<QueuedPacket> queue = toEgress ? stitch.ForwardQueue : stitch.ReverseQueue;

            while (queue.Count > 0)
            {
                QueuedPacket packet = queue.Peek();
                PortSendResult result = SendAcross(stitch, toEgress, packet.Data, packet.QBit, packet.MBit, packet.DBit);

                if (result == PortSendResult.WindowFull)
                {
                    return;   // still shut; the port will say so again
                }

                if (result == PortSendResult.Failed)
                {
                    ClearStitchBothWays(stitch, CauseOutOfOrder, 0);
                    return;
                }

                queue.Dequeue();
                CountForwarded(stitch, toEgress, packet.Data.Length);
            }

            HoldSender(stitch, toEgress, busy: false);
        }

        private void OnPortStateChanged(ISwitchPort port, bool active)
        {
            // PVCs first: X.25 4.6 resets them and never clears them, and their join outlives the outage.
            PvcPortStateChanged(port, active);

            if (active)
            {
                return;
            }

            // The port is down, so every circuit through it is gone. X.25 4.1.2: a restart clears
            // every virtual call on the interface it happens on - and the far leg of each of those
            // calls is on another interface, which knows nothing about it unless the switch says
            // so. Left alone, the far DTE holds a circuit open against a switch that has forgotten
            // it, until something times out.
            List<VcStitch> stitches = _node.SnapshotStitches();
            for (int i = 0; i < stitches.Count; i++)
            {
                VcStitch stitch = stitches[i];
                bool isIngress = ReferenceEquals(stitch.InPort, port);
                bool isEgress = ReferenceEquals(stitch.OutPort, port);
                if ((!isIngress && !isEgress) || IsPvc(stitch))
                {
                    continue;
                }

                // Claim the stitch first, as OnCallCleared does: the far leg's clear confirmation
                // raises a clear event that must find no stitch to act on.
                if (!_node.DestroyStitch(stitch))
                {
                    continue;
                }

                // Only the leg that is still up is told; sending to the port that just went down
                // would go nowhere.
                stitch.ForwardQueue.Clear();
                stitch.ReverseQueue.Clear();

                ISwitchPort farPort = isIngress ? stitch.OutPort : stitch.InPort;
                ushort farLci = isIngress ? stitch.OutLci : stitch.InLci;
                farPort.ClearCall(farLci, CauseOutOfOrder, 0,
                    ClearFacilitiesFor(stitch, toCaller: !isIngress, Array.Empty<byte>()));

                WriteCdr(stitch, CauseOutOfOrder, 0);
                AccountForCallClear(stitch);

                CallClear?.Invoke(port.Id, isIngress ? stitch.InLci : stitch.OutLci, CauseOutOfOrder, 0);
            }
        }

        /// <summary>
        /// X.25 4.6 for the PVCs through a port whose interface went out of order or came back.
        /// </summary>
        /// <remarks>
        /// 4.6.3: when an out of order condition is detected the DCE sends the remote end "a reset with the cause 'Out of
        /// order' for each permanent virtual circuit". 4.6.5: when it is recovered, "a reset with the cause 'Remote DTE
        /// operational' will be transmitted to the remote end of each permanent virtual circuit". The local end gets its
        /// restart from the port's own packet layer (4.6.5 (1)), which resets the PVC there (4.5).
        /// </remarks>
        private void PvcPortStateChanged(ISwitchPort port, bool active)
        {
            List<VcStitch> stitches = _node.SnapshotStitches();
            for (int i = 0; i < stitches.Count; i++)
            {
                VcStitch stitch = stitches[i];
                if (!IsPvc(stitch))
                {
                    continue;
                }

                bool isIngress = ReferenceEquals(stitch.InPort, port);
                bool isEgress = ReferenceEquals(stitch.OutPort, port);
                if (!isIngress && !isEgress)
                {
                    continue;
                }

                ISwitchPort farPort = isIngress ? stitch.OutPort : stitch.InPort;
                ushort farLci = isIngress ? stitch.OutLci : stitch.InLci;
                if (!active)
                {
                    if (stitch.PvcDownPort != null)
                    {
                        continue;
                    }

                    stitch.PvcDownPort = port;
                    DiscardQueues(stitch);
                    EndPvcPeriod(stitch, ResetCauseOutOfOrder, 0, true);
                    if (farPort.Active)
                    {
                        farPort.ResetCircuit(farLci, ResetCauseOutOfOrder, 0);
                    }
                }
                else if (ReferenceEquals(stitch.PvcDownPort, port))
                {
                    // Only a recovery: the first time a port comes up nothing was out of order.
                    stitch.PvcDownPort = null;
                    if (farPort.Active)
                    {
                        farPort.ResetCircuit(farLci, ResetCauseRemoteDteOperational, 0);
                    }
                }
            }
        }

        private void OnCircuitReset(ISwitchPort port, ushort lci, byte cause, byte diagnostic)
        {
            VcStitch? stitch = _node.FindIngress(port, lci);
            bool fromIngress = stitch != null;
            if (stitch == null)
            {
                stitch = _node.FindEgress(port, lci);
            }

            if (stitch == null)
            {
                return;
            }

            // X.25 4.4: the reset applies to the whole circuit, so it goes on to the other leg, and
            // everything this switch was holding for it is discarded rather than sent afterwards
            // against sequence numbers that have gone back to zero.
            DiscardQueues(stitch);
            if (IsPvc(stitch))
            {
                // A reset ends the PVC's billing period (x25emu writes the line on reset too).
                EndPvcPeriod(stitch, cause, diagnostic, true);
            }

            if (fromIngress)
            {
                stitch.OutPort.ResetCircuit(stitch.OutLci, cause, diagnostic);
            }
            else
            {
                stitch.InPort.ResetCircuit(stitch.InLci, cause, diagnostic);
            }
        }

        /// <summary>
        /// X.25 4.6.4 (2): "for any data or interrupt packet received from the remote DTE on a permanent virtual circuit,
        /// the DCE will reset the permanent virtual circuit with the cause 'Out of order'" while the other interface is out
        /// of order. The packet goes no further.
        /// </summary>
        /// <returns>
        /// True when the packet was answered with the reset and must be dropped.
        /// </returns>
        private static bool PvcOutOfOrder(VcStitch stitch, ISwitchPort from, ushort lci)
        {
            if (!IsPvc(stitch) || stitch.PvcDownPort == null || ReferenceEquals(stitch.PvcDownPort, from))
            {
                return false;
            }

            from.ResetCircuit(lci, ResetCauseOutOfOrder, 0);
            return true;
        }

        private void OnInterruptReceived(ISwitchPort port, ushort lci, byte[] userData)
        {
            VcStitch? stitch = _node.FindIngress(port, lci);
            if (stitch == null)
            {
                stitch = _node.FindEgress(port, lci);
            }

            if (stitch != null && PvcOutOfOrder(stitch, port, lci))
            {
                return;
            }

            stitch = _node.FindIngress(port, lci);
            if (stitch != null)
            {
                // X.25 4.3.2: an interrupt passes the data it overtakes, so it is not queued behind
                // what the forward queue is holding.
                stitch.OutPort.SendInterrupt(stitch.OutLci, userData);
                return;
            }

            stitch = _node.FindEgress(port, lci);
            if (stitch != null)
            {
                stitch.InPort.SendInterrupt(stitch.InLci, userData);
            }
        }

        /// <summary>
        /// Throws away everything held for a circuit and lets both senders go again.
        /// </summary>
        /// <param name="stitch">
        /// The stitch to empty.
        /// </param>
        private static void DiscardQueues(VcStitch stitch)
        {
            stitch.ForwardQueue.Clear();
            stitch.ReverseQueue.Clear();
            HoldSender(stitch, toEgress: true, busy: false);
            HoldSender(stitch, toEgress: false, busy: false);
        }

        /// <summary>
        /// Resets both legs of a stitch and discards what was held.
        /// </summary>
        /// <param name="stitch">
        /// The stitch to reset.
        /// </param>
        /// <param name="cause">
        /// Resetting cause code.
        /// </param>
        /// <param name="diagnostic">
        /// Diagnostic code.
        /// </param>
        private static void ResetStitchBothWays(VcStitch stitch, byte cause, byte diagnostic)
        {
            DiscardQueues(stitch);
            stitch.InPort.ResetCircuit(stitch.InLci, cause, diagnostic);
            stitch.OutPort.ResetCircuit(stitch.OutLci, cause, diagnostic);
        }

        /// <summary>
        /// Clears both legs of a stitch, records it and tears it down.
        /// </summary>
        /// <param name="stitch">
        /// The stitch to clear.
        /// </param>
        /// <param name="cause">
        /// Clearing cause code.
        /// </param>
        /// <param name="diagnostic">
        /// Diagnostic code.
        /// </param>
        private void ClearStitchBothWays(VcStitch stitch, byte cause, byte diagnostic)
        {
            // A PVC has no clearing (X.25 4.2): a leg that cannot take a packet is reset, both ends, "Out of order", and
            // the join stays.
            if (IsPvc(stitch))
            {
                DiscardQueues(stitch);
                stitch.InPort.ResetCircuit(stitch.InLci, ResetCauseOutOfOrder, diagnostic);
                stitch.OutPort.ResetCircuit(stitch.OutLci, ResetCauseOutOfOrder, diagnostic);
                return;
            }

            // Take the stitch out of service first. A clear confirmation coming back from either leg
            // raises a clear event, and OnCallCleared must find no stitch then, or it would clear the
            // other leg a second time.
            if (!_node.DestroyStitch(stitch))
            {
                return;
            }

            DiscardQueues(stitch);
            stitch.InPort.ClearCall(stitch.InLci, cause, diagnostic,
                ClearFacilitiesFor(stitch, toCaller: true, Array.Empty<byte>()));
            stitch.OutPort.ClearCall(stitch.OutLci, cause, diagnostic,
                ClearFacilitiesFor(stitch, toCaller: false, Array.Empty<byte>()));

            WriteCdr(stitch, cause, diagnostic);
            AccountForCallClear(stitch);

            CallClear?.Invoke(stitch.InPort.Id, stitch.InLci, cause, diagnostic);
        }

        private void OnCallCleared(ISwitchPort port, ushort lci, byte cause, byte diagnostic)
        {
            // The leg's channel is free again (the engine reports this once per call, whoever ended it, and a clear this
            // switch asked for comes back here as well). That is the one place a clear is counted: counting
            // it at the places that ask for a clear counted most clears twice. See PortCounters.
            port.Counters.IncrementCallsCleared();

            // Locate the stitch from either side and clear its peer.
            VcStitch? stitch = _node.FindIngress(port, lci);
            bool clearedFromIngress = stitch != null;
            if (stitch == null)
            {
                stitch = _node.FindEgress(port, lci);
            }

            if (stitch == null)
            {
                return; // no stitch (e.g. already torn down)
            }

            // Claim the stitch before clearing the other leg. That leg's clear confirmation raises a
            // clear event of its own, on the other port's thread, and while the stitch was still
            // registered it found it and cleared this leg again: the C emulator saw a second clear
            // request on a channel that had already been confirmed free. Whoever removes the stitch
            // does the work; anyone who finds it gone has nothing left to do.
            if (!_node.DestroyStitch(stitch))
            {
                return;
            }

            // The clear's facility field goes to the other leg as it came (X.25 5.2.4.2; PLAN B1): charging information,
            // called line address modified and anything this switch does not act on.
            byte[] clearFacilities = port.GetClearFacilities(lci);
            // The end that is told gets this switch's charging answer added when it is owed one (6.22); the end that
            // cleared had its answer in the DCE Clear Confirmation (ChargingForConfirmation).
            if (clearedFromIngress)
            {
                stitch.OutPort.ClearCall(stitch.OutLci, cause, diagnostic,
                    ClearFacilitiesFor(stitch, toCaller: false, clearFacilities));
            }
            else
            {
                stitch.InPort.ClearCall(stitch.InLci, cause, diagnostic,
                    ClearFacilitiesFor(stitch, toCaller: true, clearFacilities));
            }

            WriteCdr(stitch, cause, diagnostic);
            AccountForCallClear(stitch);

            CallClear?.Invoke(port.Id, lci, cause, diagnostic);
        }

        private void WriteCdr(VcStitch stitch, byte cause, byte diagnostic)
        {
            if (CdrRecorded == null)
            {
                return;
            }

            Cdr cdr = new Cdr
            {
                StartTimeMs = stitch.StartTimeMs,
                EndTimeMs = NowMs(),
                Calling = stitch.CallingAddress,
                Called = stitch.CalledAddress,
                InPort = stitch.InPort.Id,
                OutPort = stitch.OutPort.Id,
                InLci = stitch.InLci,
                OutLci = stitch.OutLci,
                PacketsForward = stitch.PacketsForward,
                PacketsReverse = stitch.PacketsReverse,
                BytesForward = stitch.BytesForward,
                BytesReverse = stitch.BytesReverse,
                ClearCause = cause,
                ClearDiagnostic = diagnostic,
                ConnectedTimeMs = stitch.ConnectedTimeMs,
                StartWallSeconds = stitch.StartWallSeconds,
                SegmentsForward = ClosedSegments(stitch.SegmentsForward),
                SegmentsReverse = ClosedSegments(stitch.SegmentsReverse),
                Account = stitch.PayingAccount?.Name ?? string.Empty,
                Nui = stitch.PayingNui,
                ReverseCharging = stitch.ReverseCharging,
                ChargingRequested = stitch.ChargingToCaller || stitch.ChargingToCallee,
                CalledAccount = stitch.CalledAccount?.Name ?? string.Empty,
            };

            CdrRecorded.Invoke(cdr);
        }

        // The call is up: one more live call on each side's account. Made and received are counted when it ends, as
        // x25emu does - a call is a call when it has happened.
        private static void AccountForCallSetup(VcStitch stitch)
        {
            if (stitch.PayingAccount != null)
            {
                stitch.PayingAccount.CallsActive++;
            }

            if (stitch.CalledAccount != null)
            {
                stitch.CalledAccount.CallsActive++;
            }
        }

        // The call has ended: what only the end knows - that it happened and how long it lasted. The traffic was
        // credited as it passed (CreditTraffic); what is left is the message still open in each direction, if any.
        private void AccountForCallClear(VcStitch stitch)
        {
            ulong durationMs = (ulong)Math.Max(0, NowMs() - stitch.StartTimeMs);
            stitch.SegmentsForward.Close();
            stitch.SegmentsReverse.Close();
            ulong lastForward = stitch.SegmentsForward.TakeNew();
            ulong lastReverse = stitch.SegmentsReverse.TakeNew();

            SwitchAccount? caller = stitch.PayingAccount;
            if (caller != null)
            {
                caller.CallsActive--;
                caller.CallsMade++;
                caller.TotalDurationMs += durationMs;
                caller.SegmentsSent += lastForward;
                caller.SegmentsReceived += lastReverse;
            }

            SwitchAccount? callee = stitch.CalledAccount;
            if (callee != null)
            {
                callee.CallsActive--;
                callee.CallsReceived++;
                callee.TotalDurationMs += durationMs;
                callee.SegmentsSent += lastReverse;
                callee.SegmentsReceived += lastForward;
            }
        }

        // The segment count of a direction once the call has ended: a message still open is dropped (SegmentCounter.Close).
        private static ulong ClosedSegments(SegmentCounter counter)
        {
            counter.Close();
            return counter.Segments;
        }

        /// <summary>
        /// Ends one billing period of a PVC (RetroX25 PLAN B1b, the same rule as x25emu's <c>sw_pvc_period_end</c>):
        /// writes its record line and starts the next period from zero.
        /// </summary>
        /// <remarks>
        /// A PVC has no call and no clear (X.25 4.2), so its record is written in periods. "Start" is the period's start,
        /// the duration the period's, the connect time the start too (a PVC is up the whole period), and the cause 0 for a
        /// period that ran out or the reset cause that ended it. The lines of one PVC add up to its total. A message still
        /// open is kept for the next period, except after a reset, which discards everything in flight (X.25 4.4).
        /// </remarks>
        /// <param name="stitch">The PVC.</param>
        /// <param name="cause">0, or the reset cause that ended the period.</param>
        /// <param name="diagnostic">The reset diagnostic, or 0.</param>
        /// <param name="wasReset">True when a reset ended the period.</param>
        private void EndPvcPeriod(VcStitch stitch, byte cause, byte diagnostic, bool wasReset)
        {
            // The same lock the port threads count traffic under, so a packet lands wholly in one period.
            lock (stitch)
            {
                EndPvcPeriodLocked(stitch, cause, diagnostic, wasReset);
            }
        }

        private void EndPvcPeriodLocked(VcStitch stitch, byte cause, byte diagnostic, bool wasReset)
        {
            long now = NowMs();
            if (wasReset)
            {
                stitch.SegmentsForward.Close();
                stitch.SegmentsReverse.Close();
            }

            if (CdrRecorded != null)
            {
                CdrRecorded.Invoke(new Cdr
                {
                    StartTimeMs = stitch.StartTimeMs,
                    EndTimeMs = now,
                    Calling = stitch.CallingAddress,
                    Called = stitch.CalledAddress,
                    InPort = stitch.InPort.Id,
                    OutPort = stitch.OutPort.Id,
                    InLci = stitch.InLci,
                    OutLci = stitch.OutLci,
                    PacketsForward = stitch.PacketsForward,
                    PacketsReverse = stitch.PacketsReverse,
                    BytesForward = stitch.BytesForward,
                    BytesReverse = stitch.BytesReverse,
                    ClearCause = cause,
                    ClearDiagnostic = diagnostic,
                    ConnectedTimeMs = stitch.ConnectedTimeMs,
                    StartWallSeconds = stitch.StartWallSeconds,
                    SegmentsForward = stitch.SegmentsForward.Segments,
                    SegmentsReverse = stitch.SegmentsReverse.Segments,
                    Account = stitch.PayingAccount?.Name ?? string.Empty,
                    Nui = stitch.PayingNui,
                    CalledAccount = stitch.CalledAccount?.Name ?? string.Empty,
                });
            }

            if (stitch.PayingAccount != null)
            {
                stitch.PayingAccount.TotalDurationMs += (ulong)Math.Max(0, now - stitch.StartTimeMs);
            }

            stitch.PacketsForward = 0;
            stitch.PacketsReverse = 0;
            stitch.BytesForward = 0;
            stitch.BytesReverse = 0;
            stitch.SegmentsForward.StartPeriod();
            stitch.SegmentsReverse.StartPeriod();
            stitch.StartTimeMs = now;
            stitch.ConnectedTimeMs = now;
            stitch.StartWallSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        /// <summary>
        /// Writes the periodic record of every PVC whose period of <see cref="SwitchConfig.PvcRecordMinutes"/> has run
        /// out. The switch calls it once a second; a test calls it after moving <see cref="ClockMs"/>.
        /// </summary>
        public void WritePvcRecordsDue()
        {
            int minutes = _node.Config.PvcRecordMinutes;
            if (minutes <= 0)
            {
                return;
            }

            long periodMs = minutes * 60000L;

            // The timer and a caller may arrive together; one of them writes a period, the other then finds it new.
            lock (_pvcRecordGate)
            {
                long now = NowMs();
                List<VcStitch> stitches = _node.SnapshotStitches();
                for (int i = 0; i < stitches.Count; i++)
                {
                    if (IsPvc(stitches[i]) && now - stitches[i].StartTimeMs >= periodMs)
                    {
                        EndPvcPeriod(stitches[i], 0, 0, false);
                    }
                }
            }
        }

        private SwitchAccount? FindAccountByNui(byte[] nui)
        {
            // Taken once: the console may replace the table (SwitchNode.Accounts).
            IReadOnlyList<SwitchAccount> accounts = _node.Accounts;
            for (int i = 0; i < accounts.Count; i++)
            {
                if (accounts[i].HasNui(nui))
                {
                    return accounts[i];
                }
            }

            return null;
        }

        private SwitchAccount? FindAccount(string address)
        {
            IReadOnlyList<SwitchAccount> accounts = _node.Accounts;
            for (int i = 0; i < accounts.Count; i++)
            {
                if (accounts[i].Covers(address))
                {
                    return accounts[i];
                }
            }

            return null;
        }
    }
}
