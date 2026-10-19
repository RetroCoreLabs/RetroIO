//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using RetroIO.Protocols.Econet.Common;

namespace RetroIO.Protocols.Econet.Protocol.FileServer;

/// <summary>
/// Builds file server reply payloads (status bytes + command-specific data).
/// </summary>
public static class FileServerReply
{
    /// <summary>
    /// The carriage return byte, 0x0D, that ends text in a reply.
    /// </summary>
    public const byte CarriageReturn = 0x0D;

    /// <summary>
    /// Builds a success reply.
    /// </summary>
    /// <param name="payloadAfterStatus">
    /// The data placed after the two status bytes.
    /// </param>
    /// <returns>
    /// The two status bytes, both 0, followed by the data.
    /// </returns>
    public static byte[] Success(ReadOnlySpan<byte> payloadAfterStatus = default)
    {
        if (payloadAfterStatus.IsEmpty)
            return [0, 0];

        byte[] reply = new byte[2 + payloadAfterStatus.Length];
        payloadAfterStatus.CopyTo(reply.AsSpan(2));
        return reply;
    }

    /// <summary>
    /// Disc free space reply for opcode <c>0x1A</c>.
    /// </summary>
    /// <remarks>
    /// Layout: a 24-bit free count at bytes 2 to 4, then a 24-bit extent at bytes 5 to 7.
    /// </remarks>
    /// <param name="free">
    /// Free space, in the disc's allocation units.
    /// </param>
    /// <param name="extent">
    /// Total disc size, in the same units as <paramref name="free"/>.
    /// </param>
    /// <returns>
    /// The reply payload.
    /// </returns>
    public static byte[] DiscFreeSpace(uint free, uint extent)
    {
        return
        [
            0, 0,
            (byte)(free & 0xFF),
            (byte)((free >> 8) & 0xFF),
            (byte)((free >> 16) & 0xFF),
            (byte)(extent & 0xFF),
            (byte)((extent >> 8) & 0xFF),
            (byte)((extent >> 16) & 0xFF),
        ];
    }

    /// <summary>
    /// The ORIGINAL 1983 disc free-space reply for opcode <c>0x1A</c> — the classic wire profile
    /// (<c>WireProfile.Classic1983</c>).
    /// </summary>
    /// <remarks>
    /// Per Acorn's Econet Advanced User Guide (1983) the reply carries ONLY a 24-bit free-block
    /// count at bytes 2 to 4 (5 bytes total); the total-extent field (bytes 5 to 7 of
    /// <see cref="DiscFreeSpace"/>) was a later-FS addition. See
    /// <c>docs/econet/notes/wire-format-verification.md</c>.
    /// </remarks>
    /// <param name="free">
    /// Free space, in the disc's allocation units.
    /// </param>
    /// <returns>
    /// The 5-byte reply body: two zero bytes, then the low 24 bits of <paramref name="free"/>, least significant byte first.
    /// </returns>
    public static byte[] DiscFreeSpaceClassic1983(uint free)
    {
        return
        [
            0, 0,
            (byte)(free & 0xFF),
            (byte)((free >> 8) & 0xFF),
            (byte)((free >> 16) & 0xFF),
        ];
    }

    /// <summary>
    /// User free space reply for opcode <c>0x1E</c>.
    /// </summary>
    /// <remarks>
    /// A fixed 16 MB is reported rather than a real per-user figure, matching the reference, which
    /// also returns a constant here. RetroFS has no per-user quota to report.
    /// </remarks>
    /// <returns>
    /// The reply payload.
    /// </returns>
    public static byte[] UserFreeSpaceDummy() => [0, 0, 0, 0, 0, 0x10];

    /// <summary>
    /// Builds an error reply.
    /// </summary>
    /// <param name="errorCode">
    /// The error code, stored in byte 1.
    /// </param>
    /// <param name="message">
    /// The error text, which is followed by a carriage return.
    /// </param>
    /// <returns>
    /// The reply payload.
    /// </returns>
    public static byte[] Error(byte errorCode, string message)
    {
        byte[] messageBytes = Encoding.ASCII.GetBytes(message);
        byte[] reply = new byte[2 + messageBytes.Length + 1];
        reply[1] = errorCode;
        messageBytes.CopyTo(reply.AsSpan(2));
        reply[^1] = CarriageReturn;
        return reply;
    }

    /// <summary>
    /// Builds a version reply.
    /// </summary>
    /// <param name="versionString">
    /// The version text.
    /// </param>
    /// <returns>
    /// Two zero bytes, the text and a carriage return.
    /// </returns>
    public static byte[] Version(string versionString)
    {
        byte[] text = Encoding.ASCII.GetBytes(versionString);
        byte[] reply = new byte[2 + text.Length + 1];
        text.CopyTo(reply.AsSpan(2));
        reply[^1] = CarriageReturn;
        return reply;
    }

    /// <summary>
    /// Read-disc-names reply: status 10,0; count at offset 2; 17-byte entries from offset 3.
    /// </summary>
    public static byte[] DiscNames(ReadOnlySpan<DiscNameEntry> discs)
    {
        int entryBytes = discs.Length * 17;
        byte[] reply = new byte[3 + entryBytes];
        reply[0] = 10;
        reply[1] = 0;
        reply[2] = (byte)discs.Length;

        int offset = 3;
        for (int i = 0; i < discs.Length; i++)
        {
            DiscNameEntry entry = discs[i];
            reply[offset] = entry.Index;
            WritePaddedName(reply.AsSpan(offset + 1, 16), entry.Name);
            offset += 17;
        }

        return reply;
    }

    /// <summary>
    /// Builds the reply to a successful login.
    /// </summary>
    /// <param name="urd">
    /// The internal handle of the user root directory.
    /// </param>
    /// <param name="cwd">
    /// The internal handle of the current directory.
    /// </param>
    /// <param name="lib">
    /// The internal handle of the library directory.
    /// </param>
    /// <param name="bootOpt">
    /// The boot option.
    /// </param>
    /// <returns>
    /// The login return code, 0, the three handles converted to wire bit masks, and the boot option.
    /// </returns>
    public static byte[] LoginSuccess(byte urd, byte cwd, byte lib, byte bootOpt)
    {
        return
        [
            FileServerOpcodes.LoginSuccessReturnCode,
            0,
            AcornFileHandles.MulHandle(urd),
            AcornFileHandles.MulHandle(cwd),
            AcornFileHandles.MulHandle(lib),
            bootOpt,
        ];
    }

    /// <summary>
    /// *DIR reply (PiFS utilities/fs.c: return code 0x07 "changed directory" + new CSD handle).
    /// </summary>
    public static byte[] DirectoryChanged(byte cwdHandle) => [0x07, 0x00, cwdHandle];

    /// <summary>
    /// *LIB reply (PiFS utilities/fs.c: return code 0x09 "changed library" + new library handle).
    /// </summary>
    public static byte[] LibraryChanged(byte libHandle) => [0x09, 0x00, libHandle];

    /// <summary>
    /// Unrecognised OSCLI reply — PiFS <c>utilities/fs.c:11302</c> and <c>:11327</c> ("Unknown
    /// command"): return code <c>0x08</c>, then the ORIGINAL command text echoed back verbatim.
    /// </summary>
    /// <remarks>
    /// This is NOT an error. <c>0x08</c> tells the NFS/ANFS ROM "I don't know this command — you deal
    /// with it", and the ROM then performs a <c>*RUN</c> of a file with that name (searching the CSD
    /// then the library). It is the mechanism behind BOTH plain <c>*SOMEPROG</c> and <c>*OPT 4,n</c> /
    /// SHIFT-BREAK autoboot, where the ROM issues <c>*!BOOT</c> straight after login.
    ///
    /// RetroFS previously answered an unrecognised OSCLI with <c>0xFE "Bad command"</c>, which the
    /// ROM treats as final — so autoboot and every <c>*program</c> invocation failed. Caught live in
    /// BeebEm 2026-07-17: right after a successful login carrying boot option 02, the client sent
    /// OSCLI "!BOOT" and got "Bad command".
    ///
    /// PiFS copies from <c>data+5</c> for <c>datalen-5</c> bytes — i.e. the raw parameter bytes after
    /// the 5-byte request header (reply_port, fsop, URD, CWD, LIB), INCLUDING the terminating CR.
    /// </remarks>
    /// <param name="rawCommand">
    /// The raw OSCLI parameter bytes exactly as received, including the terminating CR.
    /// </param>
    /// <returns>
    /// The reply payload: <c>0x08</c>, <c>0x00</c>, then <paramref name="rawCommand"/> verbatim.
    /// </returns>
    public static byte[] UnknownCommand(ReadOnlySpan<byte> rawCommand)
    {
        byte[] reply = new byte[2 + rawCommand.Length];
        reply[0] = 0x08; // "Unknown command" — the ROM will try to *RUN it.
        reply[1] = 0x00;
        rawCommand.CopyTo(reply.AsSpan(2));
        return reply;
    }

    /// <summary>
    /// Builds a reply with a given return code.
    /// </summary>
    /// <param name="returnCode">
    /// The return code, stored in byte 0.
    /// </param>
    /// <param name="payload">
    /// The data placed after the first two bytes.
    /// </param>
    /// <returns>
    /// The reply payload.
    /// </returns>
    public static byte[] WithReturnCode(byte returnCode, ReadOnlySpan<byte> payload)
    {
        byte[] reply = new byte[2 + payload.Length];
        reply[0] = returnCode;
        payload.CopyTo(reply.AsSpan(2));
        return reply;
    }

    /// <summary>
    /// Builds a reply that holds text followed by a carriage return.
    /// </summary>
    /// <param name="text">
    /// The text.
    /// </param>
    /// <returns>
    /// Two zero bytes, the text and a carriage return.
    /// </returns>
    public static byte[] AppendCr(string text)
    {
        byte[] ascii = Encoding.ASCII.GetBytes(text);
        byte[] reply = new byte[2 + ascii.Length + 1];
        ascii.CopyTo(reply.AsSpan(2));
        reply[^1] = CarriageReturn;
        return reply;
    }

    /// <summary>
    /// Builds the reply of a load command.
    /// </summary>
    /// <param name="loadAddress">
    /// The load address, written as 4 bytes with the lowest byte first.
    /// </param>
    /// <param name="loadAddressFound">
    /// True when the load address is known. This writes 0xFF, otherwise 0.
    /// </param>
    /// <param name="filename">
    /// The file name, followed by a carriage return.
    /// </param>
    /// <returns>
    /// The reply payload.
    /// </returns>
    public static byte[] LoadOsCli(uint loadAddress, bool loadAddressFound, string filename)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(filename);
        byte[] reply = new byte[7 + nameBytes.Length + 1];
        reply[0] = FileServerOpcodes.Load;
        reply[1] = 0;
        reply[2] = (byte)loadAddress;
        reply[3] = (byte)(loadAddress >> 8);
        reply[4] = (byte)(loadAddress >> 16);
        reply[5] = (byte)(loadAddress >> 24);
        reply[6] = loadAddressFound ? (byte)0xFF : (byte)0x00;
        nameBytes.CopyTo(reply.AsSpan(7));
        reply[^1] = CarriageReturn;
        return reply;
    }

    /// <summary>
    /// Builds the 16-byte reply that gives the attributes of a file to load.
    /// </summary>
    /// <param name="load">
    /// The load address.
    /// </param>
    /// <param name="exec">
    /// The execution address.
    /// </param>
    /// <param name="length">
    /// The file length. The low 3 bytes are written.
    /// </param>
    /// <param name="perm">
    /// The access byte.
    /// </param>
    /// <param name="day">
    /// The day byte of the date.
    /// </param>
    /// <param name="monthYear">
    /// The month and year byte of the date.
    /// </param>
    /// <returns>
    /// The reply payload, with bytes 0 and 1 set to 0.
    /// </returns>
    public static byte[] LoadMetadata(uint load, uint exec, int length, byte perm, byte day, byte monthYear)
    {
        byte[] reply = new byte[16];
        WriteUInt32LE(reply, 2, load);
        WriteUInt32LE(reply, 6, exec);
        reply[10] = (byte)length;
        reply[11] = (byte)(length >> 8);
        reply[12] = (byte)(length >> 16);
        reply[13] = perm;
        reply[14] = day;
        reply[15] = monthYear;
        return reply;
    }

    /// <summary>
    /// Builds the reply that tells the client where to send the data of a save.
    /// </summary>
    /// <param name="bulkPort">
    /// The port that receives the data.
    /// </param>
    /// <returns>
    /// The bytes 0, 0, the port, 0x00 and 0x05.
    /// </returns>
    public static byte[] SaveSetup(byte bulkPort)
    {
        return [0, 0, bulkPort, 0x00, 0x05];
    }

    /// <summary>
    /// CREATE / zero-length SAVE completion (PiFS utilities/fs.c fs_save create_only / length==0 path):
    /// a 5-byte reply [00 00, access, day, monthyear] with return code 0 (no name / junk tail).
    /// </summary>
    public static byte[] SaveCreateComplete(byte access, byte day, byte monthYear)
    {
        return [0, 0, access, day, monthYear];
    }

    /// <summary>
    /// SAVE completion reply (PiFS / Level-3 close packet after bulk data received).
    /// </summary>
    public static byte[] SaveComplete(byte access, byte day, byte monthYear, string acornFilename)
    {
        byte[] reply = new byte[21];
        reply[0] = 3;
        reply[1] = 0;
        reply[2] = access;
        reply[3] = day;
        reply[4] = monthYear;
        reply.AsSpan(5, 15).Fill((byte)' ');

        byte[] nameBytes = Encoding.ASCII.GetBytes(acornFilename);
        int copyLength = Math.Min(nameBytes.Length, 12);
        nameBytes.AsSpan(0, copyLength).CopyTo(reply.AsSpan(5));

        reply[17] = 0x80;
        reply[18] = 0x20;
        reply[19] = 0xA9;
        reply[20] = 0x24;
        return reply;
    }

    /// <summary>
    /// Builds an examine reply.
    /// </summary>
    /// <param name="examined">
    /// The number of entries, stored in byte 2.
    /// </param>
    /// <param name="dirSize">
    /// The value stored in byte 3.
    /// </param>
    /// <param name="entries">
    /// The entry bytes, placed from byte 4.
    /// </param>
    /// <param name="terminal">
    /// True to add a final 0x80 byte.
    /// </param>
    /// <returns>
    /// The reply payload.
    /// </returns>
    public static byte[] ExamineEntries(int examined, int dirSize, ReadOnlySpan<byte> entries, bool terminal)
    {
        int total = 4 + entries.Length + (terminal ? 1 : 0);
        byte[] reply = new byte[total];
        reply[2] = (byte)examined;
        reply[3] = (byte)dirSize;
        entries.CopyTo(reply.AsSpan(4));
        if (terminal)
            reply[^1] = 0x80;
        return reply;
    }

    /// <summary>
    /// Builds the reply to a read byte request.
    /// </summary>
    /// <param name="value">
    /// The byte that was read.
    /// </param>
    /// <param name="flags">
    /// The flag byte placed after the value.
    /// </param>
    /// <returns>
    /// The bytes 0, 0, the value and the flags.
    /// </returns>
    public static byte[] GetByte(byte value, byte flags)
    {
        return [0, 0, value, flags];
    }

    /// <summary>
    /// Builds the 35-byte reply of a catalogue header request.
    /// </summary>
    /// <param name="dirTail">
    /// The directory name, padded or cut to 11 characters.
    /// </param>
    /// <param name="ownerFlag">
    /// The owner character, stored in byte 13.
    /// </param>
    /// <param name="discName">
    /// The disc name, padded or cut to 16 characters.
    /// </param>
    /// <returns>
    /// The reply payload, which ends with a carriage return and 0x80.
    /// </returns>
    public static byte[] CatHeader(string dirTail, char ownerFlag, string discName)
    {
        byte[] reply = new byte[35];
        byte[] tail = Encoding.ASCII.GetBytes(dirTail.PadRight(11).Substring(0, 11));
        tail.CopyTo(reply.AsSpan(2));
        reply[13] = (byte)ownerFlag;
        byte[] disc = Encoding.ASCII.GetBytes(discName.PadRight(16).Substring(0, 16));
        disc.CopyTo(reply.AsSpan(17));
        reply[33] = CarriageReturn;
        reply[34] = 0x80;
        return reply;
    }

    /// <summary>
    /// Builds the reply that lists logged on users.
    /// </summary>
    /// <param name="count">
    /// The number of entries, stored in byte 2.
    /// </param>
    /// <param name="entries">
    /// The entry bytes, placed from byte 3.
    /// </param>
    /// <returns>
    /// The reply payload.
    /// </returns>
    public static byte[] LoggedOnUsers(int count, ReadOnlySpan<byte> entries)
    {
        byte[] reply = new byte[3 + entries.Length];
        reply[2] = (byte)count;
        entries.CopyTo(reply.AsSpan(3));
        return reply;
    }

    /// <summary>
    /// Builds the reply for an object that does not exist.
    /// </summary>
    /// <returns>
    /// Three zero bytes.
    /// </returns>
    public static byte[] ObjectInfoNotFound()
    {
        return [0, 0, 0];
    }

    /// <summary>
    /// PiFS/MDFS variable-length get-object-info reply (fsop 0x12).
    /// </summary>
    public static byte[] ObjectInfoForCommand(
        byte command,
        byte fileType,
        uint load,
        uint exec,
        int length,
        byte access,
        byte day,
        byte monthYear,
        bool isOwner,
        ReadOnlySpan<byte> directoryName10,
        byte directoryEntryCount)
    {
        int payloadLength = 1; // fileType
        if (command is 2 or 5 or FileServerOpcodes.GetObjectInfoCanonical)
            payloadLength += 8;
        if (command is 3 or 5 or FileServerOpcodes.GetObjectInfoCanonical)
            payloadLength += 3;
        if (command is 4 or 5 or FileServerOpcodes.GetObjectInfoCanonical)
            payloadLength += 1;
        if (command is 1 or 5 or FileServerOpcodes.GetObjectInfoCanonical)
            payloadLength += 2;
        if (command is 4 or 5 or FileServerOpcodes.GetObjectInfoCanonical)
            payloadLength += 1;
        if (command == 6)
            payloadLength += 2 + 10 + 2;

        byte[] reply = new byte[2 + payloadLength];
        int offset = 2;
        reply[offset++] = fileType;

        if (command is 2 or 5 or FileServerOpcodes.GetObjectInfoCanonical)
        {
            WriteUInt32LE(reply, offset, load);
            offset += 4;
            WriteUInt32LE(reply, offset, exec);
            offset += 4;
        }

        if (command is 3 or 5 or FileServerOpcodes.GetObjectInfoCanonical)
        {
            reply[offset++] = (byte)length;
            reply[offset++] = (byte)(length >> 8);
            reply[offset++] = (byte)(length >> 16);
        }

        if (command is 4 or 5 or FileServerOpcodes.GetObjectInfoCanonical)
            reply[offset++] = access;

        if (command is 1 or 5 or FileServerOpcodes.GetObjectInfoCanonical)
        {
            reply[offset++] = day;
            reply[offset++] = monthYear;
        }

        if (command is 4 or 5 or FileServerOpcodes.GetObjectInfoCanonical)
            reply[offset++] = isOwner ? (byte)0x00 : (byte)0xFF;

        if (command == 6)
        {
            reply[offset++] = 0;
            reply[offset++] = 10;
            directoryName10.Slice(0, Math.Min(10, directoryName10.Length)).CopyTo(reply.AsSpan(offset));
            if (directoryName10.Length < 10)
                reply.AsSpan(offset + directoryName10.Length, 10 - directoryName10.Length).Fill((byte)' ');
            offset += 10;
            reply[offset++] = isOwner ? (byte)0x00 : (byte)0xFF;
            reply[offset++] = directoryEntryCount;
        }

        return reply;
    }

    /// <summary>
    /// Builds an object information reply.
    /// </summary>
    /// <param name="fileType">
    /// The object type byte.
    /// </param>
    /// <param name="load">
    /// The load address.
    /// </param>
    /// <param name="exec">
    /// The execution address.
    /// </param>
    /// <param name="length">
    /// The length. The low 3 bytes are written.
    /// </param>
    /// <param name="access">
    /// The access byte.
    /// </param>
    /// <param name="extra">
    /// Extra bytes placed after the access byte.
    /// </param>
    /// <returns>
    /// The reply payload.
    /// </returns>
    public static byte[] ObjectInfo(
        byte fileType,
        uint load,
        uint exec,
        int length,
        byte access,
        ReadOnlySpan<byte> extra)
    {
        byte[] reply = new byte[2 + 1 + 8 + 3 + 1 + extra.Length];
        int offset = 2;
        reply[offset++] = fileType;
        WriteUInt32LE(reply, offset, load);
        offset += 4;
        WriteUInt32LE(reply, offset, exec);
        offset += 4;
        reply[offset++] = (byte)length;
        reply[offset++] = (byte)(length >> 8);
        reply[offset++] = (byte)(length >> 16);
        reply[offset++] = access;
        extra.CopyTo(reply.AsSpan(offset));
        return reply;
    }

    /// <summary>
    /// Get random access info (fsop 0x0C). PiFS <c>fs_get_random_access_info</c> (utilities/fs.c:8460)
    /// returns a 5-byte reply <c>[0, 0, value_u24_LE]</c> where the value is chosen by the request's
    /// function byte (0 = cursor position, 1/2 = extent/file length). NOT the 14-byte cursor+length block
    /// the previous implementation emitted.
    /// </summary>
    public static byte[] RandomAccessInfo(int value24)
    {
        return
        [
            0,
            0,
            (byte)value24,
            (byte)(value24 >> 8),
            (byte)(value24 >> 16),
        ];
    }

    /// <summary>
    /// Builds the final reply of a read bytes request.
    /// </summary>
    /// <param name="eof">
    /// True to set the end of file flag, which is 0x80.
    /// </param>
    /// <param name="totalReceived">
    /// The number of bytes transferred. The low 3 bytes are written.
    /// </param>
    /// <returns>
    /// The reply payload.
    /// </returns>
    public static byte[] GetBytesComplete(bool eof, int totalReceived)
    {
        return
        [
            0,
            0,
            eof ? (byte)0x80 : (byte)0x00,
            (byte)totalReceived,
            (byte)(totalReceived >> 8),
            (byte)(totalReceived >> 16),
        ];
    }

    /// <summary>
    /// Builds the reply of a save command.
    /// </summary>
    /// <param name="load">
    /// The load address.
    /// </param>
    /// <param name="exec">
    /// The execution address.
    /// </param>
    /// <param name="length">
    /// The file length. The low 3 bytes are written.
    /// </param>
    /// <param name="filename">
    /// The file name, followed by a carriage return.
    /// </param>
    /// <returns>
    /// The reply payload.
    /// </returns>
    public static byte[] SaveOsCli(uint load, uint exec, int length, string filename)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(filename);
        byte[] reply = new byte[13 + nameBytes.Length + 1];
        reply[0] = FileServerOpcodes.Save;
        reply[1] = 0;
        WriteUInt32LE(reply, 2, load);
        WriteUInt32LE(reply, 6, exec);
        reply[10] = (byte)length;
        reply[11] = (byte)(length >> 8);
        reply[12] = (byte)(length >> 16);
        nameBytes.CopyTo(reply.AsSpan(13));
        reply[^1] = CarriageReturn;
        return reply;
    }

    /// <summary>
    /// Read file server time (fsop 0x10). PiFS <c>fs_read_time</c> (utilities/fs.c:7513-7543) returns a
    /// 7-byte reply <c>[0, 0, day, monthyear, hour, min, sec]</c> — the Acorn two-byte date pair
    /// (same packing as file catalogue dates via <see cref="AcornPath.EncodeFileDate"/>) followed by
    /// hour/min/sec. NOT the 9-byte RISC-OS-style block the previous implementation emitted.
    /// </summary>
    public static byte[] ReadTime(DateTime time)
    {
        // Reuse the same day/monthyear packing as every other RetroFS date field, exactly as PiFS reuses
        // fs_date_to_two_bytes for both file dates and read-time. (PiFS uses localtime; RetroFS uses the
        // server clock supplied by the caller.)
        AcornPath.EncodeFileDate(time, out byte day, out byte monthYear);
        return
        [
            0,
            0,
            day,
            monthYear,
            (byte)time.Hour,
            (byte)time.Minute,
            (byte)time.Second,
        ];
    }

    /// <summary>
    /// Read user info (fsop 0x18). PiFS <c>fs_read_user_info</c> (utilities/fs.c:7622) locates the session
    /// where the *named* user is logged on and returns a 5-byte reply
    /// <c>[0, 0, flag, station, net]</c> — flag = 0x40 for a System (privileged) user, else 0. It is NOT a
    /// username/boot-option block; PiFS returns the user's network *location*. If the named user is not
    /// logged on, PiFS replies with error 0xBC "No such user or not logged on".
    /// </summary>
    public static byte[] UserLocation(byte systemFlag, byte station, byte net)
    {
        return [0, 0, systemFlag, station, net];
    }

    /// <summary>
    /// Read client ID (fsop 0x20). PiFS (utilities/fs.c:11482) returns the caller's own username as raw
    /// ASCII terminated by 0x0D, with NO leading status bytes (reply length = username length + 1). The
    /// previous <c>[0,0,net,stn,index]</c> form was wrong.
    /// </summary>
    public static byte[] ClientUsername(string username)
    {
        // Trim at the first space (PiFS copies up to 10 chars then terminates at the first space with 0x0D).
        string trimmed = username;
        int space = trimmed.IndexOf(' ');
        if (space >= 0)
            trimmed = trimmed.Substring(0, space);
        if (trimmed.Length > 10)
            trimmed = trimmed.Substring(0, 10);

        byte[] name = Encoding.ASCII.GetBytes(trimmed);
        byte[] reply = new byte[name.Length + 1];
        name.CopyTo(reply, 0);
        reply[^1] = CarriageReturn;
        return reply;
    }

    /// <summary>
    /// Builds a reply that holds a count as 2 bytes, lowest byte first.
    /// </summary>
    /// <param name="count">
    /// The count.
    /// </param>
    /// <returns>
    /// The reply payload.
    /// </returns>
    public static byte[] LogoffCount(int count)
    {
        return [0, 0, (byte)count, (byte)(count >> 8)];
    }

    /// <summary>
    /// Writes a 27-byte catalogue entry into a buffer.
    /// </summary>
    /// <param name="destination">
    /// The buffer, which must hold at least 27 bytes.
    /// </param>
    /// <param name="name">
    /// The name, cut to 10 characters and padded with spaces.
    /// </param>
    /// <param name="isDirectory">
    /// True for a directory. A directory gets the length 0x000200.
    /// </param>
    /// <param name="length">
    /// The file length. The low 3 bytes are written for a file.
    /// </param>
    /// <param name="perm">
    /// The access byte, written at offset 18.
    /// </param>
    /// <returns>
    /// The number of bytes in an entry, 27.
    /// </returns>
    public static int WriteCatalogEntry(Span<byte> destination, string name, bool isDirectory, long length, byte perm)
    {
        byte[] ascii = Encoding.ASCII.GetBytes(name);
        int copyLength = Math.Min(10, ascii.Length);
        destination.Slice(0, 10).Fill((byte)' ');
        ascii.AsSpan(0, copyLength).CopyTo(destination.Slice(0, copyLength));

        if (isDirectory)
        {
            destination[18] = perm;
            // PiFS fs_examine arg 0: directory length is &200 (FS3 catalogue entry size)
            destination[24] = 0x00;
            destination[25] = 0x02;
            destination[26] = 0x00;
        }
        else
        {
            destination[18] = perm;
            destination[10] = 0;
            destination[11] = 0;
            destination[12] = 0;
            destination[13] = 0;
            destination[24] = (byte)length;
            destination[25] = (byte)(length >> 8);
            destination[26] = (byte)(length >> 16);
        }

        return 27;
    }

    private static void WriteUInt32LE(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static void WritePaddedName(Span<byte> destination, string name)
    {
        destination.Fill((byte)' ');
        byte[] ascii = Encoding.ASCII.GetBytes(name);
        int copyLength = Math.Min(ascii.Length, destination.Length);
        ascii.AsSpan(0, copyLength).CopyTo(destination);
    }
}
