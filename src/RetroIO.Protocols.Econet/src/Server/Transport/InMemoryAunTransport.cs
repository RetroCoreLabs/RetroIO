//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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

    public string TransportType => "InMemory";

    public TransportCapabilities Capabilities => new()
    {
        SupportsMultipleClients = true,
        MaxConcurrentConnections = int.MaxValue,
        RequiresConnectionState = false,
        IsReliable = true,
        AutoAckInboundData = false,
    };

    public event EconetMessageReceivedHandler? OnMessageReceived;

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

    public ValueTask SendAsync(EconetMessage message) => _outbound.Writer.WriteAsync(message);

    public async Task SendTestMessageAsync(EconetMessage message)
    {
        if (message.ClientPort == 0)
            message = CloneWithClient(message, _virtualClientAddress, _virtualClientPort);
        else
            message = CloneWithClient(message, message.ClientAddress, message.ClientPort);

        await _inbound.Writer.WriteAsync(message);
    }

    public Task<EconetMessage> ReceiveResponseAsync(CancellationToken cancellationToken = default) =>
        _outbound.Reader.ReadAsync(cancellationToken).AsTask();

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
