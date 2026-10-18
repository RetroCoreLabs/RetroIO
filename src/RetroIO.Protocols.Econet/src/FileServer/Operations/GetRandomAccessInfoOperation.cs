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
/// <c>GetRandomAccessInfo</c> (fsop 0x0C) — returns a channel's cursor (function 0) or extent/length
/// (function 1/2) as a single 24-bit little-endian value in a 5-byte reply. Unknown channel is
/// <c>0xDE</c>.
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandleGetRandomAccessInfo</c>, unchanged. PiFS dispatch
/// (utilities/fs.c:11384): handle = <c>data[5]</c>, function = <c>data[6]</c>.
/// </remarks>
public sealed class GetRandomAccessInfoOperation : IFileServerOperation
{
    /// <summary>
    /// Gets the fsop byte this operation serves: <see cref="FileServerOpcodes.GetRandomAccessInfo"/> (0x0C).
    /// The dispatcher uses it directly as the table index.
    /// </summary>
    public byte Opcode => FileServerOpcodes.GetRandomAccessInfo;

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
    /// Returns the cursor (function 0) or the file length (function 1 or 2) of the channel named by
    /// the first parameter byte, cut to 24 bits. Any other function value returns 0.
    /// </summary>
    /// <remarks>
    /// PiFS (fs_get_random_access_info) handles only functions 0, 1 and 2 and sends the three data
    /// bytes unset for any other value; this operation sends zeros for those.
    /// </remarks>
    /// <param name="call">
    /// Everything the operation needs: the request, the decoded header, the session, and services.
    /// </param>
    /// <returns>
    /// The random-access info reply, or 0xDE "Channel ?" for an unknown channel.
    /// </returns>
    public byte[] Execute(in FsOpCall call)
    {
        ReadOnlySpan<byte> parameters = call.FsRequest.Parameters;
        byte handle = parameters.Length > 0 ? parameters[0] : (byte)0;
        byte function = parameters.Length > 1 ? parameters[1] : (byte)0;
        OpenFileHandle? open = call.Context.Handles.Get(handle);
        if (open == null)
            return FileServerReply.Error(FileServerErrors.BadChannel, "Channel ?");

        // function 0 = cursor position; 1/2 = extent (file length); anything else = 0 (PiFS leaves the
        // value unset). Reply is a single 24-bit value.
        long value = function == 0 ? open.Cursor : (function == 1 || function == 2 ? open.Stream.Length : 0);
        return FileServerReply.RandomAccessInfo((int)(value & 0xFFFFFF));
    }
}
