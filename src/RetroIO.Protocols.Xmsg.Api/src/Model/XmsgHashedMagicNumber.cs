using System;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The 16-bit hashed magic number (RPORT) - an almost-unique abbreviation of a remote port.
    /// </summary>
    /// <remarks>
    /// Returned in the A register by XFPST and XFRCV. The manual (section 1.2.3, appendix A
    /// section 3.1.3) describes it as "a value that is usually unique for the remote port, so it
    /// can be used for a quick check that the message has come from a known partner". It is
    /// therefore a HINT, not an identity: two different ports may collide, so never use it to
    /// authorise anything. The hashing function is not published, so this type stays opaque.
    /// </remarks>
    public readonly struct XmsgHashedMagicNumber : IEquatable<XmsgHashedMagicNumber>
    {
        /// <summary>
        /// The hashed magic number meaning "no remote port".
        /// </summary>
        public static readonly XmsgHashedMagicNumber None = new XmsgHashedMagicNumber(0);

        private readonly ushort _value;

        /// <summary>
        /// Initialises a hashed magic number from its raw 16-bit value.
        /// </summary>
        /// <param name="value">
        /// The opaque 16-bit value as returned in the A register.
        /// </param>
        public XmsgHashedMagicNumber(ushort value)
        {
            _value = value;
        }

        /// <summary>
        /// Gets the raw opaque 16-bit value.
        /// </summary>
        public ushort Value
        {
            get { return _value; }
        }

        /// <summary>
        /// Determines whether this hashed magic number equals another.
        /// </summary>
        /// <param name="other">
        /// The hashed magic number to compare with.
        /// </param>
        /// <returns>
        /// True when both carry the same raw value.
        /// </returns>
        public bool Equals(XmsgHashedMagicNumber other)
        {
            return _value == other._value;
        }

        /// <summary>
        /// Determines whether this hashed magic number equals another object.
        /// </summary>
        /// <param name="obj">
        /// The object to compare with.
        /// </param>
        /// <returns>
        /// True when the object is a hashed magic number carrying the same raw value.
        /// </returns>
        public override bool Equals(object? obj)
        {
            return obj is XmsgHashedMagicNumber other && Equals(other);
        }

        /// <summary>
        /// Gets a hash code for this value.
        /// </summary>
        /// <returns>
        /// The raw value.
        /// </returns>
        public override int GetHashCode()
        {
            return _value;
        }

        /// <summary>
        /// Formats the hashed magic number as four hexadecimal digits.
        /// </summary>
        /// <returns>
        /// The raw value in hexadecimal.
        /// </returns>
        public override string ToString()
        {
            return "0x" + _value.ToString("X4");
        }

        /// <summary>
        /// Determines whether two hashed magic numbers are equal.
        /// </summary>
        /// <param name="left">
        /// The first value.
        /// </param>
        /// <param name="right">
        /// The second value.
        /// </param>
        /// <returns>
        /// True when both carry the same raw value.
        /// </returns>
        public static bool operator ==(XmsgHashedMagicNumber left, XmsgHashedMagicNumber right)
        {
            return left.Equals(right);
        }

        /// <summary>
        /// Determines whether two hashed magic numbers differ.
        /// </summary>
        /// <param name="left">
        /// The first value.
        /// </param>
        /// <param name="right">
        /// The second value.
        /// </param>
        /// <returns>
        /// True when the raw values differ.
        /// </returns>
        public static bool operator !=(XmsgHashedMagicNumber left, XmsgHashedMagicNumber right)
        {
            return !left.Equals(right);
        }
    }
}
