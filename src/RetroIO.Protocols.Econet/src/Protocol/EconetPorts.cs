//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.Protocol;

/// <summary>
/// Well-known Econet application ports carried inside AUN payloads.
/// </summary>
public static class EconetPorts
{
    /// <summary>
    /// Port 0x99, the Econet port that file server requests are sent to.
    /// </summary>
    public const byte FileServer = 0x99;
}
