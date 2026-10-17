//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Fa
{
    /// <summary>
    /// The operations a <c>*FA-SERVER</c> request can carry, as the server itself names them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are not inferred from behaviour. The server program <c>COS-FA-SERV-E04:PROG</c> holds a
    /// packed list of thirteen command names at <c>BANK2::8731</c>, followed by a row of addresses
    /// that was long read as a handler table. It is not one - see "RESOLVED" below - but the row is
    /// reproduced as first transcribed, because the numbers really are in the binary:
    /// </para>
    /// <code>
    /// index  0     1     2     3     4     5     6     7     8     9    10    11    12    13   14
    /// addr 1fb0  1fb0  1ead  1ecc  1edd  1eee  1eff  1f10  1f21  1f32  1f43  1f6c  1f7d  1f8e 1f9f
    /// name       disc  Rsrv  Rlse  ChId  Open  Clos  SetB  Read  Writ  Crea  Dele  SIII  Devf
    /// </code>
    /// <para><b>The names read out of the binary, 2026-08-06</b></para>
    /// The abbreviations above were an analyst's shorthand. The name table is plain ASCII, run
    /// together without separators, and these are the server's OWN names in table order:
    ///  - 1 <c>File-entry-disconnect</c>, 2 <c>Reserve-file-entry</c>, 3 <c>Release-file-entry</c>.
    ///  - 4 <c>Change-file-entry-id</c>, 5 <c>Open-file</c>, 6 <c>Close-file</c>.
    ///  - 7 <c>Set-block-size</c>, 8 <c>Read-file</c>, 9 <c>Write-file</c>, 10 <c>Create-file</c>.
    ///  - 11 <c>Delete-file</c>, 12 <c>SIII-special</c>, 13 <c>Device-function</c>.
    /// <para>
    /// Every C# name in this enum matches, which is worth stating because three of them were
    /// chosen before the strings were read. The bank-2 base needed to locate them is 65792 file
    /// words, anchored on the <c>"FA-server"</c> status text sitting exactly where the analysis
    /// records it (<c>BANK2::842c</c>); the name table then lands exactly on <c>BANK2::8731</c>,
    /// which is the second, independent confirmation of that base.
    /// </para>
    /// <para><b>RESOLVED 2026-08-06: that second row is a NAME table, not handlers</b></para>
    /// Disassembled in Ghidra (the region needed forcing - nothing had analysed it, which is why two
    /// earlier readings of it were wrong). Every entry is the same 17-word stub:
    /// <code>
    /// SAA  n          ; n = the command name's length MINUS ONE
    /// ...
    /// LDT  *addr      ; addr holds the ADDRESS of that name's ASCII text
    /// LDD  *addr+1
    /// JPL / JPL / JMP     ; into shared tails
    /// </code>
    /// So these routines carry a string descriptor - a length and a pointer to the name - which
    /// makes the row a command-name table, almost certainly the parser that matches a typed
    /// command. They are NOT the wire-request handlers. Those are ordinary named functions between
    /// <c>ram:2xxx</c> and <c>ram:4xxx</c>; operation 4's is <c>fa_change_file_entry_id</c> at
    /// <c>ram:2e12</c>, reached from a helper rather than from any table.
    /// <para><b>And the row is OFF BY ONE against the names</b></para>
    /// Entry <c>i</c> serves name <c>i-1</c>. Confirmed six times over, by two independent fields
    /// that agree in every case:
    ///  - <c>1ead</c>: <c>SAA 20</c>, name ptr <c>8731</c> = <c>File-entry-disconnect</c> (21 chars).
    ///  - <c>1edd</c>: <c>SAA 17</c>, name ptr <c>8745</c> = <c>Release-file-entry</c> (18).
    ///  - <c>1eee</c>: <c>SAA 19</c> = <c>Change-file-entry-id</c> (20).
    ///  - <c>1f21</c>: <c>SAA 13</c>, name ptr <c>8762</c> = <c>Set-block-size</c> (14).
    ///  - <c>1f8e</c>: <c>SAA 11</c>, name ptr <c>877f</c> = <c>SIII-special</c> (12).
    ///  - <c>1f9f</c>: <c>SAA 14</c>, name ptr <c>8785</c> = <c>Device-function</c> (15).
    /// <para>
    /// So "the operation code is the index into that table" was wrong by one, and the earlier claim
    /// that slot 14 was an unnamed fourteenth handler is DEAD - slot 14 is <c>Device-function</c>.
    /// There is no fourteenth operation. The operation CODES themselves are untouched by this: they
    /// come from captured traffic, not from this table.
    /// </para>
    /// <para><b>What is still unknown</b></para>
    /// No wire layout was recovered for any uncaptured operation, and this route cannot give one -
    /// it ends at the command's own name text. Operations 1 and 13 have no named handler in the
    /// carve at all. All three stay refused.
    /// <para>
    /// The operation travels as a two-byte value behind a <c>0x92</c> tag, immediately followed by the
    /// exchange sequence tagged the same way. A reply echoes both.
    /// </para>
    /// </remarks>
    public enum FaOperation : ushort
    {
        /// <summary>
        /// Disconnects a file entry. On the wire as <c>0x0001</c>.
        /// </summary>
        /// <remarks>
        /// The server calls it <c>File-entry-disconnect</c>.
        /// <para>
        /// Its slot in the handler table holds the same address as the table's padding, so it is not
        /// dispatched through the normal path. That fits it belonging to teardown rather than being a
        /// request a client sends. Never recorded, and its handler body has not been read - see the
        /// enum's remarks for why.
        /// </para>
        /// </remarks>
        FileEntryDisconnect = 0x0001,

        /// <summary>
        /// Reserves a file entry. On the wire as <c>0x0002</c>.
        /// </summary>
        /// <remarks>
        /// Opens every recorded conversation, carrying a 112-byte body with the user and directory
        /// specification. Previously called "open spec" in this library, which described how it looked
        /// rather than what it is.
        /// </remarks>
        ReserveFileEntry = 0x0002,

        /// <summary>
        /// Releases a file entry. On the wire as <c>0x0003</c>.
        /// </summary>
        /// <remarks>
        /// Closes every recorded conversation. Previously called "close spec" here.
        /// </remarks>
        ReleaseFileEntry = 0x0003,

        /// <summary>
        /// Changes a file entry's identifier. On the wire as <c>0x0004</c>. Never recorded.
        /// </summary>
        /// <remarks>
        /// The server calls it <c>Change-file-entry-id</c>, and its handler is
        /// <c>fa_change_file_entry_id</c> at <c>ram:2e12</c>.
        /// <para>
        /// From the comment left by an earlier Ghidra pass, so second-hand rather than re-derived
        /// here: it finds the entry by key, REQUIRES that entry to be reserved (else error
        /// <c>0xd</c>) and of type 8, then either adds to or replaces a two-word value in the
        /// entry's sub-block before writing the new id and attributes. So it edits an existing
        /// reservation rather than creating anything.
        /// </para>
        /// <para>
        /// That is behaviour, NOT a wire layout. How the request encodes the entry key and the new
        /// value is still unknown, no traffic carries it, and it stays refused.
        /// </para>
        /// </remarks>
        ChangeFileEntryId = 0x0004,

        /// <summary>
        /// Opens a file and returns the file number to use for it. On the wire as <c>0x0005</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The request names the file; the reply carries the REMOTE file number, which is not the
        /// number the operator is shown. The access mode rides under field selector 3 and is omitted
        /// entirely for read.
        /// </para>
        /// </remarks>
        OpenFile = 0x0005,

        /// <summary>
        /// Closes an open file. On the wire as <c>0x0006</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Recorded only at session teardown, never when the operator typed <c>CLOSE-FILE</c> - that
        /// went to the separate <c>*FA-USER</c> service instead, which is still unexplained. An
        /// earlier note in this library concluded from that absence that the operation could not be a
        /// close. The server's own table says otherwise, and the table wins.
        /// </para>
        /// </remarks>
        CloseFile = 0x0006,

        /// <summary>
        /// Sets the block size on an open file. On the wire as <c>0x0007</c>. Served, and driven by
        /// a real client.
        /// </summary>
        /// <remarks>
        /// A real client sends this between <c>OpenFile</c> and the first <c>ReadFile</c>, and it is
        /// not optional: while we refused it as uncaptured, the client gave up with
        /// <c>NO ANSWER FROM REMOTE SYSTEM; FILE-ACCESS CONNECTION ABORTED</c>. The block size it
        /// sets is what makes a partial transfer fine-grained - see <see cref="ReadFile"/>.
        /// </remarks>
        SetBlockSize = 0x0007,

        /// <summary>
        /// Reads from an open file. On the wire as <c>0x0008</c>. Served, and verified live.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The operation behind the terminal's <c>RFILE</c> command. The terminal command takes a
        /// MEMORY ADDRESS argument, which is why it was not driven by hand on a live machine; the
        /// protocol operation carries no such thing, so a client can issue it safely.
        /// </para>
        /// <para><b>Layouts are known, not unknown</b></para>
        /// CORRECTED 2026-08-06. This used to say the layouts were UNKNOWN and the handler at
        /// <c>BANK2::1f21</c> unread. Both are written up in
        /// <c>DOC/FA-READ-WRITE-WIRE-PROTOCOL-2026-08-04.md</c> from a capture of a real machine
        /// reading a 17905-byte file in nine steps, and a 12690-byte file has since been read off
        /// our own server byte-for-byte.
        /// <para>
        /// The request carries a block position, not a byte offset. That follows <c>RFILE</c>
        /// itself, which reads "any number of bytes" but "must start at the beginning of a block" -
        /// so a transfer is block-aligned at the start and arbitrary in length, and finer
        /// granularity comes from <see cref="SetBlockSize"/> rather than from a byte offset.
        /// </para>
        /// </remarks>
        ReadFile = 0x0008,

        /// <summary>
        /// Writes to an open file. On the wire as <c>0x0009</c>. Served, and verified live.
        /// </summary>
        /// <remarks>
        /// The operation behind the terminal's <c>WFILE</c> command. Handler at <c>BANK2::1f32</c>.
        /// <para>
        /// CORRECTED 2026-08-06 - this used to say "never recorded, layouts UNKNOWN". The write
        /// side is the mirror of <see cref="ReadFile"/>: the request arms the transfer and the
        /// content follows as data messages. A 12690-byte file has been written back to our server
        /// from a real D100 and compares byte-identical with the original.
        /// </para>
        /// </remarks>
        WriteFile = 0x0009,

        /// <summary>
        /// Creates a file with a reserved page count and no contents. On the wire as <c>0x000A</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>What the wire shows - VERIFIED</b></para>
        /// The request carries the file name under selector 2 and a 32-bit page count under selector
        /// 4, and nothing else. Captured in
        /// <c>claude-create-file-102-to-100-2026-07-30.pcapng</c> and pinned by
        /// <c>FaLiveRequestRegressionTests</c>. The reply returns no value at all.
        /// <para><b>CORRECTION 2026-08-06 - "contiguity is implied" was NOT sourced</b></para>
        /// This remark used to end "The file it produces is a continuous file, so contiguity is
        /// implied by the operation rather than sent." No manual or capture says that, and ND's own
        /// documentation for the monitor call this operation is named after points the other way.
        /// <c>Reference-Manuals\ND-860228-2-EN SINTRAN III Monitor Calls.md</c> line 5270,
        /// <c>221B CreateFile CRALF</c>, takes THREE parameters - file name (default type
        /// <c>:DATA</c>), a start address in the directory ("Use 0 if you want to create a
        /// contiguous or indexed file") and a length in pages ("Use 0 if you want to create
        /// an indexed file"). So under the monitor call the page count is what DECIDES the file
        /// kind: zero gives an indexed file, non-zero a contiguous one.
        /// <para>
        /// Whether the FA operation carries the same meaning is UNKNOWN - the captured request has
        /// two fields where the monitor call has three, so they are not the same shape, and no
        /// manual documents the FA wire protocol at all. Our server ignores the page count and
        /// creates an ordinary file; if a client ever depends on the indexed/contiguous distinction
        /// that is where to look.
        /// </para>
        /// </remarks>
        CreateFile = 0x000A,

        /// <summary>
        /// Deletes a file. On the wire as <c>0x000B</c>.
        /// </summary>
        DeleteFile = 0x000B,

        /// <summary>
        /// A catch-all whose sub-function is carried under field selector 1. On the wire as
        /// <c>0x000C</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A directory listing uses this with <c>0x0078</c> under selector 1; a request seen at
        /// session teardown uses <c>0x003B</c>. That looked like a broken constant until the command
        /// table named this operation: selector 1 holds the SUB-FUNCTION, so those are two different
        /// sub-functions rather than an inconsistent field. Which is which is UNKNOWN.
        /// </para>
        /// </remarks>
        SiiiSpecial = 0x000C,

        /// <summary>
        /// A device function. On the wire as <c>0x000D</c>. Never recorded.
        /// </summary>
        /// <remarks>
        /// The server calls it <c>Device-function</c>, the last name in its table. Nothing more is
        /// established: no capture carries it and its handler at <c>1f8e</c> has not been read. If
        /// it mirrors SINTRAN's device-level calls it would be the FA equivalent of a
        /// <c>MAGTP</c>-style control, but that is a guess from the name and is recorded as one.
        /// </remarks>
        DeviceFunction = 0x000D,
    }
}
