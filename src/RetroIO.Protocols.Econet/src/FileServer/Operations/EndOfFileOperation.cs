//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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
    /// <inheritdoc/>
    public byte Opcode => FileServerOpcodes.EndOfFile;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in FsOpCall call)
    {
        byte handle = call.FsRequest.Parameters.Length > 0 ? call.FsRequest.Parameters[0] : (byte)0;
        OpenFileHandle? open = call.Context.Handles.Get(handle);
        if (open == null)
            return FileServerReply.Error(FileServerErrors.BadChannel, "Channel ?");

        open.PastEof = open.Stream.Position >= open.Stream.Length;
        byte eofFlag = open.PastEof ? (byte)0x80 : (byte)0x00;
        return FileServerReply.Success([eofFlag]);
    }
}
