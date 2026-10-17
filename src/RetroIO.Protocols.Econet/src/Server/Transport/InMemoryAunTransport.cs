//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Net;
using System.Threading.Channels;

namespace RetroIO.Protocols.Econet.Server.Transport;

/// <summary>
/// In-memory transport for unit tests.
/// </summary>
public sealed class InMemoryAunTransport : ITransport
{
    private readonly Channel<EconetMessage> _inbound = Channel.CreateUnbounded<EconetMessage>();
    private readonly Channel<EconetMessage> _outbound = Channel.CreateUnbounded<EconetMessage>();
    private readonly IPAddress _virtualClientAddress;
    private readonly int _virtualClientPort;
    private readonly byte _virtualClientNet;
    private readonly byte _virtualClientStation;

    /// <summary>
    /// Creates the transport with the address of the virtual client.
    /// </summary>
    /// <param name="virtualClientAddress">
    /// The IP address given to the virtual client. When null, the loopback address is used.
    /// </param>
    /// <param name="virtualClientPort">
    /// The port given to the virtual client.
    /// </param>
    /// <param name="virtualClientNet">
    /// The network number given to the virtual client.
    /// </param>
    /// <param name="virtualClientStation">
    /// The station number given to the virtual client.
    /// </param>
    public InMemoryAunTransport(
        IPAddress? virtualClientAddress = null,
        int virtualClientPort = 50000,
        byte virtualClientNet = 1,
        byte virtualClientStation = 1)
    {
        _virtualClientAddress = virtualClientAddress ?? IPAddress.Loopback;
        _virtualClientPort = virtualClientPort;
        _virtualClientNet = virtualClientNet;
        _virtualClientStation = virtualClientStation;
    }

    /// <inheritdoc/>
    public string TransportType => "InMemory";

    /// <inheritdoc/>
    public TransportCapabilities Capabilities => new()
    {
        SupportsMultipleClients = true,
        MaxConcurrentConnections = int.MaxValue,
        RequiresConnectionState = false,
        IsReliable = true,
        AutoAckInboundData = false,
    };

    /// <inheritdoc/>
    public event EconetMessageReceivedHandler? OnMessageReceived;

    /// <inheritdoc/>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ValueTask<EconetMessage> readTask = _inbound.Reader.ReadAsync(cancellationToken);
            EconetMessage message = await readTask;
            EconetMessage routed = CloneWithClient(message, message.ClientAddress, message.ClientPort);

            routed.Transport = this;
            EconetMessageReceivedHandler? handler = OnMessageReceived;
            if (handler != null)
                await handler(routed);
        }
    }

    /// <inheritdoc/>
    public ValueTask SendAsync(EconetMessage message) => _outbound.Writer.WriteAsync(message);

    /// <summary>
    /// Puts a message in the inbound queue as if the virtual client had sent it. A message with client port 0 gets the address and port of the virtual client.
    /// </summary>
    /// <param name="message">
    /// The message to deliver.
    /// </param>
    /// <returns>
    /// A task that completes when the message is queued.
    /// </returns>
    public async Task SendTestMessageAsync(EconetMessage message)
    {
        if (message.ClientPort == 0)
            message = CloneWithClient(message, _virtualClientAddress, _virtualClientPort);
        else
            message = CloneWithClient(message, message.ClientAddress, message.ClientPort);

        await _inbound.Writer.WriteAsync(message);
    }

    /// <summary>
    /// Waits for the next message the server sent.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token that cancels the wait.
    /// </param>
    /// <returns>
    /// The next sent message.
    /// </returns>
    public Task<EconetMessage> ReceiveResponseAsync(CancellationToken cancellationToken = default) =>
        _outbound.Reader.ReadAsync(cancellationToken).AsTask();

    /// <summary>
    /// Completes the inbound and outbound queues.
    /// </summary>
    public void Dispose()
    {
        _inbound.Writer.Complete();
        _outbound.Writer.Complete();
    }

    private EconetMessage CloneWithClient(EconetMessage message, IPAddress address, int port)
    {
        return new EconetMessage
        {
            PacketType = message.PacketType,
            EconetPort = message.EconetPort,
            Control = message.Control,
            Sequence = message.Sequence,
            Payload = message.Payload,
            ClientAddress = address,
            ClientPort = port,
            ClientNet = _virtualClientNet,
            ClientStation = _virtualClientStation,
            Transport = message.Transport,
        };
    }
}
