namespace RetroIO.Protocols.Xmsg.Node.Tad
{
    /// <summary>
    /// The per-frame envelope values a <see cref="TadSession"/> builder needs to assemble a
    /// byte-identical data (subtype <c>0x0E</c>) frame: the SINTRAN header addressing and the
    /// full XMSG sub-header field set.
    /// </summary>
    /// <remarks>
    /// <para><b>Why an explicit context</b></para>
    /// Most sub-header bytes cannot be derived - the per-direction counter, the frame-flags
    /// byte (<c>0x82</c>/<c>0x84</c>/<c>0x86</c>/<c>0x96</c> all seen), the role byte and the
    /// port fields are all runtime-allocated by SINTRAN and only known from capture. So the
    /// builder is given the exact context (OBSERVED values) rather than inventing them; this
    /// keeps the state machine honest - it reproduces what was captured and does not fabricate
    /// envelope bytes it never saw. Only the user-data length (XMLEN) is derived, because it
    /// equals the trailer length in 100% of observed data frames.
    /// </remarks>
    public sealed class TadFrameContext
    {
        /// <summary>
        /// Gets or sets the SINTRAN destination node number (header offsets 4-5).
        /// </summary>
        public ushort DestinationNode { get; set; }

        /// <summary>
        /// Gets or sets the SINTRAN source node number (header offsets 6-7).
        /// </summary>
        public ushort SourceNode { get; set; }

        /// <summary>
        /// Gets or sets the datagram sequence carried in Flags 1 (header offsets 8-9).
        /// </summary>
        public ushort DatagramSequence { get; set; }

        /// <summary>
        /// Gets or sets the frame-class word carried in Flags 2 (header offsets 10-11).
        /// </summary>
        /// <remarks>
        /// OBSERVED values: <c>0x0400</c> on XROUT setup frames, <c>0x0108</c> on DC/TAD
        /// data frames, <c>0x0008</c> on bare-TAD control frames.
        /// </remarks>
        public ushort FrameClass { get; set; }

        /// <summary>
        /// Gets or sets the sub-protocol selector (header offset 12).
        /// </summary>
        public SintranProtocolId ProtocolId { get; set; }

        /// <summary>
        /// Gets or sets the XMSG per-direction counter (sub-header offset 0).
        /// </summary>
        public byte Counter { get; set; }

        /// <summary>
        /// Gets or sets the frame-flags byte (sub-header offset 3).
        /// </summary>
        public byte FrameFlags { get; set; }

        /// <summary>
        /// Gets or sets the role byte (sub-header offset 4).
        /// </summary>
        public byte Role { get; set; }

        /// <summary>
        /// Gets or sets the XMDSY destination system number (sub-header offsets 5-6).
        /// </summary>
        public ushort DestinationSystem { get; set; }

        /// <summary>
        /// Gets or sets the XMDPT destination port (sub-header offsets 7-8).
        /// </summary>
        public ushort DestinationPort { get; set; }

        /// <summary>
        /// Gets or sets the XMSSY source system number (sub-header offsets 9-10).
        /// </summary>
        public ushort SourceSystem { get; set; }

        /// <summary>
        /// Gets or sets the XMSPT source port (sub-header offsets 11-12).
        /// </summary>
        public ushort SourcePort { get; set; }

        /// <summary>
        /// Gets or sets the XMCSM control / service word (sub-header offsets 13-16).
        /// </summary>
        /// <remarks>
        /// OBSERVED dispatch values: <c>0x04000041</c> (XSLET letter to the directory
        /// service), <c>0x01080000</c> (DC/TAD terminal data), <c>0x00080000</c> (bare-TAD
        /// control), <c>0x04000000</c> (XROUT-channel TAD chain).
        /// </remarks>
        public uint ControlService { get; set; }
    }
}
