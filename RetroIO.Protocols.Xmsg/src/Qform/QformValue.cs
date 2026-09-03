using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Qform
{
    /// <summary>
    /// Reads the value out of a decoded QFORM field, and finds fields by the selector before them.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists</b></para>
    /// Six codecs each carried a private <c>ReadUInt16</c>, two carried a <c>ReadUInt32</c>, and two
    /// hand-rolled the same loop that walks fields while tracking the selector last seen. All of
    /// that is here once.
    /// <para><b>The selector convention</b></para>
    /// A QFORM body names a value by putting a selector in front of it, so "the file position" is
    /// "the four-byte typed integer that follows selector 1". <see cref="TryFindValue"/> expresses
    /// exactly that, which is what the codecs actually want to ask.
    /// </remarks>
    public static class QformValue
    {
        /// <summary>
        /// Reads a big-endian 16-bit value.
        /// </summary>
        /// <param name="body">
        /// The QFORM body the field was decoded from.
        /// </param>
        /// <param name="field">
        /// The field to read.
        /// </param>
        /// <returns>
        /// The value.
        /// </returns>
        public static ushort ReadUInt16(ReadOnlySpan<byte> body, QformField field)
        {
            return (ushort)((body[field.ValueOffset] << 8) | body[field.ValueOffset + 1]);
        }

        /// <summary>
        /// Reads a big-endian 32-bit value.
        /// </summary>
        /// <param name="body">
        /// The QFORM body the field was decoded from.
        /// </param>
        /// <param name="field">
        /// The field to read.
        /// </param>
        /// <returns>
        /// The value.
        /// </returns>
        public static uint ReadUInt32(ReadOnlySpan<byte> body, QformField field)
        {
            int at = field.ValueOffset;
            return (uint)((body[at] << 24) | (body[at + 1] << 16) | (body[at + 2] << 8) | body[at + 3]);
        }

        /// <summary>
        /// Finds the value carried under a given selector.
        /// </summary>
        /// <param name="body">
        /// The QFORM body.
        /// </param>
        /// <param name="fields">
        /// The decoded fields, in order.
        /// </param>
        /// <param name="selector">
        /// The selector to look behind.
        /// </param>
        /// <param name="valueClass">
        /// The class the value must have.
        /// </param>
        /// <param name="valueLength">
        /// The length the value must have, or zero to accept any length.
        /// </param>
        /// <param name="found">
        /// The field, when one matched.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when a matching value was found.
        /// </returns>
        /// <remarks>
        /// The class and length are part of the question because the same selector number carries
        /// different shapes in different messages - selector 3 is a two-byte access mode in an open
        /// request and a four-byte file size in its reply.
        /// </remarks>
        public static bool TryFindValue(
            ReadOnlySpan<byte> body,
            IReadOnlyList<QformField> fields,
            ushort selector,
            QformClass valueClass,
            int valueLength,
            out QformField found)
        {
            found = default;

            ushort current = 0;
            for (int i = 0; i < fields.Count; i++)
            {
                QformField field = fields[i];

                if (field.Class == QformClass.Selector && field.ValueLength == 2)
                {
                    current = ReadUInt16(body, field);
                    continue;
                }

                if (current != selector || field.Class != valueClass)
                {
                    continue;
                }

                if (valueLength != 0 && field.ValueLength != valueLength)
                {
                    continue;
                }

                found = field;
                return true;
            }

            return false;
        }
    }
}
