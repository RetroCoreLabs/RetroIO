//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.Server;

/// <summary>
/// Opcode name lookup for activity log and diagnostics.
/// </summary>
public static class EconetOpcodeNames
{
    public static string GetName(byte opcode) => FileServerOpcodeNames.GetName(opcode);

    public static string GetDisplayName(byte opcode) => FileServerOpcodeNames.GetDisplayName(opcode);
}
