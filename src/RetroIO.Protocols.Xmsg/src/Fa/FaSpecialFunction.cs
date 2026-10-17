//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Fa
{
    /// <summary>
    /// What a listing request is asking the file server for.
    /// </summary>
    /// <remarks>
    /// <para><b>Where it sits on the wire</b></para>
    /// A listing request body reads <c>92 000C</c>, <c>92 serial</c>, <c>F2 0001</c>,
    /// <c>92 function</c>, <c>F2 0002</c>, then the constructed argument. This enum is that fourth
    /// field. It is NOT a flags word - the three values are alternatives.
    /// <para><b>How the values were established</b></para>
    /// By pairing every request with its reply through the session-header byte in
    /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-list-files.txt</c>. The request sizes and
    /// reply sizes sort cleanly into three groups, and the sub-function is the only field that
    /// varies with them.
    /// <para><b>Why a listing needs all three</b></para>
    /// SINTRAN names a file <c>D100.(PACK-ONE:SYSTEM)SINTRAN:DATA;1</c>. The directory and the user
    /// in that header come from two separate requests the client makes right after the first file,
    /// not from the file entries.
    /// </remarks>
    public enum FaSpecialFunction : ushort
    {
        /// <summary>
        /// Give me the next file entry, wire value <c>0x0078</c>.
        /// </summary>
        /// <remarks>
        /// Answered with a 98-byte reply carrying the 64-byte object entry. This is the only
        /// sub-function the server answered before 2026-08-05, and it answered it for every request.
        /// </remarks>
        NextFileEntry = 0x0078,

        /// <summary>
        /// Give me the directory entry, wire value <c>0x00A4</c>.
        /// </summary>
        /// <remarks>
        /// Answered with a 70-byte reply carrying the 42-byte directory entry. Sent once per
        /// listing, immediately after the first file entry.
        /// </remarks>
        DirectoryEntry = 0x00A4,

        /// <summary>
        /// Give me the user entry, wire value <c>0x008C</c>.
        /// </summary>
        /// <remarks>
        /// Answered with a 40-byte reply carrying a 16-byte user name. Sent once per listing,
        /// immediately after the directory entry.
        /// </remarks>
        UserEntry = 0x008C,

        /// <summary>
        /// Set the file's byte length, wire value <c>0x003B</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the END-OF-FILE SETTER, not teardown</b></para>
        /// It was recorded in <c>FaOperation.SiiiSpecial</c> as an unexplained sub-function seen at
        /// teardown. <c>DOC\FA-READ-WRITE-WIRE-PROTOCOL-2026-08-04.md</c> section 8 settles it: a
        /// write ships whole 2048-byte blocks and then states the file's TRUE length before closing.
        /// <code>
        /// 92 000C  92 000D  F2 0001 92 003B  F2 0002 8C 80 05 A4 000045F0  F2 00FF
        /// </code>
        /// <c>0x45F0</c> = 17904, the real size of the file that was written, and NOT a multiple of
        /// 2048 - which is what proves the field is a byte length rather than a block count. The
        /// same number appears in the <c>0x0021</c> file-information reply taken during the
        /// corresponding read, alongside a block count of nine.
        /// <para><b>Do not read the length by offset</b></para>
        /// The three bytes <c>8C 80 05</c> in front of the <c>A4</c> are INFERRED and the write-up
        /// says so in as many words: <c>0x80 05</c> does not read as a length there. Find the
        /// four-byte value by its tag and class, never by counting from the front.
        /// </remarks>
        SetEndOfFile = 0x003B,

        /// <summary>
        /// Asks for the open file's directory entry: its size, block count, dates and access.
        /// </summary>
        /// <remarks>
        /// <para><b>Where it sits in a read</b></para>
        /// Between <c>SetBlockSize</c> and the first <c>ReadFile</c>. <c>COPY-FILE</c> sends it;
        /// the plain <c>COPY</c> command does not, which is why one command could read from us
        /// while the other could not.
        /// <para><b>The request carries nothing but the sub-function</b></para>
        /// Captured at <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-read.txt</c> line 63:
        /// <code>
        /// 92 000C  92 0004  F2 0001 92 0021  F2 00FF
        /// </code>
        /// <para><b>The reply is the 64-byte directory entry</b></para>
        /// The same record a listing hands out, wrapped without the listing's three <c>A2</c>
        /// values - line 64 of the same capture:
        /// <code>
        /// 92 000C  92 0004  F2 0002 8C 42  B0 40 64 bytes  F2 00FF
        /// </code>
        /// Inside that record, <c>00000009</c> is the block count and <c>000045F0</c> = 17904 the
        /// byte length.
        /// <para><b>Found live 2026-08-06</b></para>
        /// Refusing this made <c>COPY-FILE</c> close the file and give up without ever issuing a
        /// <c>ReadFile</c>.
        /// </remarks>
        FileInformation = 0x0021,
    }
}
