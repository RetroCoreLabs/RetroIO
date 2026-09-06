using System;

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

        /// <summary>
        /// Packet sequence numbering modulus. Only 8 is implemented; 128 (extended) and 32768 (super
        /// extended) are rejected by <see cref="Validate"/> until they exist, rather than silently run
        /// as 8 the way the C emulator does (audit F-02-20).
        /// </summary>
        public int Modulo { get; set; } = 8;

        /// <summary>
        /// Checks the configuration against X.25 and throws with the clause when it cannot be run.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// A value is outside what the Recommendation allows or this engine implements.
        /// </exception>
        /// <remarks>
        /// Until 2026-09-06 nothing was checked: a range running past 4095 handed out impossible
        /// logical channels, a window of 0 blocked every send, and modulo 128 ran as 8.
        /// </remarks>
        public void Validate()
        {
            if (Modulo != 8)
            {
                throw new ArgumentOutOfRangeException(nameof(Modulo), Modulo,
                    "only modulo 8 sequence numbering is implemented (X.25 5.1.2 SS bits 01); 128 and 32768 are not");
            }

            if (LciCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(LciCount), LciCount, "at least one logical channel is needed for virtual calls");
            }

            // X.25 5.1.3: the logical channel number is twelve bits, 0..4095, and 0 is reserved for
            // restart and diagnostic packets (5.1.3.1).
            int first = LciBase == 0 ? 1 : LciBase;
            int last = LciBase + LciCount - 1;
            if (last > 4095)
            {
                throw new ArgumentOutOfRangeException(nameof(LciCount), LciCount,
                    "logical channels run 1..4095 (X.25 5.1.3); base " + LciBase + " + count " + LciCount + " reaches " + last);
            }

            if (first > last)
            {
                throw new ArgumentOutOfRangeException(nameof(LciBase), LciBase, "the channel range is empty");
            }

            if (DefaultWindowSize < 1 || DefaultWindowSize > 7)
            {
                throw new ArgumentOutOfRangeException(nameof(DefaultWindowSize), DefaultWindowSize,
                    "X.25 7.3.1.2: the window is 1..7 with modulo 8; zero is not allowed");
            }

            bool powerOfTwo = DefaultPacketSize >= 16 && DefaultPacketSize <= 4096 && (DefaultPacketSize & (DefaultPacketSize - 1)) == 0;
            if (!powerOfTwo)
            {
                throw new ArgumentOutOfRangeException(nameof(DefaultPacketSize), DefaultPacketSize,
                    "X.25 7.3.1.1: the packet size is a power of two from 16 to 4096");
            }
        }
    }
}
