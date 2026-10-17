//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.Client;

/// <summary>
/// Default values for AUN file server clients.
/// </summary>
public static class EconetClientConstants
{
  /// <summary>
  /// The default UDP port of an AUN file server.
  /// </summary>
  public const int DefaultAunUdpPort = 32768;
  /// <summary>
  /// The default Econet port the client uses to receive file server replies.
  /// </summary>
  public const byte DefaultReplyPort = 0x80;
  /// <summary>
  /// The default Econet port the client uses to receive bulk data.
  /// </summary>
  public const byte DefaultDataPort = 0x40;
  /// <summary>
  /// The default local UDP port the client binds to.
  /// </summary>
  public const int DefaultClientPort = 50000;
}
