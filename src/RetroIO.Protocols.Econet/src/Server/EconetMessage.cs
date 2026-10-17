//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
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
    /// <summary>
    /// Gets the AUN packet type.
    /// </summary>
    public required AunPacketType PacketType { get; init; }
    /// <summary>
    /// Gets the Econet port.
    /// </summary>
    public required byte EconetPort { get; init; }
    /// <summary>
    /// Gets the control byte.
    /// </summary>
    public required byte Control { get; init; }
    /// <summary>
    /// Gets the AUN sequence number.
    /// </summary>
    public required uint Sequence { get; init; }
    /// <summary>
    /// Gets the payload bytes.
    /// </summary>
    public required byte[] Payload { get; init; }
    /// <summary>
    /// Gets the IP address of the client.
    /// </summary>
    public required IPAddress ClientAddress { get; init; }
    /// <summary>
    /// Gets the UDP port of the client.
    /// </summary>
    public required int ClientPort { get; init; }
    /// <summary>
    /// Gets the Econet network number of the client.
    /// </summary>
    public byte ClientNet { get; init; }
    /// <summary>
    /// Gets the Econet station number of the client.
    /// </summary>
    public byte ClientStation { get; init; }
    /// <summary>
    /// Gets or sets the transport the message arrived on or is sent through.
    /// </summary>
    public ITransport? Transport { get; set; }

    /// <summary>
    /// Returns a copy of the message with a different payload.
    /// </summary>
    /// <param name="payload">
    /// The payload of the copy.
    /// </param>
    /// <returns>
    /// The new message.
    /// </returns>
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
