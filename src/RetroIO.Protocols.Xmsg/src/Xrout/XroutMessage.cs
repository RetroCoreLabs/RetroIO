//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// An XROUT standard message (the "letter") body: a 4-byte header followed by
    /// even-aligned TLV parameter blocks.
    /// </summary>
    /// <remarks>
    /// <para><b>Header</b></para>
    /// The first four bytes of the message body (XMSG-API.md section 4.1):
    ///  - byte 0 serial number, echoed unchanged by XROUT so replies can be matched.
    ///  - byte 1 service number on request; XROUT overwrites it with the return status on reply.
    ///  - bytes 2-3 length of the remainder of the message in bytes (big-endian).
    /// Every parameter block starts on an even byte boundary; a <c>0x00</c> fill byte
    /// is inserted where needed to realign.
    /// </remarks>
    public sealed class XroutMessage
    {
        /// <summary>
        /// Size of the fixed XROUT message header (serial, service, length) in bytes.
        /// </summary>
        public const int HeaderSize = 4;

        private readonly List<XroutParameter> _parameters;

        /// <summary>
        /// Initialises an empty XROUT message.
        /// </summary>
        public XroutMessage()
        {
            _parameters = new List<XroutParameter>();
        }

        /// <summary>
        /// Gets or sets the serial number (byte 0). The high bit should be 0.
        /// </summary>
        public byte Serial { get; set; }

        /// <summary>
        /// Gets or sets the service number on request, or the return status on reply (byte 1).
        /// </summary>
        public byte Service { get; set; }

        /// <summary>
        /// Gets or sets whether a FINAL parameter of odd length is padded to a word boundary.
        /// Off by default.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the writer's choice, not a rule - two real ND writers disagree</b></para>
        /// <para>
        /// Padding BETWEEN parameters is not optional and is always emitted. A pad after the LAST
        /// parameter is, and the captures show both forms working on the same machine:
        /// </para>
        ///  - <c>XMSG-COMMAND</c> writes <c>*TADADM</c> as <c>FF 07 seven-chars 00</c>, declared
        ///    length 10 - padded although nothing follows.
        ///  - <c>TADADM</c> itself registers as <c>FF 07 seven-chars</c>, declared length 8, with
        ///    no pad. See <c>DOC/XMSG-XSCRS-CONNECTION-PORTS-CAPTURED-2026-07-27.md</c> section 6.
        /// <para>
        /// Both registered and resolved correctly, so neither form is wrong. The default is OFF,
        /// which matches TADADM and is what every existing caller already emitted.
        /// </para>
        /// <para>
        /// Turn it ON only to reproduce a specific captured writer. <c>XftraRequests</c> does,
        /// because the <c>APPEND-REMOTE-BATCH</c> capture ends with the 11-byte
        /// <c>"ARBOUT:SYMB"</c> followed by a pad, and its XMLEN of <c>0x0044</c> COUNTS that pad -
        /// so a request built without it is a byte short in the length as well as on the wire.
        /// </para>
        /// </remarks>
        public bool PadFinalParameter { get; set; }

        /// <summary>
        /// Gets or sets the declared remainder length in bytes (bytes 2-3).
        /// </summary>
        /// <remarks>
        /// This is the value parsed from or computed for the wire. It equals the total
        /// serialised length of all parameter blocks including alignment fill bytes.
        /// </remarks>
        public ushort Length { get; set; }

        /// <summary>
        /// Gets the parameter blocks of this message in order.
        /// </summary>
        public IReadOnlyList<XroutParameter> Parameters
        {
            get { return _parameters; }
        }

        /// <summary>
        /// Appends a parameter block to this message.
        /// </summary>
        /// <param name="parameter">
        /// The parameter to append.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="parameter"/> is null.
        /// </exception>
        public void AddParameter(XroutParameter parameter)
        {
            if (parameter == null)
            {
                throw new ArgumentNullException(nameof(parameter));
            }

            _parameters.Add(parameter);
        }

        /// <summary>
        /// Parses an XROUT standard message from a payload span.
        /// </summary>
        /// <param name="source">
        /// The message-body bytes starting at the serial number; must contain at
        /// least <see cref="HeaderSize"/> bytes.
        /// </param>
        /// <returns>
        /// The parsed <see cref="XroutMessage"/>.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="source"/> is shorter than <see cref="HeaderSize"/> bytes.
        /// </exception>
        public static XroutMessage Parse(ReadOnlySpan<byte> source)
        {
            return Parse(source, XroutMessageFraming.WithHeader);
        }

        /// <summary>
        /// Parses an XROUT message in the requested framing.
        /// </summary>
        /// <param name="source">
        /// The message bytes.
        /// </param>
        /// <param name="framing">
        /// Whether the four-byte header is present. Messages taken off the wire are
        /// <see cref="XroutMessageFraming.BodyOnly"/>; see that type for the evidence.
        /// </param>
        /// <returns>
        /// The parsed <see cref="XroutMessage"/>. With body-only framing the
        /// <see cref="Serial"/> and <see cref="Service"/> properties stay zero, because those
        /// values are not in the bytes - the service is in the frame's XMCSM word instead.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when header framing is requested and <paramref name="source"/> is shorter than
        /// <see cref="HeaderSize"/> bytes.
        /// </exception>
        public static XroutMessage Parse(ReadOnlySpan<byte> source, XroutMessageFraming framing)
        {
            XroutMessage message = new XroutMessage();
            ReadOnlySpan<byte> remainder;

            if (framing == XroutMessageFraming.BodyOnly)
            {
                message.Length = (ushort)source.Length;
                remainder = source;
            }
            else
            {
                if (source.Length < HeaderSize)
                {
                    throw new ArgumentException("XROUT message requires at least a 4-byte header.", nameof(source));
                }

                message.Serial = source[0];
                message.Service = source[1];
                message.Length = NdEndian.GetBe16(source, 2);

                // Bound the remainder by the declared length, clamped to what is present.
                int available = source.Length - HeaderSize;
                int remainderLength = message.Length;
                if (remainderLength > available)
                {
                    remainderLength = available;
                }

                remainder = source.Slice(HeaderSize, remainderLength);
            }

            int pos = 0;
            while (pos < remainder.Length)
            {
                sbyte typeByte = (sbyte)remainder[pos];
                if (typeByte == 0)
                {
                    // Fill/skip byte used to keep the next block on an even boundary.
                    pos++;
                    continue;
                }

                if (pos + 1 >= remainder.Length)
                {
                    // Truncated - no length byte available; stop parsing.
                    break;
                }

                int length = remainder[pos + 1];
                int dataStart = pos + 2;
                if (dataStart + length > remainder.Length)
                {
                    // Truncated data; stop parsing rather than reading past the end.
                    break;
                }

                bool isString = typeByte < 0;
                int parameterNumber = isString ? -typeByte : typeByte;

                byte[] data = new byte[length];
                for (int i = 0; i < length; i++)
                {
                    data[i] = remainder[dataStart + i];
                }

                message._parameters.Add(new XroutParameter(parameterNumber, isString, data));
                pos = dataStart + length;
            }

            return message;
        }

        /// <summary>
        /// Computes the serialised remainder (all parameter blocks with alignment fill).
        /// </summary>
        /// <returns>
        /// A new array holding the parameter blocks as they appear after the 4-byte header.
        /// </returns>
        public byte[] BuildRemainder()
        {
            // First pass: compute the total size including even-alignment fill bytes.
            int size = 0;
            for (int i = 0; i < _parameters.Count; i++)
            {
                if ((size & 1) != 0)
                {
                    size++; // fill byte to reach an even boundary
                }

                size += 2 + _parameters[i].Length; // type + length + data
            }

            // A FINAL parameter of odd length may be padded to a word boundary, and that is the
            // WRITER'S CHOICE - see PadFinalParameter. Off by default, which is the form TADADM
            // itself writes.
            if (PadFinalParameter && (size & 1) != 0)
            {
                size++;
            }

            byte[] buffer = new byte[size];
            int cursor = 0;
            for (int i = 0; i < _parameters.Count; i++)
            {
                XroutParameter parameter = _parameters[i];
                if ((cursor & 1) != 0)
                {
                    buffer[cursor++] = 0x00; // fill byte
                }

                buffer[cursor++] = parameter.TypeByte;
                buffer[cursor++] = (byte)parameter.Length;
                byte[] data = parameter.Data;
                for (int j = 0; j < data.Length; j++)
                {
                    buffer[cursor++] = data[j];
                }
            }

            return buffer;
        }

        /// <summary>
        /// Serialises this message (header plus parameter blocks) into a new array.
        /// </summary>
        /// <returns>
        /// A new array containing the serialised message body.
        /// </returns>
        /// <remarks>
        /// The <see cref="Length"/> property is updated to the computed remainder length.
        /// </remarks>
        public byte[] ToArray()
        {
            return ToArray(XroutMessageFraming.WithHeader);
        }

        /// <summary>
        /// Serialises this message in the requested framing.
        /// </summary>
        /// <param name="framing">
        /// Whether to emit the four-byte header. Use
        /// <see cref="XroutMessageFraming.BodyOnly"/> for bytes destined for an XMSG data frame.
        /// </param>
        /// <returns>
        /// A new array containing the serialised message.
        /// </returns>
        /// <remarks>
        /// The <see cref="Length"/> property is updated to the computed remainder length either
        /// way.
        /// </remarks>
        public byte[] ToArray(XroutMessageFraming framing)
        {
            if (framing == XroutMessageFraming.BodyOnly)
            {
                byte[] body = BuildRemainder();
                Length = (ushort)body.Length;
                return body;
            }

            byte[] remainder = BuildRemainder();
            Length = (ushort)remainder.Length;

            byte[] buffer = new byte[HeaderSize + remainder.Length];
            buffer[0] = Serial;
            buffer[1] = Service;
            NdEndian.PutBe16(buffer, 2, Length);
            for (int i = 0; i < remainder.Length; i++)
            {
                buffer[HeaderSize + i] = remainder[i];
            }

            return buffer;
        }
    }
}
