using System;

namespace RetroIO.Protocols.Xmsg.Qform
{
    /// <summary>
    /// One decoded QFORM field: its tag, its class, and where its value sits in the body.
    /// </summary>
    public readonly struct QformField
    {
        /// <summary>
        /// Initialises a decoded field.
        /// </summary>
        /// <param name="tag">
        /// The raw tag byte.
        /// </param>
        /// <param name="valueOffset">
        /// Offset of the value's first byte within the body.
        /// </param>
        /// <param name="valueLength">
        /// Length of the value in bytes.
        /// </param>
        /// <param name="depth">
        /// Nesting depth; 0 is top level, 1 is inside one constructed value.
        /// </param>
        public QformField(byte tag, int valueOffset, int valueLength, int depth)
        {
            Tag = tag;
            ValueOffset = valueOffset;
            ValueLength = valueLength;
            Depth = depth;
        }

        /// <summary>
        /// The raw tag byte as it appears on the wire.
        /// </summary>
        public byte Tag { get; }

        /// <summary>
        /// Offset of the value's first byte within the body.
        /// </summary>
        public int ValueOffset { get; }

        /// <summary>
        /// Length of the value in bytes.
        /// </summary>
        public int ValueLength { get; }

        /// <summary>
        /// Nesting depth. 0 is top level; 1 is inside one constructed value.
        /// </summary>
        public int Depth { get; }

        /// <summary>
        /// The tag's type class, from bits 4 to 6.
        /// </summary>
        public QformClass Class
        {
            get { return (QformClass)((Tag & 0x70) >> 4); }
        }

        /// <summary>
        /// The subtype of a constructed value (class 0), masked with 0x17. Meaningless for other
        /// classes, where the low nibble is a length instead.
        /// </summary>
        public int ConstructedSubtype
        {
            get { return Tag & 0x17; }
        }

        /// <summary>
        /// True when this field's value is itself a tagged QFORM stream.
        /// </summary>
        public bool IsConstructed
        {
            get { return Class == QformClass.Constructed; }
        }
    }
}
