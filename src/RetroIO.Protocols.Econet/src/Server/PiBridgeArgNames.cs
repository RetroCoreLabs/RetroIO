//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.Server;

/// <summary>
/// PiBridge FS op 0x60 argument names (PiFS FSOP).
/// </summary>
public static class PiBridgeArgNames
{
    /// <summary>
    /// Gets the name of a PiBridge argument.
    /// </summary>
    /// <param name="arg">
    /// The argument byte.
    /// </param>
    /// <returns>
    /// The name, or the text arg followed by the number for an unknown argument.
    /// </returns>
    public static string GetName(byte arg) => arg switch
    {
        0 => "Bridge version",
        1 => "Shutdown Pi",
        16 => "User lookup",
        17 => "Read configuration",
        18 => "Write configuration",
        19 => "Shutdown fileserver",
        20 => "Force logoff",
        _ => $"arg {arg}",
    };
}
