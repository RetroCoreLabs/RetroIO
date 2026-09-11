//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*I AM</c> (and its <c>*IAM</c> / <c>*LOGIN</c> / <c>*LOGON</c> spellings) — authenticates a
/// station.
/// </summary>
/// <remarks>
/// The only command that runs without an existing session, so it declares
/// <see cref="RequiresLogin"/> false. Registered once per spelling, since each has its own minimum
/// abbreviation: "I AM" 2, "IAM" 3, "LOGIN" 4, "LOGON" 5.
///
/// A repeat login from an already-logged-in station performs an implicit logout first (matching a
/// BBC that was reset without a clean <c>*BYE</c>), closing its handles and releasing bulk
/// transfers before the new session is created.
/// </remarks>
public sealed class LoginCommand : IOsCliCommand
{
    private readonly string _name;
    private readonly int _minimumAbbreviation;

    /// <summary>
    /// Creates the command under one login spelling.
    /// </summary>
    /// <param name="name">
    /// "I AM", "IAM", "LOGIN", or "LOGON".
    /// </param>
    /// <param name="minimumAbbreviation">
    /// The spelling's minimum abbreviation length.
    /// </param>
    public LoginCommand(string name, int minimumAbbreviation)
    {
        _name = name;
        _minimumAbbreviation = minimumAbbreviation;
    }

    /// <inheritdoc/>
    public string Name => _name;

    /// <inheritdoc/>
    public int MinimumAbbreviation => _minimumAbbreviation;

    /// <inheritdoc/>
    public bool RequiresLogin => false;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in OsCliCall call)
    {
        // Re-parse the full command text so all four spellings and their username/password split are
        // handled in one place.
        if (!AcornPath.TryParseIam(call.CommandText, out string username, out string password))
            return FileServerReply.Error(FileServerErrors.BadCommand, "Bad command");

        FileServerContext context = call.Context;
        byte clientNet = call.Request.ClientNet;
        byte clientStation = call.Request.ClientStation;
        string clientIp = call.Request.ClientAddress?.ToString() ?? "UNKNOWN";

        // Implicit silent logout when the station logs in again (e.g. after a BBC reset).
        if (context.Sessions.IsLoggedIn(clientNet, clientStation))
        {
            context.Handles.CloseAllForStation(clientNet, clientStation);
            context.BulkTransfers.ReleaseStation(clientNet, clientStation);
            context.Sessions.Logout(clientNet, clientStation);
        }

        if (context.Sessions.TryLogin(clientNet, clientStation, username, password, out FileServerSession? session, out byte errorCode, out string errorMessage))
        {
            // TryLogin gave the session its disc-layout default home/lib (discPath/USER). Apply the
            // user's persisted *SETHOME/*SETLIB preferences on top — synchronously, BEFORE the reply is
            // sent, so the login handshake bytes are unchanged (LoginSuccess carries only handles + boot
            // option, none of which depend on the home/lib paths) and no client ever sees a transient
            // wrong-home window. Every override is guarded (see ApplyProfileDirectories).
            ApplyProfileDirectories(context, session!);

            EconetSecurityAudit.EmitAuthAsync(context.SecurityEvents, session!.RetroUserId, session.Username, true, clientIp)
                .AsTask().GetAwaiter().GetResult();
            return FileServerReply.LoginSuccess(session.UrdHandle, session.CwdHandle, session.LibHandle, session.BootOpt);
        }

        EconetSecurityAudit.EmitAuthAsync(context.SecurityEvents, Guid.Empty, username.TrimEnd(), false, clientIp)
            .AsTask().GetAwaiter().GetResult();
        return FileServerReply.Error(errorCode, errorMessage);
    }

    // The sentinel values EconetUserProfile.FromRetroUser fills in when a user has NO econet.home /
    // econet.lib metadata. A profile whose Home/Lib still equals its sentinel is treated as "not set",
    // so the disc-layout default from TryLogin stands and a default user's login stays byte-identical
    // (pinned by GoldenLoginEnvTests). Consequence: an admin cannot force home to the disc root "$" via
    // the profile — "$" reads as "unset" — which matches the sentinel meaning.
    private const string DefaultHome = "$";
    private const string DefaultLib = "LIB:$.Library";

    /// <summary>
    /// Overlays the user's persisted home/lib preferences onto a freshly-built session, each guarded by
    /// "value is non-default AND resolves AND the target directory exists" so a bad or absent preference
    /// silently leaves the disc-layout default in place.
    /// </summary>
    private static void ApplyProfileDirectories(FileServerContext context, FileServerSession session)
    {
        EconetUserProfile? profile = context.Users.GetProfile(session.RetroUserId);
        if (profile == null)
            return;

        // HOME (URD): resolve against the session's current home base, mirroring *DIR/*LIB resolution.
        if (!string.IsNullOrWhiteSpace(profile.Home) && profile.Home != DefaultHome)
        {
            string acornHome = AcornDirectoryNavigation.Resolve(session.AcornHomePath, profile.Home);
            if (context.Paths.TryResolve(session, acornHome, out string homeUnix, out _, out _) &&
                context.FileSystem.DirectoryExists(homeUnix))
            {
                session.AcornHomePath = acornHome;
                session.AcornCwdPath = acornHome;
                session.HomeDirectoryUnix = homeUnix;
                session.CurrentDirectoryUnix = homeUnix;
            }
        }

        // LIB: resolve against the session's current library base, same guard.
        if (!string.IsNullOrWhiteSpace(profile.Lib) && profile.Lib != DefaultLib)
        {
            string acornLib = AcornDirectoryNavigation.Resolve(session.AcornLibPath, profile.Lib);
            if (context.Paths.TryResolve(session, acornLib, out string libUnix, out _, out _) &&
                context.FileSystem.DirectoryExists(libUnix))
            {
                session.AcornLibPath = acornLib;
                session.LibraryDirectoryUnix = libUnix;
            }
        }
    }
}
