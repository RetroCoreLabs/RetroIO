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
    /// <inheritdoc/>
    public byte Opcode => FileServerOpcodes.GetRandomAccessInfo;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in FsOpCall call)
    {
        ReadOnlySpan<byte> parameters = call.FsRequest.Parameters;
        byte handle = parameters.Length > 0 ? parameters[0] : (byte)0;
        byte function = parameters.Length > 1 ? parameters[1] : (byte)0;
        OpenFileHandle? open = call.Context.Handles.Get(handle);
        if (open == null)
            return FileServerReply.Error(FileServerErrors.BadChannel, "Channel ?");

        // function 0 = cursor position; 1/2 = extent (file length). Reply is a single 24-bit value.
        long value = function == 0 ? open.Cursor : open.Stream.Length;
        return FileServerReply.RandomAccessInfo((int)(value & 0xFFFFFF));
    }
}
