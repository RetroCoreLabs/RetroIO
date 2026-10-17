//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*FLOG</c> / <c>*LOGOFF</c> — a System user force-logs-off another station or user.
/// </summary>
/// <remarks>
/// Reference behaviour (PiFS, facts only): System-only. The argument is EITHER a station
/// (<c>net.stn</c>, or a bare <c>stn</c> which defaults net to 0) OR a username. A malformed station is
/// <c>0xFF "Bad station specification"</c>; an unknown username is <c>0xFF "Unknown user"</c>. Every
/// matching active session gets a silent <c>*BYE</c> (handles closed, bulk transfers released, session
/// dropped); the command always replies OK. Registered under two spellings: <c>FLOG</c> (min 3) and
/// <c>LOGOFF</c> (min 5).
/// <para>
/// Divergence: a bare station number matches net 0 (as in PiFS); a station on a non-zero Econet net must
/// be given as <c>net.stn</c>.
/// </para>
/// </remarks>
public sealed class FlogCommand : IOsCliCommand
{
    private readonly string _name;
    private readonly int _minimumAbbreviation;

    /// <summary>
    /// Creates the command under one spelling.
    /// </summary>
    /// <param name="name"><c>FLOG</c> or <c>LOGOFF</c>.</param>
    /// <param name="minimumAbbreviation">The spelling's minimum abbreviation length.</param>
    public FlogCommand(string name, int minimumAbbreviation)
    {
        _name = name;
        _minimumAbbreviation = minimumAbbreviation;
    }

    /// <inheritdoc/>
    public string Name => _name;

    /// <inheritdoc/>
    public int MinimumAbbreviation => _minimumAbbreviation;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => true;

    /// <inheritdoc/>
    public byte[] Execute(in OsCliCall call)
    {
        string argument = call.Argument.Trim();
        if (argument.Length == 0)
            return FileServerReply.Error(FileServerErrors.BadCommand, "Bad command");

        FileServerContext context = call.Context;

        // A leading digit means a station specification; otherwise the argument is a username.
        if (char.IsDigit(argument[0]))
        {
            if (!TryParseStation(argument, out byte targetNet, out byte targetStation))
                return FileServerReply.Error(FileServerErrors.Unsupported, "Bad station specification");

            LogOffStation(context, targetNet, targetStation);
            return FileServerReply.Success();
        }

        // Username form: the user must exist; then every active session it holds is logged off.
        if (!context.Users.UserExists(argument))
            return FileServerReply.Error(FileServerErrors.Unsupported, "Unknown user");

        FileServerSession[] active = context.Sessions.GetActiveSessions();
        for (int i = 0; i < active.Length; i++)
        {
            if (string.Equals(active[i].Username?.Trim(), argument, StringComparison.OrdinalIgnoreCase))
                LogOffStation(context, active[i].ClientNet, active[i].ClientStation);
        }

        return FileServerReply.Success();
    }

    /// <summary>
    /// Parses <c>net.stn</c> or a bare <c>stn</c> (net defaults to 0), matching PiFS's
    /// <c>sscanf("%hd.%hd")</c> then <c>sscanf("%hd")</c> fallback.
    /// </summary>
    private static bool TryParseStation(string argument, out byte targetNet, out byte targetStation)
    {
        targetNet = 0;
        targetStation = 0;

        int dot = argument.IndexOf('.');
        if (dot >= 0)
        {
            return byte.TryParse(argument.AsSpan(0, dot), out targetNet) &&
                   byte.TryParse(argument.AsSpan(dot + 1), out targetStation);
        }

        return byte.TryParse(argument, out targetStation);
    }

    /// <summary>
    /// Silent <c>*BYE</c> for one station: close its handles, release its bulk transfers, drop the
    /// session — the same teardown <see cref="LoginCommand"/> does on an implicit re-login.
    /// </summary>
    private static void LogOffStation(FileServerContext context, byte targetNet, byte targetStation)
    {
        context.Handles.CloseAllForStation(targetNet, targetStation);
        context.BulkTransfers.ReleaseStation(targetNet, targetStation);
        context.Sessions.Logout(targetNet, targetStation);
    }
}
