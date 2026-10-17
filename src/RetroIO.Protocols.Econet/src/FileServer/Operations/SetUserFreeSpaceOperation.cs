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
/// <c>SetUserFreeSpace</c> (fsop 0x1F) — sets a user's free-space quota. System-privilege only.
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandleSetUserFreeSpace</c>, unchanged. RetroFS does not enforce
/// per-user quotas, so once the login and system gates (now the dispatcher's job) pass, this simply
/// acknowledges success — matching the legacy behaviour.
/// </remarks>
public sealed class SetUserFreeSpaceOperation : IFileServerOperation
{
    /// <inheritdoc/>
    public byte Opcode => FileServerOpcodes.SetUserFreeSpace;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => true;

    /// <inheritdoc/>
    public byte[] Execute(in FsOpCall call) => FileServerReply.Success();
}
