//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Ndfs
{
    /// <summary>
    /// The flag bits of the object entry header word, record bytes 0-1.
    /// </summary>
    /// <remarks>
    /// <para><b>Only the high bits are flags</b></para>
    /// Bits 10-0 of the header word are the terminal number of the reserving user, not flags. Mask
    /// them off with <see cref="FaObjectEntryHeader.TerminalNumberMask"/> before treating the word as
    /// this enum.
    /// <para><b>Measured over the capture</b></para>
    /// Header words seen across the 49 records of
    /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-list-files.txt</c>:
    ///  - <c>0x9000</c> on 40 records, every one of which carries a real max byte pointer.
    ///  - <c>0x8000</c> on 8 records, every one of which has a max byte pointer of <c>0xFFFFFFFF</c>.
    ///  - <c>0xA000</c> on 1 record, <c>COSMOS-SPOOLING</c>, which also has no byte pointer.
    /// </remarks>
    [Flags]
    public enum FaObjectHeaderFlags : ushort
    {
        /// <summary>
        /// No flags set, which marks the slot FREE.
        /// </summary>
        None = 0,

        /// <summary>
        /// The entry is in use, wire bit <c>0x8000</c>.
        /// </summary>
        /// <remarks>
        /// Clear this and the record reads as a free directory slot and the file vanishes from the
        /// listing entirely.
        /// </remarks>
        EntryUsed = 1 << 15,

        /// <summary>
        /// The file is open for write, wire bit <c>0x4000</c>.
        /// </summary>
        /// <remarks>
        /// Named from the <c>RetroIO.Storage.Ndfs.Elements.ObjectEntry</c> model. It is set on no record in
        /// the capture, so this name is UNVERIFIED here.
        /// </remarks>
        OpenForWrite = 1 << 14,

        /// <summary>
        /// Reserved, wire bit <c>0x2000</c>.
        /// </summary>
        /// <remarks>
        /// Set on exactly one captured record, <c>COSMOS-SPOOLING</c>. What distinguishes that file
        /// is UNKNOWN.
        /// </remarks>
        Reserved = 1 << 13,

        /// <summary>
        /// The max byte pointer is meaningful, wire bit <c>0x1000</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>The name describes a correlation, not a proven meaning</b></para>
        /// Over all 49 captured records this bit is set on the 40 that carry a real byte count and
        /// clear on the 9 whose max byte pointer is <c>0xFFFFFFFF</c>. No record mixes the two.
        /// <para><b>The ambiguity, stated plainly</b></para>
        /// <c>ObjectEntry</c> calls this bit "file modified", and on this data the two readings cannot
        /// be told apart: every file with a real length has also been written, and every peripheral
        /// file has neither. Both readings demand the same value for a file we serve, so the
        /// ambiguity costs nothing here. It would matter to anything that READ the bit for meaning.
        /// </remarks>
        MaxBytePointerValid = 1 << 12,
    }
}
