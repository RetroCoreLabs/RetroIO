using System;

namespace RetroIO.Protocols.Xmsg.Qform
{
    /// <summary>
    /// Writes QFORM tagged values into a caller-supplied buffer.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists</b></para>
    /// Five codecs each carried their own private <c>WriteTagged</c>, and every one of them wrote
    /// the tag byte as a bare hex literal. One writer means one place that knows how a tag and its
    /// value are laid out, and the tag comes from <see cref="QformTagByte"/> rather than a number.
    /// <para><b>It is a ref struct on purpose</b></para>
    /// It wraps a <see cref="Span{T}"/> and holds only a position, so it never allocates and cannot
    /// outlive the buffer it writes into.
    /// <para><b>Length forms</b></para>
    /// A constructed value carries its length either directly or behind an escape byte, and the
    /// captures use both. See <see cref="WriteConstructed"/> and
    /// <see cref="WriteConstructedEscaped"/>.
    /// </remarks>
    public ref struct QformWriter
    {
        private readonly Span<byte> _destination;

        private int _position;

        /// <summary>
        /// Initialises a writer over a buffer.
        /// </summary>
        /// <param name="destination">
        /// The buffer to write into. It must be long enough for everything the caller writes.
        /// </param>
        public QformWriter(Span<byte> destination)
        {
            _destination = destination;
            _position = 0;
        }

        /// <summary>
        /// Gets the number of bytes written so far.
        /// </summary>
        public int Position
        {
            get { return _position; }
        }

        /// <summary>
        /// Writes a plain two-byte integer.
        /// </summary>
        /// <param name="value">
        /// The value to write.
        /// </param>
        public void WriteInteger(ushort value)
        {
            WriteTagged(QformTagByte.Integer, value);
        }

        /// <summary>
        /// Writes a typed two-byte integer.
        /// </summary>
        /// <param name="value">
        /// The value to write.
        /// </param>
        public void WriteTypedInteger(ushort value)
        {
            WriteTagged(QformTagByte.TypedInteger, value);
        }

        /// <summary>
        /// Writes a typed four-byte integer.
        /// </summary>
        /// <param name="value">
        /// The value to write.
        /// </param>
        public void WriteTypedInteger32(uint value)
        {
            _destination[_position++] = (byte)QformTagByte.TypedInteger32;
            _destination[_position++] = (byte)(value >> 24);
            _destination[_position++] = (byte)(value >> 16);
            _destination[_position++] = (byte)(value >> 8);
            _destination[_position++] = (byte)value;
        }

        /// <summary>
        /// Writes a selector, which introduces the field that follows it.
        /// </summary>
        /// <param name="value">
        /// The selector number.
        /// </param>
        public void WriteSelector(ushort value)
        {
            WriteTagged(QformTagByte.Selector, value);
        }

        /// <summary>
        /// Writes the selector that ends a list.
        /// </summary>
        public void WriteEndOfList()
        {
            WriteTagged(QformTagByte.Selector, QformReader.EndOfListSelector);
        }

        /// <summary>
        /// The longest string the COMPACT form can carry, its length living in the tag nibble.
        /// </summary>
        /// <remarks>
        /// Anything longer needs <see cref="WriteByteString"/>. This is a property of the
        /// encoding, not a limit on what the protocol accepts - a captured client sent a 62-byte
        /// specification using the long form.
        /// </remarks>
        public const int MaxCompactByteStringLength = 0x0F;

        /// <summary>
        /// Writes a byte string: the tag, a length byte, then the bytes.
        /// </summary>
        /// <param name="value">
        /// The bytes to write. Its length becomes the declared length.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="value"/> is too long for a single length byte.
        /// </exception>
        public void WriteByteString(ReadOnlySpan<byte> value)
        {
            if (value.Length > 0x7F)
            {
                throw new ArgumentException(
                    "A byte string of " + value.Length + " does not fit in one length byte.",
                    nameof(value));
            }

            _destination[_position++] = (byte)QformTagByte.ByteString;
            _destination[_position++] = (byte)value.Length;

            for (int i = 0; i < value.Length; i++)
            {
                _destination[_position++] = value[i];
            }
        }

        /// <summary>
        /// Writes a byte string in the COMPACT form, with its length in the tag's low nibble.
        /// </summary>
        /// <param name="value">
        /// The bytes to write. Must be 1 to 15 bytes - longer strings have no compact form.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="value"/> is empty or longer than 15 bytes.
        /// </exception>
        /// <remarks>
        /// <para><b>Two legal encodings, and real writers use both</b></para>
        /// <para>
        /// The grammar allows a string's length either in the tag's low nibble (<c>BF</c> for
        /// fifteen bytes) or escaped into a following byte (<c>B0 0F</c>). They mean the same
        /// thing.
        /// </para>
        /// <para>
        /// A real client uses the COMPACT form where it fits: the captured OpenFile carries
        /// <c>BF</c> followed by fifteen characters, and switches to <c>B0 10</c> only at sixteen.
        /// <see cref="WriteByteString"/> always escapes, which is what our server replies do.
        /// </para>
        /// <para>
        /// The two are deliberately separate methods. Making <see cref="WriteByteString"/> pick the
        /// compact form was tried on 2026-08-09 and broke eight FA wiring tests, so the escaped
        /// form is load-bearing on the paths that use it. Whether a peer accepts either form for a
        /// short string is UNKNOWN - so requests reproduce what a real client sends, and replies go
        /// on doing what already works.
        /// </para>
        /// </remarks>
        public void WriteByteStringCompact(ReadOnlySpan<byte> value)
        {
            if (value.Length < 1 || value.Length > 0x0F)
            {
                throw new ArgumentException(
                    "The compact form carries 1 to 15 bytes; " + value.Length
                        + " needs WriteByteString.",
                    nameof(value));
            }

            _destination[_position++] = (byte)((byte)QformTagByte.ByteString | (byte)value.Length);

            for (int i = 0; i < value.Length; i++)
            {
                _destination[_position++] = value[i];
            }
        }

        /// <summary>
        /// Writes the header of a constructed value with its length in the following byte.
        /// </summary>
        /// <param name="length">
        /// The length in bytes of the contents that will follow.
        /// </param>
        /// <remarks>
        /// The direct form, as the replies use: <c>8C 4B</c> for a 75-byte listing entry.
        /// </remarks>
        public void WriteConstructed(int length)
        {
            _destination[_position++] = (byte)QformTagByte.Constructed;
            _destination[_position++] = (byte)length;
        }

        /// <summary>
        /// Writes the header of a constructed value with its length behind the escape byte.
        /// </summary>
        /// <param name="length">
        /// The length in bytes of the contents that will follow.
        /// </param>
        /// <remarks>
        /// The escaped form, as the requests use: <c>8C 80 46</c> for a 70-byte block. Both forms
        /// occur in the captures for the same tag, so a reader must accept either and a writer has
        /// to be able to reproduce whichever the peer expects.
        /// </remarks>
        public void WriteConstructedEscaped(int length)
        {
            _destination[_position++] = (byte)QformTagByte.Constructed;
            _destination[_position++] = (byte)QformTagByte.LengthEscape;
            _destination[_position++] = (byte)length;
        }

        /// <summary>
        /// Copies raw bytes through without a tag.
        /// </summary>
        /// <param name="value">
        /// The bytes to copy.
        /// </param>
        /// <remarks>
        /// For content that is already encoded, such as a record built elsewhere.
        /// </remarks>
        public void WriteRaw(ReadOnlySpan<byte> value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                _destination[_position++] = value[i];
            }
        }

        /// <summary>
        /// Throws when the buffer was not filled exactly.
        /// </summary>
        /// <param name="what">
        /// What was being built, for the message.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the bytes written do not fill the buffer.
        /// </exception>
        /// <remarks>
        /// Every body in this protocol is sized up front, so a mismatch is a coding error and the
        /// codecs all checked for it by hand. This is that check, once.
        /// </remarks>
        public void EnsureComplete(string what)
        {
            if (_position != _destination.Length)
            {
                throw new InvalidOperationException(
                    what + " length mismatch: wrote " + _position + " of " + _destination.Length + ".");
            }
        }

        /// <summary>
        /// Writes a tag and a big-endian 16-bit value.
        /// </summary>
        /// <param name="tag">
        /// The tag byte.
        /// </param>
        /// <param name="value">
        /// The value.
        /// </param>
        private void WriteTagged(QformTagByte tag, ushort value)
        {
            _destination[_position++] = (byte)tag;
            _destination[_position++] = (byte)(value >> 8);
            _destination[_position++] = (byte)value;
        }
    }
}
