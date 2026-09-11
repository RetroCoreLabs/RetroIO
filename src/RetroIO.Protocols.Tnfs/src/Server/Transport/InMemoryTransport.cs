//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Net;
using System.Threading.Channels;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Protocols.Tnfs.Server;

namespace RetroIO.Protocols.Tnfs.Server.Transport;

/// <summary>
/// In-memory transport for unit testing.
/// Provides bidirectional channels for fast testing without network overhead.
/// </summary>
public sealed class InMemoryTransport : ITransport
{
    private readonly Channel<TnfsMessage> _serverInbound;
    private readonly Channel<TnfsMessage> _serverOutbound;
    private readonly IPAddress _virtualClientIP;
    private readonly int _virtualClientPort;

    public string TransportType => "InMemory";

    public TransportCapabilities Capabilities => new()
    {
        SupportsMultipleClients = true,
        MaxConcurrentConnections = int.MaxValue,
        RequiresConnectionState = false,
        IsReliable = true
    };

    public event Func<TnfsMessage, ValueTask>? OnMessageReceived;

    public InMemoryTransport(IPAddress? virtualClientIP = null, int virtualClientPort = 50000)
    {
        _serverInbound = Channel.CreateUnbounded<TnfsMessage>();
        _serverOutbound = Channel.CreateUnbounded<TnfsMessage>();
        _virtualClientIP = virtualClientIP ?? IPAddress.Loopback;
        _virtualClientPort = virtualClientPort;
    }

    /// <summary>
    /// Process messages from test client.
    /// Runs until cancellation requested.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var message in _serverInbound.Reader.ReadAllAsync(cancellationToken))
        {
            // Link message to this transport
            message.Transport = this;

            if (OnMessageReceived != null)
            {
                await OnMessageReceived(message);
            }
        }
    }

    /// <summary>
    /// Send response back to test client.
    /// </summary>
    public async ValueTask SendAsync(TnfsMessage message)
    {
        await _serverOutbound.Writer.WriteAsync(message);
    }

    // Test helper methods

    /// <summary>
    /// Test client sends message to server.
    /// </summary>
    public async Task SendTestMessageAsync(TnfsMessage message)
    {
        // Ensure message has client info
        if (message.Header.ClientIP == null)
        {
            var header = message.Header;
            header.ClientIP = _virtualClientIP;
            header.ClientPort = _virtualClientPort;
            message.Header = header;
        }

        await _serverInbound.Writer.WriteAsync(message);
    }

    /// <summary>
    /// Test client receives response from server.
    /// </summary>
    public async Task<TnfsMessage> ReceiveResponseAsync(CancellationToken cancellationToken = default)
    {
        return await _serverOutbound.Reader.ReadAsync(cancellationToken);
    }

    /// <summary>
    /// Test client receives response with timeout.
    /// </summary>
    public async Task<TnfsMessage> ReceiveResponseAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await _serverOutbound.Reader.ReadAsync(cts.Token);
    }

    /// <summary>
    /// Try to receive response without blocking.
    /// </summary>
    public bool TryReceiveResponse(out TnfsMessage? message)
    {
        return _serverOutbound.Reader.TryRead(out message);
    }

    public void Dispose()
    {
        _serverInbound.Writer.Complete();
        _serverOutbound.Writer.Complete();
    }
}
