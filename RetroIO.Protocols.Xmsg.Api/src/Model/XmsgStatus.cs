using System;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The completion status returned by every XMSG function in the T register.
    /// </summary>
    /// <remarks>
    /// The manual (appendix A section 2) defines exactly three outcomes for the T register:
    ///  - a positive number - success, with a function-specific meaning (a message type, a count).
    ///  - zero - the operation was NOT terminated (for example a poll that found nothing).
    ///  - a negative number - an error code from the XE* set (appendix D).
    /// The PLANC/FORTRAN libraries express "normal return" as the symbol XMOK, which is zero for
    /// the library routines because they map a completed call onto zero. This type keeps the raw
    /// T-register value so both readings stay available; use <see cref="IsError"/> rather than
    /// testing for zero.
    /// </remarks>
    public readonly struct XmsgStatus : IEquatable<XmsgStatus>
    {
        /// <summary>
        /// The status meaning "operation not terminated" (T register zero).
        /// </summary>
        public static readonly XmsgStatus NotTerminated = new XmsgStatus(0);

        /// <summary>
        /// A plain successful completion, for functions that return no value of their own.
        /// </summary>
        /// <remarks>
        /// The T register only has to be positive to mean success; functions such as XFCLS carry
        /// no further information in it, so they report with this value.
        /// </remarks>
        public static readonly XmsgStatus Completed = new XmsgStatus(1);

        private readonly int _value;

        /// <summary>
        /// Initialises a status from a raw T-register value.
        /// </summary>
        /// <param name="value">
        /// The signed T-register value as returned by MON 200B.
        /// </param>
        public XmsgStatus(int value)
        {
            _value = value;
        }

        /// <summary>
        /// Gets the raw signed T-register value.
        /// </summary>
        public int Value
        {
            get { return _value; }
        }

        /// <summary>
        /// Gets a value indicating whether the call completed successfully.
        /// </summary>
        /// <remarks>
        /// True for a positive T register. A zero T register is NOT an error but is also not a
        /// completion - see <see cref="IsNotTerminated"/>.
        /// </remarks>
        public bool IsSuccess
        {
            get { return _value > 0; }
        }

        /// <summary>
        /// Gets a value indicating whether the operation was not terminated.
        /// </summary>
        /// <remarks>
        /// Returned when a call without the XFWTF wait option found nothing to do, for example
        /// XFRCV on an empty port queue.
        /// </remarks>
        public bool IsNotTerminated
        {
            get { return _value == 0; }
        }

        /// <summary>
        /// Gets a value indicating whether the call failed.
        /// </summary>
        public bool IsError
        {
            get { return _value < 0; }
        }

        /// <summary>
        /// Gets the error code when the call failed.
        /// </summary>
        /// <returns>
        /// The XE* error code, or <c>null</c> when the call did not fail.
        /// </returns>
        public XmsgError? Error
        {
            get
            {
                if (_value >= 0)
                {
                    return null;
                }

                return (XmsgError)_value;
            }
        }

        /// <summary>
        /// Creates a success status carrying a function-specific positive value.
        /// </summary>
        /// <param name="value">
        /// The positive value to report (a message type, a byte count, a queue length).
        /// </param>
        /// <returns>
        /// A success status.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="value"/> is not positive.
        /// </exception>
        public static XmsgStatus Success(int value)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "A success status must be positive.");
            }

            return new XmsgStatus(value);
        }

        /// <summary>
        /// Creates a failure status from an XE* error code.
        /// </summary>
        /// <param name="error">
        /// The error code to report.
        /// </param>
        /// <returns>
        /// A failure status.
        /// </returns>
        public static XmsgStatus Failure(XmsgError error)
        {
            return new XmsgStatus((int)error);
        }

        /// <summary>
        /// Throws when this status reports an error.
        /// </summary>
        /// <param name="function">
        /// The function that produced the status, used in the exception message.
        /// </param>
        /// <exception cref="XmsgException">
        /// Thrown when <see cref="IsError"/> is true.
        /// </exception>
        public void ThrowIfError(XmsgFunction function)
        {
            if (_value < 0)
            {
                throw new XmsgException(function, (XmsgError)_value);
            }
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
        public bool Equals(XmsgStatus other)
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
            return obj is XmsgStatus other && Equals(other);
        }

        /// <summary>
        /// Gets a hash code for this status.
        /// </summary>
        /// <returns>
        /// The raw value's hash code.
        /// </returns>
        public override int GetHashCode()
        {
            return _value;
        }

        /// <summary>
        /// Formats the status for diagnostics.
        /// </summary>
        /// <returns>
        /// The error symbol for a failure, "NotTerminated" for zero, or the positive value.
        /// </returns>
        public override string ToString()
        {
            if (_value < 0)
            {
                return ((XmsgError)_value).ToString() + " (" + _value.ToString() + ")";
            }

            if (_value == 0)
            {
                return "NotTerminated";
            }

            return "OK (" + _value.ToString() + ")";
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
        public static bool operator ==(XmsgStatus left, XmsgStatus right)
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
        public static bool operator !=(XmsgStatus left, XmsgStatus right)
        {
            return !left.Equals(right);
        }
    }
}
