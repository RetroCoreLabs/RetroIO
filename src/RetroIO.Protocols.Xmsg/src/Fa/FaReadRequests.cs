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
    /// Builds the QFORM field lists a client sends for the read operations that differ from a
    /// write.
    /// </summary>
    /// <remarks>
    /// <para><b>Only the differences live here</b></para>
    /// <para>
    /// Most of a read ladder is byte-identical to a write's, so it reuses
    /// <see cref="FaWriteRequests"/> rather than repeating it - the reserve, the block size, the
    /// close and the release are the SAME requests, and the captures agree field for field. Three
    /// things differ, and only those three are here:
    /// </para>
    ///  - the open, which carries no access selector - see <see cref="OpenFile"/>.
    ///  - the file-information request, which a write never sends.
    ///  - the block request, which is <see cref="FaOperation.ReadFile"/> rather than a write.
    /// <para>
    /// The class is not named <c>FaReadWriteRequests</c> and does not absorb the write builders,
    /// because the two ladders were measured from different captures and it should stay obvious
    /// which capture any given byte came from.
    /// </para>
    /// <para><b>Checked against a real client</b></para>
    /// <para>
    /// Every method here reproduces bytes from
    /// <c>DOC/captures/ND-TO-ND-WRITE-2026-08-10/readback-10-blocks.pcapng</c>, D102 reading
    /// <c>BIGPSH3:TXT</c> from D100. Both ends are real ND machines.
    /// </para>
    /// <para><b>The trailing pad is not modelled</b></para>
    /// <para>
    /// Same as on the write side: a captured body that lands on an odd length carries one more
    /// byte, and its value differs every time, so it is leftover buffer content rather than a
    /// field. <see cref="FaClientConversation.BuildRequest"/> adds a defined zero when one is
    /// needed. An ODD body is dropped in silence, so the padding itself is not optional - only its
    /// value is meaningless.
    /// </para>
    /// </remarks>
    public static class FaReadRequests
    {
        /// <summary>
        /// Builds the fields for the <see cref="FaOperation.OpenFile"/> that opens a file for
        /// READING.
        /// </summary>
        /// <param name="fileSpec">
        /// The file specification, WITHOUT quotes - for example <c>BIGPSH3:TXT</c>.
        /// </param>
        /// <returns>
        /// The fields, ending with the end-of-list selector.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="fileSpec"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when the specification and its suffix do not fit a compact byte string.
        /// </exception>
        /// <remarks>
        /// <para><b>The captured field, byte for byte</b></para>
        /// <code>
        /// F2 0002  BD  "BIGPSH3:TXT'."     13 bytes: the spec, an apostrophe, a full stop
        /// F2 00FF
        /// </code>
        /// <para><b>Two differences from the write open, both deliberate</b></para>
        ///  - NO SELECTOR 3. A write open ends <c>F2 0003 92 0001</c>; this one goes straight to
        ///    the end-of-list. Sending the write's selector 3 here would be describing a write.
        ///  - NO QUOTES around the name. A write that CREATES a file quotes it, matching SINTRAN's
        ///    own rule that quotes go around a name being created. A read opens a file that already
        ///    exists, so the name goes bare - which is why this takes an UNQUOTED specification
        ///    where <see cref="FaWriteRequests.OpenFile"/> takes a quoted one. The difference in
        ///    the parameter is the difference in the protocol, made impossible to miss.
        /// <para><b>The suffix character is reproduced, not understood</b></para>
        /// <para>
        /// See <see cref="FaReadLadder.ReadAccessSuffix"/>. Writes put a letter there and use more
        /// than one, so it is not simply an access mode, and nothing here pretends otherwise.
        /// </para>
        /// <para><b>The eleven-character budget</b></para>
        /// <para>
        /// The compact string form holds at most fifteen bytes, and the suffix costs two of them,
        /// so a specification has THIRTEEN to live in - and the write side is tighter still at
        /// eleven, because its quotes cost two more. What a real client does with a longer name is
        /// UNKNOWN, so this throws rather than silently switching to the long string form and
        /// sending something no capture has ever shown.
        /// </para>
        /// </remarks>
        public static byte[] OpenFile(string fileSpec)
        {
            if (fileSpec == null)
            {
                throw new ArgumentNullException(nameof(fileSpec));
            }

            string field = fileSpec + "'" + FaReadLadder.ReadAccessSuffix;
            byte[] text = System.Text.Encoding.ASCII.GetBytes(field);

            if (text.Length < 1 || text.Length > 0x0F)
            {
                throw new ArgumentException(
                    "'" + fileSpec + "' plus its suffix is " + text.Length
                        + " bytes; a QFORM byte string holds at most 127.",
                    nameof(fileSpec));
            }

            // F2 0002 <string> F2 00FF - and nothing else.
            // PICK THE FORM BY LENGTH, which is what a real client does. The compact string
            // carries its length in the tag nibble and so stops at 15 bytes; the long form is
            // B0 followed by a whole length byte. BOTH are in the captures - BF with a 15-byte
            // field in the write capture, and B0 10 with a 16-byte field holding
            // "PATCH-FILE:OUT" in the read one, which is a FOURTEEN-character name.
            //
            // Until 2026-08-24 this always wrote the compact form. That capped a specification
            // at 13 characters including its quotes, so every file deployed to a machine needed
            // a shortened name and a rename afterwards - and the limit was written up in two
            // skills as if it were the protocol's.
            bool compact = text.Length <= QformWriter.MaxCompactByteStringLength;
            byte[] buffer = new byte[3 + (compact ? 1 : 2) + text.Length + 3];
            QformWriter writer = new QformWriter(buffer);
            writer.WriteSelector(2);
            if (compact)
            {
                writer.WriteByteStringCompact(text);
            }
            else
            {
                writer.WriteByteString(text);
            }
            writer.WriteEndOfList();
            writer.EnsureComplete("Read OpenFile fields");
            return buffer;
        }

        /// <summary>
        /// Builds the fields for the <see cref="FaSpecialFunction.FileInformation"/> request a
        /// reader sends before its first block.
        /// </summary>
        /// <returns>
        /// The fields, ending with the end-of-list selector.
        /// </returns>
        /// <remarks>
        /// <para><b>The whole request is the sub-function and nothing else</b></para>
        /// <code>
        /// 92 000C  92 0004  F2 0001 92 0021  F2 00FF
        /// </code>
        /// <para>
        /// Captured identically in two places two months apart:
        /// <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-read.txt</c> line 63, and the 2026-08-10
        /// readback. The reply is the 64-byte directory entry - the same record a listing hands out,
        /// wrapped without the listing's three <c>A2</c> values.
        /// </para>
        /// <para><b>It is not optional</b></para>
        /// <para>
        /// Our own server refused this sub-function until 2026-08-06, and a real <c>COPY-FILE</c>
        /// responded by closing the file and giving up WITHOUT ever issuing a <c>ReadFile</c> - no
        /// error, just no transfer. A reader that skips it is imitating <c>COPY</c> rather than
        /// <c>COPY-FILE</c>; both exist, but only one of them was captured, so this sends what was
        /// captured.
        /// </para>
        /// <para>
        /// The reply is not parsed here. Everything a reader needs - the length - already arrived
        /// in the open reply, and decoding a 64-byte record to re-learn it would be inventing a
        /// second source of truth for the same number.
        /// </para>
        /// </remarks>
        public static byte[] FileInformation()
        {
            // F2 0001 92 0021 F2 00FF
            byte[] buffer = new byte[3 + 3 + 3];
            QformWriter writer = new QformWriter(buffer);
            writer.WriteSelector(1);
            writer.WriteInteger((ushort)FaSpecialFunction.FileInformation);
            writer.WriteEndOfList();
            writer.EnsureComplete("FileInformation fields");
            return buffer;
        }

        /// <summary>
        /// Builds the fields for <see cref="FaOperation.ReadFile"/>.
        /// </summary>
        /// <param name="blockNumber">
        /// Which block of the file to read, counting from zero. The captured session read blocks 0
        /// to 9, one request each.
        /// </param>
        /// <returns>
        /// The fields, ending with the end-of-list selector.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Byte for byte the same as <see cref="FaWriteRequests.WriteFile"/> - selector 1, a 32-bit
        /// <c>A4</c> position, end of list - which is exactly what
        /// <c>DOC/FA-READ-WRITE-WIRE-PROTOCOL-2026-08-04.md</c> section 5 says: "the request is the
        /// READ request with the operation changed from <c>0x0008</c> to <c>0x0009</c>. Nothing
        /// else differs."
        /// </para>
        /// <para><b>So why does this method exist at all?</b></para>
        /// <para>
        /// Because the operation is passed separately by the caller, calling
        /// <c>FaWriteRequests.WriteFile</c> to build a READ would read as a mistake at every call
        /// site, and a comment explaining that it is fine would be needed at each one. It forwards
        /// rather than duplicating, so there is still one builder and one place to change if the
        /// two ever diverge.
        /// </para>
        /// <para><b>The position counts BLOCKS, not bytes</b></para>
        /// <para>
        /// Blocks of whatever <see cref="FaOperation.SetBlockSize"/> last asked for. The capture's
        /// ten reads run <c>00000000</c> to <c>00000009</c> over a 20400-byte file, which is ten
        /// units of 2048. Multiply by the block size to get a byte offset.
        /// </para>
        /// </remarks>
        public static byte[] ReadFile(uint blockNumber)
        {
            return FaWriteRequests.WriteFile(blockNumber);
        }
    }
}
