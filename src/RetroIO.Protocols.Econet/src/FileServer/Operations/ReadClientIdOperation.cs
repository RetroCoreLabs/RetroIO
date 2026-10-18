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
    /// <summary>
    /// Gets the fsop byte this operation serves: <see cref="FileServerOpcodes.ReadClientId"/> (0x20).
    /// The dispatcher uses it directly as the table index.
    /// </summary>
    public byte Opcode => FileServerOpcodes.ReadClientId;

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
    /// Returns the caller's own username.
    /// </summary>
    /// <param name="call">
    /// Everything the operation needs: the request, the decoded header, the session, and services.
    /// </param>
    /// <returns>
    /// The username reply built from the session's username.
    /// </returns>
    public byte[] Execute(in FsOpCall call) =>
        FileServerReply.ClientUsername(call.Session!.Username);
}
