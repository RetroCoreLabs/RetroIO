//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Ethernet
{
    /// <summary>
    /// Builds and parses the IEEE 802.3 + LLC1 frame that carries COSMOS traffic on Ethernet.
    /// </summary>
    /// <remarks>
    /// <para>Frame layout (VERIFIED on the wire 2026-08-01)</para>
    /// Byte offsets from the start of the frame:
    ///  - <c>0</c>..<c>5</c>: destination MAC (see <see cref="NdMacAddress"/>).
    ///  - <c>6</c>..<c>11</c>: source MAC.
    ///  - <c>12</c>..<c>13</c>: the 802.3 LENGTH, big-endian - NOT an EtherType.
    ///  - <c>14</c>: LLC DSAP, <c>0xA8</c>.
    ///  - <c>15</c>: LLC SSAP, <c>0xA8</c>.
    ///  - <c>16</c>: LLC control, <c>0x03</c> = UI (unnumbered information).
    ///  - <c>17</c>..: the LLC payload - an <see cref="NdLinkHeader"/> then the SINTRAN datagram.
    /// <para><b>Length, not EtherType</b></para>
    /// <para>
    /// Bytes 12-13 hold a length, and a value of 1500 or less is exactly what tells a receiver to
    /// parse 802.3/LLC rather than DIX/EtherType. A decoder that reads these as an EtherType looks
    /// up a nonsense value and finds nothing. <see cref="TryParse"/> rejects anything above
    /// <see cref="MaxLlcLength"/> for that reason: such a frame is DIX (the TCP/IP product shares
    /// the same card) and is not ours.
    /// </para>
    /// <para><b>No SNAP</b></para>
    /// <para>
    /// DSAP/SSAP are <c>0xA8</c> directly. There is no <c>AA AA 03</c> SNAP escape and no
    /// OUI/type after the control byte.
    /// </para>
    /// <para><b>Trust the declared length, never the buffer length</b></para>
    /// <para>
    /// The declared length covers the three LLC bytes, so the payload is
    /// <c>length - 3</c> bytes. Short frames are padded to <see cref="MinimumFrameLength"/> and the
    /// padding is not part of the message. In one captured frame there were four non-zero bytes
    /// immediately past the declared end before the zero padding began; their origin is UNKNOWN
    /// (stale transmit-buffer contents is one possibility, not established). Reading past the
    /// declared length would have consumed them as protocol.
    /// </para>
    /// <para>
    /// Doc: <c>SINTRAN/XMSG/DOC/COSMOS-ETHERNET-TRANSPORT-FRAMING-2026-08-01.md</c> section 2.
    /// </para>
    /// </remarks>
    public static class Ieee8023Frame
    {
        /// <summary>
        /// Offset of the 802.3 length field.
        /// </summary>
        public const int LengthFieldOffset = 12;

        /// <summary>
        /// Offset of the LLC header (DSAP, SSAP, control).
        /// </summary>
        public const int LlcOffset = 14;

        /// <summary>
        /// Length of the LLC header in bytes.
        /// </summary>
        public const int LlcHeaderLength = 3;

        /// <summary>
        /// Offset of the LLC payload - where the <see cref="NdLinkHeader"/> starts.
        /// </summary>
        public const int PayloadOffset = LlcOffset + LlcHeaderLength;

        /// <summary>
        /// The ND/COSMOS LLC service access point, used as both DSAP and SSAP.
        /// </summary>
        public const byte NdCosmosSap = 0xA8;

        /// <summary>
        /// LLC control byte for an unnumbered information (UI) frame.
        /// </summary>
        public const byte LlcUnnumberedInformation = 0x03;

        /// <summary>
        /// Largest value the 802.3 length field may hold. Above this the field is an EtherType and
        /// the frame is DIX, not 802.3.
        /// </summary>
        public const int MaxLlcLength = 1500;

        /// <summary>
        /// Minimum Ethernet frame length; shorter frames are padded out to this.
        /// </summary>
        public const int MinimumFrameLength = 60;

        /// <summary>
        /// Builds a complete frame around an LLC payload.
        /// </summary>
        /// <param name="destination">
        /// The destination station address.
        /// </param>
        /// <param name="source">
        /// The source station address.
        /// </param>
        /// <param name="payload">
        /// The LLC payload - an <see cref="NdLinkHeader"/> followed by its data.
        /// </param>
        /// <param name="buffer">
        /// The buffer to build into. Must be at least <see cref="MinimumFrameLength"/> bytes and
        /// large enough for the payload.
        /// </param>
        /// <returns>
        /// The number of bytes written, never less than <see cref="MinimumFrameLength"/>.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="buffer"/> is too small, or when <paramref name="payload"/>
        /// would push the declared length above <see cref="MaxLlcLength"/>.
        /// </exception>
        public static int Build(NdMacAddress destination, NdMacAddress source, ReadOnlySpan<byte> payload, Span<byte> buffer)
        {
            int declaredLength = LlcHeaderLength + payload.Length;
            if (declaredLength > MaxLlcLength)
            {
                throw new ArgumentException(
                    $"Payload of {payload.Length} bytes gives a declared length of {declaredLength}, above the 802.3 maximum of {MaxLlcLength}.",
                    nameof(payload));
            }

            int total = PayloadOffset + payload.Length;
            int framed = total < MinimumFrameLength ? MinimumFrameLength : total;
            if (buffer.Length < framed)
            {
                throw new ArgumentException($"Need {framed} bytes to build this frame, got {buffer.Length}.", nameof(buffer));
            }

            destination.Write(buffer);
            source.Write(buffer.Slice(NdMacAddress.Length));
            buffer[LengthFieldOffset] = (byte)(declaredLength >> 8);
            buffer[LengthFieldOffset + 1] = (byte)(declaredLength & 0xFF);
            buffer[LlcOffset] = NdCosmosSap;
            buffer[LlcOffset + 1] = NdCosmosSap;
            buffer[LlcOffset + 2] = LlcUnnumberedInformation;
            payload.CopyTo(buffer.Slice(PayloadOffset));

            // Pad to the Ethernet minimum. The padding is NOT part of the message - the declared
            // length above is what a receiver must use.
            for (int i = total; i < framed; i++)
            {
                buffer[i] = 0;
            }

            return framed;
        }

        /// <summary>
        /// Parses a received frame and locates its LLC payload.
        /// </summary>
        /// <param name="frame">
        /// The received bytes, starting at the destination MAC. Any trailing padding is ignored.
        /// </param>
        /// <param name="destination">
        /// Receives the destination station address.
        /// </param>
        /// <param name="source">
        /// Receives the source station address.
        /// </param>
        /// <param name="payloadOffset">
        /// Receives the offset of the LLC payload within <paramref name="frame"/>.
        /// </param>
        /// <param name="payloadLength">
        /// Receives the payload length taken from the declared 802.3 length, excluding the LLC
        /// header.
        /// </param>
        /// <returns>
        /// True when the frame is a well-formed ND/COSMOS 802.3 LLC1 UI frame.
        /// </returns>
        /// <remarks>
        /// Returns false - rather than throwing - for a DIX frame, a non-ND SAP, a non-UI control
        /// byte, or a declared length that runs past the end of the buffer. A node sharing a
        /// segment with other traffic will see all of those routinely.
        /// </remarks>
        public static bool TryParse(
            ReadOnlySpan<byte> frame,
            out NdMacAddress destination,
            out NdMacAddress source,
            out int payloadOffset,
            out int payloadLength)
        {
            destination = default;
            source = default;
            payloadOffset = 0;
            payloadLength = 0;

            if (frame.Length < PayloadOffset)
            {
                return false;
            }

            int declaredLength = (frame[LengthFieldOffset] << 8) | frame[LengthFieldOffset + 1];

            // Above 1500 this field is an EtherType, so the frame is DIX and not ours.
            if (declaredLength > MaxLlcLength || declaredLength < LlcHeaderLength)
            {
                return false;
            }

            if (frame[LlcOffset] != NdCosmosSap
                || frame[LlcOffset + 1] != NdCosmosSap
                || frame[LlcOffset + 2] != LlcUnnumberedInformation)
            {
                return false;
            }

            int declaredPayload = declaredLength - LlcHeaderLength;
            if (PayloadOffset + declaredPayload > frame.Length)
            {
                return false;
            }

            destination = new NdMacAddress(frame.Slice(0, NdMacAddress.Length));
            source = new NdMacAddress(frame.Slice(NdMacAddress.Length, NdMacAddress.Length));
            payloadOffset = PayloadOffset;
            payloadLength = declaredPayload;
            return true;
        }
    }
}
