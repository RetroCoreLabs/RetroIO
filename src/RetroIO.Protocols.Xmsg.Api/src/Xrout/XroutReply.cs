using System;

using RetroIO.Protocols.Xmsg.ListRouting;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// A reply from XROUT: the request message with byte 1 overwritten by the return status.
    /// </summary>
    /// <remarks>
    /// Appendix B section 2. XROUT reuses the caller's buffer, so a reply is structurally the same
    /// XROUT message as the request - same serial number, same parameter-block format - with two
    /// differences: byte 1 now holds an XR* status instead of the service number, and the parameter
    /// list is whatever the service documents as its OUT parameters.
    /// Because the service number is destroyed by the reply, remember which service you sent; the
    /// serial number in byte 0 is what matches a reply to its request when several are outstanding.
    /// Replies also arrive with XMSG message type XMROU, which distinguishes them from ordinary
    /// task-to-task traffic.
    /// </remarks>
    public sealed class XroutReply
    {
        private readonly XroutMessage _message;

        /// <summary>
        /// Wraps a received XROUT message as a reply.
        /// </summary>
        /// <param name="message">
        /// The message received from XROUT.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="message"/> is null.
        /// </exception>
        public XroutReply(XroutMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            _message = message;
        }

        /// <summary>
        /// Gets the underlying message, for access to the raw parameter blocks.
        /// </summary>
        public XroutMessage Message
        {
            get { return _message; }
        }

        /// <summary>
        /// Gets the serial number echoed from the request.
        /// </summary>
        public byte Serial
        {
            get { return _message.Serial; }
        }

        /// <summary>
        /// Gets the return status XROUT wrote over the service number.
        /// </summary>
        /// <remarks>
        /// Values are in the range 0 to 255 octal; a task may set bit 7 to signal its own result
        /// codes, so a status with the high bit set is a USER status, not an XROUT one.
        /// </remarks>
        public XroutError Status
        {
            get { return (XroutError)_message.Service; }
        }

        /// <summary>
        /// Gets a value indicating whether the service succeeded.
        /// </summary>
        public bool IsSuccess
        {
            get { return _message.Service == (byte)XroutError.XRSOK; }
        }

        /// <summary>
        /// Gets a value indicating whether the status byte carries a user-defined code.
        /// </summary>
        /// <remarks>
        /// XROUT's own service numbers and result codes never set bit 7, so the manual reserves
        /// that bit for user services and user result statuses.
        /// </remarks>
        public bool IsUserStatus
        {
            get { return (_message.Service & 0x80) != 0; }
        }

        /// <summary>
        /// Parses a reply from raw message-buffer bytes.
        /// </summary>
        /// <param name="source">
        /// The user data of the received message buffer.
        /// </param>
        /// <returns>
        /// The parsed reply.
        /// </returns>
        public static XroutReply Parse(ReadOnlySpan<byte> source)
        {
            return Parse(source, XroutMessageFraming.WithHeader);
        }

        /// <summary>
        /// Parses a reply from raw bytes in the requested framing.
        /// </summary>
        /// <param name="source">
        /// The reply bytes.
        /// </param>
        /// <param name="framing">
        /// Whether the four-byte header is present. Bytes taken from an XMSG data frame are
        /// <see cref="XroutMessageFraming.BodyOnly"/>, in which case <see cref="Status"/> is not
        /// meaningful - the service and its status live in the frame's XMCSM word instead.
        /// </param>
        /// <returns>
        /// The parsed reply.
        /// </returns>
        public static XroutReply Parse(ReadOnlySpan<byte> source, XroutMessageFraming framing)
        {
            return new XroutReply(XroutMessage.Parse(source, framing));
        }

        /// <summary>
        /// Finds a parameter by its number.
        /// </summary>
        /// <param name="parameterNumber">
        /// The one-based parameter number to look for.
        /// </param>
        /// <returns>
        /// The parameter, or <c>null</c> when the reply does not carry it. An absent parameter is
        /// meaningful in several services, so treat null as data rather than as an error.
        /// </returns>
        public XroutParameter? Find(int parameterNumber)
        {
            for (int i = 0; i < _message.Parameters.Count; i++)
            {
                XroutParameter parameter = _message.Parameters[i];
                if (parameter.ParameterNumber == parameterNumber)
                {
                    return parameter;
                }
            }

            return null;
        }

        /// <summary>
        /// Reads a string parameter.
        /// </summary>
        /// <param name="parameterNumber">
        /// The one-based parameter number.
        /// </param>
        /// <param name="value">
        /// On return, the decoded ASCII text, or an empty string when absent.
        /// </param>
        /// <returns>
        /// True when the parameter was present and was a string parameter.
        /// </returns>
        public bool TryGetString(int parameterNumber, out string value)
        {
            XroutParameter? parameter = Find(parameterNumber);
            if (parameter == null || !parameter.IsString)
            {
                value = string.Empty;
                return false;
            }

            value = parameter.AsText();
            return true;
        }

        /// <summary>
        /// Reads an integer parameter of any width.
        /// </summary>
        /// <param name="parameterNumber">
        /// The one-based parameter number.
        /// </param>
        /// <param name="value">
        /// On return, the decoded value, or zero when absent.
        /// </param>
        /// <returns>
        /// True when the parameter was present, was an integer parameter, and had a decodable
        /// width of one, two or four bytes.
        /// </returns>
        public bool TryGetInteger(int parameterNumber, out uint value)
        {
            XroutParameter? parameter = Find(parameterNumber);
            if (parameter == null)
            {
                value = 0;
                return false;
            }

            return parameter.TryGetUInt32(out value);
        }

        /// <summary>
        /// Reads a magic-number parameter.
        /// </summary>
        /// <param name="parameterNumber">
        /// The one-based parameter number.
        /// </param>
        /// <param name="magic">
        /// On return, the magic number, or <see cref="XmsgMagicNumber.None"/> when absent.
        /// </param>
        /// <returns>
        /// True when the parameter was present and decodable.
        /// </returns>
        public bool TryGetMagicNumber(int parameterNumber, out XmsgMagicNumber magic)
        {
            uint raw;
            if (!TryGetInteger(parameterNumber, out raw))
            {
                magic = XmsgMagicNumber.None;
                return false;
            }

            magic = new XmsgMagicNumber(raw);
            return true;
        }

        /// <summary>
        /// Interprets this reply as the four-parameter answer of the routing-information service.
        /// </summary>
        /// <param name="entry">
        /// On return, the decoded routing-table entry.
        /// </param>
        /// <returns>
        /// True when parameters 1 to 4 were all present and decodable.
        /// </returns>
        /// <remarks>
        /// Reuses <see cref="RoutingTableEntry"/>, which already models exactly this reply:
        /// parameter 1 the system found, 2 the connection type, 3 the type-dependent extra info,
        /// and 4 the network info whose left byte counts wide-area hops and right byte counts hops.
        /// A returned system number of zero means no system at or above the one requested, which
        /// terminates a walk of the routing table.
        /// </remarks>
        public bool TryGetRoutingEntry(out RoutingTableEntry entry)
        {
            uint system;
            uint connectionType;
            uint extraInfo;
            uint networkInfo;

            if (!TryGetInteger(1, out system)
                || !TryGetInteger(2, out connectionType)
                || !TryGetInteger(3, out extraInfo)
                || !TryGetInteger(4, out networkInfo))
            {
                entry = default;
                return false;
            }

            entry = new RoutingTableEntry(
                (ushort)system,
                (XroutConnectionType)(int)connectionType,
                (ushort)extraInfo,
                (byte)(networkInfo & 0xFF),
                (byte)((networkInfo >> 8) & 0xFF));
            return true;
        }
    }
}
