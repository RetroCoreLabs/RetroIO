//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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
    /// <inheritdoc/>
    public string Name => "LIB";

    /// <inheritdoc/>
    public int MinimumAbbreviation => 3;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
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
