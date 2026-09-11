//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Net;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Server.Transport;

namespace RetroIO.Protocols.Econet.Server;

/// <summary>
/// One decoded AUN datagram with client routing metadata.
/// </summary>
public sealed class EconetMessage
{
    public required AunPacketType PacketType { get; init; }
    public required byte EconetPort { get; init; }
    public required byte Control { get; init; }
    public required uint Sequence { get; init; }
    public required byte[] Payload { get; init; }
    public required IPAddress ClientAddress { get; init; }
    public required int ClientPort { get; init; }
    public byte ClientNet { get; init; }
    public byte ClientStation { get; init; }
    public ITransport? Transport { get; set; }

    public EconetMessage WithPayload(byte[] payload) => new()
    {
        PacketType = PacketType,
        EconetPort = EconetPort,
        Control = Control,
        Sequence = Sequence,
        Payload = payload,
        ClientAddress = ClientAddress,
        ClientPort = ClientPort,
        ClientNet = ClientNet,
        ClientStation = ClientStation,
        Transport = Transport,
    };

    /// <summary>
    /// Return a copy of this message carrying a different AUN sequence number. Used to stamp a fresh
    /// server sequence (PiFS get_local_seq analogue) onto outbound FS reply DATA — see
    /// <see cref="RetroIO.Protocols.Econet.Server.Transport.AunOutboundSequence"/>.
    /// </summary>
    public EconetMessage WithSequence(uint sequence) => new()
    {
        PacketType = PacketType,
        EconetPort = EconetPort,
        Control = Control,
        Sequence = sequence,
        Payload = Payload,
        ClientAddress = ClientAddress,
        ClientPort = ClientPort,
        ClientNet = ClientNet,
        ClientStation = ClientStation,
        Transport = Transport,
    };
}
