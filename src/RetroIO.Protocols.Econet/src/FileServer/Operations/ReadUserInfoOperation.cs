//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Operations;

/// <summary>
/// <c>ReadUserInfo</c> (fsop 0x18) — given a username, returns the network location of the session
/// where that user is logged on: <c>[0, 0, flag, station, net]</c> with <c>flag = 0x40</c> for a
/// System user. An unknown / not-logged-on user is <c>0xBC</c> (PiFS <c>fs_read_user_info</c>).
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandleReadUserInfo</c>, unchanged. It does NOT return
/// username/boot-option data — only the location flag and net/station.
/// </remarks>
public sealed class ReadUserInfoOperation : IFileServerOperation
{
    /// <inheritdoc/>
    public byte Opcode => FileServerOpcodes.ReadUserInfo;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in FsOpCall call)
    {
        // PiFS fs_read_user_info (utilities/fs.c:7622-7664): the request names a user (CR-terminated at
        // byte 5); the server finds the SESSION where that user is logged on and returns its network
        // location: [0, 0, flag, station, net] where flag = 0x40 for a System user, else 0. If the named
        // user is not logged on, PiFS returns error 0xBC. It does NOT return username/boot-option data.
        string wanted = AcornPath.ReadCrTerminatedString(call.FsRequest.Parameters).Trim();
        FileServerSession[] active = call.Context.Sessions.GetActiveSessions();
        for (int i = 0; i < active.Length; i++)
        {
            if (string.Equals(active[i].Username?.Trim(), wanted, System.StringComparison.OrdinalIgnoreCase))
            {
                byte flag = active[i].IsAdmin ? (byte)0x40 : (byte)0x00;
                return FileServerReply.UserLocation(flag, active[i].ClientStation, active[i].ClientNet);
            }
        }

        // 0xBC "No such user or not logged on" — matches PiFS fs_error at fs.c:7663.
        return FileServerReply.Error(0xBC, "No such user or not logged on");
    }
}
