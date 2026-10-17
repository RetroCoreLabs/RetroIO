//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Protocols.Tnfs.Server;
using RetroIO.Security.Firewall;
using RetroIO.Security.Implementation.Auditing;

namespace RetroIO.Protocols.Tnfs.Server.Transport;

/// <summary>
/// UDP transport for TNFS protocol.
/// Handles receiving and sending UDP datagrams.
/// </summary>
public sealed class UdpTransport : ITransport
{
    /// <summary>
    /// The bound socket, or null while the transport is stopped.
    /// </summary>
    /// <remarks>
    /// Created and bound by <see cref="RunAsync"/>, released when it returns. It used to be
    /// created in the constructor and never closed until the process exited, so stopping the
    /// server through the management API left port {port} bound: nothing was servicing it, but
    /// no other process could take it either. Binding per run also means a stop followed by a
    /// start actually rebinds, rather than running a disposed socket.
    /// </remarks>
    private Socket? _socket;
    private readonly ILogger<UdpTransport> _logger;
    private readonly ArrayPool<byte> _bufferPool;
    private readonly int _port;
    private readonly FirewallEngine? _firewall;
    private readonly SecurityEventEmitter? _securityEvents;
    private bool _disposed;

    public string TransportType => "UDP";

    public TransportCapabilities Capabilities => new()
    {
        SupportsMultipleClients = true,
        MaxConcurrentConnections = int.MaxValue,  // Connectionless
        RequiresConnectionState = false,
        IsReliable = false
    };

    public event Func<TnfsMessage, ValueTask>? OnMessageReceived;

    public UdpTransport(
        int port,
        ILogger<UdpTransport> logger,
        FirewallEngine? firewall = null,
        SecurityEventEmitter? securityEvents = null)
    {
        _port = port;
        _logger = logger;
        _firewall = firewall;
        _securityEvents = securityEvents;
        _bufferPool = ArrayPool<byte>.Shared;
    }

    /// <summary>Creates and binds the socket this run will use.</summary>
    /// <returns>The bound socket.</returns>
    private Socket CreateAndBind()
    {
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, _port));

        _logger.LogInformation("UDP transport listening on port {Port}", _port);
        return socket;
    }

    /// <summary>
    /// Start receiving UDP messages.
    /// Runs until cancellation requested.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Socket socket = CreateAndBind();
        _socket = socket;

        _logger.LogInformation("UDP transport started");

        try
        {
        while (!cancellationToken.IsCancellationRequested)
        {
            var buffer = _bufferPool.Rent(TnfsConstants.MAXMSGSZ);
            try
            {
                // Receive datagram
                var remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
                EndPoint endPoint = remoteEndPoint;

                var result = await socket.ReceiveFromAsync(
                    buffer.AsMemory(0, TnfsConstants.MAXMSGSZ),
                    SocketFlags.None,
                    endPoint,
                    cancellationToken);

                if (result.ReceivedBytes < TnfsConstants.TNFS_HEADERSZ)
                {
                    _logger.LogWarning("Received undersized datagram ({Bytes} bytes) from {Remote}",
                        result.ReceivedBytes, result.RemoteEndPoint);
                    continue;
                }

                var clientEP = (IPEndPoint)result.RemoteEndPoint;
                string clientIP = clientEP.Address.ToString();

                // CHECK FIREWALL FIRST (before parsing message)
                if (_firewall != null)
                {
                    var firewallDecision = _firewall.CheckAccess(clientIP);

                    if (!firewallDecision.IsAllowed)
                    {
                        // Emit security event
                        if (_securityEvents != null)
                        {
                            await _securityEvents.EmitFirewallEventAsync(
                                clientIP, false, firewallDecision.Reason);
                        }

                        _logger.LogWarning("Firewall blocked UDP connection from {IP}: {Reason}",
                            clientIP, firewallDecision.Reason);

                        // Drop packet silently (no response to blocked IPs)
                        continue;
                    }
                }

                // Parse message
                var message = TnfsMessage.ParseRequest(
                    buffer.AsSpan(0, result.ReceivedBytes),
                    clientEP.Address,
                    clientEP.Port,
                    tcpConnectionId: 0);

                // Link message to this transport
                message.Transport = this;

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("UDP received {Bytes} bytes from {Remote}: {Message}",
                        result.ReceivedBytes, result.RemoteEndPoint, message);
                }

                // Dispatch to handler
                if (OnMessageReceived != null)
                {
                    await OnMessageReceived(message);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error receiving UDP message");
            }
            finally
            {
                _bufferPool.Return(buffer);
            }
        }
        }
        finally
        {
            // Releases the port. Without this the socket outlived every stop, so an operator
            // restarting the service quickly could not rebind until the process itself exited.
            _socket = null;
            socket.Dispose();
            _logger.LogInformation("UDP transport stopped, port {Port} released", _port);
        }
    }

    /// <summary>
    /// Send response message via UDP.
    /// </summary>
    public async ValueTask SendAsync(TnfsMessage message)
    {
        var buffer = _bufferPool.Rent(TnfsConstants.MAXMSGSZ);
        try
        {
            // Serialize response
            int length = message.SerializeResponse(buffer);

            // The socket is gone if the server stopped between receiving a request and
            // answering it; dropping the reply is correct, and UDP callers already retry.
            Socket? socket = _socket;
            if (socket == null) return;

            // Send datagram
            var endPoint = new IPEndPoint(message.ClientIP, message.ClientPort);
            await socket.SendToAsync(
                buffer.AsMemory(0, length),
                SocketFlags.None,
                endPoint);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("UDP sent {Bytes} bytes to {Remote}: SID=0x{SessionId:X4}, Cmd=0x{Command:X2}, Status=0x{Status:X2}",
                    length, endPoint, message.Header.SessionId, message.Header.Command, message.Header.Status);
            }
        }
        finally
        {
            _bufferPool.Return(buffer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _socket?.Dispose();
        _disposed = true;
    }
}
