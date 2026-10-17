//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.Protocol.Aun;

/// <summary>
/// AUN packet type — first byte of the UDP payload (Acorn AUN numbering).
/// </summary>
public enum AunPacketType : byte
{
    /// <summary>
    /// A broadcast packet, value 0x01.
    /// </summary>
    Broadcast = 0x01,
    /// <summary>
    /// A data packet sent to one station, value 0x02.
    /// </summary>
    Unicast = 0x02,
    /// <summary>
    /// An acknowledgement, value 0x03.
    /// </summary>
    Ack = 0x03,
    /// <summary>
    /// A negative acknowledgement, value 0x04.
    /// </summary>
    Nak = 0x04,
    /// <summary>
    /// An immediate operation request, value 0x05.
    /// </summary>
    Immediate = 0x05,
    /// <summary>
    /// A reply to an immediate operation, value 0x06.
    /// </summary>
    ImmediateReply = 0x06,
    /// <summary>
    /// A negative reply to an immediate operation, value 0x07.
    /// </summary>
    ImmediateNak = 0x07,
}
