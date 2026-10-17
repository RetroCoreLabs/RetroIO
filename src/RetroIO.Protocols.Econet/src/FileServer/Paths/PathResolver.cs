//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Discs;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Protocols.Econet.FileServer.Paths;

/// <summary>
/// Turns Acorn paths into host paths for a session, using its disc and its directory handles.
/// </summary>
public sealed class PathResolver
{
    private readonly IFileSystemBackend _fileSystem;
    private readonly DiscCatalog _discs;
    private readonly string _rootDirectory;

    /// <summary>
    /// Creates the resolver.
    /// </summary>
    /// <param name="fileSystem">
    /// The file system the discs are stored on.
    /// </param>
    /// <param name="discs">
    /// The disc catalogue.
    /// </param>
    /// <param name="rootDirectory">
    /// The root directory that holds the disc directories.
    /// </param>
    public PathResolver(IFileSystemBackend fileSystem, DiscCatalog discs, string rootDirectory)
    {
        _fileSystem = fileSystem;
        _discs = discs;
        _rootDirectory = rootDirectory;
    }

    /// <summary>
    /// Resolves an Acorn path relative to the current directory of the session.
    /// </summary>
    /// <param name="session">
    /// The session that supplies the disc and directories.
    /// </param>
    /// <param name="acornPath">
    /// The Acorn path.
    /// </param>
    /// <param name="unixPath">
    /// Receives the host path.
    /// </param>
    /// <param name="errorCode">
    /// Receives the file server error code. It is the not found code on failure.
    /// </param>
    /// <param name="errorMessage">
    /// Receives the error text. It is Not found on failure.
    /// </param>
    /// <returns>
    /// True when the path resolves to an existing file or directory.
    /// </returns>
    public bool TryResolve(FileServerSession session, string acornPath, out string unixPath, out byte errorCode, out string errorMessage)
    {
        return TryResolve(session, acornPath, session.CwdHandle, out unixPath, out errorCode, out errorMessage);
    }

    /// <summary>
    /// Resolves an Acorn path relative to the home, current or library directory of the session.
    /// </summary>
    /// <param name="session">
    /// The session that supplies the disc and directories.
    /// </param>
    /// <param name="acornPath">
    /// The Acorn path. A path that starts with a colon names a disc, and a path that starts with a dollar sign starts at the root of the current disc.
    /// </param>
    /// <param name="relativeTo">
    /// The directory handle the path is relative to. 1 selects the home directory, 3 the library, anything else the current directory, and 0 means the current directory handle of the session.
    /// </param>
    /// <param name="unixPath">
    /// Receives the host path.
    /// </param>
    /// <param name="errorCode">
    /// Receives the file server error code. It is the not found code on failure.
    /// </param>
    /// <param name="errorMessage">
    /// Receives the error text. It is Not found on failure.
    /// </param>
    /// <returns>
    /// True when the path resolves to an existing file or directory.
    /// </returns>
    public bool TryResolve(FileServerSession session, string acornPath, byte relativeTo, out string unixPath, out byte errorCode, out string errorMessage)
    {
        unixPath = string.Empty;
        errorCode = FileServerErrors.NotFound;
        errorMessage = "Not found";

        byte handle = relativeTo switch
        {
            0 => session.CwdHandle,
            _ => relativeTo,
        };

        string acornBase = handle switch
        {
            1 => session.AcornHomePath,
            3 => session.AcornLibPath,
            _ => session.AcornCwdPath,
        };

        string trimmed = acornPath.Trim();
        if (trimmed.StartsWith(':'))
        {
            // PiFS (fs.c:2377-2406) validates the disc name BEFORE resolving: a bare ":" — or any
            // ":" without a 1-16 character disc name — is FS_PATH_ERR_NODISC and resolves to nothing.
            // Previously this branch threw the disc spec away and resolved the empty remainder against
            // the session's CURRENT disc, so ":" silently landed on the disc container directory,
            // escaping the user's root. See AcornPath.TryParseDiscPath.
            if (!AcornPath.TryParseDiscPath(trimmed, out string discName, out string rootedRemainder))
            {
                errorCode = FileServerErrors.NotFound;
                errorMessage = "Not found";
                return false;
            }

            // The disc name must name a disc we actually have (PiFS fs.c:2411-2424).
            if (!_discs.TryFind(discName, out DiscEntry? namedDisc) || namedDisc == null)
            {
                errorCode = FileServerErrors.NotFound;
                errorMessage = "Not found";
                return false;
            }

            // Resolve under the NAMED disc, not the session's current disc.
            string namedDiscPath = _discs.GetDiscDirectoryPath(namedDisc.Index);
            string namedRelative = AcornPath.AcornFullToUnixRelative(rootedRemainder);
            unixPath = string.IsNullOrEmpty(namedRelative)
                ? namedDiscPath
                : _fileSystem.GetFullPath(_fileSystem.CombinePath(namedDiscPath, namedRelative));

            return _fileSystem.DirectoryExists(unixPath) || _fileSystem.FileExists(unixPath);
        }

        if (trimmed.StartsWith('$'))
        {
            string fullAcorn = AcornPath.CombineAcorn(GetDiscRootAcorn(session), trimmed);
            string relative = AcornPath.AcornFullToUnixRelative(fullAcorn);
            return TryResolveUnixUnderDisc(session, relative, out unixPath);
        }

        if (TryResolveExplicitDisc(session, acornPath, out string? discBasePath, out string discRelative))
        {
            unixPath = _fileSystem.GetFullPath(_fileSystem.CombinePath(discBasePath!, discRelative));
            return _fileSystem.DirectoryExists(unixPath) || _fileSystem.FileExists(unixPath);
        }

        string fullAcornPath = AcornPath.CombineAcorn(acornBase, trimmed);
        string unixRelative = AcornPath.AcornFullToUnixRelative(fullAcornPath);
        if (TryResolveUnixUnderDisc(session, unixRelative, out unixPath))
            return true;

        errorCode = FileServerErrors.NotFound;
        errorMessage = "Not found";
        return false;
    }

    private bool TryResolveUnixUnderDisc(FileServerSession session, string relativeUnix, out string unixPath)
    {
        DiscEntry? disc = _discs.GetByIndex(session.CurrentDisc);
        string discPath = disc != null
            ? _fileSystem.CombinePath(_rootDirectory, disc.DirectoryName)
            : _rootDirectory;

        unixPath = string.IsNullOrEmpty(relativeUnix)
            ? discPath
            : _fileSystem.GetFullPath(_fileSystem.CombinePath(discPath, relativeUnix));

        return _fileSystem.DirectoryExists(unixPath) || _fileSystem.FileExists(unixPath);
    }

    private string GetDiscRootAcorn(FileServerSession session)
    {
        DiscEntry? disc = _discs.GetByIndex(session.CurrentDisc);
        string name = disc?.AcornName ?? "RetroFS";
        return $":{name}.$";
    }

    private bool TryResolveExplicitDisc(FileServerSession session, string acornPath, out string? discBasePath, out string relativeUnix)
    {
        discBasePath = null;
        relativeUnix = string.Empty;
        string trimmed = acornPath.Trim();
        if (!trimmed.StartsWith(':'))
            return false;

        string withoutColon = trimmed.Substring(1);
        int dotIndex = withoutColon.IndexOf('.');
        if (dotIndex <= 0)
            return false;

        string discReference = withoutColon.Substring(0, dotIndex);
        if (!_discs.TryFind(discReference, out DiscEntry? disc) || disc == null)
            return false;

        discBasePath = _discs.GetDiscDirectoryPath(disc.Index);
        string remainder = withoutColon.Substring(dotIndex + 1);
        relativeUnix = AcornPath.AcornFullToUnixRelative("$." + remainder);
        return true;
    }
}
