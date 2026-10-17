//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.Server.Transport;

/// <summary>
/// Transport abstraction for AUN datagrams.
/// </summary>
public interface ITransport : IDisposable
{
    /// <summary>
    /// Gets the name of the transport type.
    /// </summary>
    string TransportType { get; }

    /// <summary>
    /// Occurs when the transport receives a message.
    /// </summary>
    event EconetMessageReceivedHandler? OnMessageReceived;

    /// <summary>
    /// Runs the transport until the token is cancelled.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token that stops the transport.
    /// </param>
    /// <returns>
    /// A task that completes when the transport has stopped.
    /// </returns>
    Task RunAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends a message to its client.
    /// </summary>
    /// <param name="message">
    /// The message to send.
    /// </param>
    /// <returns>
    /// A task that completes when the message has been sent.
    /// </returns>
    ValueTask SendAsync(EconetMessage message);

    /// <summary>
    /// Gets what the transport can do.
    /// </summary>
    TransportCapabilities Capabilities { get; }
}

/// <summary>
/// Describes what a transport can do.
/// </summary>
public record TransportCapabilities
{
    /// <summary>
    /// Gets a value indicating whether the transport serves more than one client.
    /// </summary>
    public bool SupportsMultipleClients { get; init; }
    /// <summary>
    /// Gets the largest number of clients at the same time.
    /// </summary>
    public int MaxConcurrentConnections { get; init; }
    /// <summary>
    /// Gets a value indicating whether the transport keeps a connection for each client.
    /// </summary>
    public bool RequiresConnectionState { get; init; }
    /// <summary>
    /// Gets a value indicating whether the transport delivers messages reliably.
    /// </summary>
    public bool IsReliable { get; init; }
    /// <summary>
    /// Gets a value indicating whether the server must acknowledge inbound data packets itself.
    /// </summary>
    public bool AutoAckInboundData { get; init; }
}
