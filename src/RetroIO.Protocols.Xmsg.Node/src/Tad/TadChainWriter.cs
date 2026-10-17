//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Node.Tad
{
    /// <summary>
    /// Send-side builder for a TAD message chain: the encode inverse of
    /// <see cref="SubProtocol.TadChain"/>. Emits <c>[opcode][count][data...]</c> messages
    /// back to back.
    /// </summary>
    /// <remarks>
    /// <para><b>Provenance</b></para>
    /// The <c>[opcode][count][data]</c> framing is VERIFIED (TAD-Message-Formats.md 1). The
    /// odd-byte alignment pad (a <c>0x00</c> inserted only when a message would start on an
    /// odd offset within the buffer) is VERIFIED as a rule but is NOT applied here: every
    /// control/negotiation trailer this writer builds starts at trailer offset 0 (even) and
    /// each observed connect-to control frame carries a single message, so no interior pad
    /// arises. Multi-message packing with interior alignment is a documented gap (see
    /// TAD-MISSING.md) and is intentionally not synthesised.
    /// </remarks>
    public static class TadChainWriter
    {
        /// <summary>
        /// Builds a TAD trailer holding a single message.
        /// </summary>
        /// <param name="opcode">
        /// The TAD opcode byte (message type).
        /// </param>
        /// <param name="data">
        /// The message data bytes (may be empty for a zero-count control message).
        /// </param>
        /// <returns>
        /// A new array: <c>opcode</c>, the data count, then the data bytes.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="data"/> is longer than 255 bytes (the count is a
        /// single byte).
        /// </exception>
        public static byte[] SingleMessage(byte opcode, ReadOnlySpan<byte> data)
        {
            if (data.Length > 255)
            {
                throw new ArgumentException("TAD message data cannot exceed 255 bytes.", nameof(data));
            }

            byte[] trailer = new byte[2 + data.Length];
            trailer[0] = opcode;
            trailer[1] = (byte)data.Length;
            for (int i = 0; i < data.Length; i++)
            {
                trailer[2 + i] = data[i];
            }

            return trailer;
        }
    }
}
