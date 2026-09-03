//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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
    /// <inheritdoc/>
    public byte Opcode => FileServerOpcodes.SetRandomAccessInfo;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
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
