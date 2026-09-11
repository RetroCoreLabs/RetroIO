//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Operations;

/// <summary>
/// <c>ReadTime</c> (fsop 0x10) — returns the server clock as the PiFS 7-byte reply
/// <c>[0, 0, day, monthYear, hour, min, sec]</c>. A pure clock read: it touches no filesystem.
/// </summary>
/// <remarks>
/// First opcode migrated out of <c>MiscOpcodeHandler</c>, where it was an inline switch arm (not even a
/// method). The migration is byte-for-byte: the same <see cref="FileServerReply.ReadTime"/> payload,
/// wrapped by the dispatcher's opcode-aware <c>AunFsReply.Build</c>.
/// </remarks>
public sealed class ReadTimeOperation : IFileServerOperation
{
    /// <inheritdoc/>
    public byte Opcode => FileServerOpcodes.ReadTime;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in FsOpCall call) =>
        FileServerReply.ReadTime(call.Context.ServerClock.UtcNow);
}
