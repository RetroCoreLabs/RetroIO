//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Protocols.Econet.Server.Transport;

/// <summary>
/// Transport abstraction for AUN datagrams.
/// </summary>
public interface ITransport : IDisposable
{
    string TransportType { get; }

    event EconetMessageReceivedHandler? OnMessageReceived;

    Task RunAsync(CancellationToken cancellationToken);

    ValueTask SendAsync(EconetMessage message);

    TransportCapabilities Capabilities { get; }
}

public record TransportCapabilities
{
    public bool SupportsMultipleClients { get; init; }
    public int MaxConcurrentConnections { get; init; }
    public bool RequiresConnectionState { get; init; }
    public bool IsReliable { get; init; }
    public bool AutoAckInboundData { get; init; }
}
