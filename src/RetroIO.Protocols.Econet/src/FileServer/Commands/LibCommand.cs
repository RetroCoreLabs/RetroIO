//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*LIB</c> — changes the library directory.
/// </summary>
/// <remarks>
/// Returns the reference's library-changed return code (0x09) with the library handle, so the ROM
/// updates its cached library.
/// </remarks>
public sealed class LibCommand : IOsCliCommand
{
    /// <summary>
    /// Gets the full command name in upper case: <c>LIB</c>.
    /// </summary>
    public string Name => "LIB";

    /// <summary>
    /// Gets the shortest accepted dot abbreviation, in characters: 3, the whole name, so the
    /// command can only be typed in full.
    /// </summary>
    public int MinimumAbbreviation => 3;

    /// <summary>
    /// Gets <c>true</c>: the command needs a logged-in caller. The dispatcher answers an
    /// unauthenticated caller with "Who are you ?" and never calls <see cref="Execute"/>, so
    /// <see cref="OsCliCall.Session"/> is non-null inside it.
    /// </summary>
    public bool RequiresLogin => true;

    /// <summary>
    /// Gets <c>false</c>: the command is not limited to system-privileged users.
    /// </summary>
    public bool RequiresSystem => false;

    /// <summary>
    /// Changes the session's library directory, resolved against the current library path. An empty
    /// or <c>.</c> argument keeps the current library path.
    /// </summary>
    /// <param name="call">
    /// Everything the command needs, including the already-split argument.
    /// </param>
    /// <returns>
    /// The library-changed reply with the library handle, or 0xD6 "Not found" when the target is not
    /// an existing directory.
    /// </returns>
    public byte[] Execute(in OsCliCall call)
    {
        string argument = AcornPath.StripQuotes(call.Argument);

        // An empty argument keeps the current library path (matching the pre-refactor behaviour).
        string newAcorn = string.IsNullOrEmpty(argument) || argument == "."
            ? call.Session!.AcornLibPath
            : AcornDirectoryNavigation.Resolve(call.Session!.AcornLibPath, argument);

        if (!call.Context.Paths.TryResolve(call.Session!, newAcorn, out string unixPath, out _, out _) ||
            !call.Context.FileSystem.DirectoryExists(unixPath))
        {
            return FileServerReply.Error(FileServerErrors.NotFound, "Not found");
        }

        call.Session!.AcornLibPath = newAcorn;
        call.Session!.LibraryDirectoryUnix = unixPath;
        return FileServerReply.LibraryChanged(AcornFileHandles.MulHandle(call.Session!.LibHandle));
    }
}
