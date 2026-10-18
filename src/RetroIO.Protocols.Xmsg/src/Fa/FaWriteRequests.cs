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
    /// Builds the QFORM field lists a client sends for the simpler write operations.
    /// </summary>
    /// <remarks>
    /// <para><b>Fields only, not whole requests</b></para>
    /// <para>
    /// Each method returns just the fields that follow the operation and sequence pair, which is
    /// exactly what <see cref="FaClientConversation.BuildRequest"/> takes. That keeps the envelope
    /// and the operation/sequence header in ONE place rather than repeated per operation - the
    /// FA envelope has already produced three live defects by being written out by hand.
    /// </para>
    /// <para><b>Checked against a real client</b></para>
    /// <para>
    /// Every method here reproduces bytes from
    /// <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-write.txt</c>, node 100 writing to node
    /// 102.
    /// </para>
    /// <para><b>The trailing pad is NOT reproduced, on purpose</b></para>
    /// <para>
    /// The captured bodies end on an odd byte and carry one more to reach an even length, but that
    /// byte is not a constant: the four requests carry <c>0A</c>, <c>BF</c>, <c>0A</c> and
    /// <c>0E</c> respectively. A value that differs every time is leftover buffer content, not a
    /// field, so it is not modelled and not compared. Nothing here should be read as saying the
    /// pad's value matters.
    /// </para>
    /// <para><b>What is NOT here</b></para>
    /// <para>
    /// <see cref="FaOperation.ReserveFileEntry"/> and <see cref="FaOperation.OpenFile"/> carry
    /// nested constructed blocks whose fields are not fully decoded, and
    /// <see cref="FaOperation.SiiiSpecial"/>'s purpose is unknown. Building those from a partial
    /// reading would be a guess, so they are left out rather than approximated.
    /// </para>
    /// </remarks>
    public static class FaWriteRequests
    {
        /// <summary>
        /// The block size the captured client asked for.
        /// </summary>
        /// <remarks>
        /// 2048, sent as <c>A2 0800</c>. Recorded because a builder must send something and this is
        /// what a real client chose - NOT because any other value is known to fail.
        /// </remarks>
        public const ushort CapturedBlockSize = 2048;

        /// <summary>
        /// Builds the fields for <see cref="FaOperation.SetBlockSize"/>.
        /// </summary>
        /// <param name="blockSize">
        /// The block size to ask for. See <see cref="CapturedBlockSize"/>.
        /// </param>
        /// <returns>
        /// The fields, ending with the end-of-list selector.
        /// </returns>
        public static byte[] SetBlockSize(ushort blockSize)
        {
            // F2 0001 selects field 1, A2 carries a 16-bit value, then the list ends.
            byte[] buffer = new byte[3 + 3 + 3];
            QformWriter writer = new QformWriter(buffer);
            writer.WriteSelector(1);
            writer.WriteTypedInteger(blockSize);
            writer.WriteEndOfList();
            writer.EnsureComplete("SetBlockSize fields");
            return buffer;
        }

        /// <summary>
        /// Builds the fields for <see cref="FaOperation.WriteFile"/>.
        /// </summary>
        /// <param name="blockNumber">
        /// Which block of the file this write covers, counting from zero. The captured session
        /// wrote blocks 0 to 8, one request each.
        /// </param>
        /// <returns>
        /// The fields, ending with the end-of-list selector.
        /// </returns>
        /// <remarks>
        /// The block number is a 32-BIT value (<c>A4</c>), not 16. A file bigger than 65535 blocks
        /// is not something this capture reached, but the field is wide enough for one and sending
        /// a narrow value would be a different message.
        /// </remarks>
        public static byte[] WriteFile(uint blockNumber)
        {
            byte[] buffer = new byte[3 + 5 + 3];
            QformWriter writer = new QformWriter(buffer);
            writer.WriteSelector(1);
            writer.WriteTypedInteger32(blockNumber);
            writer.WriteEndOfList();
            writer.EnsureComplete("WriteFile fields");
            return buffer;
        }

        /// <summary>
        /// The value field 1 of a reserve carries. Constant across every capture.
        /// </summary>
        /// <remarks>
        /// 2000, sent as <c>A2 07D0</c>. MEANING UNKNOWN - it is identical in all five captured
        /// reserves, so nothing distinguishes it from a constant.
        /// </remarks>
        public const ushort ReserveField1 = 2000;

        /// <summary>
        /// Builds the fields for <see cref="FaOperation.ReserveFileEntry"/>.
        /// </summary>
        /// <param name="backgroundProgram">
        /// The asking process, as SINTRAN names it - <c>BAK03</c>, <c>BAK04</c>, <c>BAK05</c> in
        /// the captures. Five characters.
        /// </param>
        /// <param name="user">
        /// The user asking, <c>SYSTEM</c> in every capture. At most 15 characters, since it is
        /// written with a terminating apostrophe into a 16-byte field.
        /// </param>
        /// <returns>
        /// The fields, ending with the end-of-list selector.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="backgroundProgram"/> or <paramref name="user"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when either value does not fit its captured field width.
        /// </exception>
        /// <remarks>
        /// <para><b>The reserve does not name the file</b></para>
        /// <para>
        /// That is the thing to understand before reading the fields. Comparing all five captured
        /// reserves - file-stat, create-file, delete-file, open-close and write - EVERY field is
        /// byte-identical except one string, and that string is the asking process and user:
        /// </para>
        /// <code>
        /// create-file, open-close   BAK03  SYSTEM
        /// write                     BAK04  SYSTEM
        /// file-stat, delete-file    BAK05  SYSTEM
        /// </code>
        /// <para>
        /// The file is named later, by <see cref="OpenFile"/>. So a reserve says WHO IS ASKING,
        /// and it is constant for a given background program - which is exactly why four captures
        /// of four different operations produced only three distinct bodies.
        /// </para>
        /// <para><b>The layout, decoded</b></para>
        /// <code>
        /// F2 0001  A2 07D0                      2000, meaning UNKNOWN
        /// F2 0002  8C 06  92 0001  92 0001      a constructed pair, both 1, meaning UNKNOWN
        /// F2 0003  BD "BAK04  SYSTEM"           the asker: five characters, two spaces, the
        ///                                       LOCAL user - who is making the request
        /// F2 0004  8C 38                        the CREDENTIALS block, 56 bytes:
        ///            B0 10 "SECRET'" + NULs       the REMOTE user, apostrophe-terminated, in 16
        ///            E1 80                        a marker, meaning UNKNOWN
        ///            B0 10 6D2A + NULs            the FOLDED PASSWORD word, in 16
        ///            B0 10 sixteen NULs           meaning UNKNOWN
        /// </code>
        /// <para><b>The two users are DIFFERENT users, and that was hidden for a month</b></para>
        /// <para>
        /// This comment used to call field 4 first string "the user again", because in every
        /// capture it was built from, the client was reading its OWN directory - local and remote
        /// were both <c>SYSTEM</c>, so the two fields looked like duplicates.
        /// </para>
        /// <para>
        /// The capture <c>2026-07-29-1426-fa-list-files-user-secret-password-check-d102-to-d100.pcapng</c> in
        /// <c>DOC/captures/ARCHIVE-2026-07</c> separates them. Node 102 reads user <c>SECRET</c>
        /// on node 100, and field 4 carries <c>SECRET</c> while field 3 still carries
        /// <c>BAK03  SYSTEM</c>. So field 3 is WHO IS ASKING and field 4 is WHOSE DIRECTORY, and
        /// they are independent.
        /// </para>
        /// <para><b>The second slot is the password, folded</b></para>
        /// <para>
        /// The same frame carries <c>6D 2A</c> in the slot this builder used to fill with NULs.
        /// <c>secret</c> folds to 0x6D2A under the algorithm carved from the L-VSX-500
        /// disassembly and implemented as <c>SintranPassword.Encode</c>. The plaintext never
        /// appears anywhere in the frame: the client folds locally and sends the word. A
        /// controlled run with the wrong password changed exactly that one word and answered
        /// WRONG PASSWORD - see <c>DOC/XMSG-FA-ACCESS-PASSWORD-ON-THE-WIRE-2026-07-29.md</c>.
        /// </para>
        /// <para>
        /// The third slot stays NUL: nothing has ever been seen in it.
        /// </para>
        /// </remarks>
        public static byte[] ReserveFileEntry(string backgroundProgram, string user)
        {
            // The historical shape: read our OWN directory, no password. Both users are the same
            // one, which is exactly why the two fields looked like duplicates for a month.
            return ReserveFileEntry(backgroundProgram, user, user, 0);
        }

        /// <summary>
        /// Builds the fields for <see cref="FaOperation.ReserveFileEntry"/>, naming a remote user
        /// and password that need not be our own.
        /// </summary>
        /// <param name="backgroundProgram">
        /// The asking process, as SINTRAN names it - <c>BAK03</c>, <c>BAK04</c>, <c>BAK05</c> in
        /// the captures. Five characters.
        /// </param>
        /// <param name="localUser">
        /// The user making the request, which goes in field 3 beside
        /// <paramref name="backgroundProgram"/>.
        /// </param>
        /// <param name="remoteUser">
        /// The user whose directory is opened on the far machine. This is the one that decides
        /// where a pushed file LANDS.
        /// </param>
        /// <param name="passwordWord">
        /// That user password ALREADY FOLDED to a single word, or zero when the user has none.
        /// <para>
        /// The fold is deliberately not done here. This assembly decodes and builds wire shapes;
        /// the folding algorithm lives with the SINTRAN user model in
        /// <c>RetroIO.Protocols.Xmsg.Api.SintranPassword.Encode</c>, which this one does not
        /// reference. Taking the word also means a plaintext password is never copied into a
        /// protocol buffer, which is the right property for the layer that touches the wire.
        /// </para>
        /// </param>
        /// <returns>
        /// The fields, ending with the end-of-list selector.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any argument is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when a value does not fit its captured field width.
        /// </exception>
        public static byte[] ReserveFileEntry(
            string backgroundProgram, string localUser, string remoteUser, ushort passwordWord)
        {
            if (backgroundProgram == null)
            {
                throw new ArgumentNullException(nameof(backgroundProgram));
            }

            if (localUser == null)
            {
                throw new ArgumentNullException(nameof(localUser));
            }

            if (remoteUser == null)
            {
                throw new ArgumentNullException(nameof(remoteUser));
            }

            string user = localUser;

            // Field 3 is "BAKnn" + two spaces + the user - thirteen bytes in every capture, which
            // is the compact string form's limit of fifteen only because the user is six long.
            string asker = backgroundProgram + "  " + user;
            byte[] askerBytes = System.Text.Encoding.ASCII.GetBytes(asker);
            if (askerBytes.Length < 1 || askerBytes.Length > 0x0F)
            {
                throw new ArgumentException(
                    "'" + asker + "' is " + askerBytes.Length
                        + " bytes; the captured field holds 1 to 15.", nameof(user));
            }

            // Field 4 first sub-field: the REMOTE user - whose directory we are opening, which
            // need not be us - apostrophe-terminated, in a 16-byte slot padded with NULs.
            byte[] userSlot = new byte[16];
            byte[] userBytes = System.Text.Encoding.ASCII.GetBytes(remoteUser + "'");
            if (userBytes.Length > userSlot.Length)
            {
                throw new ArgumentException(
                    "'" + remoteUser + "' does not fit the 16-byte user field with its terminator.",
                    nameof(remoteUser));
            }

            for (int i = 0; i < userBytes.Length; i++)
            {
                userSlot[i] = userBytes[i];
            }

            // Field 4 second sub-field: the password, FOLDED to one word and written big-endian,
            // matching the captured 6D 2A for "secret". An empty password leaves the slot NUL,
            // which is what every same-user capture shows and what a user without a password
            // needs. The plaintext is never written into the buffer at all.
            byte[] passwordSlot = new byte[16];
            if (passwordWord != 0)
            {
                passwordSlot[0] = (byte)(passwordWord >> 8);
                passwordSlot[1] = (byte)(passwordWord & 0xFF);
            }

            byte[] emptySlot = new byte[16];

            byte[] buffer = new byte[
                3 + 3                       // F2 0001 A2 07D0
                + 3 + 2 + 6                 // F2 0002 8C 06 <two integers>
                + 3 + 1 + askerBytes.Length // F2 0003 BD <asker>
                + 3 + 2                     // F2 0004 8C 38
                + 2 + 16                    // B0 10 <user>
                + 2                         // E1 80
                + 2 + 16                    // B0 10 <zeros>
                + 2 + 16                    // B0 10 <zeros>
                + 3];                       // F2 00FF

            QformWriter writer = new QformWriter(buffer);
            writer.WriteSelector(1);
            writer.WriteTypedInteger(ReserveField1);

            writer.WriteSelector(2);
            writer.WriteConstructed(6);
            writer.WriteInteger(1);
            writer.WriteInteger(1);

            writer.WriteSelector(3);
            writer.WriteByteStringCompact(askerBytes);

            writer.WriteSelector(4);
            writer.WriteConstructed(0x38);
            writer.WriteByteString(userSlot);
            writer.WriteRaw(new byte[] { 0xE1, 0x80 });
            writer.WriteByteString(passwordSlot);
            writer.WriteByteString(emptySlot);

            writer.WriteEndOfList();
            writer.EnsureComplete("ReserveFileEntry fields");
            return buffer;
        }

        /// <summary>
        /// Builds the fields for <see cref="FaOperation.OpenFile"/>.
        /// </summary>
        /// <param name="quotedFileSpec">
        /// The file specification INCLUDING its quotes, for example <c>"WRTEST1:OUT"</c>.
        /// </param>
        /// <param name="access">
        /// The access letter, <c>W</c> in the capture.
        /// </param>
        /// <returns>
        /// The fields, ending with the end-of-list selector.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="quotedFileSpec"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when the specification and access letter do not fit a compact byte string.
        /// </exception>
        /// <remarks>
        /// <para>
        /// The captured field is <c>BF</c> followed by <c>"WRTEST1:OUT"'W</c> - fifteen bytes: the
        /// QUOTED specification, an apostrophe, then the access letter.
        /// </para>
        /// <para>
        /// The quotes go on the wire. Quoting differs per SINTRAN command - <c>CREATE-FILE</c> and
        /// <c>RENAME-FILE</c> want a bare name - so a builder that stripped them here because
        /// another command does would break the open. The caller passes them, because only the
        /// caller knows which command it is imitating.
        /// </para>
        /// <para>
        /// The compact string form is used because that is what the real client sent - see
        /// <c>QformWriter.WriteByteStringCompact</c>. It limits the whole field to fifteen bytes,
        /// which a long file specification will exceed; what a real client does then is UNKNOWN
        /// and untested, so this throws rather than silently switching encodings.
        /// </para>
        /// </remarks>
        public static byte[] OpenFile(string quotedFileSpec, char access)
        {
            if (quotedFileSpec == null)
            {
                throw new ArgumentNullException(nameof(quotedFileSpec));
            }

            string field = quotedFileSpec + "'" + access;
            byte[] text = System.Text.Encoding.ASCII.GetBytes(field);

            // F2 0002 <string> F2 0003 92 0001 F2 00FF
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
            byte[] buffer = new byte[3 + (compact ? 1 : 2) + text.Length + 3 + 3 + 3];
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
            writer.WriteSelector(3);
            writer.WriteInteger(1);
            writer.WriteEndOfList();
            writer.EnsureComplete("OpenFile fields");
            return buffer;
        }

        /// <summary>
        /// Builds the fields for the <see cref="FaSpecialFunction.SetEndOfFile"/> request that
        /// follows the last block.
        /// </summary>
        /// <param name="fileLength">
        /// How many bytes the file really holds. Must be at least one.
        /// </param>
        /// <returns>
        /// The fields, ending with the end-of-list selector.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="fileLength"/> is less than one or will not fit 32 bits.
        /// </exception>
        /// <remarks>
        /// <para><b>The wire carries the LAST BYTE'S INDEX, not the length</b></para>
        /// <para>
        /// So this sends <paramref name="fileLength"/> minus one. Getting it wrong truncates every
        /// file by a byte, which is close to invisible - the content is otherwise identical and
        /// only a trailing newline goes missing. Three independent sources agree, and they are set
        /// out in full on <c>FaServer.SetEndOfFile</c>:
        /// </para>
        ///  - NDIX's cps client adds one when reading this field and takes one off when sending it,
        ///    with the line tagged as a kludge.
        ///  - The captured OPEN reply for the same file carries <c>A4 000045F1</c> = 17905 where
        ///    SetEndOfFile carries <c>45F0</c> = 17904.
        ///  - Live on D100 on 2026-08-06, a 12690-byte file arrived with SetEndOfFile 12689.
        /// <para><b>Why it is needed at all</b></para>
        /// <para>
        /// Blocks are fixed size, so the last one is padded. Without this the file keeps that
        /// padding.
        /// </para>
        /// <para>
        /// The body is the sub-function then the value: <c>92 003B</c>, then a constructed block
        /// holding a 32-bit index. The constructed length uses the ESCAPED form (<c>8C 80 05</c>)
        /// in the capture even though 5 would fit a nibble, so that is what is reproduced.
        /// </para>
        /// </remarks>
        public static byte[] SetEndOfFile(long fileLength)
        {
            if (fileLength < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fileLength), "A file being closed holds at least one byte.");
            }

            long lastByteIndex = fileLength - 1;
            if (lastByteIndex > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fileLength), "The last byte index does not fit 32 bits.");
            }

            // F2 0001 92 003B  F2 0002 8C 80 05 A4 <index>  F2 00FF
            byte[] buffer = new byte[3 + 3 + 3 + 3 + 5 + 3];
            QformWriter writer = new QformWriter(buffer);
            writer.WriteSelector(1);
            writer.WriteInteger((ushort)FaSpecialFunction.SetEndOfFile);
            writer.WriteSelector(2);
            writer.WriteConstructedEscaped(5);
            writer.WriteTypedInteger32((uint)lastByteIndex);
            writer.WriteEndOfList();
            writer.EnsureComplete("SetEndOfFile fields");
            return buffer;
        }

        /// <summary>
        /// Builds the fields for <see cref="FaOperation.CloseFile"/>.
        /// </summary>
        /// <returns>
        /// The fields - just the end-of-list selector.
        /// </returns>
        /// <remarks>
        /// The captured close carries no parameters at all beyond the operation and sequence. It
        /// still MUST be sent: a client that stops after its last block leaves the file open.
        /// </remarks>
        public static byte[] CloseFile()
        {
            return EndOfListOnly("CloseFile fields");
        }

        /// <summary>
        /// Builds the fields for <see cref="FaOperation.ReleaseFileEntry"/>.
        /// </summary>
        /// <returns>
        /// The fields - just the end-of-list selector.
        /// </returns>
        /// <remarks>
        /// The last request of the session, and like the close it carries no parameters. Skipping
        /// it leaves the entry reserved on the server.
        /// </remarks>
        public static byte[] ReleaseFileEntry()
        {
            return EndOfListOnly("ReleaseFileEntry fields");
        }

        /// <summary>
        /// Builds a field list that is nothing but the end-of-list selector.
        /// </summary>
        /// <param name="what">
        /// What is being built, for the completeness check's message.
        /// </param>
        /// <returns>
        /// The three bytes of an empty list.
        /// </returns>
        private static byte[] EndOfListOnly(string what)
        {
            byte[] buffer = new byte[3];
            QformWriter writer = new QformWriter(buffer);
            writer.WriteEndOfList();
            writer.EnsureComplete(what);
            return buffer;
        }
    }
}
