using System;
using System.Collections.Generic;
using RetroIO.Protocols.Xmsg.Qform;
using RetroIO.Protocols.Xmsg.Sintran;

namespace RetroIO.Protocols.Xmsg.Fa
{
    /// <summary>
    /// Builds and reads the QFORM bodies of a remote <c>LIST-FILES</c>: the request that asks for one
    /// directory entry, and the reply that returns it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A remote directory listing is <b>not</b> one bulk reply. It is one request/reply round trip per
    /// entry, driven by a cursor. Captured from node 102 to node 100 in
    /// <c>claude-list-files-d100-system-2026-07-29.pcapng</c>, ten entries in a row:
    /// </para>
    /// <code>
    /// request  92 000C  92 serial  F2 0001 92 0078  F2 0002 8C 80 46 block  F2 00FF
    /// reply    92 000C  92 serial                    F2 0002 8C 4B record     F2 00FF
    /// </code>
    /// <para>
    /// Within the entry walk the request body is byte-identical from one entry to the next except for
    /// the serial and the LAST of the two trailing <c>A2</c> values inside the constructed block. That
    /// value is the cursor: <c>0xFFFF</c> asks for the first entry, and thereafter it is the index of
    /// the entry wanted, running 1, 2, 3 and up. The reply echoes the request's serial.
    /// </para>
    /// <para>
    /// <b>CORRECTION, 2026-07-30.</b> That sentence used to say the body was byte-identical across
    /// the whole listing, with no mention of the walk. It is not. The SYSTEM capture holds ELEVEN
    /// directory enquiries, and the one that PRECEDES the <c>0xFFFF</c> request carries the same
    /// leading filespec text with a zero-filled tail, where the ten walk requests carry values in the
    /// same positions. So the spec block is not a caller-supplied constant for the whole listing -
    /// part of it is state that only exists once the walk has begun. Its meaning is UNKNOWN; it is
    /// consistent with a position handle carried forward from the previous reply, but neither capture
    /// tests that. Locked in by <c>FaSpecBlockCrossUserTests.EntryWalkRequests_ShareOneSpecBlock</c>.
    /// </para>
    /// <para>
    /// <b>The 64-byte record is deliberately opaque here.</b> It is the SINTRAN on-disk object entry
    /// shipped verbatim - the file server does not reformat it - so its layout belongs to the file
    /// system, not to this wire codec. Decode it with
    /// <c>RetroIO.Storage.Ndfs.Elements.ObjectEntry.FromBytes</c>; the adapter in <c>RetroIO.Protocols.Xmsg.Ndfs</c> does
    /// exactly that. Duplicating the layout here would mean two copies of the same structure.
    /// </para>
    /// <para>
    /// The 62-byte block that precedes the cursor carries the directory and user spec and is
    /// <b>not decoded</b>. It is handled as opaque bytes so a captured listing can be replayed
    /// exactly. Do not synthesise one.
    /// </para>
    /// <para>
    /// The 62-byte length is confirmed across two users, and so is the leading text shape - the block
    /// opens with the parenthesised user name, the SINTRAN string terminator <c>0x27</c>, then the
    /// filespec as typed, terminated again:
    /// </para>
    /// <code>
    /// SYSTEM   (SYSTEM)'EM).(SYSTEM)'      then 0x3704 B5DC ... 48FF ... zero fill
    /// SECRET   (SECRET)'ET(SECRET)).(SECRET)'  then 0x0000 ... 48FF ... zero fill
    /// </code>
    /// <para>
    /// 23 of the 62 bytes differ between the two users. The word <c>0x48FF</c> appears in the same
    /// relative position in both. The leading text is TRUNCATED on the left - the SECRET block shows
    /// the password parentheses that were typed, the SYSTEM one does not - so the field is a
    /// fixed-width window over the typed specification rather than a self-describing string. Where
    /// exactly the window starts is UNKNOWN.
    /// </para>
    /// </remarks>
    public static class FaListFilesCodec
    {
        /// <summary>
        /// The cursor value that asks for the first entry in the directory.
        /// </summary>
        public const ushort FirstEntryCursor = 0xFFFF;

        /// <summary>
        /// The length of the opaque directory/user block in a request.
        /// </summary>
        public const int SpecBlockLength = 62;

        /// <summary>
        /// The length of the directory entry record carried in a reply.
        /// </summary>
        /// <remarks>
        /// This is the SINTRAN object entry size, and it matches
        /// <c>RetroIO.Storage.Ndfs.Elements.ObjectEntry</c>, which reads and writes 64 bytes.
        /// </remarks>
        public const int EntryRecordLength = 64;

        /// <summary>
        /// The operation code that opens the body of a directory-enquiry request and its reply.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>CORRECTION.</b> This was previously called <c>LeadingConstant</c> and documented as
        /// "meaning UNKNOWN, 0x000C on every captured frame". It is not a constant. Dumping the
        /// DELETE-FILE capture alongside this one showed the same position carrying <c>0x000B</c>
        /// there, so the field is an operation code and 0x000C only looked constant because every
        /// frame examined at the time belonged to one operation.
        /// </para>
        /// <para>
        /// See <see cref="FaExchangeCodec"/> for the shared envelope and the codes observed so far.
        /// </para>
        /// </remarks>
        public const FaOperation OperationDirectoryEnquiry = FaOperation.SiiiSpecial;

        /// <summary>
        /// The value carried under selector 1 in a request.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Meaning UNKNOWN. It is 0x0078 on every request of both captured LISTINGS, which are
        /// against two different users on node 100 (SYSTEM and SECRET). Checked by
        /// <c>FaSpecBlockCrossUserTests</c>.
        /// </para>
        /// <para>
        /// <b>But it is NOT a property of operation 0x000C.</b> Corrected 2026-07-30: the
        /// <c>claude-open-W-close-102-to-100-2026-07-30.pcapng</c> recording carries an 0x000C request
        /// with <c>92 003B</c> under selector 1, and a five-byte payload of
        /// <c>A4 FFFFFFFF</c> instead of the 62-byte directory and user block:
        /// </para>
        /// <code>
        /// 92 000C  92 0003  F2 0001 92 003B  F2 0002 8C 80 05 A4 FFFFFFFF  F2 00FF
        /// </code>
        /// <para>
        /// So 0x0078 belongs to the LIST-FILES use of the operation, not to the operation itself.
        /// Whatever that other request is - it followed an open and a close at the terminal - is
        /// UNKNOWN, and this constant must not be written into one.
        /// </para>
        /// <para>
        /// That is the fifth value in this decode to look constant only because a single kind of
        /// request had been examined. The others were the operation code itself, the responder session
        /// token, the 594-byte fragment split, and the opening request's BAK field.
        /// </para>
        /// </remarks>
        public const ushort RequestSelector1Value = (ushort)FaSpecialFunction.NextFileEntry;

        /// <summary>
        /// Selector naming the field that carries the request block or the entry.
        /// </summary>
        public const ushort PayloadSelector = 0x0002;

        /// <summary>
        /// The selector under which a request carries its sub-function.
        /// </summary>
        /// <remarks>
        /// Selector 1 in a REQUEST says which of the three things the client wants - see
        /// <see cref="FaSpecialFunction"/>. In a REPLY the same selector means something else
        /// entirely: its presence is the error signal, and the value is a SINTRAN error number.
        /// </remarks>
        public const ushort FunctionSelector = 0x0001;

        /// <summary>
        /// Builds the body of a request for one directory entry.
        /// </summary>
        /// <param name="serial">
        /// The transaction serial; the reply echoes it.
        /// </param>
        /// <param name="cursor">
        /// <see cref="FirstEntryCursor"/> for the first entry, otherwise the index wanted.
        /// </param>
        /// <param name="specBlock">
        /// The opaque <see cref="SpecBlockLength"/>-byte directory and user block, taken from a
        /// capture. See the remarks on the class.
        /// </param>
        /// <returns>
        /// The QFORM body, ready to place in an XMSG message.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// The block is the wrong length.
        /// </exception>
        public static byte[] BuildRequest(ushort serial, ushort cursor, ReadOnlySpan<byte> specBlock)
        {
            if (specBlock.Length != SpecBlockLength)
            {
                throw new ArgumentException(
                    "The directory spec block is " + SpecBlockLength + " bytes, got " + specBlock.Length.ToString() + ".",
                    nameof(specBlock));
            }

            // 92 000C | 92 serial | F2 0001 92 0078 | F2 0002 8C 80 46 <70> | F2 00FF
            byte[] body = new byte[3 + 3 + 3 + 3 + 3 + 3 + 70 + 3];
            QformWriter writer = new QformWriter(body);

            writer.WriteInteger((ushort)OperationDirectoryEnquiry);
            writer.WriteInteger(serial);
            writer.WriteSelector(FunctionSelector);
            writer.WriteInteger(RequestSelector1Value);
            writer.WriteSelector(PayloadSelector);

            // The constructed value takes the ESCAPED length form here - 8C 80 46 rather than
            // 8C 46 - which is what the captured requests carry.
            writer.WriteConstructedEscaped(70);
            writer.WriteByteString(specBlock);

            writer.WriteTypedInteger(0x0000);
            writer.WriteTypedInteger(cursor);

            writer.WriteEndOfList();
            writer.EnsureComplete("Request");

            return body;
        }

        /// <summary>
        /// Builds the body of a reply carrying one directory entry.
        /// </summary>
        /// <param name="serial">
        /// The serial from the request being answered.
        /// </param>
        /// <param name="entryRecord">
        /// The <see cref="EntryRecordLength"/>-byte object entry to return, already in on-disk form.
        /// </param>
        /// <param name="leadingA">
        /// The first <c>A2</c> value inside the constructed block.
        /// </param>
        /// <param name="leadingB">
        /// The second <c>A2</c> value: the entry's ORDINAL in the walk, counting from zero.
        /// </param>
        /// <returns>
        /// The QFORM body.
        /// </returns>
        /// <remarks>
        /// <para>
        /// The FIRST value's meaning is still UNKNOWN - captured replies carried <c>0x0000</c> and
        /// <c>0x0007</c> there on different sessions. The third was <c>0x0001</c> on every capture
        /// and is written as such.
        /// </para>
        /// <para>
        /// The SECOND value is the entry ordinal. VERIFIED 2026-08-04 in
        /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-list-files.txt</c>, where consecutive
        /// entry replies count it up while everything around it stays put:
        /// </para>
        /// <code>
        /// A2 0000  A2 0000  A2 0001  B0 40 ... "SINTRAN"
        /// A2 0000  A2 0001  A2 0001  B0 40 ... "MACM-AREA"
        /// A2 0000  A2 0002  A2 0001  B0 40 ... "SEGFIL0"
        /// A2 0000  A2 0003  A2 0001  B0 40 ... "MAILBOX"
        /// </code>
        /// <para>
        /// It defaulted to zero and <c>FaServer</c> never passed it, so every entry we served
        /// claimed to be entry zero. Live against D100 that ended the listing after two files with
        /// nothing printed at the terminal.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// The record is the wrong length.
        /// </exception>
        public static byte[] BuildReply(
            ushort serial,
            ReadOnlySpan<byte> entryRecord,
            ushort leadingA = 0,
            ushort leadingB = 0)
        {
            if (entryRecord.Length != EntryRecordLength)
            {
                throw new ArgumentException(
                    "A directory entry record is " + EntryRecordLength + " bytes, got " + entryRecord.Length.ToString() + ".",
                    nameof(entryRecord));
            }

            // 92 000C | 92 serial | F2 0002 8C 4B <75> | F2 00FF
            byte[] body = new byte[3 + 3 + 3 + 2 + 75 + 3];
            QformWriter writer = new QformWriter(body);

            writer.WriteInteger((ushort)OperationDirectoryEnquiry);
            writer.WriteInteger(serial);
            writer.WriteSelector(PayloadSelector);

            // Constructed, 75 bytes: three A2 pairs (9) plus B0 40 and the 64-byte record (66).
            writer.WriteConstructed(75);

            writer.WriteTypedInteger(leadingA);
            writer.WriteTypedInteger(leadingB);
            writer.WriteTypedInteger(0x0001);
            writer.WriteByteString(entryRecord);

            writer.WriteEndOfList();
            writer.EnsureComplete("Reply");

            return body;
        }

        /// <summary>
        /// Builds the reply to a <see cref="FaSpecialFunction.FileInformation"/> request.
        /// </summary>
        /// <param name="serial">
        /// The serial from the request being answered.
        /// </param>
        /// <param name="entryRecord">
        /// The <see cref="EntryRecordLength"/>-byte directory entry for the open file.
        /// </param>
        /// <returns>
        /// The QFORM body, from the operation word onwards.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// The record is the wrong length.
        /// </exception>
        /// <remarks>
        /// <para><b>The same record as a listing, wrapped more simply</b></para>
        /// A listing entry wraps the 64-byte record in a 75-byte constructed block behind three
        /// <c>A2</c> values (cursor, ordinal, and a constant 1). This reply has none of them - just
        /// the record, in a 66-byte block:
        /// <code>
        /// 92 000C  92 serial  F2 0002 8C 42  B0 40 64 bytes  F2 00FF
        /// </code>
        /// Measured at <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-read.txt</c> line 64.
        /// <c>0x42</c> = 66 = the <c>B0 40</c> tag pair plus the record.
        /// </remarks>
        public static byte[] BuildFileInformationReply(ushort serial, ReadOnlySpan<byte> entryRecord)
        {
            if (entryRecord.Length != EntryRecordLength)
            {
                throw new ArgumentException(
                    "A directory entry record is " + EntryRecordLength + " bytes, got " + entryRecord.Length.ToString() + ".",
                    nameof(entryRecord));
            }

            // 92 000C | 92 serial | F2 0002 8C 42 | B0 40 <64> | F2 00FF
            byte[] body = new byte[3 + 3 + 3 + 2 + 66 + 3];
            QformWriter writer = new QformWriter(body);

            writer.WriteInteger((ushort)OperationDirectoryEnquiry);
            writer.WriteInteger(serial);
            writer.WriteSelector(PayloadSelector);

            // Constructed, 66 bytes: B0 40 and the 64-byte record. No leading A2 values.
            writer.WriteConstructed(66);
            writer.WriteByteString(entryRecord);

            writer.WriteEndOfList();
            writer.EnsureComplete("FileInformationReply");

            return body;
        }

        /// <summary>
        /// The length in bytes of the 42-byte directory entry record.
        /// </summary>
        public const byte DirectoryRecordLength = 42;

        /// <summary>
        /// Builds the reply that carries the PACK directory entry rather than a file entry.
        /// </summary>
        /// <param name="serial">
        /// The transaction serial to echo.
        /// </param>
        /// <param name="directoryRecord">
        /// The 42-byte directory entry, from
        /// <c>RetroIO.Protocols.Xmsg.Ndfs.FaDirectoryEntry.BuildRecord</c>.
        /// </param>
        /// <returns>
        /// The QFORM body.
        /// </returns>
        /// <remarks>
        /// <para><b>This reply has a DIFFERENT shape from a file entry</b></para>
        /// A file entry is <c>8C 4B  A2 0000  A2 ordinal  A2 0001  B0 40 record</c> - three tagged
        /// words ahead of the record. The directory entry is <c>8C 2F  B0 2A record  A2 0001</c>:
        /// the record comes FIRST and only one tagged word follows it. Measured on the one such
        /// reply in <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-list-files.txt</c>.
        /// <para><b>It consumes no walk ordinal</b></para>
        /// The file entries either side of it count <c>A2 0000</c> then <c>A2 0001</c> unbroken, so
        /// the directory entry must not advance the walk position.
        /// <para><b>The length arithmetic</b></para>
        /// The constructed value is <c>0x2F</c> = 47 bytes: <c>B0 2A</c> plus the 42-byte record is
        /// 44, plus the trailing <c>A2 0001</c> is 3.
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="directoryRecord"/> is the wrong length.
        /// </exception>
        public static byte[] BuildDirectoryReply(ushort serial, ReadOnlySpan<byte> directoryRecord)
        {
            if (directoryRecord.Length != DirectoryRecordLength)
            {
                throw new ArgumentException(
                    "A directory entry record is " + DirectoryRecordLength + " bytes, got " + directoryRecord.Length.ToString() + ".",
                    nameof(directoryRecord));
            }

            // 92 000C | 92 serial | F2 0002 | 8C 2F <47> | F2 00FF
            byte[] body = new byte[3 + 3 + 3 + 2 + 47 + 3];

            QformWriter writer = new QformWriter(body);

            writer.WriteInteger((ushort)OperationDirectoryEnquiry);
            writer.WriteInteger(serial);
            writer.WriteSelector(PayloadSelector);

            // The record comes FIRST here and only one tagged word follows it - the opposite way
            // round from a file entry. See the remarks.
            writer.WriteConstructed(47);
            writer.WriteByteString(directoryRecord);
            writer.WriteTypedInteger(0x0001);

            writer.WriteEndOfList();
            writer.EnsureComplete("Directory reply");

            return body;
        }

        /// <summary>
        /// The length in bytes of the user-name field in a user-entry reply.
        /// </summary>
        public const byte UserNameLength = 16;

        /// <summary>
        /// The byte that ends a SINTRAN name shorter than its fixed-width field.
        /// </summary>
        /// <remarks>
        /// The apostrophe, <c>0x27</c>. It follows <c>PACK-ONE</c>, <c>SYSTEM</c>, <c>TXT1</c> and
        /// every short name in the captures. A name that fills its field exactly carries none.
        /// </remarks>
        public const byte SintranNameTerminator = SintranName.Terminator;

        /// <summary>
        /// Builds the short request the client sends for the directory or the user.
        /// </summary>
        /// <param name="serial">
        /// The transaction serial.
        /// </param>
        /// <param name="function">
        /// Which entry is wanted.
        /// </param>
        /// <returns>
        /// The QFORM body.
        /// </returns>
        /// <remarks>
        /// <para><b>Shape</b></para>
        /// <c>92 000C  92 serial  F2 0001  92 function  F2 0002  8C 80 03  A2 0000  F2 00FF</c> -
        /// the same skeleton as a file request but with a 3-byte argument instead of a 70-byte one,
        /// which is why these requests measure 32 bytes on the wire against a file request's 100.
        /// <para><b>Where it was measured</b></para>
        /// Sessions <c>82</c> and <c>83</c> of the walk in
        /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-list-files.txt</c>.
        /// </remarks>
        public static byte[] BuildShortRequest(ushort serial, FaSpecialFunction function)
        {
            // 92 000C | 92 serial | F2 0001 | 92 function | F2 0002 | 8C 80 03 A2 0000 | F2 00FF
            byte[] body = new byte[3 + 3 + 3 + 3 + 3 + 3 + 3 + 3];
            QformWriter writer = new QformWriter(body);

            writer.WriteInteger((ushort)OperationDirectoryEnquiry);
            writer.WriteInteger(serial);
            writer.WriteSelector(FunctionSelector);
            writer.WriteInteger((ushort)function);
            writer.WriteSelector(PayloadSelector);

            // Three bytes of argument instead of a file request's seventy, still in the escaped
            // length form - which is why these requests measure 32 bytes on the wire against 100.
            writer.WriteConstructedEscaped(3);
            writer.WriteTypedInteger(0x0000);

            writer.WriteEndOfList();
            writer.EnsureComplete("Short request");

            return body;
        }

        /// <summary>
        /// Builds the reply that carries the user name.
        /// </summary>
        /// <param name="serial">
        /// The transaction serial to echo.
        /// </param>
        /// <param name="userName">
        /// The user to report, for example <c>SYSTEM</c>. Upper-cased, terminated with <c>0x27</c>
        /// when shorter than <see cref="UserNameLength"/>, and truncated when longer.
        /// </param>
        /// <returns>
        /// The QFORM body.
        /// </returns>
        /// <remarks>
        /// <para><b>Shape</b></para>
        /// <c>8C 12  B0 10 name</c> - a constructed value of 18 bytes holding one 16-byte string,
        /// with no tagged word after it. Measured on the single such reply in
        /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-list-files.txt</c>.
        /// <para><b>The captured padding is junk</b></para>
        /// The real reply's 16 bytes read <c>"SYSTEM" 27</c> then <c>40 00 00 "PACK-O"</c>. The tail
        /// is uninitialised server memory, not a field - the name ends at its <c>0x27</c>. We pad
        /// with zeros instead of reproducing another server's leftovers.
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="userName"/> is null or empty.
        /// </exception>
        public static byte[] BuildUserReply(ushort serial, string userName)
        {
            if (string.IsNullOrEmpty(userName))
            {
                throw new ArgumentException("A user entry must carry a name.", nameof(userName));
            }

            // 92 000C | 92 serial | F2 0002 | 8C 12 <18> | F2 00FF
            byte[] body = new byte[3 + 3 + 3 + 2 + 18 + 3];
            QformWriter writer = new QformWriter(body);

            writer.WriteInteger((ushort)OperationDirectoryEnquiry);
            writer.WriteInteger(serial);
            writer.WriteSelector(PayloadSelector);

            // A constructed value of 18 bytes holding one 16-byte string, with no tagged word after
            // it. The name is laid out first so the writer sees a finished field.
            //
            // A heap array rather than a stackalloc: QformWriter is a ref struct over a Span, and
            // handing it a stack buffer is a ref-safety error. Sixteen bytes once per listing is
            // not worth working around.
            byte[] nameField = new byte[UserNameLength];
            SintranName.Write(nameField, userName);

            writer.WriteConstructed(18);
            writer.WriteByteString(nameField);

            writer.WriteEndOfList();
            writer.EnsureComplete("User reply");

            return body;
        }

        /// <summary>
        /// Reads the serial and cursor out of a request body.
        /// </summary>
        /// <param name="body">
        /// The QFORM body.
        /// </param>
        /// <param name="serial">
        /// The transaction serial.
        /// </param>
        /// <param name="cursor">
        /// The directory cursor.
        /// </param>
        /// <returns>
        /// <see langword="true"/> if the body looked like a listing request.
        /// </returns>
        public static bool TryReadRequest(ReadOnlySpan<byte> body, out ushort serial, out ushort cursor)
        {
            FaSpecialFunction function;
            return TryReadRequest(body, out serial, out cursor, out function);
        }

        /// <summary>
        /// Reads the serial, cursor and sub-function out of a listing request body.
        /// </summary>
        /// <param name="body">
        /// The QFORM body.
        /// </param>
        /// <param name="serial">
        /// The transaction serial.
        /// </param>
        /// <param name="cursor">
        /// The directory cursor.
        /// </param>
        /// <param name="function">
        /// Which of the three things the client is asking for.
        /// </param>
        /// <returns>
        /// <see langword="true"/> if the body looked like a listing request.
        /// </returns>
        /// <remarks>
        /// <para><b>The sub-function is the THIRD tagged word</b></para>
        /// A listing request reads <c>92 000C</c>, <c>92 serial</c>, <c>F2 0001</c>,
        /// <c>92 function</c>, <c>F2 0002</c>, then the constructed argument. The fourth field is
        /// the sub-function and it is what says whether the client wants a file, the directory or
        /// the user.
        /// <para><b>Measured</b></para>
        /// From the walk in <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-list-files.txt</c>,
        /// paired by session-header byte:
        ///  - <c>92 0078</c> asked for a file and was answered with a 98-byte reply.
        ///  - <c>92 00A4</c> asked for the directory and was answered with a 70-byte reply.
        ///  - <c>92 008C</c> asked for the user and was answered with a 40-byte reply.
        /// <para><b>Why this matters</b></para>
        /// Before 2026-08-05 every request was read as "give me the next file", so the two requests
        /// that wanted the directory and the user were answered with file entries. That is what made
        /// D100 abandon a listing after the second file, whatever the folder held.
        /// </remarks>
        public static bool TryReadRequest(
            ReadOnlySpan<byte> body,
            out ushort serial,
            out ushort cursor,
            out FaSpecialFunction function)
        {
            serial = 0;
            cursor = 0;
            function = FaSpecialFunction.NextFileEntry;

            IReadOnlyList<QformField> fields;
            if (!QformReader.TryRead(body, out fields) || fields.Count < 2)
            {
                return false;
            }

            serial = QformValue.ReadUInt16(body, fields[1]);

            // The sub-function is the next top-level plain integer after the serial.
            for (int i = 2; i < fields.Count; i++)
            {
                if (fields[i].Depth == 0 && fields[i].Class == QformClass.Integer && fields[i].ValueLength == 2)
                {
                    function = (FaSpecialFunction)QformValue.ReadUInt16(body, fields[i]);
                    break;
                }
            }

            // The cursor is the last nested A2 - the deepest, latest integer in the body.
            bool found = false;
            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i].Depth == 1 && fields[i].Class == QformClass.TypedInteger && fields[i].ValueLength == 2)
                {
                    cursor = QformValue.ReadUInt16(body, fields[i]);
                    found = true;
                }
            }

            return found;
        }

        /// <summary>
        /// Reads the sub-function out of a <c>SiiiSpecial</c> request, and nothing else.
        /// </summary>
        /// <param name="body">
        /// The QFORM body.
        /// </param>
        /// <param name="function">
        /// Which sub-function the request names.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when a sub-function was found.
        /// </returns>
        /// <remarks>
        /// <para><b>Why this is separate from the listing reader</b></para>
        /// Not every <c>SiiiSpecial</c> is a listing. <see cref="FaSpecialFunction.SetEndOfFile"/>
        /// carries a 32-bit length and NO cursor, so the listing reader - which requires one -
        /// refuses it. A server has to know WHICH sub-function it is looking at before it can pick
        /// a reader, and that is all this answers.
        /// <para><b>The sub-function is the next plain integer after the serial</b></para>
        /// The body opens <c>92 000C</c>, <c>92 serial</c>, <c>F2 0001</c>, <c>92 function</c>. The
        /// four-argument <c>TryReadRequest</c> below reads it the same way.
        /// </remarks>
        public static bool TryReadFunction(ReadOnlySpan<byte> body, out FaSpecialFunction function)
        {
            function = FaSpecialFunction.NextFileEntry;

            IReadOnlyList<QformField> fields;
            if (!QformReader.TryRead(body, out fields) || fields.Count < 2)
            {
                return false;
            }

            for (int i = 2; i < fields.Count; i++)
            {
                if (fields[i].Depth == 0 && fields[i].Class == QformClass.Integer && fields[i].ValueLength == 2)
                {
                    function = (FaSpecialFunction)QformValue.ReadUInt16(body, fields[i]);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reads the byte length carried by a <see cref="FaSpecialFunction.SetEndOfFile"/> request.
        /// </summary>
        /// <param name="body">
        /// The QFORM body.
        /// </param>
        /// <param name="byteLength">
        /// The file's true length in bytes.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when a four-byte length was found.
        /// </returns>
        /// <remarks>
        /// <para><b>Found by TAG, never by offset</b></para>
        /// The captured request is
        /// <c>92 000C 92 000D F2 0001 92 003B F2 0002 8C 80 05 A4 000045F0 F2 00FF</c>, and it is
        /// tempting to reach straight past the <c>8C 80 05</c>. Do not:
        /// <c>DOC\FA-READ-WRITE-WIRE-PROTOCOL-2026-08-04.md</c> records those three bytes as
        /// INFERRED and notes that <c>0x80 05</c> does not read as a length there. So the value is
        /// located as the four-byte typed integer it is, and the prefix is not interpreted at all.
        /// <para><b>Why a write needs it</b></para>
        /// A write ships whole 2048-byte blocks, so the last one is padded. Without this the file
        /// on disk would be rounded up to a block boundary - the captured write states
        /// <c>0x45F0</c> = 17904 bytes for a file that arrived as nine 2048-byte blocks.
        /// </remarks>
        public static bool TryReadEndOfFile(ReadOnlySpan<byte> body, out uint byteLength)
        {
            byteLength = 0;

            IReadOnlyList<QformField> fields;
            if (!QformReader.TryRead(body, out fields))
            {
                return false;
            }

            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i].Class == QformClass.TypedInteger && fields[i].ValueLength == 4)
                {
                    byteLength = QformValue.ReadUInt32(body, fields[i]);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reads the file name a listing request names, out of the request's spec block.
        /// </summary>
        /// <param name="body">
        /// The QFORM body.
        /// </param>
        /// <param name="fileName">
        /// The file specification the client named, or an empty string when it named none and wants
        /// the whole directory.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when a spec block was found and read, whether or not it named a
        /// file.
        /// </returns>
        /// <remarks>
        /// <para><b>Carved 2026-08-05 by comparing a listing that names a file with two that do not</b></para>
        /// The 62-byte block opens with the user in parentheses and then carries the filespec TWICE,
        /// each copy ended by the SINTRAN terminator <c>0x27</c>. From the captures, printable
        /// characters only:
        /// <code>
        /// FILE-STATISTICS  (SYSTEM)SINTRAN:DATA'SINTRAN:DATA'0000...
        /// LIST-FILES       (SYSTEM)'EM).(SYSTEM)'7.........H....
        /// LIST-FILES       (SECRET)'ET(SECRET)).(SECRET)'..H....
        /// </code>
        /// <para><b>An empty filespec is how "the whole directory" is said</b></para>
        /// In both <c>LIST-FILES</c> blocks the terminator comes IMMEDIATELY after the user's
        /// closing bracket - the name is zero characters long. What follows it there is residue from
        /// the caller's uninitialised buffer, which is why the two differ from each other and why
        /// only the first field can be read.
        /// <para><b>Only the first copy is read</b></para>
        /// Why the spec appears twice is UNKNOWN. One copy is enough to answer the question a server
        /// has to answer, and reading the second would mean deciding what to do when they disagree -
        /// a case no capture shows.
        /// <para><b>The rest of the block stays opaque</b></para>
        /// Bytes past the second terminator are not decoded and are not read here. This method
        /// deliberately answers one question only: which file, if any, was named.
        /// </remarks>
        public static bool TryReadRequestedFileName(ReadOnlySpan<byte> body, out string fileName)
        {
            fileName = string.Empty;

            IReadOnlyList<QformField> fields;
            if (!QformReader.TryRead(body, out fields))
            {
                return false;
            }

            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i].Class != QformClass.ByteString || fields[i].ValueLength != SpecBlockLength)
                {
                    continue;
                }

                ReadOnlySpan<byte> block = body.Slice(fields[i].ValueOffset, fields[i].ValueLength);
                fileName = ReadSpecBlockFileName(block);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reads the file name out of a <c>DeleteFile</c> request.
        /// </summary>
        /// <param name="body">
        /// The QFORM body.
        /// </param>
        /// <param name="fileName">
        /// The name, without its terminator.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when a name was found.
        /// </returns>
        /// <remarks>
        /// <para><b>The whole request</b></para>
        /// Captured live from D100 on 2026-08-06:
        /// <code>
        /// 92 000B  92 seq  F2 0001  BB "THIRD:TXT'T"    (name 9, field 11)
        /// 92 000B  92 seq  F2 0001  BD "NEWFILE:TXT'W"  (name 11, field 13)
        /// </code>
        /// <para><b>The field is name + terminator + ONE UNKNOWN BYTE</b></para>
        /// Two samples with different name lengths pin the shape: the value is always the name, the
        /// SINTRAN terminator <c>0x27</c>, and one more byte. That byte VARIES - <c>'T'</c> in the
        /// first, <c>'W'</c> in the second - so it is not a constant, and it is not word padding
        /// either because it makes the field length ODD both times. Its meaning is UNKNOWN.
        /// <para>
        /// Nothing here depends on knowing it. The name ends at the terminator, which is the same
        /// rule the spec block follows, so the byte is simply not read.
        /// </para>
        /// </remarks>
        public static bool TryReadDeleteFileName(ReadOnlySpan<byte> body, out string fileName)
        {
            fileName = string.Empty;

            IReadOnlyList<QformField> fields;
            if (!QformReader.TryRead(body, out fields) || fields.Count < 2)
            {
                return false;
            }

            for (int i = 2; i < fields.Count; i++)
            {
                if (fields[i].Class != QformClass.ByteString || fields[i].ValueLength < 2)
                {
                    continue;
                }

                ReadOnlySpan<byte> value = body.Slice(fields[i].ValueOffset, fields[i].ValueLength);

                int end = 0;
                while (end < value.Length && value[end] != SintranName.Terminator)
                {
                    end++;
                }

                if (end == 0)
                {
                    continue;
                }

                fileName = System.Text.Encoding.ASCII.GetString(value.Slice(0, end));
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reads the name and reserved page count out of a <c>CreateFile</c> request.
        /// </summary>
        /// <param name="body">
        /// The QFORM body.
        /// </param>
        /// <param name="fileName">
        /// The name, without its terminator.
        /// </param>
        /// <param name="pageCount">
        /// How many pages the client asks to reserve.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when both fields were found.
        /// </returns>
        /// <remarks>
        /// <para><b>The whole request, captured</b></para>
        /// From <c>claude-create-file-102-to-100-2026-07-30.pcapng</c>, node 102 creating
        /// <c>RONNY-R2:TXT</c> on node 100:
        /// <code>
        /// 92 000A  92 0002  F2 0002 BE "RONNY-R2:TXT'R"  F2 0004 A4 00000001  F2 00FF
        /// </code>
        /// Selector 2 carries the name, selector 4 a 32-bit page count, and nothing else - which is
        /// what <see cref="FaOperation.CreateFile"/> already described. The reply in the same
        /// capture is the plain accepted form, <c>92 000A 92 0002 F2 00FF</c>, returning no value at
        /// all - not even the file number that <c>OpenFile</c> reports.
        /// <para><b>The name field has the same shape as DeleteFile's</b></para>
        /// Name, the SINTRAN terminator <c>0x27</c>, then ONE more byte that is not read. It is
        /// <c>'R'</c> here and was <c>'T'</c> and <c>'W'</c> in the two captured deletes - three
        /// samples, three values, and the field is name + 2 long every time, so it is not word
        /// padding either. Most likely residue from the caller's buffer, as the spec block's tail is.
        /// Meaning UNKNOWN; nothing depends on it.
        /// </remarks>
        public static bool TryReadCreateFile(ReadOnlySpan<byte> body, out string fileName, out uint pageCount)
        {
            pageCount = 0;

            if (!TryReadDeleteFileName(body, out fileName))
            {
                return false;
            }

            IReadOnlyList<QformField> fields;
            if (!QformReader.TryRead(body, out fields))
            {
                return false;
            }

            // Found by TAG and width, never by counting fields - the discipline the rest of this
            // codec follows, and the reason five "constants" here turned out not to be constants.
            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i].Class == QformClass.TypedInteger && fields[i].ValueLength == 4)
                {
                    pageCount = QformValue.ReadUInt32(body, fields[i]);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Pulls the filespec out of a spec block: past the user in brackets, up to the terminator.
        /// </summary>
        /// <param name="block">
        /// The <see cref="SpecBlockLength"/>-byte block.
        /// </param>
        /// <returns>
        /// The filespec, empty when the block names no file.
        /// </returns>
        /// <remarks>
        /// The user is skipped rather than returned. A served folder is one user's whole world, so
        /// nothing here could act on it; returning it would invite a caller to start matching on a
        /// name we cannot honour.
        /// </remarks>
        private static string ReadSpecBlockFileName(ReadOnlySpan<byte> block)
        {
            int at = 0;

            // The user, when present, is the leading (NAME) - skip past its closing bracket.
            if (block.Length > 0 && block[0] == (byte)'(')
            {
                while (at < block.Length && block[at] != (byte)')')
                {
                    at++;
                }

                if (at < block.Length)
                {
                    at++;
                }
            }

            int start = at;
            while (at < block.Length
                && block[at] != SintranName.Terminator
                && block[at] != 0x00)
            {
                at++;
            }

            if (at == start)
            {
                return string.Empty;
            }

            return System.Text.Encoding.ASCII.GetString(block.Slice(start, at - start));
        }

        /// <summary>
        /// Reads the serial and the raw directory entry record out of a reply body.
        /// </summary>
        /// <param name="body">
        /// The QFORM body.
        /// </param>
        /// <param name="serial">
        /// The echoed transaction serial.
        /// </param>
        /// <param name="entryRecord">
        /// The <see cref="EntryRecordLength"/>-byte object entry, exactly as it arrived. Decode it
        /// with <c>RetroIO.Storage.Ndfs.Elements.ObjectEntry.FromBytes</c>.
        /// </param>
        /// <returns>
        /// <see langword="true"/> if the body carried an entry.
        /// </returns>
        public static bool TryReadReply(ReadOnlySpan<byte> body, out ushort serial, out byte[] entryRecord)
        {
            serial = 0;
            entryRecord = Array.Empty<byte>();

            IReadOnlyList<QformField> fields;
            if (!QformReader.TryRead(body, out fields) || fields.Count < 2)
            {
                return false;
            }

            serial = QformValue.ReadUInt16(body, fields[1]);

            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i].Class == QformClass.ByteString && fields[i].ValueLength == EntryRecordLength)
                {
                    entryRecord = new byte[EntryRecordLength];
                    for (int j = 0; j < EntryRecordLength; j++)
                    {
                        entryRecord[j] = body[fields[i].ValueOffset + j];
                    }

                    return true;
                }
            }

            return false;
        }

    }
}
