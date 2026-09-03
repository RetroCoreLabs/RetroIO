using System;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The status returned by an RR-LIB routine.
    /// </summary>
    /// <remarks>
    /// Chapter 4 section 4.8: a normal return is zero, which the PLANC definitions file calls OK;
    /// anything else is a code from the RR* set of appendix E, modelled by <see cref="RrError"/>.
    /// The raw value is kept as well, because this library's own endpoints also report XMSG and
    /// XROUT failures through this type and those are not RR-LIB codes.
    /// Note the deliberate contrast with <see cref="XmsgStatus"/>, where zero means "not
    /// terminated" rather than success. The two layers use opposite conventions and mixing them up
    /// is the classic porting bug.
    /// </remarks>
    public readonly struct RrStatus : IEquatable<RrStatus>
    {
        /// <summary>
        /// The normal return status (zero).
        /// </summary>
        public static readonly RrStatus Ok = new RrStatus(0);

        private readonly int _value;

        /// <summary>
        /// Initialises a status from its raw value.
        /// </summary>
        /// <param name="value">
        /// The value returned by the routine; zero for success.
        /// </param>
        public RrStatus(int value)
        {
            _value = value;
        }

        /// <summary>
        /// Gets the raw status value.
        /// </summary>
        public int Value
        {
            get { return _value; }
        }

        /// <summary>
        /// Gets a value indicating whether the routine succeeded.
        /// </summary>
        public bool IsOk
        {
            get { return _value == 0; }
        }

        /// <summary>
        /// Gets the RR-LIB code this status carries.
        /// </summary>
        /// <returns>
        /// The appendix E code, or <c>null</c> when the value is not one - which happens when the
        /// status is carrying an XMSG or XROUT failure from a layer below instead.
        /// </returns>
        public RrError? RrCode
        {
            get
            {
                if (!System.Enum.IsDefined(typeof(RrError), _value))
                {
                    return null;
                }

                return (RrError)_value;
            }
        }

        /// <summary>
        /// Gets a value indicating whether this status means the whole RR context has been lost.
        /// </summary>
        /// <remarks>
        /// True for the two codes that report XMSG itself gone. After either, every connection
        /// must be treated as closed, outstanding buffers hold indeterminate data, and initialise
        /// is the only call worth making.
        /// </remarks>
        public bool IsContextLost
        {
            get { return _value == (int)RrError.RRERxnru || _value == (int)RrError.RRERxcra; }
        }

        /// <summary>
        /// Determines whether this status equals another.
        /// </summary>
        /// <param name="other">
        /// The status to compare with.
        /// </param>
        /// <returns>
        /// True when both carry the same raw value.
        /// </returns>
        public bool Equals(RrStatus other)
        {
            return _value == other._value;
        }

        /// <summary>
        /// Determines whether this status equals another object.
        /// </summary>
        /// <param name="obj">
        /// The object to compare with.
        /// </param>
        /// <returns>
        /// True when the object is a status carrying the same raw value.
        /// </returns>
        public override bool Equals(object? obj)
        {
            return obj is RrStatus other && Equals(other);
        }

        /// <summary>
        /// Gets a hash code for this status.
        /// </summary>
        /// <returns>
        /// The raw value.
        /// </returns>
        public override int GetHashCode()
        {
            return _value;
        }

        /// <summary>
        /// Formats the status for diagnostics.
        /// </summary>
        /// <returns>
        /// "OK" for success, otherwise the raw error value.
        /// </returns>
        public override string ToString()
        {
            if (_value == 0)
            {
                return "OK";
            }

            RrError? code = RrCode;
            return code == null
                ? "status " + _value.ToString()
                : code.Value.ToString() + " (" + _value.ToString() + ")";
        }

        /// <summary>
        /// Determines whether two statuses are equal.
        /// </summary>
        /// <param name="left">
        /// The first status.
        /// </param>
        /// <param name="right">
        /// The second status.
        /// </param>
        /// <returns>
        /// True when both carry the same raw value.
        /// </returns>
        public static bool operator ==(RrStatus left, RrStatus right)
        {
            return left.Equals(right);
        }

        /// <summary>
        /// Determines whether two statuses differ.
        /// </summary>
        /// <param name="left">
        /// The first status.
        /// </param>
        /// <param name="right">
        /// The second status.
        /// </param>
        /// <returns>
        /// True when the raw values differ.
        /// </returns>
        public static bool operator !=(RrStatus left, RrStatus right)
        {
            return !left.Equals(right);
        }
    }
}
