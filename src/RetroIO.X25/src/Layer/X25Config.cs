namespace RetroIO.Protocols.X25.PacketLayer
{
    /// <summary>
    /// Construction parameters for an <see cref="X25Layer"/>: addressing, role, logical-channel range,
    /// default negotiated values, and protocol timer durations.
    /// </summary>
    public sealed class X25Config
    {
        /// <summary>
        /// Our local X.121 address, used as the calling address on outgoing calls.
        /// </summary>
        public string LocalAddress { get; set; } = string.Empty;

        /// <summary>
        /// True to act as DTE, false for DCE.
        /// </summary>
        public bool IsDte { get; set; } = true;

        /// <summary>
        /// Lowest logical channel available for switched calls.
        /// </summary>
        public ushort LciBase { get; set; } = 1;

        /// <summary>
        /// Number of switched logical channels available from <see cref="LciBase"/>.
        /// </summary>
        public ushort LciCount { get; set; } = 16;

        /// <summary>
        /// Default packet size in bytes.
        /// </summary>
        public ushort DefaultPacketSize { get; set; } = 128;

        /// <summary>
        /// Default window size.
        /// </summary>
        public byte DefaultWindowSize { get; set; } = 2;

        /// <summary>
        /// Call-request timer (T21) duration in milliseconds.
        /// </summary>
        public int CallRequestTimeoutMs { get; set; } = 200000;

        /// <summary>
        /// Reset-request timer (T22) duration in milliseconds.
        /// </summary>
        public int ResetRequestTimeoutMs { get; set; } = 180000;

        /// <summary>
        /// Clear-request timer (T23) duration in milliseconds.
        /// </summary>
        public int ClearRequestTimeoutMs { get; set; } = 180000;

        /// <summary>
        /// Restart-request timer (T20) duration in milliseconds. X.25 Table D.2: 180 s.
        /// </summary>
        public int RestartTimeoutMs { get; set; } = 180000;

        /// <summary>
        /// How many times an unanswered Restart, Reset or Clear Request is retransmitted before the
        /// engine gives up (X.25 Table D.2 notes 1 and 2 speak of "unsuccessful retries" without a
        /// number; the C emulator plan uses 5 for all three, and so does this).
        /// </summary>
        public int MaxRetransmissions { get; set; } = 5;
    }
}
