//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Protocols.Econet.Protocol.FileServer;

/// <summary>
/// Common Acorn file server error codes returned in the reply status byte.
/// </summary>
public static class FileServerErrors
{
    public const byte WhoAreYou = 0xBF;
    public const byte WrongPassword = 0xBC;
    public const byte TooManyUsers = 0xB8;
    public const byte InsufficientAccess = 0xBD;
    public const byte NotFound = 0xD6;
    public const byte BadDirectory = 0xA8;
    // PiFS uses the same 0xA8 for "Bad library" (*SETLIB) as for "Bad directory" (*SETHOME/*DIR).
    public const byte BadLibrary = 0xA8;
    public const byte BadChannel = 0xDE;
    public const byte Locked = 0xC0;
    public const byte AlreadyOpen = 0xC2;
    public const byte BadPath = 0xCC;
    // *ACCESS reports an unrecognised permission letter with 0xCF "Bad attribute" (PiFS fs.c:7270-7310).
    public const byte BadAttribute = 0xCF;
    public const byte BadCommand = 0xFE;
    public const byte Eof = 0xDF;
    public const byte TypesDontMatch = 0xAF;
    public const byte Unsupported = 0xFF;
}
