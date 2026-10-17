//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Protocols.Tnfs.Server;

namespace RetroIO.Protocols.Tnfs.Server.Transport;

/// <summary>
/// Transport abstraction for TNFS protocol.
/// Supports UDP, TCP, and in-memory testing transports.
/// </summary>
public interface ITransport : IDisposable
{
    /// <summary>
    /// Transport identifier (UDP, TCP, InMemory, etc.)
    /// </summary>
    string TransportType { get; }

    /// <summary>
    /// Raised when a message is received from any client.
    /// Server subscribes to this event.
    /// </summary>
    event Func<TnfsMessage, ValueTask>? OnMessageReceived;

    /// <summary>
    /// Start listening for incoming messages.
    /// Runs until cancellation requested.
    /// </summary>
    Task RunAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Send response message back to client.
    /// Transport uses ClientIP/ClientPort/TcpConnectionId from message to route.
    /// </summary>
    ValueTask SendAsync(TnfsMessage message);

    /// <summary>
    /// Transport capabilities
    /// </summary>
    TransportCapabilities Capabilities { get; }
}

/// <summary>
/// Describes capabilities of a transport implementation.
/// </summary>
public record TransportCapabilities
{
    /// <summary>
    /// Can this transport handle multiple concurrent clients?
    /// </summary>
    public bool SupportsMultipleClients { get; init; }

    /// <summary>
    /// Maximum number of concurrent connections supported.
    /// </summary>
    public int MaxConcurrentConnections { get; init; }

    /// <summary>
    /// Does this transport maintain connection state? (TCP=true, UDP=false)
    /// </summary>
    public bool RequiresConnectionState { get; init; }

    /// <summary>
    /// Is this transport reliable? (TCP=true, UDP=false)
    /// </summary>
    public bool IsReliable { get; init; }
}
