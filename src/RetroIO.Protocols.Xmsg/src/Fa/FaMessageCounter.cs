//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Fa
{
    /// <summary>
    /// The session-header counter one side of a file-server conversation stamps on every message it
    /// sends.
    /// </summary>
    /// <remarks>
    /// <para><b>One counter per side, +1 per MESSAGE, bit 7 as a flag</b></para>
    /// <para>
    /// The low seven bits are a plain counter that advances by one for every message this side
    /// sends - requests, replies and file-content messages alike. Bit 7 is NOT part of the count:
    /// it is set on every message EXCEPT the first of a two-message content delivery, which is
    /// what marks the second one as the last. See
    /// <see cref="FaFileDataCodec.LastDataMessageFlag"/>.
    /// </para>
    /// <para><b>Measured on both directions of the same protocol</b></para>
    /// <code>
    /// server sending, capture-read.txt   80 81 82 83 84 05 86 | 87 08 89 | 8A 0B 8C | 8D 0E 8F
    /// client sending, capture-write.txt  80 81 82 83 | 04 85 | 86 | 07 88 | 89 | 0A 8B | 8C
    /// low seven bits, either way         00 01 02 03   04 05   06   07 08   09   0A 0B   0C
    /// </code>
    /// <para>
    /// Reading those bytes raw makes the sequence look like it jumps - <c>05</c> then <c>86</c> -
    /// and that misreading produced two earlier wrong models: replies ECHOING the request's byte,
    /// and content messages counting on a separate counter of their own. A LISTING cannot tell
    /// those apart, because it sends no content messages; the read and write captures can.
    /// </para>
    /// <para><b>Why it is shared rather than written twice</b></para>
    /// <para>
    /// The rule is identical on both sides, and it has already been got wrong twice. Living in one
    /// class means the next correction lands once. <see cref="FaServerConversation"/> and
    /// <see cref="FaClientConversation"/> both draw from one of these.
    /// </para>
    /// </remarks>
    public sealed class FaMessageCounter
    {
        /// <summary>
        /// The next value of the seven-bit counter, before bit 7 is applied.
        /// </summary>
        private byte _next;

        /// <summary>
        /// Takes the next session-header byte and advances the counter.
        /// </summary>
        /// <param name="highBit">
        /// <see langword="true"/> for every message except the FIRST of a two-message content
        /// delivery.
        /// </param>
        /// <returns>
        /// The session-header byte to stamp on the message.
        /// </returns>
        public byte Next(bool highBit)
        {
            byte value = _next;
            _next++;
            return highBit ? (byte)(value | 0x80) : (byte)(value & 0x7F);
        }

        /// <summary>
        /// Gets how many messages have been counted, which is the value the next call will use.
        /// </summary>
        /// <remarks>
        /// The raw seven-bit count, without bit 7. It wraps at 128 exactly as the counter on the
        /// wire does.
        /// </remarks>
        public byte Count
        {
            get { return _next; }
        }
    }
}
