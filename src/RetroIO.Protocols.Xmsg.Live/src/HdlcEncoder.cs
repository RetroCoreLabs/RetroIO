using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Hdlc;

namespace RetroIO.Protocols.Xmsg.Live
{
    /// <summary>
    /// Send-side HDLC framing: the exact inverse of <see cref="HdlcDeframer"/>. Takes an
    /// unstuffed LAPB frame body (address, control and information, without FCS), appends
    /// the FCS-16, byte-stuffs, and wraps the result in <c>0x7E</c> flags.
    /// </summary>
    /// <remarks>
    /// <para><b>Framing</b></para>
    /// Steps, per XMSG-PROTOCOL.md section 2:
    ///  - compute the reflected CRC-16 over the body (poly <c>0x8408</c>, init <c>0xFFFF</c>).
    ///  - transmit FCS = <c>(~crc) AND 0xFFFF</c>, stored low byte first, then high byte.
    ///  - byte-stuff the body+FCS: every <c>0x7E</c> becomes <c>0x7D 0x5E</c> and every
    ///    <c>0x7D</c> becomes <c>0x7D 0x5D</c> (that is, escape then <c>byte XOR 0x20</c>).
    ///  - wrap with a single opening and closing <c>0x7E</c> flag.
    /// The FCS algorithm reuses <see cref="Fcs16"/>, so a frame produced here is guaranteed
    /// to fold to the good residue <see cref="Fcs16.GoodResidue"/> on receive.
    /// </remarks>
    public static class HdlcEncoder
    {
        /// <summary>
        /// The HDLC flag / frame-delimiter byte.
        /// </summary>
        private const byte Flag = 0x7E;

        /// <summary>
        /// The HDLC escape byte.
        /// </summary>
        private const byte Escape = 0x7D;

        /// <summary>
        /// The XOR mask applied to a stuffed byte.
        /// </summary>
        private const byte EscapeMask = 0x20;

        /// <summary>
        /// Encodes an unstuffed LAPB frame body into a complete on-wire HDLC frame.
        /// </summary>
        /// <param name="body">
        /// The LAPB frame body: address, control and information, without the FCS. For a
        /// SABM this is <c>01 3F</c> followed by the sending node number.
        /// </param>
        /// <returns>
        /// A new array holding the full on-wire frame: opening flag, byte-stuffed
        /// body+FCS, closing flag.
        /// </returns>
        public static byte[] Encode(ReadOnlySpan<byte> body)
        {
            // Since 2026-09-06 this delegates to the shared framer in RetroIO.Link.Hdlc - one of
            // five separate HDLC implementations that were merged into it. The wire format is
            // unchanged, and HdlcEncoderTests still asserts the captured SABM anchor byte for byte:
            // body 01 3F 00 64 -> 7E 01 3F 00 64 2E 09 7E.
            //
            // HdlcFramerOptions.Xmsg is what keeps that true. It escapes ONLY the flag and the
            // control escape, per ITU-T X.25 clause 2.2.2.2, and NOT every octet below 0x20 the way
            // the RFC 1662 default ACCM would. The captures settle it: across the whole corpus the
            // only octets ever following 0x7D are 0x5D and 0x5E, so the real ND machines do not
            // ACCM-escape and neither may we.
            return RetroIO.Link.Hdlc.HdlcEncoder.Encode(body, RetroIO.Link.Hdlc.HdlcFramerOptions.Xmsg);
        }
    }
}
