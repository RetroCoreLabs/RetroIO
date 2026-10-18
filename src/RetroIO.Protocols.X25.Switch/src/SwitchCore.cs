//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
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
        /// Reset cause: network congestion (X.25 Table 5-8). Sent to both legs when a switch has
        /// to give up on what it was holding for a circuit.
        /// </summary>
        public const byte ResetCauseNetworkCongestion = 0x05;

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
        }

        private static long NowMs()
        {
            return Environment.TickCount64;
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

            // Resolve an egress port for the dialled address; never route back to the ingress port.
            if (!_node.Config.Routing.TryLookup(calledAddress, inPort.Id, out int outPortId))
            {
                inPort.ClearCall(inLci, CauseNotObtainable, 0);
                inPort.Counters.IncrementCallsCleared();
                return;
            }

            ISwitchPort? outPort = _node.GetPort(outPortId);
            if (outPort == null)
            {
                inPort.ClearCall(inLci, CauseNotObtainable, 0);
                inPort.Counters.IncrementCallsCleared();
                return;
            }

            // Place the onward call; a zero LCI means the egress port could not initiate it.
            ushort outLci = outPort.PlaceCall(calledAddress, callingAddress, callUserData);
            if (outLci == 0)
            {
                inPort.ClearCall(inLci, CauseNotObtainable, 0);
                inPort.Counters.IncrementCallsCleared();
                return;
            }

            outPort.Counters.IncrementCallsOut();

            VcStitch stitch = _node.CreateStitch(inPort, inLci, outPort, outLci);
            stitch.CallingAddress = callingAddress;
            stitch.CalledAddress = calledAddress;
            stitch.StartTimeMs = NowMs();
            stitch.HopCount = 0;

            // The incoming call is NOT accepted here. X.25 4.1: a Call Accepted says the circuit is
            // established end to end, and at this point only the onward Call Request has gone out.
            // Confirming now tells the caller the call is up while the far end may still refuse it,
            // and anything it sends in between has nowhere to go. The accept happens in
            // OnCallConnected, when the far end has answered.
            AccountForCallSetup(callingAddress, calledAddress);
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

            stitch.Active = true;
            stitch.InPort.AcceptCall(stitch.InLci);
        }

        private void OnDataReceived(ISwitchPort port, ushort lci, byte[] data, int length, bool qBit, bool mBit, bool dBit)
        {
            port.Counters.IncrementPacketsIn();

            // Forward ingress -> egress.
            VcStitch? ingress = _node.FindIngress(port, lci);
            if (ingress != null)
            {
                Forward(ingress, toEgress: true, data, length, qBit, mBit, dBit);
                return;
            }

            // Forward egress -> ingress.
            VcStitch? egress = _node.FindEgress(port, lci);
            if (egress != null)
            {
                Forward(egress, toEgress: false, data, length, qBit, mBit, dBit);
            }
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
                if (!isIngress && !isEgress)
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
                farPort.ClearCall(farLci, CauseOutOfOrder, 0);
                farPort.Counters.IncrementCallsCleared();

                WriteCdr(stitch, CauseOutOfOrder, 0);
                AccountForCallClear(stitch);

                CallClear?.Invoke(port.Id, isIngress ? stitch.InLci : stitch.OutLci, CauseOutOfOrder, 0);
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

            if (fromIngress)
            {
                stitch.OutPort.ResetCircuit(stitch.OutLci, cause, diagnostic);
            }
            else
            {
                stitch.InPort.ResetCircuit(stitch.InLci, cause, diagnostic);
            }
        }

        private void OnInterruptReceived(ISwitchPort port, ushort lci, byte[] userData)
        {
            VcStitch? stitch = _node.FindIngress(port, lci);
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
            // Take the stitch out of service first. A clear confirmation coming back from either leg
            // raises a clear event, and OnCallCleared must find no stitch then, or it would clear the
            // other leg a second time.
            if (!_node.DestroyStitch(stitch))
            {
                return;
            }

            DiscardQueues(stitch);
            stitch.InPort.ClearCall(stitch.InLci, cause, diagnostic);
            stitch.InPort.Counters.IncrementCallsCleared();
            stitch.OutPort.ClearCall(stitch.OutLci, cause, diagnostic);
            stitch.OutPort.Counters.IncrementCallsCleared();

            WriteCdr(stitch, cause, diagnostic);
            AccountForCallClear(stitch);

            CallClear?.Invoke(stitch.InPort.Id, stitch.InLci, cause, diagnostic);
        }

        private void OnCallCleared(ISwitchPort port, ushort lci, byte cause, byte diagnostic)
        {
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

            if (clearedFromIngress)
            {
                stitch.OutPort.ClearCall(stitch.OutLci, cause, diagnostic);
            }
            else
            {
                stitch.InPort.ClearCall(stitch.InLci, cause, diagnostic);
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
                ClearDiagnostic = diagnostic
            };

            CdrRecorded.Invoke(cdr);
        }

        private void AccountForCallSetup(string callingAddress, string calledAddress)
        {
            SwitchAccount? caller = FindAccount(callingAddress);
            if (caller != null)
            {
                caller.CallsMade++;
            }

            SwitchAccount? callee = FindAccount(calledAddress);
            if (callee != null)
            {
                callee.CallsReceived++;
            }
        }

        private void AccountForCallClear(VcStitch stitch)
        {
            ulong durationMs = (ulong)Math.Max(0, NowMs() - stitch.StartTimeMs);

            SwitchAccount? caller = FindAccount(stitch.CallingAddress);
            if (caller != null)
            {
                caller.PacketsSent += stitch.PacketsForward;
                caller.PacketsReceived += stitch.PacketsReverse;
                caller.BytesSent += stitch.BytesForward;
                caller.BytesReceived += stitch.BytesReverse;
                caller.TotalDurationMs += durationMs;
            }
        }

        private SwitchAccount? FindAccount(string address)
        {
            for (int i = 0; i < _node.Accounts.Count; i++)
            {
                if (_node.Accounts[i].Covers(address))
                {
                    return _node.Accounts[i];
                }
            }

            return null;
        }
    }
}
