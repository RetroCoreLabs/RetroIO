using System;

using RetroIO.Protocols.Xmsg;

namespace RetroIO.Protocols.Xmsg.ListRouting
{
    /// <summary>
    /// Low-level wire helpers shared by <see cref="ListRoutingClient"/> and
    /// <see cref="ListRoutingServer"/> for assembling and reading the XSGSY
    /// information field (SINTRAN header + XMSG sub-header + raw parameter-block trailer).
    /// </summary>
    /// <remarks>
    /// VERIFIED trailer format (XMSG-PROTOCOL.md section 9.1): the XSGSY trailer is a
    /// sequence of 4-byte records <c>[param-number][length=2][value-hi][value-lo]</c>
    /// with a 16-bit big-endian value.
    /// <para>
    /// CORRECTED 2026-08-04. This used to say the trailer has "NO serial/service/length header -
    /// the service is identified by the sub-header XMCSM word". That was the 13+19 boundary error
    /// talking. The sub-header ends at absolute 27 and the XROUT header IS on the wire at 28-31:
    /// <c>XmcsmService.XsgsyRequest = 0x0100014B</c> decomposes into XMCSM <c>0x0100</c> plus body
    /// word <c>0x014B</c>, which is serial <c>0x01</c> and service <c>0x4B</c> = 75 = XSGSY. The
    /// parameter blocks therefore start at absolute 32 - the same number
    /// <see cref="TrailerOffset"/> always had, reached by a different and now correct sum.
    /// </para>
    /// </remarks>
    internal static class XsgsyWire
    {
        /// <summary>
        /// Size in bytes of one XSGSY parameter block (<c>[num][len=2][hi][lo]</c>).
        /// </summary>
        internal const int ParamBlockSize = 4;

        /// <summary>
        /// Byte offset of the parameter-block trailer inside a data-frame information field:
        /// the 14-byte SINTRAN header, the 14-byte XMSG sub-header, and the 4-byte XROUT header.
        /// </summary>
        internal const int TrailerOffset = SintranHeader.Size + XmsgSubHeader.Size + XroutMessage.HeaderSize;

        /// <summary>
        /// Assembles a full XSGSY information field from a header, sub-header, and trailer.
        /// </summary>
        /// <param name="header">
        /// The SINTRAN header to serialise first.
        /// </param>
        /// <param name="subHeader">
        /// The XMSG sub-header. Its <see cref="XmsgSubHeader.Xmcsm"/> is set from the high half of
        /// <paramref name="controlService"/>.
        /// </param>
        /// <param name="controlService">
        /// The historical 32-bit control/service value: XMCSM in the high half, the XROUT serial
        /// and service bytes in the low half.
        /// </param>
        /// <param name="trailer">
        /// The raw parameter-block trailer bytes.
        /// </param>
        /// <returns>
        /// A new array holding the serialised information field.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="header"/> or <paramref name="subHeader"/> is null.
        /// </exception>
        internal static byte[] BuildInfoField(SintranHeader header, XmsgSubHeader subHeader, uint controlService, ReadOnlySpan<byte> trailer)
        {
            if (header == null)
            {
                throw new ArgumentNullException(nameof(header));
            }

            if (subHeader == null)
            {
                throw new ArgumentNullException(nameof(subHeader));
            }

            subHeader.Xmcsm = (ushort)(controlService >> 16);

            byte[] buffer = new byte[TrailerOffset + trailer.Length];
            header.Serialize(buffer);
            subHeader.Serialize(buffer.AsSpan(SintranHeader.Size, XmsgSubHeader.Size));

            // The XROUT header at absolute 28-31: serial, service, then the big-endian length of
            // what follows. VERIFIED (captures): that length is the trailer byte-length - 4 for a
            // one-parameter request, 16 for a four-parameter response - which is the same number
            // the old model recorded in the phantom "XMLEN" byte at 31.
            int xroutOffset = SintranHeader.Size + XmsgSubHeader.Size;
            // The low 16 bits of the control service are the XROUT serial and service bytes; the
            // high 16 went into Xmcsm above.
            NdEndian.PutBe16(buffer, xroutOffset, (ushort)controlService);
            NdEndian.PutBe16(buffer, xroutOffset + 2, (ushort)trailer.Length);

            trailer.CopyTo(buffer.AsSpan(TrailerOffset));
            return buffer;
        }

        /// <summary>
        /// Writes one XSGSY parameter block into a span.
        /// </summary>
        /// <param name="destination">
        /// The destination span; must have room for at least <see cref="ParamBlockSize"/> bytes.
        /// </param>
        /// <param name="parameterNumber">
        /// The one-based parameter number (byte 0 of the block).
        /// </param>
        /// <param name="value">
        /// The 16-bit big-endian value (bytes 2-3 of the block).
        /// </param>
        internal static void WriteParamBlock(Span<byte> destination, byte parameterNumber, ushort value)
        {
            destination[0] = parameterNumber;
            destination[1] = 0x02; // VERIFIED (captures): every XSGSY value is a 2-byte integer.
            NdEndian.PutBe16(destination, 2, value);
        }

        /// <summary>
        /// Reads the 16-bit value of a named parameter from an XSGSY trailer.
        /// </summary>
        /// <param name="trailer">
        /// The raw parameter-block trailer bytes.
        /// </param>
        /// <param name="parameterNumber">
        /// The one-based parameter number to find.
        /// </param>
        /// <param name="value">
        /// When this method returns <c>true</c>, the parameter's 16-bit value;
        /// otherwise <c>0</c>.
        /// </param>
        /// <returns>
        /// <c>true</c> if the parameter was present; otherwise <c>false</c>.
        /// </returns>
        internal static bool TryReadParam(ReadOnlySpan<byte> trailer, byte parameterNumber, out ushort value)
        {
            int pos = 0;
            while (pos + 2 <= trailer.Length)
            {
                byte number = trailer[pos];
                int length = trailer[pos + 1];
                int dataStart = pos + 2;
                if (dataStart + length > trailer.Length)
                {
                    break; // truncated block - stop scanning.
                }

                if (number == parameterNumber && length >= 2)
                {
                    value = NdEndian.GetBe16(trailer, dataStart);
                    return true;
                }

                pos = dataStart + length;
            }

            value = 0;
            return false;
        }

        /// <summary>
        /// Extracts the parameter-block trailer from a decoded data frame.
        /// </summary>
        /// <param name="frame">
        /// The frame whose trailer is wanted.
        /// </param>
        /// <returns>
        /// A new array holding the trailer bytes (empty if the frame has none).
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="frame"/> is null.
        /// </exception>
        internal static byte[] ExtractTrailer(XmsgFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            // Serialise the frame back to its information field; when the frame was
            // decoded from a capture, ToArray reproduces the original bytes verbatim
            // (RawBytes is authoritative), so the trailer is read from real wire bytes.
            byte[] info = frame.ToArray();
            if (info.Length <= TrailerOffset)
            {
                return Array.Empty<byte>();
            }

            int trailerLength = info.Length - TrailerOffset;
            byte[] trailer = new byte[trailerLength];
            Array.Copy(info, TrailerOffset, trailer, 0, trailerLength);
            return trailer;
        }
    }
}
