//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.SubProtocol
{
    /// <summary>
    /// A decoded chain of TAD (protocol <c>0xDD</c>) messages parsed from the payload
    /// that follows a SINTRAN header.
    /// </summary>
    /// <remarks>
    /// <para><b>Chain format</b></para>
    /// The payload is a sequence of <c>opcode(1) count(1) data(count)</c> messages
    /// (hdlc_tcp.lua <c>dissect_tad</c>):
    ///  - a <c>0x00</c> byte between messages is an alignment pad and is skipped.
    ///  - a trailing fragment too short to form a complete message is kept in
    ///    <see cref="Remainder"/> so no byte is lost from the decoded view.
    /// This is a read-only analytical view; byte-identical re-serialisation is guaranteed
    /// by the owning frame's retained raw bytes, not by this type.
    /// </remarks>
    public sealed class TadChain
    {
        private readonly List<TadMessage> _messages;

        /// <summary>
        /// Initialises an empty TAD chain.
        /// </summary>
        public TadChain()
        {
            _messages = new List<TadMessage>();
            Remainder = Array.Empty<byte>();
        }

        /// <summary>
        /// Gets the decoded messages in chain order.
        /// </summary>
        public IReadOnlyList<TadMessage> Messages
        {
            get { return _messages; }
        }

        /// <summary>
        /// Gets any trailing bytes that could not be decoded as a complete message.
        /// </summary>
        public byte[] Remainder { get; private set; }

        /// <summary>
        /// Appends a TAD message (opcode + data bytes) to the chain. Used when BUILDING an
        /// outbound frame (the responder side), the inverse of <see cref="Parse"/>.
        /// </summary>
        /// <param name="opcode">
        /// The TAD opcode (for example <c>0x01</c> = BDAT terminal data).
        /// </param>
        /// <param name="data">
        /// The message data. Must be at most 255 bytes because the on-wire count is a single
        /// byte (TAD-Message-Formats.md). Null is treated as empty.
        /// </param>
        /// <returns>
        /// This chain, for chaining calls.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="data"/> is longer than 255 bytes.
        /// </exception>
        public TadChain Add(byte opcode, byte[]? data)
        {
            byte[] payload = data ?? Array.Empty<byte>();
            if (payload.Length > 255)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(data), "A single TAD message carries at most 255 data bytes (1-byte count).");
            }

            _messages.Add(new TadMessage(opcode, (byte)payload.Length, payload));
            return this;
        }

        /// <summary>
        /// Serialises the chain to its on-wire byte form: for each message <c>opcode(1)</c>,
        /// <c>count(1)</c>, then <c>count</c> data bytes. No alignment pad bytes are emitted
        /// (they are optional and only appear between messages in some captures).
        /// </summary>
        /// <returns>
        /// The serialised TAD payload (goes in the frame after the XMSG sub-header).
        /// </returns>
        public byte[] ToBytes()
        {
            int total = 0;
            for (int i = 0; i < _messages.Count; i++)
            {
                total += 2 + _messages[i].Data.Length;
            }

            byte[] buffer = new byte[total];
            int pos = 0;
            for (int i = 0; i < _messages.Count; i++)
            {
                TadMessage message = _messages[i];
                buffer[pos++] = message.Opcode;
                // Count reflects the actual data length we are emitting.
                buffer[pos++] = (byte)message.Data.Length;
                for (int j = 0; j < message.Data.Length; j++)
                {
                    buffer[pos++] = message.Data[j];
                }
            }

            return buffer;
        }

        /// <summary>
        /// Decodes a TAD message chain from a payload span.
        /// </summary>
        /// <param name="payload">
        /// The bytes following the SINTRAN header for a TAD frame.
        /// </param>
        /// <returns>
        /// The decoded <see cref="TadChain"/>.
        /// </returns>
        public static TadChain Parse(ReadOnlySpan<byte> payload)
        {
            TadChain chain = new TadChain();

            int pos = 0;
            int len = payload.Length;
            while (pos < len)
            {
                // A lone 0x00 is a TAD alignment pad byte, not a message.
                if (payload[pos] == 0x00)
                {
                    pos++;
                    continue;
                }

                // A complete message needs at least opcode + count.
                if (pos + 2 > len)
                {
                    break;
                }

                byte opcode = payload[pos];
                byte count = payload[pos + 1];
                int dataStart = pos + 2;

                int available = count;
                if (dataStart + available > len)
                {
                    available = len - dataStart;
                }

                byte[] data = new byte[available];
                for (int i = 0; i < available; i++)
                {
                    data[i] = payload[dataStart + i];
                }

                chain._messages.Add(new TadMessage(opcode, count, data));

                // Advance by the declared length; an over-long count ends the walk.
                pos = dataStart + count;
            }

            // Keep any undecoded tail (an odd leftover byte) verbatim in the view.
            if (pos < len)
            {
                int tailLength = len - pos;
                byte[] tail = new byte[tailLength];
                for (int i = 0; i < tailLength; i++)
                {
                    tail[i] = payload[pos + i];
                }

                chain.Remainder = tail;
            }

            return chain;
        }
    }
}
