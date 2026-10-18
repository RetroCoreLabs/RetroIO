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
    /// <summary>
    /// Gets the fsop byte this operation serves: <see cref="FileServerOpcodes.SetUserFreeSpace"/> (0x1F).
    /// The dispatcher uses it directly as the table index.
    /// </summary>
    public byte Opcode => FileServerOpcodes.SetUserFreeSpace;

    /// <summary>
    /// Gets <c>true</c>: the operation needs a logged-in caller. The dispatcher answers an
    /// unauthenticated caller with "Who are you ?" and never calls <see cref="Execute"/>, so
    /// <see cref="FsOpCall.Session"/> is non-null inside it.
    /// </summary>
    public bool RequiresLogin => true;

    /// <summary>
    /// Gets <c>true</c>: only a system-privileged user may run this operation. The dispatcher answers
    /// everyone else with "Insufficient access".
    /// </summary>
    public bool RequiresSystem => true;

    /// <summary>
    /// Accepts the request and does nothing else: per-user quotas are not enforced.
    /// </summary>
    /// <param name="call">
    /// Everything the operation needs: the request, the decoded header, the session, and services.
    /// </param>
    /// <returns>
    /// Always success.
    /// </returns>
    public byte[] Execute(in FsOpCall call) => FileServerReply.Success();
}
