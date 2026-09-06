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
