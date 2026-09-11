//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Handlers;

/// <summary>
/// PiFS <c>fs_bye</c> equivalent — close handles, release bulk ports, clear session, reply <c>[0,0]</c>.
/// Caller must enforce login (PiFS returns <c>0xBF</c> when not logged in).
/// </summary>
internal static class FileServerLogout
{
    public static byte[] Execute(FileServerContext context, byte clientNet, byte clientStation)
    {
        context.Handles.CloseAllForStation(clientNet, clientStation);
        context.BulkTransfers.ReleaseStation(clientNet, clientStation);
        context.Sessions.Logout(clientNet, clientStation);
        return FileServerReply.Success();
    }
}
