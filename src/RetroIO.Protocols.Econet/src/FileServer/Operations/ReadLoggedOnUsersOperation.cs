//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Text;
using RetroIO.Protocols.Econet.FileServer.Configuration;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Operations;

/// <summary>
/// <c>ReadLoggedOnUsers</c> (fsop 0x0F) — returns a variable-length block describing the users
/// currently logged on, starting at a caller-supplied index (PiFS <c>fs_read_logged_on_users</c>).
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandleReadLoggedOnUsers</c>, unchanged. The
/// <see cref="TrimAcornUsername"/> helper moved here with it (this was its only caller).
/// </remarks>
public sealed class ReadLoggedOnUsersOperation : IFileServerOperation
{
    /// <inheritdoc/>
    public byte Opcode => FileServerOpcodes.ReadLoggedOnUsers;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in FsOpCall call)
    {
        FileServerSession session = call.Session!;

        byte start = call.FsRequest.Parameters.Length > 0 ? call.FsRequest.Parameters[0] : (byte)0;
        byte count = call.FsRequest.Parameters.Length > 1 ? call.FsRequest.Parameters[1] : (byte)8;
        FileServerSession[] sessions = call.Context.Sessions.GetLoggedOnUsers(session, start, count);

        // Wire-format divergence (task 0.4): the later FS emits a VARIABLE-length per-user entry; the
        // 1983 AUG specifies a FIXED 13-byte frame. Default (LaterFS) keeps the verified variable frame;
        // a client pinned to Classic1983 gets the fixed frame.
        WireProfile profile = call.Context.WireProfiles.Resolve(call.Request.ClientNet, call.Request.ClientStation);
        if (profile == WireProfile.Classic1983)
            return FileServerReply.LoggedOnUsers(sessions.Length, BuildClassic1983Entries(sessions));

        // PiFS fs_read_logged_on_users (utilities/fs.c:7546-7618) writes a VARIABLE-length per-user entry:
        //   [station][net][username chars (space-trimmed)][0x0D][priv]  -> 4 + strlen(username) bytes.
        // (The username is copied up to 10 chars and terminated at the first space.) NOT a fixed
        // [net][stn][10-char-padded-name] block. Two passes: size the buffer, then fill it (no LINQ/foreach).
        byte[][] names = new byte[sessions.Length][];
        int total = 0;
        for (int i = 0; i < sessions.Length; i++)
        {
            names[i] = TrimAcornUsername(sessions[i].Username);
            total += 4 + names[i].Length; // station + net + name + 0x0D + priv
        }

        byte[] entries = new byte[total];
        int offset = 0;
        for (int i = 0; i < sessions.Length; i++)
        {
            entries[offset++] = sessions[i].ClientStation;
            entries[offset++] = sessions[i].ClientNet;
            names[i].CopyTo(entries.AsSpan(offset));
            offset += names[i].Length;
            entries[offset++] = FileServerReply.CarriageReturn;
            entries[offset++] = sessions[i].IsAdmin ? (byte)1 : (byte)0; // FS_PRIV_SYSTEM ? 1 : 0
        }

        return FileServerReply.LoggedOnUsers(sessions.Length, entries);
    }

    /// <summary>
    /// Build the ORIGINAL 1983 fixed per-user frames: <c>[station, net, userid(10, space-padded),
    /// privilege]</c> = 13 bytes each (Acorn <i>Econet Advanced User Guide</i>, 1983).
    /// </summary>
    /// <remarks>
    /// <b>VALIDATE ON A REAL CLIENT:</b> the AUG (OCR) says "next entry at byte 17", implying the first
    /// frame starts at byte 4; RetroFS uses its standard <c>[cmd, rc, count]</c> header (entries from
    /// byte 3), which is internally consistent with every other reply but strides at byte 16. This
    /// off-by-one in the OCR'd layout is unresolved — like the <c>*DIR &amp;FE</c> question, it needs a
    /// BeebEm/strict-1983-client capture before this frame can be trusted. Harmless until then: the
    /// LaterFS default is unchanged and Classic1983 is opt-in with no real client yet. The privilege
    /// byte reuses the later 1/0 (System/Normal) convention; the exact 1983 value is also unconfirmed.
    /// </remarks>
    private static byte[] BuildClassic1983Entries(FileServerSession[] sessions)
    {
        const int frameSize = 13; // station(1) + net(1) + userid(10) + privilege(1)
        byte[] entries = new byte[sessions.Length * frameSize];
        int offset = 0;
        for (int i = 0; i < sessions.Length; i++)
        {
            entries[offset] = sessions[i].ClientStation;
            entries[offset + 1] = sessions[i].ClientNet;

            // 10-char, space-padded, first-space-trimmed username field.
            for (int p = 0; p < 10; p++)
                entries[offset + 2 + p] = (byte)' ';
            byte[] name = TrimAcornUsername(sessions[i].Username);
            int copy = name.Length < 10 ? name.Length : 10;
            for (int p = 0; p < copy; p++)
                entries[offset + 2 + p] = name[p];

            entries[offset + 12] = sessions[i].IsAdmin ? (byte)1 : (byte)0; // FS_PRIV_SYSTEM ? 1 : 0
            offset += frameSize;
        }
        return entries;
    }

    /// <summary>
    /// Copy an Acorn username, terminated at the first space, to at most 10 ASCII bytes — mirrors PiFS's
    /// "strncpy 10 then cut at first space" convention (utilities/fs.c:7597-7600, 11491-11493).
    /// </summary>
    private static byte[] TrimAcornUsername(string? username)
    {
        string value = username ?? string.Empty;
        int space = value.IndexOf(' ');
        if (space >= 0)
            value = value.Substring(0, space);
        if (value.Length > 10)
            value = value.Substring(0, 10);
        return Encoding.ASCII.GetBytes(value);
    }
}
