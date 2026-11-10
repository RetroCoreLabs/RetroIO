//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Packet;

namespace RetroIO.Protocols.Xmsg.ListNames
{
    /// <summary>
    /// Encodes and decodes the message bodies of the XROUT <c>XSGNI</c> ("get next name") exchange
    /// that SINTRAN's XMSG-COMMAND LIST-NAMES uses to walk a name table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A body is what sits on the wire from offset 28 of the SINTRAN datagram, after the 14-byte
    /// SINTRAN header and the 14-byte XMSG sub-header: the 4-byte XROUT header (serial, service,
    /// 16-bit length of what follows) and then the parameter blocks.
    /// </para>
    /// <para>
    /// VERIFIED 2026-11-10 from a complete LIST-NAMES walk, D102 asking D100, decoded byte for byte
    /// from the COSMOS Ethernet hub:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>first request: <c>01 45 00 04 01 02 00 00</c> - serial 1, service 0x45, parameter 1 as a 16-bit zero;</description></item>
    ///   <item><description>every later request: <c>01 45 00 06 01 04 &lt;magic32&gt;</c>, the previous reply's magic plus one;</description></item>
    ///   <item><description>a row: <c>01 00 &lt;len16&gt; 01 04 &lt;magic32&gt; FE &lt;n&gt; &lt;name&gt;</c>, one pad byte <c>00</c> when n is odd (n itself is unchanged and the pad is counted in len16), then <c>03 02 &lt;free16&gt;</c> for a connection port only;</description></item>
    ///   <item><description>end of table: <c>01 00 00 04 01 02 00 00</c>.</description></item>
    /// </list>
    /// The parameter-block layout itself is the general XROUT one, so this class builds and reads
    /// bodies through <see cref="XroutMessage"/> rather than by hand.
    /// </remarks>
    public static class XsgniWire
    {
        /// <summary>
        /// The XROUT service byte of a request, <c>0x45</c> = 69 = <c>XSGNI</c>.
        /// </summary>
        public const byte RequestService = (byte)XroutService.XSGNI;

        /// <summary>
        /// The XROUT service byte of a reply, <c>0x00</c>.
        /// </summary>
        public const byte ReplyService = 0x00;

        /// <summary>
        /// The serial byte SINTRAN LIST-NAMES puts on every request of a walk (<c>0x01</c>).
        /// </summary>
        public const byte DefaultSerial = 0x01;

        /// <summary>
        /// The XMCSM word at wire offset 26 of both request and reply (<c>0x0100</c>).
        /// </summary>
        public const ushort Xmcsm = 0x0100;

        /// <summary>
        /// The wire offset of the body inside a SINTRAN datagram: the SINTRAN header plus the XMSG
        /// sub-header.
        /// </summary>
        public const int BodyOffset = SintranHeader.Size + XmsgSubHeader.Size;

        /// <summary>
        /// Builds a request body that asks for the first name at or after a magic number.
        /// </summary>
        /// <param name="serial">
        /// The XROUT serial byte; the reply echoes it.
        /// </param>
        /// <param name="fromMagic">
        /// The lower bound. <c>0</c> starts a walk.
        /// </param>
        /// <returns>
        /// A new array holding the body from wire offset 28.
        /// </returns>
        /// <remarks>
        /// A walk's first request carries parameter 1 as a 16-bit zero and every later one carries
        /// it as 32 bits, exactly as LIST-NAMES does on the wire. So <c>0</c> is written in the
        /// 16-bit form and anything else in the 32-bit form.
        /// </remarks>
        public static byte[] BuildRequestBody(byte serial, uint fromMagic)
        {
            XroutMessage message = new XroutMessage();
            message.Serial = serial;
            message.Service = RequestService;
            if (fromMagic == 0)
            {
                message.AddParameter(XroutParameter.Integer16(1, 0));
            }
            else
            {
                message.AddParameter(XroutParameter.Integer32(1, fromMagic));
            }

            return message.ToArray(XroutMessageFraming.WithHeader);
        }

        /// <summary>
        /// Builds a reply body for one row, or the end-of-table body.
        /// </summary>
        /// <param name="serial">
        /// The serial byte of the request being answered.
        /// </param>
        /// <param name="entry">
        /// The row to report, or <c>null</c> for the end of the table.
        /// </param>
        /// <returns>
        /// A new array holding the body from wire offset 28.
        /// </returns>
        public static byte[] BuildReplyBody(byte serial, NameTableEntry? entry)
        {
            XroutMessage message = new XroutMessage();
            message.Serial = serial;
            message.Service = ReplyService;

            if (!entry.HasValue)
            {
                // End of table: parameter 1 as a 16-bit zero, nothing else.
                message.AddParameter(XroutParameter.Integer16(1, 0));
                return message.ToArray(XroutMessageFraming.WithHeader);
            }

            NameTableEntry row = entry.Value;
            message.AddParameter(XroutParameter.Integer32(1, row.Magic));
            message.AddParameter(XroutParameter.Text(2, row.Name));
            if (row.HasFreeConnections)
            {
                // XroutMessage puts the fill byte before this block when the name is odd.
                message.AddParameter(XroutParameter.Integer16(3, row.FreeConnections));
            }

            // An odd-length name that ENDS the body is padded too: *XM-ENNS0 and *TADADM both carry a
            // trailing 00, and the 16-bit length counts it (0x12 and 0x10).
            message.PadFinalParameter = true;
            return message.ToArray(XroutMessageFraming.WithHeader);
        }

        /// <summary>
        /// Reads a request body.
        /// </summary>
        /// <param name="body">
        /// The body from wire offset 28.
        /// </param>
        /// <param name="serial">
        /// When this method returns <c>true</c>, the request's serial byte.
        /// </param>
        /// <param name="fromMagic">
        /// When this method returns <c>true</c>, the lower bound asked for (a 16-bit parameter is
        /// widened to 32 bits).
        /// </param>
        /// <returns>
        /// <c>true</c> when the body is an <c>XSGNI</c> request with an integer parameter 1;
        /// otherwise <c>false</c>.
        /// </returns>
        /// <remarks>
        /// A request with NO parameter 1 is refused here. The COSMOS service description says it
        /// asks for the name of the local system, but no capture shows that form, so it is not
        /// answered until one does.
        /// </remarks>
        public static bool TryReadRequestBody(ReadOnlySpan<byte> body, out byte serial, out uint fromMagic)
        {
            serial = 0;
            fromMagic = 0;
            if (body.Length < XroutMessage.HeaderSize || body[1] != RequestService)
            {
                return false;
            }

            XroutMessage message = XroutMessage.Parse(body, XroutMessageFraming.WithHeader);
            XroutParameter? first = Find(message, 1);
            if (first == null || !first.TryGetUInt32(out uint value))
            {
                return false;
            }

            serial = message.Serial;
            fromMagic = value;
            return true;
        }

        /// <summary>
        /// Reads a reply body.
        /// </summary>
        /// <param name="body">
        /// The body from wire offset 28.
        /// </param>
        /// <param name="endOfTable">
        /// When this method returns <c>true</c>, whether the reply is the end-of-table form
        /// (parameter 1 is zero).
        /// </param>
        /// <param name="entry">
        /// When this method returns <c>true</c> and <paramref name="endOfTable"/> is <c>false</c>,
        /// the row.
        /// </param>
        /// <returns>
        /// <c>true</c> when the body is a well-formed reply; otherwise <c>false</c>.
        /// </returns>
        public static bool TryReadReplyBody(ReadOnlySpan<byte> body, out bool endOfTable, out NameTableEntry entry)
        {
            endOfTable = false;
            entry = default;
            if (body.Length < XroutMessage.HeaderSize || body[1] != ReplyService)
            {
                return false;
            }

            XroutMessage message = XroutMessage.Parse(body, XroutMessageFraming.WithHeader);
            XroutParameter? magicParameter = Find(message, 1);
            if (magicParameter == null || !magicParameter.TryGetUInt32(out uint magic))
            {
                return false;
            }

            if (magic == 0)
            {
                endOfTable = true;
                return true;
            }

            XroutParameter? nameParameter = Find(message, 2);
            if (nameParameter == null || !nameParameter.IsString)
            {
                return false;
            }

            XroutParameter? freeParameter = Find(message, 3);
            if (freeParameter != null && freeParameter.TryGetUInt32(out uint free))
            {
                entry = new NameTableEntry(magic, nameParameter.AsText(), (ushort)free);
            }
            else
            {
                entry = new NameTableEntry(magic, nameParameter.AsText());
            }

            return true;
        }

        /// <summary>
        /// Returns the body of a datagram, from wire offset 28.
        /// </summary>
        /// <param name="frame">
        /// The datagram.
        /// </param>
        /// <returns>
        /// A new array holding the body; empty when the frame is shorter than 28 bytes.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="frame"/> is null.
        /// </exception>
        public static byte[] ExtractBody(XmsgFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            // ToArray reproduces the captured bytes verbatim for a decoded frame (RawBytes wins).
            byte[] info = frame.ToArray();
            if (info.Length <= BodyOffset)
            {
                return Array.Empty<byte>();
            }

            return info.AsSpan(BodyOffset).ToArray();
        }

        /// <summary>
        /// Assembles a whole datagram from a header, a sub-header and a body.
        /// </summary>
        /// <param name="header">
        /// The SINTRAN header; its checksum is stamped here.
        /// </param>
        /// <param name="subHeader">
        /// The XMSG sub-header; its XMCSM is set to <see cref="Xmcsm"/>.
        /// </param>
        /// <param name="body">
        /// The body from wire offset 28, XROUT header included.
        /// </param>
        /// <returns>
        /// A new array holding the datagram.
        /// </returns>
        internal static byte[] BuildDatagram(SintranHeader header, XmsgSubHeader subHeader, ReadOnlySpan<byte> body)
        {
            // Word 6 is the ones-complement checksum over words 0-5, never a chosen value.
            XmsgEnvelope.StampChecksum(header);
            subHeader.Xmcsm = Xmcsm;

            byte[] buffer = new byte[BodyOffset + body.Length];
            header.Serialize(buffer);
            subHeader.Serialize(buffer.AsSpan(SintranHeader.Size, XmsgSubHeader.Size));
            body.CopyTo(buffer.AsSpan(BodyOffset));
            return buffer;
        }

        /// <summary>
        /// Finds a parameter by number.
        /// </summary>
        /// <param name="message">
        /// The parsed message.
        /// </param>
        /// <param name="number">
        /// The parameter number.
        /// </param>
        /// <returns>
        /// The parameter, or <c>null</c> when absent.
        /// </returns>
        private static XroutParameter? Find(XroutMessage message, int number)
        {
            for (int i = 0; i < message.Parameters.Count; i++)
            {
                if (message.Parameters[i].ParameterNumber == number)
                {
                    return message.Parameters[i];
                }
            }

            return null;
        }
    }
}
