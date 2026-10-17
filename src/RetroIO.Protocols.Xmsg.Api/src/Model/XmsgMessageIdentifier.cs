//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// A message-buffer identifier (MESAD) as returned by the reserve and receive functions.
    /// </summary>
    /// <remarks>
    /// Per the manual (section 1.2.4, appendix A section 3.2) most functions take no explicit
    /// message identifier because a CURRENT message is assumed, and the reserved value -1 means
    /// exactly that. Which buffer "current" resolves to depends on the call:
    ///  - for calls that reference a port: the port-current message if one exists, otherwise the
    ///    task-current message.
    ///  - for all other calls: the task-current message.
    /// A message becomes task-current when the task refers to it or receives it, and additionally
    /// port-current when it is received as a SECURE message on that port. Sending or releasing a
    /// buffer loses its currency; the set-current-message function changes it explicitly.
    /// </remarks>
    public readonly struct XmsgMessageIdentifier : IEquatable<XmsgMessageIdentifier>
    {
        /// <summary>
        /// The current (default) message, encoded as -1.
        /// </summary>
        public static readonly XmsgMessageIdentifier Current = new XmsgMessageIdentifier(-1);

        /// <summary>
        /// No message.
        /// </summary>
        public static readonly XmsgMessageIdentifier None = new XmsgMessageIdentifier(0);

        private readonly int _value;

        /// <summary>
        /// Initialises a message identifier from its raw value.
        /// </summary>
        /// <param name="value">
        /// The identifier as carried in the A or D register; -1 means the current message.
        /// </param>
        public XmsgMessageIdentifier(int value)
        {
            _value = value;
        }

        /// <summary>
        /// Gets the raw identifier value.
        /// </summary>
        public int Value
        {
            get { return _value; }
        }

        /// <summary>
        /// Gets a value indicating whether this refers to the current message.
        /// </summary>
        public bool IsCurrent
        {
            get { return _value == -1; }
        }

        /// <summary>
        /// Gets a value indicating whether this refers to no message at all.
        /// </summary>
        public bool IsNone
        {
            get { return _value == 0; }
        }

        /// <summary>
        /// Determines whether this identifier equals another.
        /// </summary>
        /// <param name="other">
        /// The identifier to compare with.
        /// </param>
        /// <returns>
        /// True when both carry the same raw value.
        /// </returns>
        public bool Equals(XmsgMessageIdentifier other)
        {
            return _value == other._value;
        }

        /// <summary>
        /// Determines whether this identifier equals another object.
        /// </summary>
        /// <param name="obj">
        /// The object to compare with.
        /// </param>
        /// <returns>
        /// True when the object is an identifier carrying the same raw value.
        /// </returns>
        public override bool Equals(object? obj)
        {
            return obj is XmsgMessageIdentifier other && Equals(other);
        }

        /// <summary>
        /// Gets a hash code for this identifier.
        /// </summary>
        /// <returns>
        /// The raw value.
        /// </returns>
        public override int GetHashCode()
        {
            return _value;
        }

        /// <summary>
        /// Formats the identifier for diagnostics.
        /// </summary>
        /// <returns>
        /// "current", "none", or the numeric identifier.
        /// </returns>
        public override string ToString()
        {
            if (_value == -1)
            {
                return "current";
            }

            if (_value == 0)
            {
                return "none";
            }

            return _value.ToString();
        }

        /// <summary>
        /// Determines whether two identifiers are equal.
        /// </summary>
        /// <param name="left">
        /// The first identifier.
        /// </param>
        /// <param name="right">
        /// The second identifier.
        /// </param>
        /// <returns>
        /// True when both carry the same raw value.
        /// </returns>
        public static bool operator ==(XmsgMessageIdentifier left, XmsgMessageIdentifier right)
        {
            return left.Equals(right);
        }

        /// <summary>
        /// Determines whether two identifiers differ.
        /// </summary>
        /// <param name="left">
        /// The first identifier.
        /// </param>
        /// <param name="right">
        /// The second identifier.
        /// </param>
        /// <returns>
        /// True when the raw values differ.
        /// </returns>
        public static bool operator !=(XmsgMessageIdentifier left, XmsgMessageIdentifier right)
        {
            return !left.Equals(right);
        }
    }
}
