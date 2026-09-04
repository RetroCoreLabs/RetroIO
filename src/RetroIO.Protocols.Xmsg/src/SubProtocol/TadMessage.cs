using System;

namespace RetroIO.Protocols.Xmsg.SubProtocol
{
    /// <summary>
    /// A single TAD (protocol <c>0xDD</c>) message: an opcode, a declared data count,
    /// and the captured data bytes.
    /// </summary>
    /// <remarks>
    /// TAD messages are chained as <c>opcode(1) count(1) data(count)</c>. This type is a
    /// read-only decoded view; the authoritative bytes for re-serialisation are retained
    /// by the owning <see cref="XmsgFrame"/> (see <see cref="XmsgFrame.RawBytes"/>).
    /// </remarks>
    public sealed class TadMessage
    {
        /// <summary>
        /// Initialises a TAD message from its decoded fields.
        /// </summary>
        /// <param name="opcode">
        /// The TAD opcode byte.
        /// </param>
        /// <param name="declaredCount">
        /// The count byte as it appears on the wire (may exceed the captured data length
        /// on a truncated frame).
        /// </param>
        /// <param name="data">
        /// The captured data bytes (clamped to what was actually present).
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="data"/> is null.
        /// </exception>
        public TadMessage(byte opcode, byte declaredCount, byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            Opcode = opcode;
            DeclaredCount = declaredCount;
            Data = data;
        }

        /// <summary>
        /// Gets the TAD opcode byte.
        /// </summary>
        public byte Opcode { get; }

        /// <summary>
        /// Gets the mnemonic name of <see cref="Opcode"/>, for example <c>BDAT</c>.
        /// </summary>
        public string OpcodeName
        {
            get { return TadOpcodes.Name(Opcode); }
        }

        /// <summary>
        /// Gets the declared data count byte from the wire.
        /// </summary>
        public byte DeclaredCount { get; }

        /// <summary>
        /// Gets the captured data bytes for this message.
        /// </summary>
        public byte[] Data { get; }

        /// <summary>
        /// Gets a value indicating whether the captured data was truncated relative to the
        /// declared count.
        /// </summary>
        public bool IsTruncated
        {
            get { return Data.Length < DeclaredCount; }
        }
    }
}
