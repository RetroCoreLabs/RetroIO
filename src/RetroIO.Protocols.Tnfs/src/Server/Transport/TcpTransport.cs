//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Protocols.Tnfs.Server;
using RetroIO.Security.Firewall;
using RetroIO.Security.Implementation.Auditing;

namespace RetroIO.Protocols.Tnfs.Server.Transport;

/// <summary>
/// TCP transport for TNFS protocol.
/// Handles accepting connections and managing TCP streams.
/// </summary>
public sealed class TcpTransport : ITransport
{
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<int, TcpConnection> _connections;
    private readonly ILogger<TcpTransport> _logger;
    private readonly ArrayPool<byte> _bufferPool;
    private readonly int _port;
    private readonly FirewallEngine? _firewall;
    private readonly SecurityEventEmitter? _securityEvents;
    private int _nextConnectionId = 0;
    private bool _disposed;

    public string TransportType => "TCP";

    public TransportCapabilities Capabilities => new()
    {
        SupportsMultipleClients = true,
        MaxConcurrentConnections = 1000,  // Configurable limit
        RequiresConnectionState = true,
        IsReliable = true
    };

    public event Func<TnfsMessage, ValueTask>? OnMessageReceived;

    public TcpTransport(
        int port,
        ILogger<TcpTransport> logger,
        FirewallEngine? firewall = null,
        SecurityEventEmitter? securityEvents = null)
    {
        _port = port;
        _logger = logger;
        _firewall = firewall;
        _securityEvents = securityEvents;
        _bufferPool = ArrayPool<byte>.Shared;
        _connections = new ConcurrentDictionary<int, TcpConnection>();

        // Create TCP listener
        _listener = new TcpListener(IPAddress.Any, port);

        _logger.LogInformation("TCP transport created for port {Port}", port);
    }

    /// <summary>
    /// Start accepting TCP connections.
    /// Runs until cancellation requested.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        _logger.LogInformation("TCP transport listening on port {Port}", _port);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync(cancellationToken);
                    var remoteEP = (IPEndPoint?)tcpClient.Client.RemoteEndPoint;
                    string? clientIP = remoteEP?.Address.ToString();

                    // CHECK FIREWALL FIRST (before accepting connection)
                    if (_firewall != null && clientIP != null)
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

                            _logger.LogWarning("Firewall blocked TCP connection from {IP}: {Reason}",
                                clientIP, firewallDecision.Reason);

                            // Close connection immediately
                            tcpClient.Close();
                            continue;
                        }
                    }

                    var connectionId = Interlocked.Increment(ref _nextConnectionId);

                    var connection = new TcpConnection(connectionId, tcpClient);
                    _connections.TryAdd(connectionId, connection);

                    _logger.LogInformation("TCP connection {Id} from {Remote}",
                        connectionId, tcpClient.Client.RemoteEndPoint);

                    // Handle connection in background
                    _ = HandleConnectionAsync(connection, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error accepting TCP connection");
                }
            }
        }
        finally
        {
            _listener.Stop();
            _logger.LogInformation("TCP transport stopped");
        }
    }

    private async Task HandleConnectionAsync(
        TcpConnection connection,
        CancellationToken ct)
    {
        var buffer = _bufferPool.Rent(TnfsConstants.MAXMSGSZ + 2);
        try
        {
            var stream = connection.Client.GetStream();

            while (!ct.IsCancellationRequested && connection.Client.Connected)
            {
                // Read message length (2 bytes, little-endian)
                int lengthBytesRead = await stream.ReadAsync(buffer.AsMemory(0, 2), ct);
                if (lengthBytesRead < 2)
                {
                    _logger.LogDebug("TCP connection {Id} closed by client", connection.Id);
                    break;
                }

                ushort msgLength = BitConverter.ToUInt16(buffer, 0);

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("TCP connection {Id} received length prefix: {Length} bytes",
                        connection.Id, msgLength);
                }

                if (msgLength == 0)
                {
                    _logger.LogWarning("TCP connection {Id} sent zero-length message (protocol error)", connection.Id);
                    break;
                }

                if (msgLength < TnfsConstants.TNFS_HEADERSZ)
                {
                    _logger.LogWarning("TCP connection {Id} sent message too small ({Length} bytes, need at least {MinSize})",
                        connection.Id, msgLength, TnfsConstants.TNFS_HEADERSZ);
                    break;
                }

                if (msgLength > TnfsConstants.MAXMSGSZ)
                {
                    _logger.LogWarning("TCP connection {Id} sent oversized message ({Length} bytes)",
                        connection.Id, msgLength);
                    break;
                }

                // Read message body
                int totalRead = 0;
                while (totalRead < msgLength)
                {
                    int bytesRead = await stream.ReadAsync(
                        buffer.AsMemory(totalRead, msgLength - totalRead), ct);

                    if (bytesRead == 0)
                    {
                        _logger.LogWarning("TCP connection {Id} closed mid-message", connection.Id);
                        return;
                    }

                    totalRead += bytesRead;
                }

                // Parse message
                var message = TnfsMessage.ParseRequest(
                    buffer.AsSpan(0, msgLength),
                    connection.RemoteIP,
                    connection.RemotePort,
                    tcpConnectionId: connection.Id);

                // Link message to this transport
                message.Transport = this;

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("TCP connection {Id} received {Bytes} bytes: {Message}",
                        connection.Id, msgLength, message);
                }

                // Dispatch to handler
                if (OnMessageReceived != null)
                {
                    await OnMessageReceived(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("TCP connection {Id} cancelled", connection.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TCP connection {Id} error", connection.Id);
        }
        finally
        {
            _bufferPool.Return(buffer);
            _connections.TryRemove(connection.Id, out _);
            connection.Dispose();
            _logger.LogInformation("TCP connection {Id} closed", connection.Id);
        }
    }

    /// <summary>
    /// Send response message via TCP.
    /// </summary>
    public async ValueTask SendAsync(TnfsMessage message)
    {
        // TCP sends via existing connection
        if (_connections.TryGetValue(message.TcpConnectionId, out var connection))
        {
            var buffer = _bufferPool.Rent(TnfsConstants.MAXMSGSZ + 2);
            try
            {
                // Serialize message (without length prefix)
                int length = message.SerializeResponse(buffer.AsSpan(2));

                // Prepend message length (2 bytes, little-endian)
                BitConverter.TryWriteBytes(buffer.AsSpan(0, 2), (ushort)length);

                // Send: length(2) + message
                await connection.Client.GetStream()
                    .WriteAsync(buffer.AsMemory(0, length + 2));

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("TCP sent {Bytes} bytes to connection {Id}: SID=0x{SessionId:X4}, Cmd=0x{Command:X2}, Status=0x{Status:X2}",
                        length, connection.Id, message.Header.SessionId, message.Header.Command, message.Header.Status);
                }
            }
            finally
            {
                _bufferPool.Return(buffer);
            }
        }
        else
        {
            _logger.LogWarning("TCP connection {Id} not found for response", message.TcpConnectionId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _listener?.Stop();

        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }

        _connections.Clear();
        _disposed = true;
    }
}

/// <summary>
/// Represents a single TCP connection.
/// </summary>
internal sealed class TcpConnection : IDisposable
{
    public int Id { get; }
    public TcpClient Client { get; }
    public IPAddress RemoteIP { get; }
    public int RemotePort { get; }

    public TcpConnection(int id, TcpClient client)
    {
        Id = id;
        Client = client;
        var ep = (IPEndPoint)client.Client.RemoteEndPoint!;
        RemoteIP = ep.Address;
        RemotePort = ep.Port;
    }

    public void Dispose()
    {
        Client?.Dispose();
    }
}
