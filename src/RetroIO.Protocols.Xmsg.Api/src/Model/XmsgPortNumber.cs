using System;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// A local port identifier (PORTNO) as assigned by XMSG when a task opens a port.
    /// </summary>
    /// <remarks>
    /// Per the manual (section 1.2.3, appendix A section 3.1) a port number is local to the task,
    /// works like a file number, and carries two reserved values:
    ///  - zero means the DEFAULT port, that is the most recently opened one; XMSG keeps the task's
    ///    ports in a list with the newest on top and substitutes the head of that list.
    ///  - a negative value means ALL ports of the task, accepted only by XFCLS (close ports).
    /// The manual's own worked example - ports 15, 4, 6, 19 opened in that order forming the list
    /// 19-6-4-15 - is what <see cref="Default"/> resolves against at call time; this type does not
    /// resolve it, the kernel does.
    /// </remarks>
    public readonly struct XmsgPortNumber : IEquatable<XmsgPortNumber>
    {
        /// <summary>
        /// The default port (value zero): the task's most recently opened port.
        /// </summary>
        public static readonly XmsgPortNumber Default = new XmsgPortNumber(0);

        /// <summary>
        /// All ports owned by the calling task; accepted only by the close-ports function.
        /// </summary>
        public static readonly XmsgPortNumber All = new XmsgPortNumber(-1);

        private readonly int _value;

        /// <summary>
        /// Initialises a port number from its raw value.
        /// </summary>
        /// <param name="value">
        /// The port identifier; zero for the default port, negative for all ports.
        /// </param>
        public XmsgPortNumber(int value)
        {
            _value = value;
        }

        /// <summary>
        /// Gets the raw port identifier as passed in the A register.
        /// </summary>
        public int Value
        {
            get { return _value; }
        }

        /// <summary>
        /// Gets a value indicating whether this refers to the task's default port.
        /// </summary>
        public bool IsDefault
        {
            get { return _value == 0; }
        }

        /// <summary>
        /// Gets a value indicating whether this refers to all of the task's ports.
        /// </summary>
        public bool IsAll
        {
            get { return _value < 0; }
        }

        /// <summary>
        /// Determines whether this port number equals another.
        /// </summary>
        /// <param name="other">
        /// The port number to compare with.
        /// </param>
        /// <returns>
        /// True when both carry the same raw value.
        /// </returns>
        public bool Equals(XmsgPortNumber other)
        {
            return _value == other._value;
        }

        /// <summary>
        /// Determines whether this port number equals another object.
        /// </summary>
        /// <param name="obj">
        /// The object to compare with.
        /// </param>
        /// <returns>
        /// True when the object is a port number carrying the same raw value.
        /// </returns>
        public override bool Equals(object? obj)
        {
            return obj is XmsgPortNumber other && Equals(other);
        }

        /// <summary>
        /// Gets a hash code for this port number.
        /// </summary>
        /// <returns>
        /// The raw value.
        /// </returns>
        public override int GetHashCode()
        {
            return _value;
        }

        /// <summary>
        /// Formats the port number for diagnostics.
        /// </summary>
        /// <returns>
        /// "default", "all", or the numeric identifier.
        /// </returns>
        public override string ToString()
        {
            if (_value == 0)
            {
                return "default";
            }

            if (_value < 0)
            {
                return "all";
            }

            return _value.ToString();
        }

        /// <summary>
        /// Determines whether two port numbers are equal.
        /// </summary>
        /// <param name="left">
        /// The first port number.
        /// </param>
        /// <param name="right">
        /// The second port number.
        /// </param>
        /// <returns>
        /// True when both carry the same raw value.
        /// </returns>
        public static bool operator ==(XmsgPortNumber left, XmsgPortNumber right)
        {
            return left.Equals(right);
        }

        /// <summary>
        /// Determines whether two port numbers differ.
        /// </summary>
        /// <param name="left">
        /// The first port number.
        /// </param>
        /// <param name="right">
        /// The second port number.
        /// </param>
        /// <returns>
        /// True when the raw values differ.
        /// </returns>
        public static bool operator !=(XmsgPortNumber left, XmsgPortNumber right)
        {
            return !left.Equals(right);
        }
    }
}
