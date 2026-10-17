//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Operations;

/// <summary>
/// <c>SetRtc</c> (fsop 0x1C) — sets the server's real-time clock. System-privilege only; a malformed
/// date is <c>0xFE "Bad time"</c>.
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandleSetRtc</c>, unchanged. The login and system gates are now
/// the dispatcher's job (<see cref="RequiresLogin"/>/<see cref="RequiresSystem"/>), so the in-body
/// session/admin checks are gone — <see cref="FsOpCall.Session"/> is a non-null admin here.
/// </remarks>
public sealed class SetRtcOperation : IFileServerOperation
{
    /// <inheritdoc/>
    public byte Opcode => FileServerOpcodes.SetRtc;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => true;

    /// <inheritdoc/>
    public byte[] Execute(in FsOpCall call)
    {
        ReadOnlySpan<byte> parameters = call.FsRequest.Parameters;
        if (parameters.Length >= 5)
        {
            int year = 1900 + (parameters[4] >> 1);
            int month = parameters[3];
            int day = parameters[2];
            int hour = parameters[1];
            int minute = parameters[0];
            int second = parameters.Length > 5 ? parameters[5] : 0;
            try
            {
                call.Context.ServerClock.SetUtc(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc));
            }
            catch (ArgumentOutOfRangeException)
            {
                return FileServerReply.Error(FileServerErrors.BadCommand, "Bad time");
            }
        }

        return FileServerReply.Success();
    }
}
