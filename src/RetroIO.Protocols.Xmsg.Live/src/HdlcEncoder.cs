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
            // Frame check sequence: the one's-complement of the reflected CRC over the body,
            // stored low byte first. VERIFIED against the SABM anchor (XMSG-PROTOCOL.md
            // section 2): body 01 3F 00 64 yields FCS 0x092E -> wire bytes 2E 09.
            ushort fcs = (ushort)(Fcs16.Compute(body) ^ 0xFFFF);
            byte fcsLow = (byte)(fcs & 0xFF);
            byte fcsHigh = (byte)((fcs >> 8) & 0xFF);

            // Worst case: every body byte and both FCS bytes need escaping (x2), plus two
            // flags. Building into a right-sized list keeps the code simple and correct;
            // the frame sizes here are small (<= ~312 body bytes, XMSG-PROTOCOL.md sec 7).
            List<byte> output = new List<byte>((body.Length + 2) * 2 + 2);
            output.Add(Flag);

            for (int i = 0; i < body.Length; i++)
            {
                Stuff(output, body[i]);
            }

            Stuff(output, fcsLow);
            Stuff(output, fcsHigh);

            output.Add(Flag);
            return output.ToArray();
        }

        /// <summary>
        /// Appends one byte to the output, byte-stuffing it if it is a flag or escape.
        /// </summary>
        /// <param name="output">
        /// The growing output byte list.
        /// </param>
        /// <param name="value">
        /// The raw byte to emit.
        /// </param>
        private static void Stuff(List<byte> output, byte value)
        {
            if (value == Flag || value == Escape)
            {
                output.Add(Escape);
                output.Add((byte)(value ^ EscapeMask));
            }
            else
            {
                output.Add(value);
            }
        }
    }
}
