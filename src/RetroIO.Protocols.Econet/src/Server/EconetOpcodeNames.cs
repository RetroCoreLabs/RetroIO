//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.Server;

/// <summary>
/// Opcode name lookup for activity log and diagnostics.
/// </summary>
public static class EconetOpcodeNames
{
    /// <summary>
    /// Gets the display name of an opcode byte.
    /// </summary>
    /// <param name="opcode">
    /// The opcode byte.
    /// </param>
    /// <returns>
    /// The name from FileServerOpcodeNames.
    /// </returns>
    public static string GetName(byte opcode) => FileServerOpcodeNames.GetName(opcode);

    /// <summary>
    /// Gets the display name of an opcode byte with its value in hex.
    /// </summary>
    /// <param name="opcode">
    /// The opcode byte.
    /// </param>
    /// <returns>
    /// The text from FileServerOpcodeNames.
    /// </returns>
    public static string GetDisplayName(byte opcode) => FileServerOpcodeNames.GetDisplayName(opcode);
}
