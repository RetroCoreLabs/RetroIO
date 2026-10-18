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
    /// <summary>
    /// Gets the fsop byte this operation serves: <see cref="FileServerOpcodes.SetRtc"/> (0x1C).
    /// The dispatcher uses it directly as the table index.
    /// </summary>
    public byte Opcode => FileServerOpcodes.SetRtc;

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
    /// Sets the server clock from the parameters: minute, hour, day, month, then the year as 1900 plus
    /// the fifth byte shifted right by one, and an optional second.
    /// </summary>
    /// <remarks>
    /// With fewer than 5 parameter bytes the clock is not changed and the reply is success.
    /// </remarks>
    /// <param name="call">
    /// Everything the operation needs: the request, the decoded header, the session, and services.
    /// </param>
    /// <returns>
    /// Success, or 0xFE "Bad time" when the values do not form a valid date and time.
    /// </returns>
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
