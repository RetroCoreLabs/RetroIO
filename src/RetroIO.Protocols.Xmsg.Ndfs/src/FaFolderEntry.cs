//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Protocols.Xmsg.Ndfs
{
    /// <summary>
    /// Turns a file in a served Windows folder into the 64-byte SINTRAN object entry a
    /// <c>LIST-FILES</c> reply has to carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These entries are SYNTHESISED, not read off a pack.</b> A remote directory listing returns
    /// the SINTRAN on-disk object entry verbatim (VERIFIED - see
    /// <see cref="FaDirectoryListing"/>), and a plain Windows folder simply does not hold the
    /// ND-specific fields: the owner, the page allocation, the header flag bits and the three packed
    /// ND dates have no counterpart in a Windows directory entry. That gap is written up in
    /// <c>DOC/PLAN-CSHARP-FILE-SERVER-AND-FOLDER-SYNC-2026-08-01.md</c> section 1, which calls
    /// serving a Windows folder "design B" and says a sidecar file is needed to do it properly.
    /// </para>
    /// <para>
    /// So everything this class puts in the fields that Windows cannot supply is <b>INFERRED</b> or
    /// simply chosen, and is marked as such below. What IS honest here: the name, the type, the
    /// length in bytes, and the modification time all come from the real file.
    /// </para>
    /// <para>
    /// No claim is made that a real SINTRAN client accepts these entries - nobody has yet watched one
    /// try. Do not turn this into a "verified" listing path on the strength of it compiling.
    /// </para>
    /// </remarks>
    public static class FaFolderEntry
    {
        /// <summary>
        /// Words per SINTRAN page, used to derive the page count from a byte length.
        /// </summary>
        /// <remarks>
        /// One page is 1024 words = 2048 bytes (the repository's standing memory convention: words
        /// first, bytes in brackets).
        /// </remarks>
        public const int BytesPerPage = 2048;

        /// <summary>
        /// The file-access word written to record bytes 26-27: own RWA and D, friend RWA, public R.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The word is three 5-bit tiers - bits 14-10 PUBLIC, bits 9-5 FRIEND, bits 4-0 OWN - with
        /// bit 15 unused; see <see cref="FaAccessRights"/> for the bits within a tier. The tier split
        /// and order are stated in
        /// <c>Reference-Manuals\ND-860228-2-EN SINTRAN III Monitor Calls.md</c> appendix C ("File
        /// access") and in the annotated <c>@DUMP-OBJECT-ENTRY</c> listing in
        /// <c>Operations\SINTRAN\ND-30.003.007 EN SINTRAN III System Supervisor.md</c>, which prints
        /// this word as "ACCESS WORD".
        /// </para>
        /// <para>
        /// <b>CONFIRMED END TO END 2026-08-05.</b> <c>FILE-STATISTICS</c> on D100, against a file we
        /// served with this exact word, printed our three tiers back:
        /// </para>
        /// <code>
        /// PUBLIC ACCESS : READ
        /// FRIEND ACCESS : READ, WRITE, APPEND
        /// OWN ACCESS : READ, WRITE, APPEND, DIRECTORY
        /// </code>
        /// <para>
        /// So the tier split, the tier ORDER and four of the five bit names are no longer inferred -
        /// ND's own decoder named them.
        /// </para>
        /// <para>
        /// <c>0x04F7</c> is what 40 of the 49 captured directory records carry, and it is the value
        /// an ordinary user file has; the nine that differ are system files (<c>0x0007</c>, own only)
        /// and peripheral files. We serve ordinary files, so we send the ordinary value.
        /// </para>
        /// <para>
        /// <b>This used to be zero</b>, because nothing ever assigned it - a file with an access word
        /// of zero grants NOBODY read, write or append, including its owner. That is the leading
        /// suspect for why D100 ran the whole listing conversation and then discarded it.
        /// </para>
        /// </remarks>
        public static readonly ushort DefaultAccessWord = FaObjectEntryHeader.PackAccess(
            own: FaAccessRights.Read | FaAccessRights.Write | FaAccessRights.Append | FaAccessRights.Directory,
            friend: FaAccessRights.Read | FaAccessRights.Write | FaAccessRights.Append,
            @public: FaAccessRights.Read);

        /// <summary>
        /// The block-pointer page id written to record bytes 60-63, under the pointer-type bits.
        /// </summary>
        /// <remarks>
        /// All 49 captured records carry this same page id for 49 DIFFERENT files, so it is NOT the
        /// file's real first block - on a real pack those differ (the System Supervisor manual's
        /// dumped entry shows <c>0x400049F5</c>). INFERRED that the FA server substitutes something
        /// of its own here rather than handing a remote client disc addresses. Only the top two bits
        /// track the file, and they track its allocation style - see the pointer type below.
        /// </remarks>
        public const uint ListingFilePointerPage = 0x78DA;

        /// <summary>
        /// The header word written to record bytes 0-1 for a served file.
        /// </summary>
        /// <remarks>
        /// <para><b>Why both bits</b></para>
        /// <see cref="FaObjectHeaderFlags.EntryUsed"/> is mandatory or the record reads as a free
        /// slot. <see cref="FaObjectHeaderFlags.MaxBytePointerValid"/> is set because a served file
        /// always carries a real byte count, and over the whole capture that bit and a real byte
        /// count never appear apart.
        /// <para><b>The terminal number stays zero</b></para>
        /// Bits 10-0 hold the terminal number of the reserving user. Nothing reserves a file we
        /// serve out of a Windows folder, so zero is correct rather than merely convenient.
        /// </remarks>
        public const FaObjectHeaderFlags DefaultHeader =
            FaObjectHeaderFlags.EntryUsed | FaObjectHeaderFlags.MaxBytePointerValid;

        /// <summary>
        /// The span in years of the packed ND date word - 1950 to 2013 inclusive.
        /// </summary>
        /// <remarks>
        /// The year is six bits counted from 1950, so the format holds exactly 64 of them and
        /// cannot express any year from 2014 onwards. See
        /// <c>RetroIO.Storage.Ndfs.Elements.NdDateTime</c> for the full bit layout.
        /// </remarks>
        public const int NdDateSpanYears = 64;

        /// <summary>
        /// The first year the packed ND date word can express.
        /// </summary>
        public const int NdDateFirstYear = 1950;

        /// <summary>
        /// The last year the packed ND date word can express.
        /// </summary>
        public const int NdDateLastYear = 2013;

        /// <summary>
        /// Packs a timestamp into the ND date word, folding the year into the range the format can
        /// hold instead of giving up and writing zero.
        /// </summary>
        /// <param name="when">
        /// The timestamp to encode. Any year is accepted.
        /// </param>
        /// <returns>
        /// The packed ND date word. Never zero for a real timestamp.
        /// </returns>
        /// <remarks>
        /// <para>
        /// <b>Why this exists.</b> <c>NdDateTime.ToNdDateOrUnset</c> returns 0 for anything from
        /// 2014 onwards, which is the right answer when WRITING a file to a real pack - better no
        /// timestamp than a fabricated one. It is the wrong answer for a listing record, because
        /// the wire says so: in the 49 records of
        /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-list-files.txt</c>, <b>the creation date
        /// is non-zero on every single one</b>. The last-opened fields are often zero (17 and 9
        /// records respectively); the creation date never is. Every record we served carried zero,
        /// because every file in a Windows folder has a present-day timestamp.
        /// </para>
        /// <para>
        /// <b>What the fold does.</b> It subtracts whole 64-year cycles until the year is in range,
        /// so 2026 becomes 1962 and the month, day and time of day are carried across untouched. The
        /// year is therefore congruent to the real one modulo the format's own span, which makes the
        /// mapping reversible and derived from the format rather than an invented constant.
        /// </para>
        /// <para>
        /// <b>It is still not the real year, and that is a compromise.</b> The alternatives were a
        /// zero the wire contradicts, or a fixed made-up date that carries no information at all.
        /// The proper fix is the sidecar that holds the ND-specific fields for a served folder - see
        /// <c>DOC\PLAN-CSHARP-FILE-SERVER-AND-FOLDER-SYNC-2026-08-01.md</c> section 1.
        /// </para>
        /// <para>
        /// 29 February needs no special handling: a fold only ever lands in 1950-2013, whose one
        /// century year (2000) IS a leap year, so a leap day cannot fold onto a non-leap year.
        /// </para>
        /// </remarks>
        public static uint ToListingDate(DateTime when)
        {
            int year = when.Year;

            while (year > NdDateLastYear)
            {
                year -= NdDateSpanYears;
            }

            while (year < NdDateFirstYear)
            {
                year += NdDateSpanYears;
            }

            DateTime folded = new DateTime(
                year,
                when.Month,
                when.Day,
                when.Hour,
                when.Minute,
                when.Second,
                DateTimeKind.Unspecified);

            return NdDateTime.ToNdDate(folded);
        }

        /// <summary>
        /// Builds the 64-byte object-entry record for one served file.
        /// </summary>
        /// <param name="file">
        /// The file as the store reports it.
        /// </param>
        /// <param name="userIndex">
        /// The owning user's index, written to byte 34. We have no real user directory behind a
        /// Windows folder, so the caller supplies whatever user it is serving as. INFERRED that a
        /// client cares about this field at all.
        /// </param>
        /// <param name="ordinal">
        /// The entry's position in the directory walk, counting from zero. It is written to the
        /// object index (bytes 34-35) and to both version pointers (bytes 22-25), which on a real
        /// pack all hold the entry's directory SLOT - the same number only while the directory has
        /// no holes in it, which is always true of a folder we synthesise. See the body.
        /// </param>
        /// <returns>
        /// The 64-byte record, ready to place in a listing reply.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="file"/> is null.
        /// </exception>
        public static byte[] BuildRecord(FaFileInfo file, byte userIndex, int ordinal)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            ObjectEntry entry = new ObjectEntry();

            // The header word MUST have the "entry used" bit or the record reads as a free slot and
            // the file vanishes from the listing.
            //
            // Bit 12 as well - CORRECTED 2026-08-05. The note here used to say bit 12 "describes pack
            // state we do not have", so we sent a bare 0x8000. Measured over all 49 captured records,
            // bit 12 tracks the max byte pointer EXACTLY:
            //
            //     bit 12 SET   + a real max byte pointer      40 records
            //     bit 12 CLEAR + max byte pointer 0xFFFFFFFF   9 records
            //     any other combination                        0 records
            //
            // We were sending bit 12 CLEAR together with a real byte count - a pairing that occurs
            // nowhere in the capture, and the same kind of self-contradiction as declaring an indexed
            // file with a contiguous pointer.
            //
            // HONEST LIMIT: this does NOT prove bit 12 means "the byte count is valid". ObjectEntry
            // calls it "file modified", and on this data the two readings are indistinguishable -
            // every file with a real length has also been written, and every device file has neither.
            // It does not matter here, because BOTH readings say the same thing about a file we
            // serve: it has been written and it has a real byte count. Set the bit.
            entry.Header = (uint)DefaultHeader;

            entry.ObjectName = file.Name.Name;
            entry.Type = file.Name.Type ?? string.Empty;
            entry.UserIndex = userIndex;

            // Bytes 26-27, the ACCESS WORD. Named in the SINTRAN III Monitor Calls manual appendix C
            // and printed as "ACCESS WORD" by @DUMP-OBJECT-ENTRY; see DefaultAccessWord above for
            // the tier layout and for why leaving it at zero was a defect rather than a blank.
            entry.AccessBits = DefaultAccessWord;

            // Bytes 28-29, the ATTRIBUTE / logical-file-type word. ObjectEntry already defaults this
            // to IndexedFile, which is right for the ordinary files we serve and is what 37 of the 49
            // captured records carry - it is set explicitly here so the value does not depend on a
            // default in another repository.
            //
            // NOT A DEFECT, despite what the 2026-08-04 record diff suggested: that diff compared our
            // record against SINTRAN:DATA, which carries 0x0020 (allocated) because it is a
            // CONTIGUOUS system file. An indexed user file carries 0x0008 and always has.
            entry.FileTypeFlags = FileTypes.IndexedFile;

            // Bytes 34-35, the OBJECT INDEX: the entry's SLOT in the directory, not its position in
            // the walk. The two look identical until the directory has holes in it. In
            // capture-list-files.txt the first 42 entries agree, then deleted slots start being
            // skipped and they part company - walk position 45 (LOAD-MODE:BATC) carries object index
            // 0x0030 = 48. So "the walk ordinal" was the wrong name for this field, even though it
            // produced the right bytes on the gap-free directories we had looked at.
            //
            // We synthesise the directory from a Windows folder and never leave a hole, so slot and
            // walk position coincide and the ordinal is the correct value to write. If a served
            // folder ever gains stable slot numbers (the sidecar design), write the slot instead.
            //
            // INFERRED that the low byte is all a client reads - byte 35 cannot hold a number above
            // 255 (see the ObjectEntry remarks), so a folder with more than 255 files will collide
            // there. Not fixed here because the fix belongs with the sidecar design.
            entry.ObjectIndex = (ushort)ordinal;

            // Bytes 22-23 and 24-25 are the NEXT and PREVIOUS VERSION pointers, and each holds an
            // object index. A file with only one version points both of them at ITSELF - the
            // System Supervisor manual's annotated dump of object entry 025 prints
            // "000025 000025  POINTERS TO NEXT AND PREVIOUS VERSION", and every captured record
            // repeats its own object index in both words. Previously written by hand after ToBytes
            // and described as "the walk ordinal", which happened to give the same bytes.
            entry.ObjectEntryNextVersion = (ushort)ordinal;
            entry.ObjectEntryPrevVersion = (ushort)ordinal;

            // Bytes 60-63, the FILE POINTER: two bits of pointer type over a 30-bit page id. The
            // type bits must agree with the attribute word above - an entry that calls itself
            // indexed and then hands over a CONTIGUOUS pointer is self-contradictory, which is what
            // we were sending. In the capture the two always agree: every record with attributes
            // 0x0008 carries pointer type 01 (0x4000....), and the contiguous and peripheral ones
            // carry type 00.
            //
            // This replaces a hand-written 0x000078DA and the claim that bytes 60-63 were constant
            // on all 49 records. They are not - only the low page id is. THAT CLAIM WAS WRONG.
            entry.FilePointer = new BlockPointer(
                ListingFilePointerPage,
                BlockPointer.PointerType.Indexed);

            // Length: bytes exactly, pages rounded up. ObjectEntry stores "bytes in file" on the wire
            // as the value minus one and adds it back on read, so assigning the true length here is
            // correct - do not pre-subtract.
            entry.BytesInFile = (ulong)(file.ByteLength < 0 ? 0 : file.ByteLength);
            entry.PagesInFile = (uint)((file.ByteLength + BytesPerPage - 1) / BytesPerPage);

            // The three ND dates, set to match how the capture uses them rather than all alike.
            //
            // CORRECTED 2026-08-05. The previous note here said the all-zero dates were "not a
            // protocol fault - the capture has real records carrying 0000 in these fields too". That
            // is true of the last-opened fields and FALSE of the creation date. Counted over all 49
            // records in capture-list-files.txt:
            //
            //     DateCreated           0 of 49 are zero      <- never
            //     LastDateOpenedForRead 17 of 49 are zero
            //     LastDateOpenedForWrite 9 of 49 are zero
            //
            // So a zero creation date is something no real record has ever carried, and ours carried
            // one on every file. ToListingDate folds the year into the range the format can hold
            // instead of returning zero - see its remarks for why that compromise, and what its
            // limits are.
            uint stamp = ToListingDate(file.ModifiedUtc);
            entry.DateCreated = stamp;
            entry.LastDateOpenedForWrite = stamp;

            // Left at zero deliberately. A Windows folder has no "last opened for read" to report,
            // and a third of the real records carry zero here, so zero is a value the wire actually
            // uses for this field - unlike the creation date above.
            entry.LastDateOpenedForRead = 0;

            // Every field the FA listing record carries is now set through ObjectEntry itself.
            // Nothing is poked into the buffer afterwards: ObjectEntry DOES model bytes 22-25,
            // 26-27, 28-29 and 60-63, and writing them by hand on top of it only hid that.
            byte[] record = new byte[64];
            entry.ToBytes(record, 0);

            return record;
        }
    }
}
