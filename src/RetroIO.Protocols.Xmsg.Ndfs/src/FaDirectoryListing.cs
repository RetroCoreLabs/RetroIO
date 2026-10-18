//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Protocols.Xmsg.Fa;
using RetroIO.Storage.Ndfs.Elements;

namespace RetroIO.Protocols.Xmsg.Ndfs
{
    /// <summary>
    /// Bridges the COSMOS file-access wire format to the SINTRAN file system: turns the opaque
    /// 64-byte record in a <c>LIST-FILES</c> reply into a <see cref="ObjectEntry"/>, and back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The COSMOS file server does not reformat directory entries. It reads the object entry off the
    /// disc and puts those 64 bytes on the wire unchanged, which is why this adapter is a straight
    /// hand-off rather than a translation.
    /// </para>
    /// <para>
    /// That was established by decoding a captured listing
    /// (<c>claude-list-files-d100-system-2026-07-29.pcapng</c>, <c>LIST-FILES d100(system).</c> from
    /// node 102 against node 100) field by field against
    /// <c>RetroIO.Storage.Ndfs.Elements.ObjectEntry</c>. Every offset agreed, and the three packed date words
    /// decoded through <see cref="NdDateTime"/> to times that match the machine: the system files all
    /// created together on 1998-07-06 at 16:55, SEGFIL0 and RTFIL last opened at the previous boot on
    /// 1998-07-28 07:41, and SYSTEM-OUTPUT-1 written on the day of the capture.
    /// </para>
    /// </remarks>
    public static class FaDirectoryListing
    {
        /// <summary>
        /// Reads the directory entry out of a <c>LIST-FILES</c> reply body.
        /// </summary>
        /// <param name="body">
        /// The QFORM reply body.
        /// </param>
        /// <param name="serial">
        /// The echoed transaction serial, pairing this reply to its request.
        /// </param>
        /// <param name="entry">
        /// The decoded object entry.
        /// </param>
        /// <returns>
        /// <c>true</c> if the reply carried an entry that decoded. A reply whose record has
        /// the in-use bit clear yields <c>false</c>, matching
        /// <see cref="ObjectEntry.FromBytes"/>.
        /// </returns>
        public static bool TryReadReply(ReadOnlySpan<byte> body, out ushort serial, out ObjectEntry? entry)
        {
            entry = null;

            byte[] record;
            if (!FaListFilesCodec.TryReadReply(body, out serial, out record))
            {
                return false;
            }

            entry = ObjectEntry.FromBytes(record, 0);
            return entry != null;
        }

        /// <summary>
        /// Builds a <c>LIST-FILES</c> reply carrying one directory entry.
        /// </summary>
        /// <param name="serial">
        /// The serial of the request being answered.
        /// </param>
        /// <param name="entry">
        /// The entry to return.
        /// </param>
        /// <returns>
        /// The QFORM reply body.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// The entry is null.
        /// </exception>
        public static byte[] BuildReply(ushort serial, ObjectEntry entry)
        {
            if (entry == null) { throw new ArgumentNullException(nameof(entry)); }

            byte[] record = new byte[FaListFilesCodec.EntryRecordLength];
            entry.ToBytes(record, 0);

            return FaListFilesCodec.BuildReply(serial, record);
        }

        /// <summary>
        /// Decodes a raw 64-byte record on its own, for callers that already have one.
        /// </summary>
        /// <param name="record">
        /// The object entry as it arrived on the wire.
        /// </param>
        /// <returns>
        /// The decoded entry, or <c>null</c> if the in-use bit is clear.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// The record is the wrong length.
        /// </exception>
        public static ObjectEntry? ParseRecord(byte[] record)
        {
            if (record == null) { throw new ArgumentNullException(nameof(record)); }
            if (record.Length != FaListFilesCodec.EntryRecordLength)
            {
                throw new ArgumentException(
                    "A directory entry record is " + FaListFilesCodec.EntryRecordLength
                    + " bytes, got " + record.Length.ToString() + ".",
                    nameof(record));
            }

            return ObjectEntry.FromBytes(record, 0);
        }
    }
}
