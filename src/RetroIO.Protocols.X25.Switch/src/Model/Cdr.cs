namespace RetroIO.Protocols.X25.Switch.Model
{
    /// <summary>
    /// A Call Detail Record: a summary of one completed circuit, produced when a stitch is torn down.
    /// </summary>
    public sealed class Cdr
    {
        /// <summary>
        /// Start timestamp (milliseconds).
        /// </summary>
        public long StartTimeMs { get; set; }

        /// <summary>
        /// End timestamp (milliseconds).
        /// </summary>
        public long EndTimeMs { get; set; }

        /// <summary>
        /// Originating X.121 address.
        /// </summary>
        public string Calling { get; set; } = string.Empty;

        /// <summary>
        /// Destination X.121 address.
        /// </summary>
        public string Called { get; set; } = string.Empty;

        /// <summary>
        /// Ingress port number.
        /// </summary>
        public int InPort { get; set; }

        /// <summary>
        /// Egress port number.
        /// </summary>
        public int OutPort { get; set; }

        /// <summary>
        /// Ingress logical channel.
        /// </summary>
        public ushort InLci { get; set; }

        /// <summary>
        /// Egress logical channel.
        /// </summary>
        public ushort OutLci { get; set; }

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
        /// Clearing cause code.
        /// </summary>
        public byte ClearCause { get; set; }

        /// <summary>
        /// Diagnostic code.
        /// </summary>
        public byte ClearDiagnostic { get; set; }
    }
}
