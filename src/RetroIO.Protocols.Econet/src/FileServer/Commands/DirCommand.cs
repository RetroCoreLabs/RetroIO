//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Configuration;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*DIR</c> (and <c>*DIR^</c>) — changes the current selected directory.
/// </summary>
/// <remarks>
/// Returns the reference's directory-changed return code (0x07) with the CSD handle. Registered
/// twice: once as "DIR" (minimum 3), and once as "DIR^" (minimum 4), the parent shortcut, which
/// behaves as <c>*DIR ^</c>. The two names do not overlap, so their relative order is free.
/// </remarks>
public sealed class DirCommand : IOsCliCommand
{
    private readonly string _name;
    private readonly int _minimumAbbreviation;
    private readonly string? _forcedArgument;

    /// <summary>
    /// Creates the command under one of its names.
    /// </summary>
    /// <param name="name">
    /// "DIR" or "DIR^".
    /// </param>
    /// <param name="minimumAbbreviation">
    /// 3 for "DIR", 4 for "DIR^".
    /// </param>
    /// <param name="forcedArgument">
    /// Null for "DIR" (use the typed argument); "^" for "DIR^", which always means the parent.
    /// </param>
    public DirCommand(string name, int minimumAbbreviation, string? forcedArgument)
    {
        _name = name;
        _minimumAbbreviation = minimumAbbreviation;
        _forcedArgument = forcedArgument;
    }

    /// <inheritdoc/>
    public string Name => _name;

    /// <inheritdoc/>
    public int MinimumAbbreviation => _minimumAbbreviation;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in OsCliCall call)
    {
        string argument = _forcedArgument ?? AcornPath.StripQuotes(call.Argument);

        // An empty (or ".") argument selects the user's home directory, matching the pre-refactor
        // behaviour.
        string newAcorn = string.IsNullOrEmpty(argument) || argument == "."
            ? call.Session!.AcornHomePath
            : AcornDirectoryNavigation.Resolve(call.Session!.AcornCwdPath, argument);

        if (!call.Context.Paths.TryResolve(call.Session!, newAcorn, out string unixPath, out _, out _) ||
            !call.Context.FileSystem.DirectoryExists(unixPath))
        {
            // Wire-format divergence (task 0.4): the later FS reports a failed *DIR as &D6 "Not found";
            // the original 1983 NFS ROM used &FE. Default (LaterFS) keeps the verified &D6 byte; a
            // client pinned to Classic1983 gets &FE. Message text is display-only and left unchanged.
            WireProfile profile = call.Context.WireProfiles.Resolve(
                call.Request.ClientNet, call.Request.ClientStation);
            byte notFoundCode = profile == WireProfile.Classic1983
                ? FileServerErrors.BadCommand
                : FileServerErrors.NotFound;
            return FileServerReply.Error(notFoundCode, "Not found");
        }

        call.Session!.AcornCwdPath = newAcorn;
        call.Session!.CurrentDirectoryUnix = unixPath;
        return FileServerReply.DirectoryChanged(AcornFileHandles.MulHandle(call.Session!.CwdHandle));
    }
}
