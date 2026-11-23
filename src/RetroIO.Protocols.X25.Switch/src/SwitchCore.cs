//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Threading;
using RetroIO.Protocols.X25.Models;
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
        /// Clearing cause Network congestion. X.25 (10/96) Table 5-7 (clearing cause field): bits 0000 0101 = 0x05.
        /// Used when the switch already carries <see cref="SwitchConfig.MaxCalls"/> calls. The list of error
        /// conditions after Table C.3, row 36 ("Temporary network congestion or fault condition within the network"),
        /// gives the cause Network congestion with diagnostic # 0, # 122 or greater than 127; this switch sends # 0.
        /// </summary>
        public const byte CauseNetworkCongestion = 0x05;

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
        /// <see cref="ManagementCall"/> instead of being routed. Null or empty disables it. Held by the
        /// switch's router (<see cref="Router"/>), which decides it like every other address.
        /// </summary>
        public string? ManagementAddress
        {
            get => Router.ManagementAddress;
            set => Router.ManagementAddress = value;
        }

        /// <summary>
        /// The one routing decision of this switch: every call that comes in on any port is decided
        /// here, and the port only carries out the answer.
        /// </summary>
        public SwitchCallRouter Router { get; }

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
            Router = new SwitchCallRouter(_node.Config.Routing, _node.GetPort);
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
            port.InterruptConfirmed += OnInterruptConfirmed;
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

            // On a port with a calling address base the network, not the DTE, says where the call comes from: the line's
            // address plus the call's channel (PortConfig.CallingAddressBase). Done first, so the management surface,
            // billing by address, the call record and the far end all see the same address.
            string lineAddress = inPort.Config.CallingAddressFor(inLci);
            if (lineAddress.Length > 0)
            {
                callingAddress = lineAddress;
            }

            // Calls to the reserved management address are for the switch itself: the router delivers
            // them locally, to the management surface. Asked before the NUI checks, which are about who
            // pays for a call through the network.
            if (Router.IsForThisSwitch(calledAddress))
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
                    RefuseCall(inPort, inLci, calledAddress, CauseInvalidFacilityRequest, DiagnosticNuiProblem);
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
                RefuseCall(inPort, inLci, calledAddress, CauseLocalProcedureError, DiagnosticNuiProblem);
                return;
            }
            else
            {
                paying = FindAccount(callingAddress);
            }

            // The router decides where the call goes (SwitchCallRouter): no route or no such port is Not
            // obtainable 67, a port whose line is out of order is Out of order 0 (X.25 Table C.1, 4.6.4).
            // This port only sends the refusal. Until 2026-11-18 every one of these was 0x0D with
            // diagnostic 0, and a call was placed on a port that was down. Never routed back out of the
            // port it came in on (X121RoutingTable).
            // The switch-wide call limit (SwitchConfig.MaxCalls; 0 = none). A call is one stitch from routing to
            // clearing, so the leg in and the leg out count once together. Refused before routing, so the far end
            // is never offered a call that is then dropped: Network congestion, diagnostic 0 (Table 5-7; the list
            // after Table C.3, row 36).
            int maxCalls = _node.Config.MaxCalls;
            if (maxCalls > 0 && _node.ActiveStitchCount >= maxCalls)
            {
                RefuseCall(inPort, inLci, calledAddress, CauseNetworkCongestion, 0);
                return;
            }

            // The hop count (added 2026-11-21; until then [switch] max_hops was read, shown and written back and limited
            // nothing, RetroIO BUGS.md). x25emu's rule (tools/x25switch.c, the check after the transit loop check): a call
            // that comes in on an internal trunk carries how many of our switches it has crossed in a private facility
            // (HopsCode, after the marker); this switch is one more. Past max_hops it is cleared. X.110 3.2 keeps an
            // international route to "no more than four international data links in tandem"; this is the same kind of
            // limit inside our network, which ITU does not specify. The clear is Network congestion (X.25 Table 5-7,
            // "temporary network congestion or fault condition within the network") with x25emu's diagnostic 128
            // (SW_DIAG_HOP_LIMIT), so both switches answer a looping call the same way. A call from anywhere else starts
            // at 0.
            int hops = 0;
            if (IsInternalTrunk(inPort) && haveParameters && TryFindPrivate(asked.OtherFacilities, HopsCode, out byte[] carriedHops, out _, out _)
                && carriedHops.Length == 1)
            {
                hops = carriedHops[0] + 1;
                if (hops > _node.Config.MaxHops)
                {
                    RefuseCall(inPort, inLci, calledAddress, CauseNetworkCongestion, DiagnosticHopLimit);
                    return;
                }
            }

            X25CallDecision decision = Router.Decide(calledAddress, inPort.Id);
            if (decision.Action != X25CallAction.Forward)
            {
                RefuseCall(inPort, inLci, calledAddress, decision.Cause, decision.Diagnostic);
                return;
            }

            ISwitchPort outPort = _node.GetPort(decision.OutPort)!;

            // The called number as the out port wants it written (PortConfig.CalledAddressForm): toward another
            // network the full international number without the prefix (X.121 7.8.1, 7.12.6-7.12.7). The call
            // record keeps the number the caller dialled.
            string onwardCalled = decision.CalledAddress ?? calledAddress;

            // Between networks (X.75): read the utility field the calling network sent, and build the one for the next
            // network. A refusal here is sent back on the X.75 link with the cause and diagnostic Annex F a) gives.
            // Inside the network the field travels in a private facility on internal trunks (CarriedUtilitiesCode), so it is
            // taken out of what the caller's side sent and, toward another switch of ours, put back with what is built here.
            byte[]? onwardUtilities = null;
            if (!TryBuildOnwardUtilities(inPort, inLci, outPort, out onwardUtilities, out byte[]? onwardCarried, out byte refuseDiagnostic))
            {
                RefuseCall(inPort, inLci, calledAddress, CauseNetworkCongestion, refuseDiagnostic);
                return;
            }

            // The hop count goes on only to another of our switches; toward a subscriber or another network it is taken
            // out, as x25emu does (sw_hops_set / sw_hops_strip): what follows the marker is our network's own signalling.
            byte[]? onwardHops = IsInternalTrunk(outPort) ? new[] { (byte)Math.Min(hops, 255) } : null;
            if (haveParameters)
            {
                asked = WithPrivate(WithCarriedUtilities(asked, onwardCarried), HopsCode, onwardHops);
            }

            // Place the onward call; a zero LCI means the egress port could not initiate it.
            // X.25 6.9 and 6.12: the window and packet size of a call are negotiated end to end. The onward Call Request
            // asks for what the caller asked for (the directions swapped: what the caller sends, the callee receives),
            // limited to the largest packet the egress port is set up for. Asking for the switch's own defaults here made
            // the caller think it had the sizes it asked for while the callee ran with others, and the first big packet
            // reset the call.
            ushort outLci;
            X25CallDecision? portRefusal;
            if (haveParameters)
            {
                // The NUI says who pays this network; it never goes on to the called DTE (X.25 6.21).
                X25CallParameters toAsk = asked.Swapped().WithPacketSizeAtMost(outPort.Config.MaxPacketSize).WithoutNuiSelection()
                    .WithoutChargingRequest()    // Table 7-2: not in an Incoming Call; this DCE answers it (6.22)
                    .WithNetworkUtilities(onwardUtilities);
                outLci = outPort.PlaceCall(onwardCalled, callingAddress, callUserData, toAsk, out portRefusal);
            }
            else if (onwardUtilities != null || onwardCarried != null || onwardHops != null)
            {
                // No values from the caller's side, but the next network (or switch) still needs the utility field or the
                // hop count: ask for the out port's own defaults with them.
                X25CallParameters defaults = WithPrivate(WithCarriedUtilities(new X25CallParameters(
                    outPort.Config.DefaultWindowSize, outPort.Config.DefaultWindowSize,
                    outPort.Config.DefaultPacketSize, outPort.Config.DefaultPacketSize, null, onwardUtilities), onwardCarried),
                    HopsCode, onwardHops);
                outLci = outPort.PlaceCall(onwardCalled, callingAddress, callUserData, defaults, out portRefusal);
            }
            else
            {
                outLci = outPort.PlaceCall(onwardCalled, callingAddress, callUserData, null, out portRefusal);
            }

            if (outLci == 0)
            {
                // The router found the port in service and the port still could not place the call. A port
                // that knows why says so (the XOT port: far end unreachable is Out of order #0, X.25 Table
                // C.3 a) item 30; no channel left is Number busy #71, item 31). Otherwise its state is read
                // again: a line that went down meanwhile is Out of order (4.6.4), else Number busy #71.
                X25CallDecision failed = portRefusal ?? (SwitchCallRouter.IsInService(outPort)
                    ? X25CallDecision.Busy()
                    : X25CallDecision.DestinationOutOfOrder());
                RefuseCall(inPort, inLci, calledAddress, failed.Cause, failed.Diagnostic);
                return;
            }

            outPort.Counters.IncrementCallsOut();

            VcStitch stitch = _node.CreateStitch(inPort, inLci, outPort, outLci);
            stitch.CallingAddress = callingAddress;
            stitch.CalledAddress = calledAddress;
            stitch.StartTimeMs = NowMs();
            stitch.StartWallSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            stitch.HopCount = hops;
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

        // The next call identifier this network gives a call it originates toward another network (X.75 5.3.2: "established
        // by the originating network", 24 bits of binary data, 5.4.3.2). A counter: with the calling DTE address it names
        // the call, and the uniqueness is "only guaranteed over a period of time" whose length X.75 leaves for further
        // study - the counter repeats after 16 777 215 calls.
        private int _nextCallIdentifier = 1;

        /// <summary>
        /// Reads the X.75 utility field of a call that came in from another network and builds the field for the next one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// X.75 5.3.1 (read 2026-11-18): "A transit network identification is always present in the call request packet for
        /// each transit network controlling the virtual circuit up to this point of call set-up", in the order crossed. So
        /// only a TRANSIT network - one the call entered from another network and leaves to another network - adds its
        /// own DNIC. The originating network does not name itself, and the destination network adds nothing.
        /// </para>
        /// <para>
        /// X.75 5.3.2: the call identifier "is always present in the call request packet" and is "established by the
        /// originating network". A call from one of our own subscribers gets a new one; a call from another network keeps
        /// the one it came with.
        /// </para>
        /// <para>
        /// Refusals, all clearing cause network congestion (Annex F a), call request packet): no call identifier is row 10
        /// "Utility expected and not provided" # 76; a length past the packet is row 14 # 101; a code repeated is row 15
        /// # 73; a transit network named twice is row 16 "Duplicate TNIC" # 66; bits 8 or 7 of the length octet set are
        /// row 18 # 101 (the packet parser already refuses those). Our own DNIC already in the list means the call has
        /// come round in a circle: row 42 ("a circular routing would be formed") # 120. A digit that is not BCD has no row
        /// of its own in Annex F a); it is refused with # 0.
        /// </para>
        /// <para>
        /// Inside the network the field crosses internal trunks in a private facility (<see cref="CarriedUtilitiesCode"/>,
        /// the same coding as x25emu), so a call that enters at one switch and leaves at another keeps its list.
        /// Whether this network is a transit network for the call is told by the call identifier: 5.3.2 makes it "always
        /// present in the call request packet" between networks and "established by the originating network", and this
        /// switch sets one only on the way out of the network. So a call that already has one entered from another
        /// network. A peer that breaks 5.3.2 is refused at the border (# 76), so that case does not reach the exit.
        /// </para>
        /// </remarks>
        private bool TryBuildOnwardUtilities(ISwitchPort inPort, ushort inLci, ISwitchPort outPort, out byte[]? onward, out byte[]? onwardCarried, out byte refuseDiagnostic)
        {
            onward = null;
            onwardCarried = null;
            refuseDiagnostic = 0;
            bool fromNetwork = inPort.Config.BetweenNetworks;
            bool toNetwork = outPort.Config.BetweenNetworks;
            bool toInternal = IsInternalTrunk(outPort);

            // What the caller's side brought: the X.75 field from another network, or the carried copy from another of
            // our switches, or nothing (a call from our own subscriber).
            byte[]? received = null;
            bool haveParameters = inPort.TryGetCallParameters(inLci, out X25CallParameters p);
            if (fromNetwork)
            {
                received = haveParameters ? p.NetworkUtilities : Array.Empty<byte>();
            }
            else if (IsInternalTrunk(inPort) && haveParameters && TryFindCarried(p.OtherFacilities, out byte[] carried, out _, out _))
            {
                received = carried;
            }

            if (received == null && !toNetwork)
            {
                // Our own subscriber's call staying inside the network, or reaching a subscriber: no field at all.
                return true;
            }

            string ownDnic = _node.Config.Dnic;
            X75Utilities u;
            if (received != null)
            {
                X75UtilityError error = X75Utilities.TryDecode(received, out u);
                switch (error)
                {
                    case X75UtilityError.None:
                        break;
                    case X75UtilityError.RepeatedCode:
                        refuseDiagnostic = 73;
                        return false;
                    case X75UtilityError.InvalidDigit:
                        refuseDiagnostic = 0;
                        return false;
                    default:
                        refuseDiagnostic = 101;
                        return false;
                }

                if (fromNetwork && !u.HasCallIdentifier)
                {
                    refuseDiagnostic = 76;
                    return false;
                }

                List<string> transit = u.TransitNetworks;
                for (int i = 0; i < transit.Count; i++)
                {
                    for (int j = i + 1; j < transit.Count; j++)
                    {
                        if (transit[i] == transit[j])
                        {
                            refuseDiagnostic = 66;
                            return false;
                        }
                    }

                    if (fromNetwork && transit[i] == ownDnic)
                    {
                        refuseDiagnostic = 120;
                        return false;
                    }
                }
            }
            else
            {
                u = new X75Utilities();
            }

            if (toInternal)
            {
                // To another of our switches: the field goes along as it is; the switch that leaves the network decides.
                onwardCarried = received != null ? u.Encode() : null;
                return true;
            }

            if (!toNetwork)
            {
                // Destination network: the field stops here. Its transit list is echoed in the Call Connected (AnswerTransitList).
                return true;
            }

            if (u.HasCallIdentifier)
            {
                // A transit network: the call entered from another network (it carries that network's identifier).
                u.TransitNetworks.Add(ownDnic);

                // X.110 (11/88) 3.2: a route "should be so planned as to encompass no more than four international data
                // links in tandem". The list now names every transit network up to and including this one, each entered
                // by one international link, and the originating network's link came first, so the link this call would
                // leave on is number Count + 1. Past four it is refused: X.75 (10/96) Table E.1 item 40, "the routing
                // indicated in the received call request packet is too long for an overall routing conforming to X.110",
                // Network congestion # 120. Added 2026-11-21 (RetroIO BUGS.md); until then any length was sent on.
                if (u.TransitNetworks.Count + 1 > MaxInternationalLinks)
                {
                    refuseDiagnostic = 120;
                    return false;
                }
            }
            else
            {
                // Originating network: a new call identifier, and no transit network.
                u.HasCallIdentifier = true;
                u.CallIdentifier = _nextCallIdentifier;
                _nextCallIdentifier = _nextCallIdentifier >= X75Utilities.MaxCallIdentifier ? 1 : _nextCallIdentifier + 1;
            }

            onward = u.Encode();
            return true;
        }

        /// <summary>
        /// Refuses a call this switch received, with the cause and diagnostic given.
        /// </summary>
        /// <remarks>
        /// X.75 5.3.1 puts the transit list in a refusal but does not say whether a transit network that refuses adds
        /// itself. Decided by Ronny 2026-11-23: it does, so the caller can see which network refused
        /// (<see cref="RefusalTransitList"/>). Before, the list went back as it had arrived.
        /// </remarks>
        /// <param name="inPort">
        /// The port the call came in on.
        /// </param>
        /// <param name="inLci">
        /// Its channel.
        /// </param>
        /// <param name="calledAddress">
        /// The called address as it arrived.
        /// </param>
        /// <param name="cause">
        /// Clearing cause.
        /// </param>
        /// <param name="diagnostic">
        /// Diagnostic code.
        /// </param>
        private void RefuseCall(ISwitchPort inPort, ushort inLci, string calledAddress, byte cause, byte diagnostic)
        {
            byte[]? list = null;
            if (inPort.Config.BetweenNetworks && inPort.TryGetCallParameters(inLci, out X25CallParameters asked))
            {
                list = RefusalTransitList(asked.NetworkUtilities, calledAddress);
            }

            if (list == null)
            {
                inPort.ClearCall(inLci, cause, diagnostic);
                return;
            }

            inPort.ClearCall(inLci, cause, diagnostic, Array.Empty<byte>(), list);
        }

        /// <summary>
        /// The transit list for a refusal sent back to another network: the list given, with this network's code added
        /// at the end when this network is a transit network for the call and the list does not already name it.
        /// </summary>
        /// <remarks>
        /// A transit network is one the called address is not in: the address from another network is the full
        /// international number (X.121), so its first digits name the network. At the destination network the list goes
        /// back as received, as in a Call Connected (5.3.1). Decided by Ronny 2026-11-23.
        /// </remarks>
        /// <param name="utilities">
        /// The X.75 utility field holding the list so far.
        /// </param>
        /// <param name="calledAddress">
        /// The called address.
        /// </param>
        /// <returns>
        /// The utility field to send, or null when the given one does not decode (the clear then carries the received list).
        /// </returns>
        private byte[]? RefusalTransitList(byte[]? utilities, string calledAddress)
        {
            X75Utilities answer = new X75Utilities();
            if (utilities != null && utilities.Length > 0)
            {
                if (X75Utilities.TryDecode(utilities, out X75Utilities read) != X75UtilityError.None)
                {
                    return null;
                }

                List<string> transit = read.TransitNetworks;
                for (int i = 0; i < transit.Count; i++)
                {
                    answer.TransitNetworks.Add(transit[i]);
                }
            }

            string ownDnic = _node.Config.Dnic;
            if (ownDnic.Length > 0 && !IsForOwnNetwork(calledAddress) && !answer.TransitNetworks.Contains(ownDnic))
            {
                answer.TransitNetworks.Add(ownDnic);
            }

            return answer.Encode();
        }

        /// <summary>
        /// True when the called address belongs to one of this network's own DNICs (or no DNIC is set up).
        /// </summary>
        private bool IsForOwnNetwork(string calledAddress)
        {
            IReadOnlyList<string> own = _node.Config.Routing.OwnDnics;
            if (own.Count == 0)
            {
                return _node.Config.Dnic.Length == 0 || calledAddress.StartsWith(_node.Config.Dnic, StringComparison.Ordinal);
            }

            for (int i = 0; i < own.Count; i++)
            {
                if (calledAddress.StartsWith(own[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True for a trunk to another switch of this network (not to another network).
        /// </summary>
        private static bool IsInternalTrunk(ISwitchPort port)
        {
            return port.Config.Type == PortType.Trunk && !port.Config.BetweenNetworks;
        }

        /// <summary>
        /// The private facility that carries the X.75 utility field across an internal trunk: code FE, after the marker
        /// (00 00), with a length octet (class D, X.25 7.1) and then the field. The same coding as x25emu
        /// (<c>SW_FAC_TRANSIT</c> in its <c>tools/x25switch.h</c>), so the two switches can share a network. X.25 7.1: what
        /// follows a marker is not an X.25 facility, so this is the network's own signalling and never reaches a DTE.
        /// </summary>
        private const byte CarriedUtilitiesCode = 0xFE;

        /// <summary>
        /// Finds the carried utility field in a facility field: an FE element after the first marker.
        /// </summary>
        private static bool TryFindCarried(byte[] field, out byte[] utilities, out int at, out int length)
        {
            return TryFindPrivate(field, CarriedUtilitiesCode, out utilities, out at, out length);
        }

        /// <summary>
        /// Returns the values with any carried utility field removed (and the marker too when nothing else follows it),
        /// then, when <paramref name="carried"/> is given, a new one added after a marker.
        /// </summary>
        private static X25CallParameters WithCarriedUtilities(X25CallParameters values, byte[]? carried)
        {
            if (carried != null && carried.Length > 255)
            {
                throw new InvalidOperationException("a carried utility field is longer than a class D element holds");
            }

            return WithPrivate(values, CarriedUtilitiesCode, carried);
        }

        /// <summary>
        /// The private facility that carries the hop count across an internal trunk: code 3F after the marker, one value
        /// octet (class A, X.25 7.1: codes 00 to 3F carry one parameter octet). The same coding as x25emu
        /// (<c>SW_FAC_HOPS</c> in its <c>tools/x25switch.h</c>).
        /// </summary>
        private const byte HopsCode = 0x3F;

        /// <summary>
        /// The most international data links a route may hold in tandem (X.110 (11/88) 3.2).
        /// </summary>
        private const int MaxInternationalLinks = 4;

        /// <summary>
        /// The diagnostic of a call cleared for crossing more than max_hops switches: x25emu's <c>SW_DIAG_HOP_LIMIT</c>,
        /// 128, from the range X.25 Annex E leaves to networks.
        /// </summary>
        private const byte DiagnosticHopLimit = 128;

        /// <summary>
        /// Finds a private element by its code after the first marker; its parameter octets come back without the code
        /// (and without the length octet of a class D element).
        /// </summary>
        private static bool TryFindPrivate(byte[] field, byte wanted, out byte[] value, out int at, out int length)
        {
            int i = 0;
            bool afterMarker = false;
            while (i < field.Length)
            {
                byte code = field[i];
                int n;
                int head;
                switch (code >> 6)
                {
                    case 0: n = 2; head = 1; break;
                    case 1: n = 3; head = 1; break;
                    case 2: n = 4; head = 1; break;
                    default: n = i + 1 < field.Length ? 2 + field[i + 1] : field.Length - i; head = 2; break;
                }

                if (i + n > field.Length)
                {
                    break;
                }

                if (afterMarker && code == wanted && n >= head)
                {
                    value = new byte[n - head];
                    Array.Copy(field, i + head, value, 0, value.Length);
                    at = i;
                    length = n;
                    return true;
                }

                if (code == 0x00)
                {
                    afterMarker = true;
                }

                i += n;
            }

            value = Array.Empty<byte>();
            at = -1;
            length = 0;
            return false;
        }

        /// <summary>
        /// Returns the values with the private element of <paramref name="code"/> removed (and the marker too when
        /// nothing else follows it), then, when <paramref name="value"/> is given, a new one added after a marker.
        /// </summary>
        /// <remarks>
        /// <paramref name="value"/> must have the length the code's class gives (1, 2 or 3 octets for classes A, B and
        /// C); a class D value is given without its length octet.
        /// </remarks>
        private static X25CallParameters WithPrivate(X25CallParameters values, byte code, byte[]? value)
        {
            byte[] field = values.OtherFacilities;
            if (TryFindPrivate(field, code, out _, out int at, out int length))
            {
                byte[] kept = new byte[field.Length - length];
                Array.Copy(field, 0, kept, 0, at);
                Array.Copy(field, at + length, kept, at, field.Length - at - length);
                field = kept;
                if (field.Length >= 2 && field[field.Length - 2] == 0x00 && field[field.Length - 1] == 0x00)
                {
                    byte[] shorter = new byte[field.Length - 2];
                    Array.Copy(field, shorter, shorter.Length);
                    field = shorter;
                }
            }

            if (value != null)
            {
                bool classD = (code >> 6) == 3;
                bool hasMarker = HasMarker(field);
                int extra = (hasMarker ? 0 : 2) + 1 + (classD ? 1 : 0) + value.Length;
                byte[] grown = new byte[field.Length + extra];
                field.CopyTo(grown, 0);
                int pos = field.Length;
                if (!hasMarker)
                {
                    grown[pos++] = 0x00;
                    grown[pos++] = 0x00;
                }

                grown[pos++] = code;
                if (classD)
                {
                    grown[pos++] = (byte)value.Length;
                }

                value.CopyTo(grown, pos);
                field = grown;
            }

            if (ReferenceEquals(field, values.OtherFacilities))
            {
                return values;
            }

            return new X25CallParameters(values.WindowSizeOut, values.WindowSizeIn, values.PacketSizeOut, values.PacketSizeIn, field, values.NetworkUtilities);
        }

        // True when a facility field has a marker (code 00) among its elements (X.25 7.1).
        private static bool HasMarker(byte[] field)
        {
            int i = 0;
            while (i < field.Length)
            {
                byte code = field[i];
                if (code == 0x00)
                {
                    return true;
                }

                switch (code >> 6)
                {
                    case 0: i += 2; break;
                    case 1: i += 3; break;
                    case 2: i += 4; break;
                    default: i += i + 1 < field.Length ? 2 + field[i + 1] : field.Length - i; break;
                }
            }

            return false;
        }

        /// <summary>
        /// The transit list to answer the caller's side with, from what the called side answered: the X.75 field from
        /// another network, the carried copy from another of our switches, or - at the destination - the list the call
        /// came with. Null when there is none.
        /// </summary>
        /// <remarks>
        /// X.75 5.3.1: "A transit network identification is always present for each transit network in the call connected
        /// packet", in the order crossed from the calling to the called DTE. The destination network answers with the list
        /// it received; a transit network passes on the list the next network answered with, which already names it. The
        /// call identifier is left out: "The use of the call identifier in the call connected packet is for further study"
        /// (5.3.2).
        /// </remarks>
        private static byte[]? AnswerTransitList(VcStitch stitch, X25CallParameters answered)
        {
            byte[]? source = null;
            if (stitch.OutPort.Config.BetweenNetworks)
            {
                source = answered.NetworkUtilities;
            }
            else if (IsInternalTrunk(stitch.OutPort))
            {
                if (TryFindCarried(answered.OtherFacilities, out byte[] carried, out _, out _))
                {
                    source = carried;
                }
            }
            else if (stitch.InPort.TryGetCallParameters(stitch.InLci, out X25CallParameters asked))
            {
                // The destination switch: the list the call arrived with.
                if (stitch.InPort.Config.BetweenNetworks)
                {
                    source = asked.NetworkUtilities;
                }
                else if (TryFindCarried(asked.OtherFacilities, out byte[] carried, out _, out _))
                {
                    source = carried;
                }
            }

            if (source == null)
            {
                return null;
            }

            X75Utilities answer = new X75Utilities();
            if (X75Utilities.TryDecode(source, out X75Utilities read) == X75UtilityError.None)
            {
                List<string> transit = read.TransitNetworks;
                for (int i = 0; i < transit.Count; i++)
                {
                    answer.TransitNetworks.Add(transit[i]);
                }
            }

            return answer.Encode();
        }

        /// <summary>
        /// The answer to the caller's side with the transit list where that side wants it: in the X.75 field toward
        /// another network, in the carried facility toward another of our switches, and nowhere toward a subscriber.
        /// </summary>
        private static X25CallParameters WithAnswerTransitList(VcStitch stitch, X25CallParameters toCaller, byte[]? list)
        {
            if (stitch.InPort.Config.BetweenNetworks)
            {
                return WithCarriedUtilities(toCaller, null).WithNetworkUtilities(list ?? new X75Utilities().Encode());
            }

            if (IsInternalTrunk(stitch.InPort))
            {
                return WithCarriedUtilities(toCaller, list).WithNetworkUtilities(null);
            }

            return WithCarriedUtilities(toCaller, null).WithNetworkUtilities(null);
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
                // The transit list goes back the way the caller's side wants it (X.75 5.3.1; WithAnswerTransitList).
                stitch.InPort.AcceptCall(stitch.InLci, WithAnswerTransitList(stitch,
                    agreed.Swapped().WithoutNuiSelection().WithoutChargingRequest(), AnswerTransitList(stitch, agreed)));
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
        private void CountForwarded(VcStitch stitch, bool toEgress, int length)
        {
            // The switch's own totals since start (show x25 stats), which outlive the stitch.
            _node.CountForwardedBytes(toEgress, length);

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

            // X.25 Table C-4: a circuit reset for a procedure error at one interface resets the distant DTE "with the cause
            // 'Remote procedure error' (same diagnostic)". The layer reports such a reset as local procedure error the moment
            // the bad packet arrives, so the other leg is reset at once (decided by Ronny 2026-11-23, matching x25emu
            // 9e40f78). The same cause toward another network: X.75 (10/96) Table 23 lists "Remote procedure error" (read
            // 2026-11-23), and network congestion would also lose the diagnostic (4.4.3.2, Table 24 maps 1-111 to 114). A
            // network-congestion rule for X.75 legs was written and taken out the same day. Added 2026-11-23; before, the other
            // leg got the local cause, or 0 / network congestion, after the first reset completed.
            ISwitchPort other = fromIngress ? stitch.OutPort : stitch.InPort;
            ushort otherLci = fromIngress ? stitch.OutLci : stitch.InLci;
            byte onward = cause;
            if (cause == X25Cause.ResettingLocalProcedureError)
            {
                onward = X25Cause.ResettingRemoteProcedureError;
            }

            other.ResetCircuit(otherLci, onward, diagnostic);
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

            // A circuit this switch does not pass on ends here, so the switch is the far end and confirms it (X.25
            // 4.3.7). A stitched one is confirmed when the other side confirms it (OnInterruptConfirmed).
            if (stitch == null)
            {
                port.ConfirmInterrupt(lci);
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
        /// The far end confirmed an Interrupt this switch passed on: confirm the one it came from, on the other side of
        /// the stitch. X.25 4.3.7 and X.75 3.3.5: a confirmation means "the interrupt has been confirmed by the remote
        /// DTE". Added 2026-11-23; until then each port confirmed an Interrupt as it arrived.
        /// </summary>
        /// <param name="port">
        /// The port the confirmation arrived on.
        /// </param>
        /// <param name="lci">
        /// Its logical channel.
        /// </param>
        private void OnInterruptConfirmed(ISwitchPort port, ushort lci)
        {
            VcStitch? stitch = _node.FindEgress(port, lci);
            if (stitch != null)
            {
                stitch.InPort.ConfirmInterrupt(stitch.InLci);
                return;
            }

            stitch = _node.FindIngress(port, lci);
            if (stitch != null)
            {
                stitch.OutPort.ConfirmInterrupt(stitch.OutLci);
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

            // A call cleared for a procedure error at one interface clears the distant end with "remote procedure error"
            // and the same diagnostic, on X.25 and X.75 alike (X.75 Table 21 has 0x11). Added 2026-11-23, with x25emu; the
            // call record keeps the cause as it came.
            byte onwardCause = cause == X25Cause.ClearingLocalProcedureError ? X25Cause.ClearingRemoteProcedureError : cause;

            // The end that is told gets this switch's charging answer added when it is owed one (6.22); the end that
            // cleared had its answer in the DCE Clear Confirmation (ChargingForConfirmation).
            if (clearedFromIngress)
            {
                stitch.OutPort.ClearCall(stitch.OutLci, onwardCause, diagnostic,
                    ClearFacilitiesFor(stitch, toCaller: false, clearFacilities));
            }
            else
            {
                // X.75 5.3.1: a refusal names the transit networks up to the one that refused. A call not yet connected
                // goes back to the caller's side with the list the same rule as a Call Connected gives (AnswerTransitList):
                // the one the next network refused with, the carried copy from another of our switches, or at the
                // destination the list the call came with. It is placed as WithAnswerTransitList places an answer's: in the
                // utility field toward another network, in the carried private element toward another of our switches, and
                // nowhere toward a subscriber. Added 2026-11-22; until then the caller's side was given the list it had sent,
                // and none crossed an internal trunk.
                byte[]? transitList = null;
                if (!stitch.Active)
                {
                    X25CallParameters refused = new X25CallParameters(
                        X25Facilities.DefaultWindowSize, X25Facilities.DefaultWindowSize,
                        X25Facilities.DefaultPacketSize, X25Facilities.DefaultPacketSize,
                        clearFacilities, port.GetClearUtilities(lci));
                    transitList = AnswerTransitList(stitch, refused);

                    // Toward another network a refusal from inside a transit network names this network too (Ronny,
                    // 2026-11-23; RefusalTransitList). A refusal from a network further on already names it: this switch
                    // added itself when it passed the call on.
                    if (stitch.InPort.Config.BetweenNetworks)
                    {
                        byte[]? received = transitList;
                        if (received == null && stitch.InPort.TryGetCallParameters(stitch.InLci, out X25CallParameters asked))
                        {
                            received = asked.NetworkUtilities;
                        }

                        transitList = RefusalTransitList(received, stitch.CalledAddress) ?? transitList;
                    }
                }

                // The carried element is this network's own signalling (X.25 7.1): it never goes on as it came.
                X25CallParameters toCaller = WithCarriedUtilities(new X25CallParameters(
                    X25Facilities.DefaultWindowSize, X25Facilities.DefaultWindowSize,
                    X25Facilities.DefaultPacketSize, X25Facilities.DefaultPacketSize,
                    ClearFacilitiesFor(stitch, toCaller: true, clearFacilities), null),
                    IsInternalTrunk(stitch.InPort) ? transitList : null);
                stitch.InPort.ClearCall(stitch.InLci, onwardCause, diagnostic, toCaller.OtherFacilities,
                    stitch.InPort.Config.BetweenNetworks ? transitList : null);
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
