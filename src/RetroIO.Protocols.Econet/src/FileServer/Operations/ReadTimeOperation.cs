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
    /// <summary>
    /// Gets the fsop byte this operation serves: <see cref="FileServerOpcodes.ReadTime"/> (0x10).
    /// The dispatcher uses it directly as the table index.
    /// </summary>
    public byte Opcode => FileServerOpcodes.ReadTime;

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
    /// Returns the server clock.
    /// </summary>
    /// <param name="call">
    /// Everything the operation needs: the request, the decoded header, the session, and services.
    /// </param>
    /// <returns>
    /// The time reply built from <c>ServerClock.UtcNow</c> of the server context.
    /// </returns>
    public byte[] Execute(in FsOpCall call) =>
        FileServerReply.ReadTime(call.Context.ServerClock.UtcNow);
}
