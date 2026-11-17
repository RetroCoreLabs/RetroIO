//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Switch.Model
{
    /// <summary>
    /// A virtual-circuit stitch: the mapping that ties an ingress circuit on one port to an egress circuit
    /// on another, so packets are forwarded between them in both directions.
    /// </summary>
    /// <remarks>
    /// Forwarding counters are updated by the switching engine on its single processing path; management
    /// surfaces read them for reporting and may observe slightly stale values.
    /// </remarks>
    public sealed class VcStitch
    {
        /// <summary>
        /// Whether the stitch is currently carrying a circuit.
        /// </summary>
        public bool Active { get; set; }

        /// <summary>
        /// The ingress port.
        /// </summary>
        public ISwitchPort InPort { get; }

        /// <summary>
        /// The ingress logical channel.
        /// </summary>
        public ushort InLci { get; }

        /// <summary>
        /// The egress port.
        /// </summary>
        public ISwitchPort OutPort { get; }

        /// <summary>
        /// The egress logical channel.
        /// </summary>
        public ushort OutLci { get; set; }

        /// <summary>
        /// Hop count carried for loop prevention. A permanent (PVC) stitch uses -1 as a sentinel.
        /// </summary>
        public int HopCount { get; set; }

        /// <summary>
        /// A PVC only: the port whose interface is out of order (X.25 4.6), or null while both are up. While it is set,
        /// data or an interrupt from the other end is answered with a reset "Out of order" (4.6.4).
        /// </summary>
        public ISwitchPort? PvcDownPort { get; set; }

        /// <summary>
        /// Calling (origin) X.121 address for accounting and reporting.
        /// </summary>
        public string CallingAddress { get; set; } = string.Empty;

        /// <summary>
        /// Called (destination) X.121 address for accounting and reporting.
        /// </summary>
        public string CalledAddress { get; set; } = string.Empty;

        /// <summary>
        /// Timestamp (milliseconds) when the stitch was created, set by the engine.
        /// </summary>
        public long StartTimeMs { get; set; }

        /// <summary>
        /// The account the caller's side of this call is billed to: the account its NUI names (X.25 6.21), the port's
        /// default NUI's account, or else the account whose address range holds the calling address. Null when none
        /// applies. Chosen once, when the call is routed, so the clear bills the same account as the set-up.
        /// </summary>
        public SwitchAccount? PayingAccount { get; set; }

        /// <summary>
        /// The NUI the call was billed by: the one it named, or the port's default; empty when none (for the call record).
        /// </summary>
        public string PayingNui { get; set; } = string.Empty;

        /// <summary>
        /// The account whose address range holds the called address, or null: the called party's side of the call.
        /// Credited with what it receives and sends, as x25emu does.
        /// </summary>
        public SwitchAccount? CalledAccount { get; set; }

        /// <summary>
        /// When the far end answered (milliseconds, the same clock as <see cref="StartTimeMs"/>); 0 until then. Call
        /// duration for charging runs from here (X.25 7.3.8.4): a call that was never connected lasted no time.
        /// </summary>
        public long ConnectedTimeMs { get; set; }

        /// <summary>
        /// When the call was routed, by the wall clock: seconds since 1970-01-01 UTC (for the call record).
        /// </summary>
        public long StartWallSeconds { get; set; }

        /// <summary>
        /// Segments of data the calling DTE sent (D.12), counted as they arrive on the ingress leg.
        /// </summary>
        /// <remarks>
        /// D.12's 64 octets unless the switch was given another <c>segment_size</c> (set when the stitch is made).
        /// </remarks>
        public SegmentCounter SegmentsForward { get; init; } = new SegmentCounter();

        /// <summary>
        /// Segments of data the called DTE sent (D.12), counted as they arrive on the egress leg.
        /// </summary>
        public SegmentCounter SegmentsReverse { get; init; } = new SegmentCounter();

        /// <summary>
        /// The maximum user data field length of a packet arriving on the ingress leg (X.25 6.12), read once; 0 until read.
        /// </summary>
        public int IngressFullSize { get; set; }

        /// <summary>
        /// The maximum user data field length of a packet arriving on the egress leg, read once; 0 until read.
        /// </summary>
        public int EgressFullSize { get; set; }

        /// <summary>
        /// True when the calling DTE is the DTE to be charged and asked for charging information (X.25 6.22, code 04 bit 1
        /// in its Call Request) on an access port of this switch: it gets the charging answer when the call ends.
        /// </summary>
        public bool ChargingToCaller { get; set; }

        /// <summary>
        /// True when the called DTE is the DTE to be charged (reverse charging accepted, X.25 6.18) and asked for charging
        /// information in its Call Accepted, on an access port of this switch.
        /// </summary>
        public bool ChargingToCallee { get; set; }

        /// <summary>
        /// True when the caller asked for reverse charging (X.25 6.18, code 01 bit 1): the called DTE pays if it accepts.
        /// </summary>
        public bool ReverseCharging { get; set; }

        /// <summary>
        /// Packets forwarded ingress to egress.
        /// </summary>
        public uint PacketsForward { get; set; }

        /// <summary>
        /// Packets forwarded egress to ingress.
        /// </summary>
        public uint PacketsReverse { get; set; }

        /// <summary>
        /// Bytes forwarded ingress to egress.
        /// </summary>
        public ulong BytesForward { get; set; }

        /// <summary>
        /// Bytes forwarded egress to ingress.
        /// </summary>
        public ulong BytesReverse { get; set; }

        /// <summary>
        /// Packets waiting to go out of the egress leg because its window was shut.
        /// </summary>
        /// <remarks>
        /// A refused packet is held here rather than dropped, and the ingress leg is put into RNR
        /// so the sender stops rather than filling this without limit. The queue is emptied when
        /// either leg is reset (X.25 4.4: everything in flight is discarded) and when the stitch
        /// is torn down.
        /// </remarks>
        public Queue<QueuedPacket> ForwardQueue { get; } = new Queue<QueuedPacket>();

        /// <summary>
        /// Packets waiting to go out of the ingress leg because its window was shut.
        /// </summary>
        public Queue<QueuedPacket> ReverseQueue { get; } = new Queue<QueuedPacket>();

        /// <summary>
        /// True while the ingress leg has been told RNR because the forward queue is holding data.
        /// </summary>
        public bool IngressHeldBusy { get; set; }

        /// <summary>
        /// True while the egress leg has been told RNR because the reverse queue is holding data.
        /// </summary>
        public bool EgressHeldBusy { get; set; }

        /// <summary>
        /// Creates a stitch between an ingress and an egress circuit.
        /// </summary>
        /// <param name="inPort">
        /// Ingress port.
        /// </param>
        /// <param name="inLci">
        /// Ingress logical channel.
        /// </param>
        /// <param name="outPort">
        /// Egress port.
        /// </param>
        /// <param name="outLci">
        /// Egress logical channel.
        /// </param>
        public VcStitch(ISwitchPort inPort, ushort inLci, ISwitchPort outPort, ushort outLci)
        {
            InPort = inPort;
            InLci = inLci;
            OutPort = outPort;
            OutLci = outLci;
        }
    }
}
