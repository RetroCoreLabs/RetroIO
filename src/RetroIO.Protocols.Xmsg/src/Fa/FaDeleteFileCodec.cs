//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;

using RetroIO.Protocols.Xmsg.Qform;

namespace RetroIO.Protocols.Xmsg.Fa
{
    /// <summary>
    /// Builds a <c>DeleteFile</c> request, the client half of the operation.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists</b></para>
    /// <para>
    /// The server half has been implemented since August and the reader since before that, but
    /// nothing could ever SEND one - so the sync daemon has never been able to mirror a deletion,
    /// and task B5 in the unknowns register stayed open for want of a builder rather than for want
    /// of knowledge.
    /// </para>
    /// <para><b>Measured, not guessed</b></para>
    /// <para>
    /// Every byte below comes from real traffic between two real machines:
    /// <c>DOC/captures/ARCHIVE-2026-07/2026-07-29-2047-fa-delete-file-d102-to-d100.pcapng</c>, where
    /// D102 deletes <c>XFERTEST:DATA</c> on D100, plus the two requests D100 sent us on 2026-08-06
    /// that <see cref="FaListFilesCodec.TryReadDeleteFileName"/> records.
    /// </para>
    /// <code>
    /// 92 000B  92 0002  F2 0001  BF "XFERTEST:DATA" 27 46  F2 00FF
    /// 92 000B  92 seq   F2 0001  BB "THIRD:TXT"     27 54  F2 00FF
    /// 92 000B  92 seq   F2 0001  BD "NEWFILE:TXT"   27 57  F2 00FF
    /// </code>
    /// <para><b>The byte after the terminator</b></para>
    /// <para>
    /// The name field is always the name, the SINTRAN terminator <c>0x27</c>, and ONE more byte -
    /// so the field is the name length plus two. That byte VARIES across all four samples we hold
    /// (<c>T</c>, <c>W</c>, <c>F</c>, <c>R</c>) and no rule has been found for it: it is not a
    /// constant, not padding - the field is odd-length three times out of four - and not derivable
    /// from the name.
    /// </para>
    /// <para>
    /// So this writes a fixed <see cref="TrailingByte"/> and says plainly that the value is
    /// arbitrary. That is safe on the evidence we have: our own reader stops at the terminator, and
    /// a real server must do the same or it could not accept four different values. If a live
    /// server ever refuses one of ours, THAT is the thing to change, and this remark is where to
    /// look.
    /// </para>
    /// </remarks>
    public static class FaDeleteFileCodec
    {
        /// <summary>
        /// The selector that introduces the file name.
        /// </summary>
        /// <remarks>
        /// ONE, not two. <c>CreateFile</c> uses selector 2 for its name and it is easy to copy the
        /// wrong constant across - the captures show <c>F2 0001</c> for every delete.
        /// </remarks>
        public const ushort NameSelector = 0x0001;

        /// <summary>
        /// The SINTRAN string terminator that closes a file name.
        /// </summary>
        public const byte NameTerminator = 0x27;

        /// <summary>
        /// The one byte written after the terminator.
        /// </summary>
        /// <remarks>
        /// Arbitrary - see the remarks on the class. A space is chosen because it is visible in a
        /// hex dump as something deliberate rather than as a stray zero, and because zero is what a
        /// truncated buffer looks like.
        /// </remarks>
        public const byte TrailingByte = 0x20;

        /// <summary>
        /// Builds the request that asks a server to delete a file.
        /// </summary>
        /// <param name="serial">
        /// The exchange sequence, which the reply echoes.
        /// </param>
        /// <param name="fileName">
        /// The file to delete, as the remote machine names it.
        /// </param>
        /// <returns>
        /// The QFORM body, from the operation word onwards.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="fileName"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="fileName"/> is empty, or too long for one QFORM field.
        /// </exception>
        public static byte[] BuildRequest(ushort serial, string fileName)
        {
            if (fileName == null)
            {
                throw new ArgumentNullException(nameof(fileName));
            }

            if (fileName.Length == 0)
            {
                throw new ArgumentException("A delete needs a file name.", nameof(fileName));
            }

            // The field is the name, the terminator and the trailing byte. A QFORM byte string
            // longer than 255 cannot be expressed with a single escaped length, and no real name
            // comes close - refusing is better than emitting a field the peer will misread.
            int fieldLength = fileName.Length + 2;
            if (fieldLength > 0x7F)
            {
                throw new ArgumentException(
                    "The name is too long for one QFORM field: " + fileName.Length + " characters.",
                    nameof(fileName));
            }

            byte[] field = new byte[fieldLength];
            Encoding.ASCII.GetBytes(fileName, 0, fileName.Length, field, 0);
            field[fileName.Length] = NameTerminator;
            field[fileName.Length + 1] = TrailingByte;

            // COMPACT UP TO FIFTEEN BYTES, ESCAPED FROM SIXTEEN - what a real client does, and what
            // every captured delete shows: BB for eleven, BD for thirteen, BF for fifteen. The
            // compact tag carries its length in the low nibble and so costs one byte; the escaped
            // form costs two. Writing the wrong one is not a size bug, it is a different encoding.
            bool compact = fieldLength <= 15;
            int fieldCost = compact ? 1 + fieldLength : 2 + fieldLength;

            // 92 000B | 92 serial | F2 0001 | <byte string> | F2 00FF
            byte[] body = new byte[3 + 3 + 3 + fieldCost + 3];
            QformWriter writer = new QformWriter(body);

            writer.WriteInteger((ushort)FaOperation.DeleteFile);
            writer.WriteInteger(serial);
            writer.WriteSelector(NameSelector);

            if (compact)
            {
                writer.WriteByteStringCompact(field);
            }
            else
            {
                writer.WriteByteString(field);
            }

            writer.WriteEndOfList();
            writer.EnsureComplete("Delete request");

            return body;
        }
    }
}
