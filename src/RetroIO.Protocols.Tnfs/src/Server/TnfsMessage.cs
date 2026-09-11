//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Net;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Server.Core.Utils;

namespace RetroIO.Protocols.Tnfs.Server;

/// <summary>
/// Complete TNFS message (header + data).
/// Uses pooled buffers for zero-allocation message processing.
/// </summary>
public sealed class TnfsMessage : IDisposable
{
    private TnfsBuffer? _buffer;
    private bool _disposed;

    /// <summary>
    /// Message header.
    /// </summary>
    public TnfsHeader Header { get; set; }

    /// <summary>
    /// Message data (after header, before status in request / after status in response).
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; private set; }

    /// <summary>
    /// Total message length (header + data).
    /// </summary>
    public int Length { get; private set; }

    /// <summary>
    /// Client IP address.
    /// </summary>
    public IPAddress ClientIP => Header.ClientIP ?? IPAddress.None;

    /// <summary>
    /// Client port.
    /// </summary>
    public int ClientPort => Header.ClientPort;

    /// <summary>
    /// TCP connection ID (0 if UDP).
    /// </summary>
    public int TcpConnectionId => Header.TcpConnectionId;

    /// <summary>
    /// Is this a TCP message?
    /// </summary>
    public bool IsTcp => TcpConnectionId != 0;

    /// <summary>
    /// Transport that received this message.
    /// Used to send response back via same transport.
    /// </summary>
    public Transport.ITransport? Transport { get; internal set; }

    /// <summary>
    /// Parse TNFS message from received buffer.
    /// </summary>
    public static TnfsMessage ParseRequest(ReadOnlySpan<byte> buffer, IPAddress clientIP, int clientPort, int tcpConnectionId = 0)
    {
        if (buffer.Length < TnfsConstants.TNFS_HEADERSZ)
            throw new ArgumentException($"Buffer too small for TNFS message (need at least {TnfsConstants.TNFS_HEADERSZ} bytes)");

        var header = TnfsHeader.Parse(buffer);
        header.ClientIP = clientIP;
        header.ClientPort = clientPort;
        header.TcpConnectionId = tcpConnectionId;

        var message = new TnfsMessage
        {
            Header = header,
            Length = buffer.Length
        };

        // Data starts after header (no status byte in request)
        if (buffer.Length > TnfsConstants.TNFS_HEADERSZ)
        {
            var dataLength = buffer.Length - TnfsConstants.TNFS_HEADERSZ;
            var dataBuffer = TnfsBuffer.Rent(dataLength);
            buffer.Slice(TnfsConstants.TNFS_HEADERSZ, dataLength).CopyTo(dataBuffer.Span);
            message._buffer = dataBuffer;
            message.Data = dataBuffer.Memory.Slice(0, dataLength);
        }
        else
        {
            message.Data = ReadOnlyMemory<byte>.Empty;
        }

        return message;
    }

    /// <summary>
    /// Create response message from request.
    /// </summary>
    public static TnfsMessage CreateResponse(TnfsMessage request, TnfsReturnCode returnCode, ReadOnlySpan<byte> responseData = default)
    {
        var response = new TnfsMessage
        {
            Header = new TnfsHeader
            {
                SessionId = request.Header.SessionId,
                SequenceNumber = request.Header.SequenceNumber,
                Command = request.Header.Command,
                Status = (byte)returnCode,
                ClientIP = request.Header.ClientIP,
                ClientPort = request.Header.ClientPort,
                TcpConnectionId = request.Header.TcpConnectionId
            }
        };

        if (responseData.Length > 0)
        {
            var buffer = TnfsBuffer.Rent(responseData.Length);
            responseData.CopyTo(buffer.Span);
            response._buffer = buffer;
            response.Data = buffer.Memory.Slice(0, responseData.Length);
        }
        else
        {
            response.Data = ReadOnlyMemory<byte>.Empty;
        }

        response.Length = TnfsConstants.TNFS_HEADERSZ + 1 + responseData.Length;
        return response;
    }

    /// <summary>
    /// Serialize response to buffer for transmission.
    /// Returns number of bytes written.
    /// </summary>
    public int SerializeResponse(Span<byte> outputBuffer)
    {
        // Write header + status (5 bytes)
        int bytesWritten = Header.SerializeResponse(outputBuffer);

        // Write data if present
        if (Data.Length > 0)
        {
            Data.Span.CopyTo(outputBuffer.Slice(bytesWritten));
            bytesWritten += Data.Length;
        }

        return bytesWritten;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _buffer?.Dispose();
        _buffer = null;
        _disposed = true;
    }

    public override string ToString()
    {
        return $"TNFS Message: {Header}, DataLen={Data.Length}, Total={Length} bytes";
    }
}
