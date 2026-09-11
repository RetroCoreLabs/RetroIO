//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Security.Firewall;
using RetroIO.Security.Implementation.Auditing;

namespace RetroIO.Protocols.Econet.Server.Transport;

/// <summary>
/// UDP transport for AUN datagrams.
/// </summary>
public sealed class UdpAunTransport : ITransport
{
    private const int MaxDatagramSize = 2048;

    /// <summary>Port this transport binds, kept so each run can rebind it.</summary>
    private readonly int _port;

    /// <summary>
    /// The bound socket, or null while the transport is stopped.
    /// </summary>
    /// <remarks>
    /// Same change as TNFS's UdpTransport, for the same reason: it used to be created in the
    /// constructor and never closed until the process exited, so stopping the server through the
    /// management API left the AUN port bound with nothing servicing it.
    /// </remarks>
    private Socket? _socket;
    private readonly ILogger<UdpAunTransport> _logger;
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;
    private readonly AunAddressMapper _addressMapper;
    private readonly FirewallEngine? _firewall;
    private readonly SecurityEventEmitter? _securityEvents;
    private bool _disposed;

    public UdpAunTransport(
        int port,
        AunAddressMapper addressMapper,
        ILogger<UdpAunTransport> logger,
        FirewallEngine? firewall = null,
        SecurityEventEmitter? securityEvents = null)
    {
        _addressMapper = addressMapper;
        _logger = logger;
        _firewall = firewall;
        _securityEvents = securityEvents;

        _port = port;
    }

    /// <summary>Creates and binds the socket this run will use.</summary>
    /// <returns>The bound socket.</returns>
    private Socket CreateAndBind()
    {
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, _port));

        _logger.LogInformation("Econet AUN UDP transport listening on port {Port}", _port);
        return socket;
    }

    public string TransportType => "UDP";

    public TransportCapabilities Capabilities => new()
    {
        SupportsMultipleClients = true,
        MaxConcurrentConnections = int.MaxValue,
        RequiresConnectionState = false,
        IsReliable = false,
        AutoAckInboundData = true,
    };

    public event EconetMessageReceivedHandler? OnMessageReceived;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Socket socket = CreateAndBind();
        _socket = socket;

        _logger.LogInformation("Econet AUN UDP transport started");

        try
        {
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[] buffer = _bufferPool.Rent(MaxDatagramSize);
            try
            {
                IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
                EndPoint endPoint = remoteEndPoint;

                SocketReceiveFromResult result = await socket.ReceiveFromAsync(
                    buffer.AsMemory(0, MaxDatagramSize),
                    SocketFlags.None,
                    endPoint,
                    cancellationToken);

                if (result.ReceivedBytes < AunPacket.HeaderSize)
                {
                    _logger.LogWarning(
                        "Ignored undersized AUN datagram ({Bytes} bytes) from {Remote}",
                        result.ReceivedBytes,
                        result.RemoteEndPoint);
                    continue;
                }

                IPEndPoint clientEndPoint = (IPEndPoint)result.RemoteEndPoint;
                string clientIp = clientEndPoint.Address.ToString();

                if (_firewall != null)
                {
                    FirewallDecision decision = _firewall.CheckAccess(clientIp);
                    if (!decision.IsAllowed)
                    {
                        if (_securityEvents != null)
                            await _securityEvents.EmitFirewallEventAsync(clientIp, false, decision.Reason);

                        _logger.LogWarning("Firewall blocked AUN datagram from {IP}: {Reason}", clientIp, decision.Reason);
                        continue;
                    }
                }

                ReadOnlySpan<byte> datagram = buffer.AsSpan(0, result.ReceivedBytes);
                if (!AunPacket.TryDecode(
                        datagram,
                        out AunPacketType type,
                        out byte port,
                        out byte control,
                        out uint sequence,
                        out int payloadOffset))
                {
                    continue;
                }

                int payloadLength = result.ReceivedBytes - payloadOffset;
                byte[] payload = new byte[payloadLength];
                datagram.Slice(payloadOffset, payloadLength).CopyTo(payload);

                byte clientNet = 0;
                byte clientStation = 0;
                if (!_addressMapper.TryResolve(clientEndPoint.Address, out clientNet, out clientStation))
                {
                    _logger.LogWarning(
                        "Ignored AUN datagram from non-IPv4 client {Remote}",
                        clientEndPoint);
                    continue;
                }

                EconetMessage message = new EconetMessage
                {
                    PacketType = type,
                    EconetPort = port,
                    Control = control,
                    Sequence = sequence,
                    Payload = payload,
                    ClientAddress = clientEndPoint.Address,
                    ClientPort = clientEndPoint.Port,
                    ClientNet = clientNet,
                    ClientStation = clientStation,
                    Transport = this,
                };

                EconetMessageReceivedHandler? handler = OnMessageReceived;
                if (handler != null)
                    await handler(message);
            }
            finally
            {
                _bufferPool.Return(buffer);
            }
        }
        }
        finally
        {
            _socket = null;
            socket.Dispose();
            _logger.LogInformation("Econet AUN UDP transport stopped, port {Port} released", _port);
        }
    }

    public async ValueTask SendAsync(EconetMessage message)
    {
        byte[] buffer = new byte[AunPacket.HeaderSize + message.Payload.Length];
        AunPacket.Encode(
            buffer,
            message.PacketType,
            message.EconetPort,
            message.Control,
            message.Sequence,
            message.Payload);

        // Gone if the server stopped between receiving a request and answering it. Dropping the
        // reply is correct: an AUN client retries.
        Socket? socket = _socket;
        if (socket == null) return;

        IPEndPoint endPoint = new IPEndPoint(message.ClientAddress, message.ClientPort);
        await socket.SendToAsync(buffer, SocketFlags.None, endPoint);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _socket?.Dispose();
    }
}
