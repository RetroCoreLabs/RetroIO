//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using RetroIO.Protocols.Econet.Common;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// Resolves relative and absolute Acorn directory arguments for <c>*DIR</c> and <c>*LIB</c>.
/// </summary>
/// <remarks>
/// Shared by <see cref="DirCommand"/> and <see cref="LibCommand"/> so the parent (<c>^</c>) and
/// disc-specifier rules live in one place. Extracted unchanged from the pre-refactor
/// OsCliCommandHandler.
/// </remarks>
public static class AcornDirectoryNavigation
{
    /// <summary>
    /// Resolves a directory argument against a base Acorn path.
    /// </summary>
    /// <param name="acornBase">
    /// The path the argument is relative to (the current CSD or library path).
    /// </param>
    /// <param name="argument">
    /// The argument: <c>^</c> for the parent, an absolute path starting ':' or '$', or a relative
    /// name.
    /// </param>
    /// <returns>
    /// The resolved Acorn path.
    /// </returns>
    public static string Resolve(string acornBase, string argument)
    {
        if (argument == "^")
            return Parent(acornBase);

        if (argument.StartsWith(':'))
        {
            // Normalise ":DISC" to ":DISC.$" so the stored CSD always carries the '$' root marker.
            // Without it a bare ":" would be stored raw and Parent below could never climb out (no
            // '$' to clamp against), making *DIR ^ a dead end. Malformed disc specs are left as-is
            // here and rejected later by the path resolver, so the CSD is only updated on success.
            if (AcornPath.TryParseDiscPath(argument, out string discName, out string rootedRemainder))
                return $":{discName}.{rootedRemainder}";

            return argument;
        }

        if (argument.StartsWith('$'))
            return argument;

        return AcornPath.CombineAcorn(acornBase, argument);
    }

    /// <summary>
    /// Returns the parent of an Acorn path, never ascending above the disc root.
    /// </summary>
    /// <param name="acornPath">
    /// The path to take the parent of.
    /// </param>
    /// <returns>
    /// The parent path, or <paramref name="acornPath"/> unchanged when it is already at the root.
    /// </returns>
    public static string Parent(string acornPath)
    {
        int lastDot = acornPath.LastIndexOf('.');
        if (lastDot <= 0)
            return acornPath;

        string parent = acornPath.Substring(0, lastDot);
        // The '$' root marker must survive: never climb above the disc root.
        return parent.Contains('$') ? parent : acornPath;
    }
}
