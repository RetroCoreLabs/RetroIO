//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Protocols.Econet.Client;

/// <summary>
/// Default values for AUN file server clients.
/// </summary>
public static class EconetClientConstants
{
  public const int DefaultAunUdpPort = 32768;
  public const byte DefaultReplyPort = 0x80;
  public const byte DefaultDataPort = 0x40;
  public const int DefaultClientPort = 50000;
}
