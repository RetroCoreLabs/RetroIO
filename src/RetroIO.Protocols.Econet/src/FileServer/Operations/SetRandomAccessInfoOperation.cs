//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Handles;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Operations;

/// <summary>
/// <c>SetRandomAccessInfo</c> (fsop 0x0D) — sets a channel's cursor from a 32-bit little-endian value.
/// Fewer than 5 parameter bytes is <c>0xFE "Bad command"</c>; an unknown channel is <c>0xDE</c>.
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandleSetRandomAccessInfo</c>, unchanged.
/// </remarks>
public sealed class SetRandomAccessInfoOperation : IFileServerOperation
{
    /// <summary>
    /// Gets the fsop byte this operation serves: <see cref="FileServerOpcodes.SetRandomAccessInfo"/> (0x0D).
    /// The dispatcher uses it directly as the table index.
    /// </summary>
    public byte Opcode => FileServerOpcodes.SetRandomAccessInfo;

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
    /// Sets the cursor of the channel named by the first parameter byte to the 32-bit little-endian
    /// value in the next four bytes.
    /// </summary>
    /// <param name="call">
    /// Everything the operation needs: the request, the decoded header, the session, and services.
    /// </param>
    /// <returns>
    /// Success; 0xFE "Bad command" when there are fewer than 5 parameter bytes; or 0xDE "Channel ?"
    /// for an unknown channel.
    /// </returns>
    public byte[] Execute(in FsOpCall call)
    {
        ReadOnlySpan<byte> parameters = call.FsRequest.Parameters;
        if (parameters.Length < 5)
            return FileServerReply.Error(FileServerErrors.BadCommand, "Bad command");

        byte handle = parameters[0];
        long cursor = (long)(uint)(parameters[1] | (parameters[2] << 8) | (parameters[3] << 16) | (parameters[4] << 24));
        OpenFileHandle? open = call.Context.Handles.Get(handle);
        if (open == null)
            return FileServerReply.Error(FileServerErrors.BadChannel, "Channel ?");

        open.Stream.Position = cursor;
        open.SyncCursorFromStream();
        return FileServerReply.Success();
    }
}
