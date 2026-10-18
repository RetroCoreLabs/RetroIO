//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Protocols.Xmsg.Qform;

namespace RetroIO.Protocols.Xmsg.Fa
{
    /// <summary>
    /// Reads the refusal a file server puts in a reply when it will not do what was asked.
    /// </summary>
    /// <remarks>
    /// <para><b>A refusal is selector 1, and a success omits it</b></para>
    /// The reply body is a QFORM selector/value stream. When the server refuses, it writes selector
    /// <c>1</c> followed by a typed two-byte integer holding a SINTRAN III file-system error number.
    /// When it succeeds, selector 1 is simply not there - there is no zero to read.
    /// <para><b>Measured against a real ND, 2026-08-18</b></para>
    /// A pull of a file that does not exist was answered by D100 on the FIRST step of the ladder:
    /// <code>
    /// OpenFile reply   07F0 0002 81 00 9169 92 0005 92 0002  F2 0001  A2 002E  F2 00FF 00
    /// </code>
    /// <c>0x002E</c> is 46, "NO SUCH FILE NAME" - the same number recorded from the 2026-08-04
    /// captures. A successful 53-block read of the same file server carried no selector 1 anywhere,
    /// so testing for it cannot turn a healthy transfer into a failure.
    /// <para><b>Why this walks the stream instead of indexing a fixed offset</b></para>
    /// The fields ahead of the status differ between servers and between operations - the captures
    /// above carry two class-2 fields whose VALUES change from reply to reply. Counting bytes works
    /// until it does not, and the failure is silent: an offset that lands on the wrong field reads a
    /// number and believes it. The reader already knows how to walk this, so it walks it.
    /// <para>
    /// See <c>DOC\CARVE-FA-READ-REFUSAL-2026-08-18.md</c> for the full carve and the controls.
    /// </para>
    /// </remarks>
    public static class FaRefusalCodec
    {
        /// <summary>
        /// The selector that carries the error number.
        /// </summary>
        public const int StatusSelector = 0x0001;

        /// <summary>
        /// Reads the error number out of a reply body, when the reply is a refusal.
        /// </summary>
        /// <param name="body">
        /// The whole file-access message body, starting at the envelope rather than at the QFORM.
        /// </param>
        /// <param name="status">
        /// Receives the SINTRAN III error number, or zero when the reply is not a refusal.
        /// </param>
        /// <returns>
        /// <c>true</c> when the reply carries a refusal.
        /// </returns>
        /// <remarks>
        /// Returns <c>false</c> for anything it cannot make sense of - a short body, a
        /// stream that will not parse, a selector 1 with no value behind it. A reply that cannot be
        /// read is not the same as a reply that refuses, and guessing either way here would be worse
        /// than leaving the caller to its own timeout.
        /// </remarks>
        public static bool TryReadStatus(ReadOnlySpan<byte> body, out ushort status)
        {
            status = 0;

            if (body.Length <= FaExchangeCodec.QformOffset)
            {
                return false;
            }

            System.Collections.Generic.IReadOnlyList<QformField>? fields;
            ReadOnlySpan<byte> qform = body.Slice(FaExchangeCodec.QformOffset);

            if (!QformReader.TryRead(qform, out fields) || fields == null)
            {
                return false;
            }

            // Walk to the selector that names field 1; its VALUE is the field that follows it.
            // A plain for loop, and the last entry is never a candidate - a selector with nothing
            // behind it is a truncated message, not a refusal.
            for (int i = 0; i < fields.Count - 1; i++)
            {
                QformField field = fields[i];

                if (field.Tag != (byte)QformTagByte.Selector || field.ValueLength != 2)
                {
                    continue;
                }

                int selector = (qform[field.ValueOffset] << 8) | qform[field.ValueOffset + 1];

                if (selector != StatusSelector)
                {
                    continue;
                }

                QformField value = fields[i + 1];

                if (value.Tag != (byte)QformTagByte.TypedInteger || value.ValueLength != 2)
                {
                    return false;
                }

                status = (ushort)((qform[value.ValueOffset] << 8) | qform[value.ValueOffset + 1]);
                return true;
            }

            return false;
        }
    }
}
