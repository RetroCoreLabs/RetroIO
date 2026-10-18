//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Handles;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Operations;

/// <summary>
/// <c>EndOfFile</c> (fsop 0x11) — reports whether an open channel's cursor is at or past end-of-file.
/// Reply is <c>[0, 0, flag]</c> where flag = <c>0x80</c> at/after EOF, else <c>0x00</c>.
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandleEndOfFile</c>, unchanged. Operates on a channel handle, not
/// the session; an unknown handle is <c>0xDE "Channel ?"</c>.
/// </remarks>
public sealed class EndOfFileOperation : IFileServerOperation
{
    /// <summary>
    /// Gets the fsop byte this operation serves: <see cref="FileServerOpcodes.EndOfFile"/> (0x11).
    /// The dispatcher uses it directly as the table index.
    /// </summary>
    public byte Opcode => FileServerOpcodes.EndOfFile;

    /// <summary>
    /// Gets <c>true</c>: the operation needs a logged-in caller. The dispatcher answers an
    /// unauthenticated caller with "Who are you ?" and never calls <see cref="Execute"/>, so
    /// <see cref="FsOpCall.Session"/> is non-null inside it.
    /// </summary>
    public bool RequiresLogin => true;

    /// <summary>
    /// Gets <c>false</c>: the operation is not limited to system-privileged users.
    /// </summary>
    public bool RequiresSystem => false;

    /// <summary>
    /// Reports whether the cursor of the channel named by the first parameter byte is at or past the
    /// end of the file, and stores that in the handle's end-of-file flag.
    /// </summary>
    /// <param name="call">
    /// Everything the operation needs: the request, the decoded header, the session, and services.
    /// </param>
    /// <returns>
    /// Success with one flag byte, 0x80 at or past the end and 0x00 otherwise; or 0xDE "Channel ?"
    /// for an unknown channel. A missing parameter byte is read as handle 0.
    /// </returns>
    public byte[] Execute(in FsOpCall call)
    {
        byte handle = call.FsRequest.Parameters.Length > 0 ? call.FsRequest.Parameters[0] : (byte)0;
        OpenFileHandle? open = call.Context.Handles.Get(call.Request.ClientNet, call.Request.ClientStation, handle);
        if (open == null)
            return FileServerReply.Error(FileServerErrors.BadChannel, "Channel ?");

        open.PastEof = open.Stream.Position >= open.Stream.Length;
        byte eofFlag = open.PastEof ? (byte)0x80 : (byte)0x00;
        return FileServerReply.Success([eofFlag]);
    }
}
