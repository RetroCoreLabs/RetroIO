//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Operations;

/// <summary>
/// <c>ReadClientId</c> (fsop 0x20) — returns the caller's OWN username as raw ASCII terminated by
/// <c>0x0D</c>, with no leading status bytes (PiFS <c>fs.c:11482</c>).
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandleReadClientId</c>. The login gate is now the dispatcher's
/// job (<see cref="RequiresLogin"/>), so <see cref="FsOpCall.Session"/> is guaranteed non-null here —
/// the old in-body <c>RequireSession</c> null-check is gone.
/// </remarks>
public sealed class ReadClientIdOperation : IFileServerOperation
{
    /// <inheritdoc/>
    public byte Opcode => FileServerOpcodes.ReadClientId;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in FsOpCall call) =>
        FileServerReply.ClientUsername(call.Session!.Username);
}
