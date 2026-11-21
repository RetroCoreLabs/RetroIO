//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.PacketLayer;
using RetroIO.Protocols.X25.Transport;

namespace RetroIO.Protocols.X25.Runtime
{
    /// <summary>
    /// Joining a call that came in on one line to a call placed on another line, so the two carry each other's
    /// traffic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Decided by Ronny 2026-11-21 (RetroIO BUGS 32): when the router answers an incoming call with "forward to line
    /// N" (<see cref="X25CallAction.Forward"/>), the runtime places a call on line N and joins the two, rather than
    /// handing such calls to the switch or refusing them. Until then an incoming call went to a local service or was
    /// refused, whatever line its route named.
    /// </para>
    /// <para>
    /// This is not the switch's stitching (<c>VcStitch</c> in the switch package): the switch is one DCE and passes
    /// packets between its ports. Here each line ends X.25 in a packet engine of its own, so the join ties two calls
    /// together, each with its own channel, window and packet size:
    /// </para>
    ///  - The incoming call waits in the incoming-call state until the far line's call is connected; then it is
    ///    accepted (X.25 4.1.3 and 4.1.4). A clear of the far call before that is passed back as the clear of the
    ///    incoming call, with the far side's cause and diagnostic.
    ///  - Data: each complete packet sequence (X.25 4.3.5, the engine's MessageReceived) is sent on the other call as
    ///    one sequence again, cut to that call's packet size. A long sequence arrives in pieces marked "more"; a piece
    ///    is sent as whole packets with M = 1 and the rest is kept for the next piece, because every M = 1 packet must
    ///    be full (4.3.4). The Q bit goes with the data (4.3.3). The D bit is not carried: this engine does not honour
    ///    it (decided 2026-09-06; 4.4.1.4 makes it optional).
    ///  - Flow: while the other call has packets queued that its window holds back, the sending side is told this end
    ///    is busy (Receive Not Ready, 4.4.1.3) and is let go again when the queue is empty, so a fast sender cannot
    ///    fill memory.
    ///  - A clear, a reset and an interrupt on one call are passed to the other (4.1.7, 4.4.3, 4.4.2), with the cause
    ///    and diagnostic unchanged; the engine applies the rule for which causes a DTE may send (Table 5-7).
    /// <para>
    /// Every step that touches the OTHER line runs on the join's own work queue, one step at a time and in order, on a
    /// pool thread. An engine reports its events while it holds its own lock; a step taken there that took the other
    /// line's lock could meet the other line doing the same in the opposite direction, and both would wait for ever.
    /// </para>
    /// <para>
    /// Both circuits are found through the runtime's circuit map under one local channel number reserved for the join:
    /// the far circuit is mapped by the normal call placement (PlaceCallOnEndpoint maps the request's SourceLcn), the
    /// incoming circuit only in the circuit-to-channel direction, so nothing else takes it for a local service.
    /// </para>
    /// </remarks>
    public partial class ProtocolRuntime
    {
        /// <summary>
        /// The joined calls, by the local channel number reserved for each. Guarded by <c>_routingLock</c>.
        /// </summary>
        private readonly Dictionary<ushort, CallJoin> _joins = new Dictionary<ushort, CallJoin>();

        /// <summary>
        /// One incoming call joined to one call placed on another line.
        /// </summary>
        private sealed class CallJoin
        {
            public CallJoin(ushort lcn, X25Endpoint incoming, ushort incomingLci)
            {
                Lcn = lcn;
                Incoming = incoming;
                IncomingLci = incomingLci;
            }

            /// <summary>
            /// Gets the local channel number reserved for this join.
            /// </summary>
            public ushort Lcn { get; }

            /// <summary>
            /// Gets the line the call came in on.
            /// </summary>
            public X25Endpoint Incoming { get; }

            /// <summary>
            /// Gets the incoming call's channel on its line.
            /// </summary>
            public ushort IncomingLci { get; }

            /// <summary>
            /// Gets the packet layer of the placed call; null until the placement is known.
            /// </summary>
            /// <remarks>
            /// Until 2026-11-21 this was the line (an <see cref="X25Endpoint"/>) the call was placed on. A call on an
            /// XOT_Client route has no line here, only its own packet layer on its own connection
            /// (<see cref="XotOutgoingCall"/>), so the join now holds the layer and its lock, which both kinds have.
            /// </remarks>
            public X25Layer? OutgoingLayer { get; private set; }

            /// <summary>
            /// Gets the lock every use of <see cref="OutgoingLayer"/> holds.
            /// </summary>
            public object? OutgoingSync { get; private set; }

            /// <summary>
            /// Gets the placed call's channel.
            /// </summary>
            public ushort OutgoingLci { get; private set; }

            /// <summary>
            /// Gets or sets the XOT call that carries the placed call when it went on an XOT_Client route; null for a
            /// call on a line.
            /// </summary>
            public XotOutgoingCall? OutgoingXot { get; set; }

            /// <summary>
            /// Gets whether the placed call is known.
            /// </summary>
            public bool HasOutgoing => OutgoingLayer != null;

            /// <summary>
            /// Records where the placed call is.
            /// </summary>
            /// <param name="layer">
            /// Its packet layer.
            /// </param>
            /// <param name="sync">
            /// The lock of that layer.
            /// </param>
            /// <param name="lci">
            /// Its channel.
            /// </param>
            public void SetOutgoing(X25Layer layer, object sync, ushort lci)
            {
                OutgoingLayer = layer;
                OutgoingSync = sync;
                OutgoingLci = lci;
            }

            /// <summary>
            /// Gets one side's layer, lock and channel.
            /// </summary>
            /// <param name="incoming">
            /// True for the incoming call, false for the placed one.
            /// </param>
            /// <param name="layer">
            /// The packet layer.
            /// </param>
            /// <param name="sync">
            /// Its lock.
            /// </param>
            /// <param name="lci">
            /// The channel.
            /// </param>
            /// <returns>
            /// False when the placed call is asked for and is not known yet.
            /// </returns>
            public bool TrySide(bool incoming, out X25Layer layer, out object sync, out ushort lci)
            {
                if (incoming)
                {
                    layer = Incoming.Layer;
                    sync = Incoming.Sync;
                    lci = IncomingLci;
                    return true;
                }

                layer = OutgoingLayer!;
                sync = OutgoingSync!;
                lci = OutgoingLci;
                return OutgoingLayer != null;
            }

            /// <summary>
            /// Gets or sets whether the placed call is connected and the incoming one accepted.
            /// </summary>
            public bool Connected { get; set; }

            /// <summary>
            /// Gets or sets whether the join is over (either call cleared); later steps do nothing.
            /// </summary>
            public bool Ended { get; set; }

            /// <summary>
            /// Gets or sets the cause the join ended with (the clear of one side); used to clear a far call that
            /// connects after the join has ended.
            /// </summary>
            public byte EndCause { get; set; }

            /// <summary>
            /// Gets or sets the diagnostic the join ended with.
            /// </summary>
            public byte EndDiagnostic { get; set; }

            /// <summary>
            /// Gets the part of a long sequence from the incoming side not yet sent (less than one packet).
            /// </summary>
            public List<byte> CarryFromIncoming { get; } = new List<byte>();

            /// <summary>
            /// Gets the part of a long sequence from the outgoing side not yet sent (less than one packet).
            /// </summary>
            public List<byte> CarryFromOutgoing { get; } = new List<byte>();

            /// <summary>
            /// Gets or sets whether the incoming side is held busy because the outgoing side's queue is full.
            /// </summary>
            public bool IncomingHeld { get; set; }

            /// <summary>
            /// Gets or sets whether the outgoing side is held busy because the incoming side's queue is full.
            /// </summary>
            public bool OutgoingHeld { get; set; }

            /// <summary>
            /// Gets the steps waiting to run, in order. Guarded by this object.
            /// </summary>
            public Queue<Action> Work { get; } = new Queue<Action>();

            /// <summary>
            /// Gets or sets whether a pool thread is running the steps. Guarded by this object.
            /// </summary>
            public bool Running { get; set; }
        }

        /// <summary>
        /// Starts a join for an incoming call the router forwards to another line: reserves a local channel, maps the
        /// incoming circuit to it, and places the call on the chosen line from the join's work queue.
        /// </summary>
        /// <remarks>
        /// Runs inside the incoming line's engine lock (the IncomingCall event), so the placement - which may open a
        /// TCP connection - is left to the work queue. The incoming call stays in the incoming-call state meanwhile; the
        /// line's T11 (180 s, Table D.1) clears it if no answer comes, and the join then clears the placed call.
        /// </remarks>
        /// <param name="endpoint">
        /// The line the call came in on.
        /// </param>
        /// <param name="lci">
        /// The call's channel there.
        /// </param>
        /// <param name="request">
        /// The incoming call.
        /// </param>
        /// <param name="outPort">
        /// The router's port number of the line to place the call on.
        /// </param>
        private void StartJoin(X25Endpoint endpoint, ushort lci, X25CallRequest request, int outPort)
        {
            CallJoin join;
            lock (_routingLock)
            {
                ushort lcn = FindAvailableLCN();
                if (lcn == 0)
                {
                    _logger.LogWarning("Joined call to {Called}: no free local channel - number busy", request.CalledAddress);
                    endpoint.Layer.ClearCall(lci, X25Cause.NumberBusy, X25Diagnostic.NoLogicalChannelAvailable);
                    return;
                }

                join = new CallJoin(lcn, endpoint, lci);
                _joins[lcn] = join;
                _localLcnByCircuit[endpoint][lci] = lcn;
            }

            X25CallRequest onward = new X25CallRequest(request.CalledAddress, request.CallingAddress, null, request.CallUserData)
            {
                SourceLcn = join.Lcn,
            };

            RunJoinStep(join, () =>
            {
                // On an XOT_Client route the call gets a connection and a packet layer of its own; the join takes that
                // layer's events from before the Call Request goes out (AttachXotCall), and this step waits here for the
                // answer (up to T21). On a line the call is handed over and its answer comes as an event of the line.
                X25CallOutcome outcome = _x25Router.PlaceCallOnPort(outPort, onward, xot => AttachXotCall(join, xot));
                if (outcome.Kind == X25CallOutcomeKind.HandedToLine || outcome.Kind == X25CallOutcomeKind.Accepted)
                {
                    // Accepted: the XOT call's own Call Connected event has queued the step that accepts the incoming
                    // call, behind this one.
                    _logger.LogInformation("Joined call to {Called}: {Outcome}, local channel {Lcn}", request.CalledAddress, outcome, join.Lcn);
                    return;
                }

                if (join.Ended)
                {
                    // The XOT call's Cleared event (a Clear Indication, or T21 running out) has already ended the join
                    // and queued the clear of the incoming call with its cause and diagnostic.
                    return;
                }

                // Not placed (the line or the connection could not take it), or the XOT connection closed before the
                // answer. X.25 Table 5-7 "Out of order": the called number cannot be reached. A refusal that came with
                // its own cause and diagnostic passes them on.
                byte cause = outcome.Kind == X25CallOutcomeKind.NotPlaced ? X25Cause.OutOfOrder : outcome.Cause;
                byte diagnostic = outcome.Kind == X25CallOutcomeKind.NotPlaced ? X25Diagnostic.None : outcome.Diagnostic;
                _logger.LogWarning("Joined call to {Called} not placed ({Outcome})", request.CalledAddress, outcome);
                EndJoin(join);
                lock (join.Incoming.Sync)
                {
                    join.Incoming.Layer.ClearCall(join.IncomingLci, cause, diagnostic);
                }
            });
        }

        /// <summary>
        /// Finds the join a circuit belongs to, and says which side of it the circuit is.
        /// </summary>
        /// <param name="endpoint">
        /// The line.
        /// </param>
        /// <param name="lci">
        /// The channel on that line.
        /// </param>
        /// <param name="join">
        /// The join, when there is one.
        /// </param>
        /// <param name="isIncoming">
        /// True when the circuit is the incoming call, false when it is the placed one.
        /// </param>
        /// <returns>
        /// True when the circuit belongs to a join.
        /// </returns>
        private bool TryJoinOf(X25Endpoint endpoint, ushort lci, out CallJoin join, out bool isIncoming)
        {
            join = null!;
            isIncoming = false;
            if (!TryLocalLcn(endpoint, lci, out ushort lcn))
            {
                return false;
            }

            lock (_routingLock)
            {
                if (!_joins.TryGetValue(lcn, out CallJoin? found))
                {
                    return false;
                }

                join = found;
                isIncoming = ReferenceEquals(endpoint, found.Incoming) && lci == found.IncomingLci;
                if (!isIncoming && !found.HasOutgoing)
                {
                    found.SetOutgoing(endpoint.Layer, endpoint.Sync, lci);
                }

                return true;
            }
        }

        /// <summary>
        /// Takes a join out of the tables: its channel is free again and neither circuit is found through it.
        /// </summary>
        /// <param name="join">
        /// The join.
        /// </param>
        private void EndJoin(CallJoin join)
        {
            lock (_routingLock)
            {
                join.Ended = true;
                _joins.Remove(join.Lcn);
                if (_localLcnByCircuit.TryGetValue(join.Incoming, out Dictionary<ushort, ushort>? map))
                {
                    map.Remove(join.IncomingLci);
                }
            }

            UnmapCircuit(join.Lcn);
        }

        /// <summary>
        /// Queues a step of a join and makes sure a pool thread runs the queue, one step at a time, in order.
        /// </summary>
        /// <param name="join">
        /// The join.
        /// </param>
        /// <param name="step">
        /// The step; it takes the lock of the line it touches itself.
        /// </param>
        private void RunJoinStep(CallJoin join, Action step)
        {
            lock (join)
            {
                join.Work.Enqueue(step);
                if (join.Running)
                {
                    return;
                }

                join.Running = true;
            }

            ThreadPool.QueueUserWorkItem(_ => DrainJoin(join));
        }

        private void DrainJoin(CallJoin join)
        {
            while (true)
            {
                Action step;
                lock (join)
                {
                    if (join.Work.Count == 0)
                    {
                        join.Running = false;
                        return;
                    }

                    step = join.Work.Dequeue();
                }

                try
                {
                    step();
                }
                catch (Exception ex)
                {
                    // A failed step must not leave the queue marked as running, or no later step would ever run. It is
                    // logged as an error; the join itself goes on with its next step.
                    _logger.LogError(ex, "Joined call on local channel {Lcn}: a step failed", join.Lcn);
                }
            }
        }

        // ------------------------------------------------------------------ events of a joined circuit

        /// <summary>
        /// The placed call is connected: the incoming call is accepted (X.25 4.1.4), and from now on traffic is passed.
        /// </summary>
        private void JoinCallConnected(CallJoin join)
        {
            RunJoinStep(join, () =>
            {
                if (join.Ended)
                {
                    return;
                }

                bool accepted;
                lock (join.Incoming.Sync)
                {
                    accepted = join.Incoming.Layer.AcceptCall(join.IncomingLci);
                }

                if (accepted)
                {
                    join.Connected = true;
                    _logger.LogInformation("Joined call on local channel {Lcn} is connected", join.Lcn);
                    return;
                }

                // The incoming call is gone (its caller cleared it, or T11 ran out); its Cleared event ends the join and
                // clears the placed call.
                _logger.LogWarning("Joined call on local channel {Lcn}: the incoming call could not be accepted", join.Lcn);
            });
        }

        /// <summary>
        /// A complete packet sequence, or a piece of a long one, arrived on one side: it goes out on the other.
        /// </summary>
        private void JoinMessage(CallJoin join, bool fromIncoming, byte[] data, bool qBit, bool moreData)
        {
            RunJoinStep(join, () =>
            {
                if (join.Ended || !join.Connected || !join.TrySide(!fromIncoming, out X25Layer targetLayer, out object targetSync, out ushort targetLci))
                {
                    return;
                }

                List<byte> carry = fromIncoming ? join.CarryFromIncoming : join.CarryFromOutgoing;
                bool held;

                lock (targetSync)
                {
                    carry.AddRange(data);
                    byte[] all = carry.ToArray();
                    carry.Clear();
                    if (moreData)
                    {
                        // Only whole packets may carry M = 1 (X.25 4.3.4); the rest waits for the next piece.
                        int size = targetLayer.TryGetCallParameters(targetLci, out X25CallParameters p) ? p.PacketSizeOut : 128;
                        int whole = all.Length / size * size;
                        for (int i = whole; i < all.Length; i++)
                        {
                            carry.Add(all[i]);
                        }

                        if (whole > 0)
                        {
                            targetLayer.SendMessage(targetLci, all.AsSpan(0, whole).ToArray(), qBit, false, moreFollows: true);
                        }
                    }
                    else
                    {
                        targetLayer.SendMessage(targetLci, all, qBit, false, moreFollows: false);
                    }

                    held = targetLayer.PendingPacketCount(targetLci) > 0;
                }

                if (held)
                {
                    HoldJoinSide(join, holdIncoming: fromIncoming, busy: true);
                }
            });
        }

        /// <summary>
        /// Tells one side of a join that this end is busy, or no longer busy (Receive Not Ready and Receive Ready,
        /// X.25 4.4.1.3).
        /// </summary>
        private void HoldJoinSide(CallJoin join, bool holdIncoming, bool busy)
        {
            if (!join.TrySide(holdIncoming, out X25Layer sideLayer, out object sideSync, out ushort sideLci))
            {
                return;
            }

            bool now = holdIncoming ? join.IncomingHeld : join.OutgoingHeld;
            if (now == busy)
            {
                return;
            }

            lock (sideSync)
            {
                sideLayer.SetBusy(sideLci, busy);
            }

            if (holdIncoming)
            {
                join.IncomingHeld = busy;
            }
            else
            {
                join.OutgoingHeld = busy;
            }
        }

        /// <summary>
        /// One side's window opened: when its queue is empty, the side that was held back for it is let go.
        /// </summary>
        private void JoinWindowOpened(CallJoin join, bool onIncoming)
        {
            RunJoinStep(join, () =>
            {
                if (join.Ended || !join.HasOutgoing || !join.TrySide(onIncoming, out X25Layer sideLayer, out object sideSync, out ushort lci))
                {
                    return;
                }

                int pending;
                lock (sideSync)
                {
                    pending = sideLayer.PendingPacketCount(lci);
                }

                if (pending == 0)
                {
                    // The side whose data was waiting on this one may send again.
                    HoldJoinSide(join, holdIncoming: !onIncoming, busy: false);
                }
            });
        }

        /// <summary>
        /// One side was cleared: the other is cleared with the same cause and diagnostic (X.25 4.1.7), and the join
        /// ends.
        /// </summary>
        private void JoinCleared(CallJoin join, bool onIncoming, byte cause, byte diagnostic)
        {
            // The tables are emptied at once, inside the engine's report, so a new call on the freed channel is never
            // taken for this join.
            join.EndCause = cause;
            join.EndDiagnostic = diagnostic;
            EndJoin(join);

            XotOutgoingCall? xot = join.OutgoingXot;
            if (onIncoming && xot != null && !join.HasOutgoing)
            {
                // The caller cleared while the call on the XOT_Client route waits for its answer. The join's queue is
                // busy with that wait (up to T21), so the far call is cleared here and now: its Call Request is
                // withdrawn with a Clear Request (X.25 4.1.7). Taking the XOT call's lock inside the incoming line's lock
                // cannot deadlock, because the XOT call never takes a line's lock. If the Call Request has not gone out
                // yet the clear is refused, and the Call Connected that may still come is cleared on arrival
                // (AttachXotCall).
                lock (xot.SyncRoot)
                {
                    xot.Layer.ClearCall(xot.Lci, cause, diagnostic);
                }

                return;
            }
            RunJoinStep(join, () =>
            {
                if (!join.TrySide(!onIncoming, out X25Layer otherLayer, out object otherSync, out ushort otherLci))
                {
                    // The incoming call was cleared before the placed call was known: nothing on the far side yet.
                    return;
                }

                lock (otherSync)
                {
                    otherLayer.ClearCall(otherLci, cause, diagnostic);
                }
            });
        }

        /// <summary>
        /// One side was reset: the other is reset with the same cause and diagnostic (X.25 4.4.3); the call stays.
        /// </summary>
        private void JoinReset(CallJoin join, bool onIncoming, byte cause, byte diagnostic)
        {
            RunJoinStep(join, () =>
            {
                if (join.Ended || !join.Connected || !join.TrySide(!onIncoming, out X25Layer otherLayer, out object otherSync, out ushort otherLci))
                {
                    return;
                }

                // 4.4.3: a reset removes the data in both directions, so a part sequence kept here goes too.
                join.CarryFromIncoming.Clear();
                join.CarryFromOutgoing.Clear();
                lock (otherSync)
                {
                    otherLayer.ResetCall(otherLci, cause, diagnostic);
                }
            });
        }

        /// <summary>
        /// An interrupt arrived on one side: it is sent on the other (X.25 4.4.2). The engine confirms the one that
        /// arrived by itself.
        /// </summary>
        private void JoinInterrupt(CallJoin join, bool onIncoming, byte[] data)
        {
            RunJoinStep(join, () =>
            {
                if (join.Ended || !join.Connected || !join.TrySide(!onIncoming, out X25Layer otherLayer, out object otherSync, out ushort otherLci))
                {
                    return;
                }

                lock (otherSync)
                {
                    if (!otherLayer.SendInterrupt(otherLci, data))
                    {
                        // 4.4.2: only one unconfirmed interrupt at a time; a second one before the confirmation cannot
                        // go out. Logged, not hidden.
                        _logger.LogWarning("Joined call on local channel {Lcn}: an interrupt could not be passed on (one is still unconfirmed)", join.Lcn);
                    }
                }
            });
        }

        /// <summary>
        /// Ties a call on an XOT_Client route (its own connection and packet layer, RFC 1613 section 5) to a join, before
        /// its Call Request is sent.
        /// </summary>
        /// <remarks>
        /// Added 2026-11-21. Until then a joined call on such a route was refused as out of order (RetroIO BUGS.md). The
        /// layer's events are the same as a line's, so they go to the same join steps; they are raised while the XOT
        /// call's lock is held, and every step that touches the other side runs on the join's work queue, as for a line.
        /// The XOT call has one channel, so every event of its layer belongs to this join.
        /// </remarks>
        /// <param name="join">
        /// The join.
        /// </param>
        /// <param name="xot">
        /// The XOT call, not yet placed.
        /// </param>
        private void AttachXotCall(CallJoin join, XotOutgoingCall xot)
        {
            X25Layer layer = xot.Layer;
            join.OutgoingXot = xot;
            layer.CallConnected += (l, lci) =>
            {
                join.SetOutgoing(l, xot.SyncRoot, lci);
                if (join.Ended)
                {
                    // The caller gave up before the far end answered: the far call is cleared at once. This runs with
                    // the XOT call's lock held, which the layer needs.
                    l.ClearCall(lci, join.EndCause, join.EndDiagnostic);
                    return;
                }

                JoinCallConnected(join);
            };
            layer.Cleared += (l, lci, cause, diagnostic) =>
            {
                if (join.Ended)
                {
                    return;
                }

                if (!join.HasOutgoing)
                {
                    // Refused (a Clear Indication) or timed out (T21) before it was connected: the incoming call is
                    // cleared with that cause and diagnostic, as for a refused call on a line.
                    join.SetOutgoing(l, xot.SyncRoot, lci);
                }

                JoinCleared(join, false, cause, diagnostic);
            };
            layer.Reset += (l, lci, cause, diagnostic) => JoinReset(join, false, cause, diagnostic);
            layer.MessageReceived += (l, lci, data, qBit, moreData) => JoinMessage(join, false, data, qBit, moreData);
            layer.WindowOpened += (l, lci) => JoinWindowOpened(join, false);
            layer.InterruptReceived += (l, lci, data) => JoinInterrupt(join, false, data);

            // The connection goes when the call is over (the XOT call reports the end once, however it ended).
            xot.CallEnded += (call, cause, diagnostic) => call.Dispose();
        }

        // ------------------------------------------------------------------ engine event handlers used only by joins

        private void OnEndpointMessage(X25Layer layer, ushort lci, byte[] data, bool qBit, bool moreData)
        {
            X25Endpoint? endpoint = EndpointOf(layer);
            if (endpoint != null && TryJoinOf(endpoint, lci, out CallJoin join, out bool isIncoming))
            {
                JoinMessage(join, isIncoming, data, qBit, moreData);
            }
        }

        private void OnEndpointWindowOpened(X25Layer layer, ushort lci)
        {
            X25Endpoint? endpoint = EndpointOf(layer);
            if (endpoint != null && TryJoinOf(endpoint, lci, out CallJoin join, out bool isIncoming))
            {
                JoinWindowOpened(join, isIncoming);
            }
        }

        private void OnEndpointInterrupt(X25Layer layer, ushort lci, byte[] data)
        {
            X25Endpoint? endpoint = EndpointOf(layer);
            if (endpoint != null && TryJoinOf(endpoint, lci, out CallJoin join, out bool isIncoming))
            {
                JoinInterrupt(join, isIncoming, data);
            }
        }
    }
}
