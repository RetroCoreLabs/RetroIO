//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Protocols.Econet.Protocol.Aun;

/// <summary>
/// AUN packet type — first byte of the UDP payload (Acorn AUN numbering).
/// </summary>
public enum AunPacketType : byte
{
    Broadcast = 0x01,
    Unicast = 0x02,
    Ack = 0x03,
    Nak = 0x04,
    Immediate = 0x05,
    ImmediateReply = 0x06,
    ImmediateNak = 0x07,
}
