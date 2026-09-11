//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Protocols.Econet.Protocol.FileServer;

/// <summary>
/// Human-readable names for Acorn file server error status bytes.
/// </summary>
public static class FileServerErrorNames
{
    public static string GetName(byte errorCode) => errorCode switch
    {
        FileServerErrors.WhoAreYou => "Not logged in",
        FileServerErrors.WrongPassword => "Wrong password",
        FileServerErrors.TooManyUsers => "Too many users",
        FileServerErrors.InsufficientAccess => "Insufficient access",
        FileServerErrors.NotFound => "Not found",
        FileServerErrors.BadDirectory => "Bad directory",
        FileServerErrors.BadChannel => "Bad channel",
        FileServerErrors.Locked => "Locked",
        FileServerErrors.AlreadyOpen => "Already open",
        FileServerErrors.BadPath => "Bad path",
        FileServerErrors.BadCommand => "Bad command",
        FileServerErrors.Eof => "End of file",
        FileServerErrors.TypesDontMatch => "Types do not match",
        FileServerErrors.Unsupported => "Unsupported",
        0x00 => "OK",
        _ => $"Error 0x{errorCode:X2}",
    };

    /// <summary>
    /// Display form for activity log: prefers server message, falls back to error name.
    /// </summary>
    public static string GetDisplay(byte errorCode, string? serverMessage)
    {
        string name = GetName(errorCode);
        if (!string.IsNullOrWhiteSpace(serverMessage))
        {
            if (serverMessage.Equals(name, StringComparison.OrdinalIgnoreCase))
                return name;

            return $"{name}: {serverMessage}";
        }

        return name;
    }
}
