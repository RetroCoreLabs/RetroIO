namespace RetroIO.Protocols.X25.Switch.Model
{
    /// <summary>
    /// One data packet a switch is holding because the leg it must go out of would not take it.
    /// </summary>
    /// <remarks>
    /// The three bits travel with the bytes because they are part of the packet, not of the send:
    /// the Q bit says the data is for the far PAD rather than the far terminal (X.25 4.3.1), the M
    /// bit says more of the same message follows (4.3.4), and the D bit asks for end-to-end
    /// confirmation (4.3.3). A switch that queued only the bytes would put them back on the wire
    /// as an ordinary packet and change what they mean.
    /// </remarks>
    public sealed class QueuedPacket
    {
        /// <summary>
        /// Creates a held packet.
        /// </summary>
        /// <param name="data">
        /// The packet's user data. Copied by the caller; this class does not copy it again.
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
        public QueuedPacket(byte[] data, bool qBit, bool mBit, bool dBit)
        {
            Data = data;
            QBit = qBit;
            MBit = mBit;
            DBit = dBit;
        }

        /// <summary>The packet's user data.</summary>
        public byte[] Data { get; }

        /// <summary>The qualifier bit.</summary>
        public bool QBit { get; }

        /// <summary>The more-data bit.</summary>
        public bool MBit { get; }

        /// <summary>The delivery-confirmation bit.</summary>
        public bool DBit { get; }
    }
}
