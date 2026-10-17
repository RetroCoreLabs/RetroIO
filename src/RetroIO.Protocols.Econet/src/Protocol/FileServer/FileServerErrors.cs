//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.Protocol.FileServer;

/// <summary>
/// Common Acorn file server error codes returned in the reply status byte.
/// </summary>
public static class FileServerErrors
{
    /// <summary>
    /// Error 0xBF, not logged in.
    /// </summary>
    public const byte WhoAreYou = 0xBF;
    /// <summary>
    /// Error 0xBC, wrong password. The server also uses it for a locked account.
    /// </summary>
    public const byte WrongPassword = 0xBC;
    /// <summary>
    /// Error 0xB8, too many users.
    /// </summary>
    public const byte TooManyUsers = 0xB8;
    /// <summary>
    /// Error 0xBD, insufficient access.
    /// </summary>
    public const byte InsufficientAccess = 0xBD;
    /// <summary>
    /// Error 0xD6, not found.
    /// </summary>
    public const byte NotFound = 0xD6;
    /// <summary>
    /// Error 0xA8, bad directory.
    /// </summary>
    public const byte BadDirectory = 0xA8;
    // PiFS uses the same 0xA8 for "Bad library" (*SETLIB) as for "Bad directory" (*SETHOME/*DIR).
    /// <summary>
    /// Error 0xA8, bad library. It has the same value as BadDirectory.
    /// </summary>
    public const byte BadLibrary = 0xA8;
    /// <summary>
    /// Error 0xDE, bad channel.
    /// </summary>
    public const byte BadChannel = 0xDE;
    /// <summary>
    /// Error 0xC0, locked.
    /// </summary>
    public const byte Locked = 0xC0;
    /// <summary>
    /// Error 0xC2, already open.
    /// </summary>
    public const byte AlreadyOpen = 0xC2;
    /// <summary>
    /// Error 0xCC, bad path.
    /// </summary>
    public const byte BadPath = 0xCC;
    // *ACCESS reports an unrecognised permission letter with 0xCF "Bad attribute" (PiFS fs.c:7270-7310).
    /// <summary>
    /// Error 0xCF, bad attribute.
    /// </summary>
    public const byte BadAttribute = 0xCF;
    /// <summary>
    /// Error 0xFE, bad command.
    /// </summary>
    public const byte BadCommand = 0xFE;
    /// <summary>
    /// Error 0xDF, end of file.
    /// </summary>
    public const byte Eof = 0xDF;
    /// <summary>
    /// Error 0xAF, types do not match.
    /// </summary>
    public const byte TypesDontMatch = 0xAF;
    /// <summary>
    /// Error 0xFF, unsupported.
    /// </summary>
    public const byte Unsupported = 0xFF;
}
