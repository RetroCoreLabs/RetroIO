//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Qform;
using RetroIO.Protocols.Xmsg.Sintran;

namespace RetroIO.Protocols.Xmsg.Fa
{
    /// <summary>
    /// Builds and reads the QFORM bodies of <c>OpenFile</c> and <c>CloseFile</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>The exchange</b></para>
    /// Measured in <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-read.txt</c>,
    /// <c>capture-write.txt</c> and <c>capture-open-error.txt</c>:
    /// <code>
    /// open request   92 0005  92 seq  F2 0002  B0 10 "PATCH-FILE:OUT" 27 54  F2 00FF
    /// open reply     92 0005  92 seq  F2 0002  A2 0040  F2 0003  A4 000045F1  F2 00FF
    /// open failure   92 0005  92 seq  F2 0001  A2 002E                        F2 00FF
    /// close request  92 0006  92 seq                                          F2 00FF
    /// close reply    92 0006  92 seq                                          F2 00FF
    /// </code>
    /// <para><b>There is no file handle on the wire</b></para>
    /// The open reply returns a file number under selector 2, and then <b>nothing ever quotes it
    /// again</b> - no read, write or close in any capture carries one. The open file belongs to the
    /// CONVERSATION, so a server remembers it per session rather than expecting the client to name
    /// it.
    /// <para><b>The size is the byte COUNT, and the directory holds one less</b></para>
    /// The open reply's selector 3 is the number of bytes in the file. The directory entry's
    /// equivalent field is the index of the LAST byte - <c>0x45F0</c> against the open reply's
    /// <c>0x45F1</c> for the same file - which is why an empty file reads <c>0xFFFFFFFF</c> there
    /// and why selector 3 is omitted altogether for one.
    /// </remarks>
    public static class FaOpenFileCodec
    {
        /// <summary>
        /// Selector under which a failure carries its SINTRAN error number.
        /// </summary>
        public const ushort ErrorSelector = 0x0001;

        /// <summary>
        /// Selector carrying the file name in a request and the file number in a reply.
        /// </summary>
        public const ushort PrimarySelector = 0x0002;

        /// <summary>
        /// Selector carrying the access mode in a request and the file size in a reply.
        /// </summary>
        public const ushort SecondarySelector = 0x0003;

        /// <summary>
        /// The access-mode value seen on an open that creates or writes a file.
        /// </summary>
        /// <remarks>
        /// Present only on the write open in <c>capture-write.txt</c>, as <c>F2 0003 92 0001</c>.
        /// The read opens carry no selector 3 at all. INFERRED that the field is an access mode and
        /// that 1 means write; no capture shows another value.
        /// </remarks>
        public const ushort AccessModeWrite = 0x0001;

        /// <summary>
        /// Reads an <c>OpenFile</c> request.
        /// </summary>
        /// <param name="body">
        /// The QFORM body, from the operation word onwards.
        /// </param>
        /// <param name="serial">
        /// The exchange sequence, echoed by the reply.
        /// </param>
        /// <param name="fileName">
        /// The file specification as typed, with the terminator and trailing junk removed.
        /// </param>
        /// <param name="forWrite">
        /// Whether the request asked for write access.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the body parsed as an open request.
        /// </returns>
        /// <remarks>
        /// <para><b>The name string carries two bytes of tail</b></para>
        /// Its declared length is <c>strlen + 2</c>: the characters, then the SINTRAN terminator,
        /// then one more byte. That last byte read <c>0x54</c>, <c>0x55</c> and <c>0x57</c> across
        /// three captures - not the last character, not a checksum, and not word padding since one
        /// of the lengths is odd. Treated as the same uninitialised tail this protocol leaves after
        /// other values, and discarded by <see cref="SintranName.Read"/>.
        /// <para><b>Quotes are part of the name</b></para>
        /// The write open carries <c>"WRTEST1:OUT"</c> with its quote characters inside the string,
        /// because quoting is how a SINTRAN caller asks for the file to be created. They are left in
        /// <paramref name="fileName"/> for the caller to act on.
        /// </remarks>
        public static bool TryReadRequest(
            ReadOnlySpan<byte> body,
            out ushort serial,
            out string fileName,
            out bool forWrite)
        {
            serial = 0;
            fileName = string.Empty;
            forWrite = false;

            IReadOnlyList<QformField> fields;
            if (!QformReader.TryRead(body, out fields) || fields.Count < 2)
            {
                return false;
            }

            serial = QformValue.ReadUInt16(body, fields[1]);

            QformField nameField;
            if (!QformValue.TryFindValue(
                    body, fields, PrimarySelector, QformClass.ByteString, 0, out nameField))
            {
                return false;
            }

            fileName = SintranName.Read(body.Slice(nameField.ValueOffset, nameField.ValueLength));

            QformField modeField;
            if (QformValue.TryFindValue(
                    body, fields, SecondarySelector, QformClass.Integer, 2, out modeField))
            {
                forWrite = QformValue.ReadUInt16(body, modeField) == AccessModeWrite;
            }

            return true;
        }

        /// <summary>
        /// Builds the reply to a successful <c>OpenFile</c>.
        /// </summary>
        /// <param name="serial">
        /// The exchange sequence to echo.
        /// </param>
        /// <param name="fileNumber">
        /// The file number to report under selector 2.
        /// </param>
        /// <param name="byteLength">
        /// The number of bytes in the file. Selector 3 is omitted when this is zero, which is what
        /// the capture does for a freshly created empty file.
        /// </param>
        /// <returns>
        /// The QFORM body, from the operation word onwards.
        /// </returns>
        public static byte[] BuildReply(ushort serial, ushort fileNumber, long byteLength)
        {
            bool withSize = byteLength > 0;

            // 92 0005 | 92 serial | F2 0002 A2 number | [F2 0003 A4 size] | F2 00FF
            byte[] body = new byte[3 + 3 + 3 + 3 + (withSize ? 3 + 5 : 0) + 3];
            QformWriter writer = new QformWriter(body);

            writer.WriteInteger((ushort)FaOperation.OpenFile);
            writer.WriteInteger(serial);
            writer.WriteSelector(PrimarySelector);
            writer.WriteTypedInteger(fileNumber);

            if (withSize)
            {
                writer.WriteSelector(SecondarySelector);
                writer.WriteTypedInteger32((uint)byteLength);
            }

            writer.WriteEndOfList();
            writer.EnsureComplete("Open reply");

            return body;
        }

        /// <summary>
        /// Reads the reply to a successful <c>OpenFile</c>: the file number and the file's length.
        /// </summary>
        /// <param name="body">
        /// The QFORM body, from the operation word onwards.
        /// </param>
        /// <param name="serial">
        /// The exchange sequence the reply echoes.
        /// </param>
        /// <param name="fileNumber">
        /// The file number the server assigned, from selector 2.
        /// </param>
        /// <param name="byteLength">
        /// The file's true length in bytes, from selector 3, or zero when the reply carried none.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the body parsed as an open reply.
        /// </returns>
        /// <remarks>
        /// <para><b>This is how a READER knows where the file ends</b></para>
        /// <para>
        /// There is no end marker anywhere in a file-access transfer and the last block arrives
        /// PADDED rather than shortened, so the length in this reply is the only thing that says
        /// where the content stops. A client that ignores it gets the file plus up to 2047 bytes of
        /// padding and no way to tell which is which.
        /// </para>
        /// <para><b>Measured</b></para>
        /// <para>
        /// <c>DOC/captures/ND-TO-ND-WRITE-2026-08-10/readback-10-blocks.pcapng</c>, D102 reading
        /// <c>BIGPSH3:TXT</c> from D100:
        /// </para>
        /// <code>
        /// 92 0005  92 0002  F2 0002 A2 0040  F2 0003 A4 00004FB0  F2 00FF
        ///                           file no 64      20400 bytes
        /// </code>
        /// <para>
        /// 20400 is exactly the file that had been pushed, and
        /// <c>FaReadLadder.BlockCountForLength</c> turns it into the ten <c>ReadFile</c> requests
        /// the capture then sends. This is the mirror of
        /// <see cref="BuildReply(ushort, ushort, long)"/>, which our own server has been sending
        /// all along - the two had simply never been read back.
        /// </para>
        /// <para><b>A missing length is reported as zero, not as a failure</b></para>
        /// <para>
        /// Selector 3 is genuinely absent from the WRITE capture's open reply, which carries only
        /// the file number. Treating that as a parse error would fail a perfectly good reply; it is
        /// the CALLER's business to decide whether it needed a length. See
        /// <see cref="FaOperation.OpenFile"/>.
        /// </para>
        /// </remarks>
        public static bool TryReadReply(
            ReadOnlySpan<byte> body,
            out ushort serial,
            out ushort fileNumber,
            out long byteLength)
        {
            serial = 0;
            fileNumber = 0;
            byteLength = 0;

            IReadOnlyList<QformField> fields;
            if (!QformReader.TryRead(body, out fields) || fields.Count < 2)
            {
                return false;
            }

            serial = QformValue.ReadUInt16(body, fields[1]);

            QformField numberField;
            if (QformValue.TryFindValue(
                    body, fields, PrimarySelector, QformClass.TypedInteger, 2, out numberField))
            {
                fileNumber = QformValue.ReadUInt16(body, numberField);
            }

            // Found by TAG and CLASS, never by counting from the front of the body. The width
            // matters as much as the selector: the length is a 32-bit A4 value, and a 16-bit read
            // of it would return the high half of the size and look plausible while being wrong.
            QformField sizeField;
            if (QformValue.TryFindValue(
                    body, fields, SecondarySelector, QformClass.TypedInteger, 4, out sizeField))
            {
                byteLength = QformValue.ReadUInt32(body, sizeField);
            }

            return true;
        }

        /// <summary>
        /// Builds the reply to <c>CloseFile</c>, which carries nothing but the echo.
        /// </summary>
        /// <param name="serial">
        /// The exchange sequence to echo.
        /// </param>
        /// <returns>
        /// The QFORM body, from the operation word onwards.
        /// </returns>
        public static byte[] BuildCloseReply(ushort serial)
        {
            byte[] body = new byte[3 + 3 + 3];
            QformWriter writer = new QformWriter(body);

            writer.WriteInteger((ushort)FaOperation.CloseFile);
            writer.WriteInteger(serial);
            writer.WriteEndOfList();
            writer.EnsureComplete("Close reply");

            return body;
        }
    }
}
