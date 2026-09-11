//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroIO.Protocols.Tnfs.Protocol;

namespace RetroIO.Protocols.Tnfs.Client;


/// <summary>
/// Low-level TNFS protocol client.
/// Implements TNFS 1.0 UDP protocol for communication with TNFS servers.
/// </summary>
/// <remarks>
/// TNFS (Trivial Network File System) is a simple network protocol designed
/// for retro computers. It uses UDP for transport and provides basic file
/// system operations similar to NFS but with much lower overhead.
///
/// Protocol details: https://github.com/FujiNetWIFI/fujinet-platformio/wiki/SIO-Command-$DC-Open-TNFS-Connection
/// </remarks>
public sealed class TnfsClient : IDisposable
{
    private readonly string _server;
    private readonly int _port;
    private readonly bool _useTcp;
    private readonly ILogger<TnfsClient> _logger;
    private UdpClient? _udpClient;
    private TcpClient? _tcpClient;
    private NetworkStream? _tcpStream;
    private ushort _sessionId;
    private byte _sequenceNumber;
    private bool _isMounted;
    private bool _disposed;
    private bool _isConnected;
    private readonly Dictionary<byte, string> _dirHandlePaths = new Dictionary<byte, string>();

    /// <summary>
    /// Initializes a new TNFS client.
    /// </summary>
    /// <param name="server">TNFS server hostname or IP address</param>
    /// <param name="port">TNFS server port (default 16384)</param>
    /// <param name="useTcp">Use TCP instead of UDP (default true for debugging)</param>
    /// <param name="logger">Logger instance (optional)</param>
    public TnfsClient(string server, int port = TnfsConstants.DEFAULT_PORT, bool useTcp = true, ILogger<TnfsClient>? logger = null)
    {
        if (string.IsNullOrEmpty(server))
        {
            throw new ArgumentException("Server cannot be null or empty.", nameof(server));
        }

        if (port <= 0 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        }

        _server = server;
        _port = port;
        _logger = logger ?? NullLogger<TnfsClient>.Instance;
        _useTcp = useTcp;
        _sessionId = 0;
        _sequenceNumber = 0;
        _isMounted = false;
        _isConnected = false;
    }

    /// <summary>
    /// Is the client currently connected and mounted?
    /// </summary>
    public bool IsMounted => _isMounted && _isConnected;

    /// <summary>
    /// Is the underlying network connection active?
    /// </summary>
    public bool IsConnected => _isConnected;

    /// <summary>
    /// Is using TCP transport?
    /// </summary>
    public bool UseTcp => _useTcp;

    /// <summary>
    /// Current session ID.
    /// </summary>
    public ushort SessionId => _sessionId;

    /// <summary>
    /// Establish network connection to TNFS server.
    /// </summary>
    private bool Connect()
    {
        try
        {
            // Check if already connected
            if (_useTcp && _tcpClient != null && _tcpClient.Connected)
            {
                return true;
            }

            if (!_useTcp && _udpClient != null)
            {
                return true;
            }

            // Close existing connection if any
            CloseConnection();

            // Create new connection
            if (_useTcp)
            {
                _tcpClient = new TcpClient();
                _tcpClient.ReceiveTimeout = TnfsConstants.DefaultTimeout;
                _tcpClient.SendTimeout = TnfsConstants.DefaultTimeout;
                _tcpClient.Connect(_server, _port);
                _tcpStream = _tcpClient.GetStream();
                _logger.LogInformation("Connected via TCP to {Server}:{Port}", _server, _port);
            }
            else
            {
                _udpClient = new UdpClient();
                _udpClient.Connect(_server, _port);
                _udpClient.Client.ReceiveTimeout = TnfsConstants.DefaultTimeout;
                _logger.LogInformation("Connected via UDP to {Server}:{Port}", _server, _port);
            }

            _isConnected = true;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connection failed to {Server}:{Port}", _server, _port);
            _isConnected = false;
            return false;
        }
    }

    /// <summary>
    /// Close network connection.
    /// </summary>
    private void CloseConnection()
    {
        _tcpStream?.Dispose();
        _tcpStream = null;
        _tcpClient?.Dispose();
        _tcpClient = null;
        _udpClient?.Dispose();
        _udpClient = null;
        _isConnected = false;
    }

    /// <summary>
    /// Ensure connection is active, reconnect if needed.
    /// </summary>
    private bool EnsureConnected()
    {
        if (_isConnected)
        {
            // Verify TCP connection is still alive
            if (_useTcp && _tcpClient != null)
            {
                if (!_tcpClient.Connected)
                {
                    _logger.LogWarning("TCP connection lost, reconnecting to {Server}:{Port}", _server, _port);
                    _isConnected = false;
                }
            }
        }

        if (!_isConnected)
        {
            return Connect();
        }

        return true;
    }

    /// <summary>
    /// Mount the TNFS filesystem.
    /// </summary>
    /// <param name="mountPath">Mount path on server (usually "/")</param>
    /// <param name="username">Username for authentication (optional)</param>
    /// <param name="password">Password for authentication (optional)</param>
    /// <returns>Result with success/error information</returns>
    public TnfsResult<bool> Mount(string mountPath = "/", string? username = null, string? password = null)
    {
        if (_isMounted)
        {
            return TnfsResult<bool>.CreateError(TnfsReturnCode.EINVAL, "Already mounted");
        }

        try
        {
            // Establish connection
            if (!Connect())
            {
                return TnfsResult<bool>.CreateError(TnfsReturnCode.EIO, "Failed to connect to server");
            }

            // Build mount packet
            byte[] packet = BuildMountPacket(mountPath, username, password);

            // Send mount request with retries
            byte[]? response = SendWithRetry(packet);
            if (response == null)
            {
                CloseConnection();
                return TnfsResult<bool>.CreateError(TnfsReturnCode.EIO, "No response from server");
            }

            // Parse response
            if (response.Length < 5)
            {
                CloseConnection();
                return TnfsResult<bool>.CreateError(TnfsReturnCode.EIO, "Invalid response length");
            }

            TnfsReturnCode returnCode = (TnfsReturnCode)response[4];
            if (returnCode != TnfsReturnCode.SUCCESS)
            {
                CloseConnection();
                return TnfsResult<bool>.CreateError(returnCode, $"Mount failed: {returnCode}");
            }

            // Extract session ID from response (little-endian)
            _sessionId = TnfsEndian.ReadUInt16(response, 0);
            _sequenceNumber = 0;
            _isMounted = true;

            return TnfsResult<bool>.CreateSuccess(true);
        }
        catch (SocketException ex)
        {
            CloseConnection();
            return TnfsResult<bool>.CreateError(TnfsReturnCode.EIO, $"Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            CloseConnection();
            return TnfsResult<bool>.CreateError(TnfsReturnCode.EIO, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Unmount the TNFS filesystem.
    /// </summary>
    public TnfsResult<bool> Unmount()
    {
        if (!_isMounted)
        {
            return TnfsResult<bool>.CreateSuccess(true);
        }

        try
        {
            byte[] packet = BuildPacket(TnfsCommand.Unmount);
            SendWithRetry(packet);

            _isMounted = false;
            _sessionId = 0;
            _sequenceNumber = 0;

            // Close the connection
            CloseConnection();

            return TnfsResult<bool>.CreateSuccess(true);
        }
        catch (Exception ex)
        {
            CloseConnection();
            return TnfsResult<bool>.CreateError(TnfsReturnCode.EIO, $"Unmount error: {ex.Message}");
        }
    }

    /// <summary>
    /// Open directory for reading.
    /// Uses OPENDIRX with default options for compatibility with TNFS_DIR_EXT servers.
    /// </summary>
    /// <param name="path">Directory path</param>
    /// <returns>Result with directory handle (single byte 0-15)</returns>
    public TnfsResult<byte> OpenDir(string path)
    {
        if (!_isMounted)
        {
            return TnfsResult<byte>.CreateError(TnfsReturnCode.EINVAL, "Not mounted");
        }

        if (!EnsureConnected())
        {
            return TnfsResult<byte>.CreateError(TnfsReturnCode.EIO, "Connection failed");
        }

        try
        {
            // Try standard OPENDIR first (simpler, more compatible)
            byte[] pathBytes = Encoding.ASCII.GetBytes(path + '\0');
            byte[] packet = BuildPacket(TnfsCommand.OpenDir, pathBytes);
            byte[]? response = SendWithRetry(packet);

            if (response == null || response.Length < 6)
            {
                return TnfsResult<byte>.CreateError(TnfsReturnCode.EIO, "Invalid response");
            }

            _logger.LogDebug("OpenDir response: {Response}", BitConverter.ToString(response));

            TnfsReturnCode returnCode = (TnfsReturnCode)response[4];
            if (returnCode != TnfsReturnCode.SUCCESS)
            {
                return TnfsResult<byte>.CreateError(returnCode, $"OpenDir failed: {returnCode}");
            }

            byte handle = response[5];
            _logger.LogDebug("OpenDir handle: {Handle}", handle);

            // Store directory path for lazy loading
            _dirHandlePaths[handle] = path.TrimEnd('/');

            return TnfsResult<byte>.CreateSuccess(handle);
        }
        catch (Exception ex)
        {
            return TnfsResult<byte>.CreateError(TnfsReturnCode.EIO, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Read directory entry using standard READDIR command.
    /// Works with directories opened with OPENDIR or OPENDIRX.
    /// </summary>
    /// <param name="handle">Directory handle from OpenDir (single byte)</param>
    /// <returns>Result with directory entry or null at end</returns>
    public TnfsResult<TnfsDirectoryEntry?> ReadDir(byte handle)
    {
        if (!_isMounted)
        {
            return TnfsResult<TnfsDirectoryEntry?>.CreateError(TnfsReturnCode.EINVAL, "Not mounted");
        }

        if (!EnsureConnected())
        {
            return TnfsResult<TnfsDirectoryEntry?>.CreateError(TnfsReturnCode.EIO, "Connection failed");
        }

        try
        {
            // Try extended READDIRX (0x18) first to get file sizes and metadata
            byte[] handleBytes = new byte[1] { handle };
            _logger.LogDebug("ReadDir sending handle: {Handle}", handle);

            byte[] packet = BuildPacket(TnfsCommand.ReadDirX, handleBytes);
            _logger.LogDebug("ReadDir packet: {Packet}", BitConverter.ToString(packet));

            byte[]? response = SendWithRetry(packet);

            if (response == null || response.Length < 5)
            {
                _logger.LogDebug("ReadDir invalid response: length={Length}", response?.Length ?? 0);
                return TnfsResult<TnfsDirectoryEntry?>.CreateError(TnfsReturnCode.EIO, "Invalid response");
            }

            _logger.LogDebug("ReadDir response: {Response}", BitConverter.ToString(response));

            TnfsReturnCode returnCode = (TnfsReturnCode)response[4];
            _logger.LogDebug("ReadDir return code: {ReturnCode}", returnCode);

            if (returnCode == TnfsReturnCode.EOF)
            {
                return TnfsResult<TnfsDirectoryEntry?>.CreateSuccess(null);
            }

            // If READDIRX not supported (EBADF/ENOSYS), fall back to standard READDIR
            if (returnCode == TnfsReturnCode.EBADF || returnCode == TnfsReturnCode.ENOSYS)
            {
                _logger.LogDebug("READDIRX not supported, falling back to READDIR");

                // Retry with standard READDIR (0x11) - no metadata
                packet = BuildPacket(TnfsCommand.ReadDir, handleBytes);
                response = SendWithRetry(packet);

                if (response == null || response.Length < 5)
                {
                    return TnfsResult<TnfsDirectoryEntry?>.CreateError(TnfsReturnCode.EIO, "Invalid response");
                }

                returnCode = (TnfsReturnCode)response[4];

                if (returnCode == TnfsReturnCode.EOF)
                {
                    return TnfsResult<TnfsDirectoryEntry?>.CreateSuccess(null);
                }

                if (returnCode != TnfsReturnCode.SUCCESS)
                {
                    return TnfsResult<TnfsDirectoryEntry?>.CreateError(returnCode, $"ReadDir failed: {returnCode}");
                }

                // Parse standard READDIR response (no size/timestamps)
                TnfsDirectoryEntry? entry = ParseDirectoryEntry(response, 5);

                // Enable lazy loading of metadata via STAT (metadata NOT loaded from READDIR)
                if (entry != null && _dirHandlePaths.TryGetValue(handle, out string? dirPath))
                {
                    string fullPath = string.IsNullOrEmpty(dirPath) || dirPath == "/"
                        ? "/" + entry.Name
                        : dirPath + "/" + entry.Name;

                    entry.EnableLazyLoad(fullPath, path =>
                    {
                        var statResult = Stat(path);
                        return statResult.Success ? statResult.Data : null;
                    }, metadataAlreadyLoaded: false); // READDIR doesn't provide metadata
                }

                return TnfsResult<TnfsDirectoryEntry?>.CreateSuccess(entry);
            }

            if (returnCode != TnfsReturnCode.SUCCESS)
            {
                return TnfsResult<TnfsDirectoryEntry?>.CreateError(returnCode, $"ReadDir failed: {returnCode}");
            }

            // Parse extended READDIRX response:
            // Offset 4: return code (already checked)
            // Offset 5+: extended directory entry data with size/timestamps
            TnfsDirectoryEntry? entry2 = ParseDirectoryEntryExtended(response, 5);
            return TnfsResult<TnfsDirectoryEntry?>.CreateSuccess(entry2);
        }
        catch (Exception ex)
        {
            return TnfsResult<TnfsDirectoryEntry?>.CreateError(TnfsReturnCode.EIO, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Close directory.
    /// </summary>
    /// <param name="handle">Directory handle (single byte)</param>
    public TnfsResult<bool> CloseDir(byte handle)
    {
        if (!_isMounted)
        {
            return TnfsResult<bool>.CreateSuccess(true);
        }

        if (!EnsureConnected())
        {
            return TnfsResult<bool>.CreateError(TnfsReturnCode.EIO, "Connection failed");
        }

        try
        {
            byte[] handleBytes = new byte[1] { handle };
            byte[] packet = BuildPacket(TnfsCommand.CloseDir, handleBytes);
            SendWithRetry(packet);

            // Clean up directory path mapping
            _dirHandlePaths.Remove(handle);

            return TnfsResult<bool>.CreateSuccess(true);
        }
        catch (Exception ex)
        {
            return TnfsResult<bool>.CreateError(TnfsReturnCode.EIO, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Open file for reading/writing.
    /// </summary>
    /// <param name="path">File path</param>
    /// <param name="flags">Open flags (read, write, etc)</param>
    /// <param name="mode">Unix file permissions (default 0x01A4 = 0644 octal)</param>
    /// <returns>Result with file handle</returns>
    public TnfsResult<ushort> Open(string path, TnfsOpenFlags flags = TnfsOpenFlags.O_RDONLY, ushort mode = 0x01A4)
    {
        if (!_isMounted)
        {
            return TnfsResult<ushort>.CreateError(TnfsReturnCode.EINVAL, "Not mounted");
        }

        if (!EnsureConnected())
        {
            return TnfsResult<ushort>.CreateError(TnfsReturnCode.EIO, "Connection failed");
        }

        try
        {
            using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
            {
                // Flags (little-endian ushort)
                byte[] flagsBytes = new byte[2];
                TnfsEndian.WriteUInt16(flagsBytes, 0, (ushort)flags);
                ms.Write(flagsBytes, 0, 2);

                // Mode (little-endian ushort)
                byte[] modeBytes = new byte[2];
                TnfsEndian.WriteUInt16(modeBytes, 0, mode);
                ms.Write(modeBytes, 0, 2);

                // Filename (null-terminated)
                byte[] pathBytes = Encoding.ASCII.GetBytes(path);
                ms.Write(pathBytes, 0, pathBytes.Length);
                ms.WriteByte(0);

                byte[] payload = ms.ToArray();
                byte[] packet = BuildPacket(TnfsCommand.Open, payload);
                byte[]? response = SendWithRetry(packet);

                if (response == null || response.Length < 6)
                {
                    return TnfsResult<ushort>.CreateError(TnfsReturnCode.EIO, "Invalid response");
                }

                TnfsReturnCode returnCode = (TnfsReturnCode)response[4];
                if (returnCode != TnfsReturnCode.SUCCESS)
                {
                    return TnfsResult<ushort>.CreateError(returnCode, $"Open failed: {returnCode}");
                }

                // File handle is 1 byte (0-255), same as directory handles
                ushort fileHandle = response[5];
                return TnfsResult<ushort>.CreateSuccess(fileHandle);
            }
        }
        catch (Exception ex)
        {
            return TnfsResult<ushort>.CreateError(TnfsReturnCode.EIO, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Read from file.
    /// </summary>
    /// <param name="handle">File handle</param>
    /// <param name="length">Number of bytes to read</param>
    /// <returns>Result with read data</returns>
    public TnfsResult<byte[]> Read(ushort handle, ushort length)
    {
        if (!_isMounted)
        {
            return TnfsResult<byte[]>.CreateError(TnfsReturnCode.EINVAL, "Not mounted");
        }

        try
        {
            // TNFS READ: handle(1) + length(2) = 3 bytes
            byte[] payload = new byte[3];
            payload[0] = (byte)handle;  // File handle is 1 byte
            TnfsEndian.WriteUInt16(payload, 1, length);

            byte[] packet = BuildPacket(TnfsCommand.Read, payload);
            byte[]? response = SendWithRetry(packet);

            // Check minimum response size (header only = 5 bytes)
            if (response == null || response.Length < 5)
            {
                return TnfsResult<byte[]>.CreateError(TnfsReturnCode.EIO, "Invalid response");
            }

            // Check status code first (error responses may only be 5 bytes)
            TnfsReturnCode returnCode = (TnfsReturnCode)response[4];
            if (returnCode == TnfsReturnCode.EOF)
            {
                return TnfsResult<byte[]>.CreateSuccess(Array.Empty<byte>());
            }

            if (returnCode != TnfsReturnCode.SUCCESS)
            {
                return TnfsResult<byte[]>.CreateError(returnCode, $"Read failed: {returnCode}");
            }

            // For SUCCESS responses, check we have the bytesRead field
            if (response.Length < 7)
            {
                return TnfsResult<byte[]>.CreateError(TnfsReturnCode.EIO, "Invalid response payload");
            }

            ushort bytesRead = TnfsEndian.ReadUInt16(response, 5);
            byte[] data = new byte[bytesRead];
            Array.Copy(response, 7, data, 0, bytesRead);

            return TnfsResult<byte[]>.CreateSuccess(data);
        }
        catch (Exception ex)
        {
            return TnfsResult<byte[]>.CreateError(TnfsReturnCode.EIO, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Close file.
    /// </summary>
    /// <param name="handle">File handle</param>
    public TnfsResult<bool> Close(ushort handle)
    {
        if (!_isMounted)
        {
            return TnfsResult<bool>.CreateSuccess(true);
        }

        try
        {
            // TNFS CLOSE: handle(1) = 1 byte
            byte[] handleBytes = new byte[1] { (byte)handle };
            byte[] packet = BuildPacket(TnfsCommand.Close, handleBytes);
            SendWithRetry(packet);

            return TnfsResult<bool>.CreateSuccess(true);
        }
        catch (Exception ex)
        {
            return TnfsResult<bool>.CreateError(TnfsReturnCode.EIO, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Get file status information.
    /// </summary>
    /// <param name="path">File path</param>
    /// <returns>Result with file status</returns>
    public TnfsResult<TnfsFileStat> Stat(string path)
    {
        if (!_isMounted)
        {
            return TnfsResult<TnfsFileStat>.CreateError(TnfsReturnCode.EINVAL, "Not mounted");
        }

        try
        {
            byte[] pathBytes = Encoding.ASCII.GetBytes(path + '\0');
            byte[] packet = BuildPacket(TnfsCommand.Stat, pathBytes);
            byte[]? response = SendWithRetry(packet);

            if (response == null || response.Length < 20)
            {
                return TnfsResult<TnfsFileStat>.CreateError(TnfsReturnCode.EIO, "Invalid response");
            }

            TnfsReturnCode returnCode = (TnfsReturnCode)response[4];
            if (returnCode != TnfsReturnCode.SUCCESS)
            {
                return TnfsResult<TnfsFileStat>.CreateError(returnCode, $"Stat failed: {returnCode}");
            }

            TnfsFileStat stat = ParseFileStat(response, 5);
            return TnfsResult<TnfsFileStat>.CreateSuccess(stat);
        }
        catch (Exception ex)
        {
            return TnfsResult<TnfsFileStat>.CreateError(TnfsReturnCode.EIO, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Write data to an open file.
    /// </summary>
    /// <param name="handle">File handle from Open()</param>
    /// <param name="data">Data to write</param>
    /// <returns>Result with number of bytes written</returns>
    public TnfsResult<ushort> Write(ushort handle, byte[] data)
    {
        if (!_isMounted)
        {
            return TnfsResult<ushort>.CreateError(TnfsReturnCode.EINVAL, "Not mounted");
        }

        if (data == null || data.Length == 0)
        {
            return TnfsResult<ushort>.CreateError(TnfsReturnCode.EINVAL, "No data to write");
        }

        try
        {
            // BUGFIX: C server expects: fd(1) + size(2) + data(N)
            byte[] payload = new byte[1 + 2 + data.Length];
            payload[0] = (byte)handle;  // File handle is single byte
            TnfsEndian.WriteUInt16(payload, 1, (ushort)data.Length);  // Size field
            Array.Copy(data, 0, payload, 3, data.Length);  // Data starts at offset 3

            byte[] packet = BuildPacket(TnfsCommand.Write, payload);
            byte[]? response = SendWithRetry(packet);

            if (response == null || response.Length < 7)
            {
                return TnfsResult<ushort>.CreateError(TnfsReturnCode.EIO, "Invalid response");
            }

            TnfsReturnCode returnCode = (TnfsReturnCode)response[4];
            if (returnCode != TnfsReturnCode.SUCCESS)
            {
                return TnfsResult<ushort>.CreateError(returnCode, $"Write failed: {returnCode}");
            }

            ushort bytesWritten = TnfsEndian.ReadUInt16(response, 5);
            return TnfsResult<ushort>.CreateSuccess(bytesWritten);
        }
        catch (Exception ex)
        {
            return TnfsResult<ushort>.CreateError(TnfsReturnCode.EIO, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Seek to a position in an open file.
    /// </summary>
    /// <param name="handle">File handle from Open()</param>
    /// <param name="offset">Offset in bytes</param>
    /// <param name="whence">Seek mode (0=SEEK_SET, 1=SEEK_CUR, 2=SEEK_END)</param>
    /// <returns>Result with success status</returns>
    public TnfsResult<bool> Lseek(ushort handle, int offset, byte whence)
    {
        if (!_isMounted)
        {
            return TnfsResult<bool>.CreateError(TnfsReturnCode.EINVAL, "Not mounted");
        }

        try
        {
            // BUGFIX: Handle is 1 byte, not 2 bytes
            byte[] payload = new byte[6];  // 1 (handle) + 1 (whence) + 4 (offset)
            payload[0] = (byte)handle;  // File handle is single byte
            payload[1] = whence;
            TnfsEndian.WriteInt32(payload, 2, offset);

            byte[] packet = BuildPacket(TnfsCommand.Seek, payload);
            byte[]? response = SendWithRetry(packet);

            if (response == null || response.Length < 5)
            {
                return TnfsResult<bool>.CreateError(TnfsReturnCode.EIO, "Invalid response");
            }

            TnfsReturnCode returnCode = (TnfsReturnCode)response[4];
            if (returnCode != TnfsReturnCode.SUCCESS)
            {
                return TnfsResult<bool>.CreateError(returnCode, $"Lseek failed: {returnCode}");
            }

            return TnfsResult<bool>.CreateSuccess(true);
        }
        catch (Exception ex)
        {
            return TnfsResult<bool>.CreateError(TnfsReturnCode.EIO, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Create a new directory.
    /// </summary>
    /// <param name="path">Directory path</param>
    /// <param name="mode">Permission mode (default 0755)</param>
    /// <returns>Result with success status</returns>
    public TnfsResult<bool> MkDir(string path, ushort mode = 0x01ED)
    {
        if (!_isMounted)
        {
            return TnfsResult<bool>.CreateError(TnfsReturnCode.EINVAL, "Not mounted");
        }

        try
        {
            // BUGFIX: C server expects ONLY path, no mode parameter (uses hardcoded 0755)
            byte[] pathBytes = Encoding.ASCII.GetBytes(path + '\0');

            byte[] packet = BuildPacket(TnfsCommand.MkDir, pathBytes);
            byte[]? response = SendWithRetry(packet);

            if (response == null || response.Length < 5)
            {
                return TnfsResult<bool>.CreateError(TnfsReturnCode.EIO, "Invalid response");
            }

            TnfsReturnCode returnCode = (TnfsReturnCode)response[4];
            if (returnCode != TnfsReturnCode.SUCCESS)
            {
                return TnfsResult<bool>.CreateError(returnCode, $"MkDir failed: {returnCode}");
            }

            return TnfsResult<bool>.CreateSuccess(true);
        }
        catch (Exception ex)
        {
            return TnfsResult<bool>.CreateError(TnfsReturnCode.EIO, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Remove a directory.
    /// </summary>
    /// <param name="path">Directory path</param>
    /// <returns>Result with success status</returns>
    public TnfsResult<bool> RmDir(string path)
    {
        if (!_isMounted)
        {
            return TnfsResult<bool>.CreateError(TnfsReturnCode.EINVAL, "Not mounted");
        }

        try
        {
            byte[] pathBytes = Encoding.ASCII.GetBytes(path + '\0');
            byte[] packet = BuildPacket(TnfsCommand.RmDir, pathBytes);
            byte[]? response = SendWithRetry(packet);

            if (response == null || response.Length < 5)
            {
                return TnfsResult<bool>.CreateError(TnfsReturnCode.EIO, "Invalid response");
            }

            TnfsReturnCode returnCode = (TnfsReturnCode)response[4];
            if (returnCode != TnfsReturnCode.SUCCESS)
            {
                return TnfsResult<bool>.CreateError(returnCode, $"RmDir failed: {returnCode}");
            }

            return TnfsResult<bool>.CreateSuccess(true);
        }
        catch (Exception ex)
        {
            return TnfsResult<bool>.CreateError(TnfsReturnCode.EIO, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Delete a file.
    /// </summary>
    /// <param name="path">File path</param>
    /// <returns>Result with success status</returns>
    public TnfsResult<bool> Unlink(string path)
    {
        if (!_isMounted)
        {
            return TnfsResult<bool>.CreateError(TnfsReturnCode.EINVAL, "Not mounted");
        }

        try
        {
            byte[] pathBytes = Encoding.ASCII.GetBytes(path + '\0');
            byte[] packet = BuildPacket(TnfsCommand.Unlink, pathBytes);
            byte[]? response = SendWithRetry(packet);

            if (response == null || response.Length < 5)
            {
                return TnfsResult<bool>.CreateError(TnfsReturnCode.EIO, "Invalid response");
            }

            TnfsReturnCode returnCode = (TnfsReturnCode)response[4];
            if (returnCode != TnfsReturnCode.SUCCESS)
            {
                return TnfsResult<bool>.CreateError(returnCode, $"Unlink failed: {returnCode}");
            }

            return TnfsResult<bool>.CreateSuccess(true);
        }
        catch (Exception ex)
        {
            return TnfsResult<bool>.CreateError(TnfsReturnCode.EIO, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Change file permissions.
    /// </summary>
    /// <param name="path">File path</param>
    /// <param name="mode">Permission mode</param>
    /// <returns>Result with success status</returns>
    public TnfsResult<bool> Chmod(string path, ushort mode)
    {
        // BUGFIX: CHMOD is NOT implemented in C TNFS server (tnfsd/src/tnfs_file.c:340-342)
        // The function exists but has an empty body and never sends a response.
        // Return ENOSYS (function not implemented) immediately instead of hanging.
        return TnfsResult<bool>.CreateError(TnfsReturnCode.ENOSYS, "CHMOD not implemented in TNFS server");
    }

    /// <summary>
    /// Rename a file or directory.
    /// </summary>
    /// <param name="sourcePath">Source path</param>
    /// <param name="destPath">Destination path</param>
    /// <returns>Result with success status</returns>
    public TnfsResult<bool> Rename(string sourcePath, string destPath)
    {
        if (!_isMounted)
        {
            return TnfsResult<bool>.CreateError(TnfsReturnCode.EINVAL, "Not mounted");
        }

        try
        {
            byte[] sourceBytes = Encoding.ASCII.GetBytes(sourcePath + '\0');
            byte[] destBytes = Encoding.ASCII.GetBytes(destPath + '\0');
            byte[] payload = new byte[sourceBytes.Length + destBytes.Length];
            Array.Copy(sourceBytes, 0, payload, 0, sourceBytes.Length);
            Array.Copy(destBytes, 0, payload, sourceBytes.Length, destBytes.Length);

            byte[] packet = BuildPacket(TnfsCommand.Rename, payload);
            byte[]? response = SendWithRetry(packet);

            if (response == null || response.Length < 5)
            {
                return TnfsResult<bool>.CreateError(TnfsReturnCode.EIO, "Invalid response");
            }

            TnfsReturnCode returnCode = (TnfsReturnCode)response[4];
            if (returnCode != TnfsReturnCode.SUCCESS)
            {
                return TnfsResult<bool>.CreateError(returnCode, $"Rename failed: {returnCode}");
            }

            return TnfsResult<bool>.CreateSuccess(true);
        }
        catch (Exception ex)
        {
            return TnfsResult<bool>.CreateError(TnfsReturnCode.EIO, $"Error: {ex.Message}");
        }
    }

    // Helper methods

    private byte[] BuildMountPacket(string path, string? username = null, string? password = null)
    {
        byte[] pathBytes = Encoding.ASCII.GetBytes(path + '\0');
        byte[] userBytes = string.IsNullOrEmpty(username) ? new byte[] { 0 } : Encoding.ASCII.GetBytes(username + '\0');
        byte[] passBytes = string.IsNullOrEmpty(password) ? new byte[] { 0 } : Encoding.ASCII.GetBytes(password + '\0');

        byte[] packet = new byte[6 + pathBytes.Length + userBytes.Length + passBytes.Length];

        // Header: Session ID (0 for mount) - little-endian
        packet[0] = 0;
        packet[1] = 0;

        // Header: Sequence number
        packet[2] = 0;

        // Header: Command
        packet[3] = (byte)TnfsCommand.Mount;

        // Payload: Protocol version (little-endian: LSB, MSB)
        packet[4] = TnfsConstants.PROTOVERSION_LSB; // 0x02
        packet[5] = TnfsConstants.PROTOVERSION_MSB; // 0x01

        // Payload: Mount path (null-terminated)
        int offset = 6;
        Array.Copy(pathBytes, 0, packet, offset, pathBytes.Length);
        offset += pathBytes.Length;

        // Payload: Username (null-terminated or just null)
        Array.Copy(userBytes, 0, packet, offset, userBytes.Length);
        offset += userBytes.Length;

        // Payload: Password (null-terminated or just null)
        Array.Copy(passBytes, 0, packet, offset, passBytes.Length);

        return packet;
    }

    private byte[] BuildPacket(TnfsCommand command, byte[]? payload = null)
    {
        int payloadLen = payload?.Length ?? 0;
        byte[] packet = new byte[4 + payloadLen];

        // Header: Session ID (little-endian)
        TnfsEndian.WriteUInt16(packet, 0, _sessionId);

        // Header: Sequence number (pre-increment to start at 1 after mount)
        packet[2] = ++_sequenceNumber;

        // Header: Command
        packet[3] = (byte)command;

        // Payload
        if (payload != null && payloadLen > 0)
        {
            Array.Copy(payload, 0, packet, 4, payloadLen);
        }

        return packet;
    }

    private byte[]? SendWithRetry(byte[] packet)
    {
        if (_useTcp)
        {
            return SendTcp(packet);
        }
        else
        {
            return SendUdp(packet);
        }
    }

    private byte[]? SendTcp(byte[] packet)
    {
        if (_tcpStream == null)
        {
            return null;
        }

        try
        {
            // TCP: Send length prefix (2 bytes, little-endian) + TNFS packet
            byte[] lengthPrefix = BitConverter.GetBytes((ushort)packet.Length);
            _logger.LogDebug("TCP sending {TotalBytes} bytes: length={Length}, packet={Packet}",
                packet.Length + 2, packet.Length, BitConverter.ToString(packet));
            _tcpStream.Write(lengthPrefix, 0, 2);
            _tcpStream.Write(packet, 0, packet.Length);
            _tcpStream.Flush();

            // Read response: length prefix (2 bytes) + TNFS packet
            byte[] lenBuffer = new byte[2];
            int lenRead = _tcpStream.Read(lenBuffer, 0, 2);
            if (lenRead < 2)
            {
                _logger.LogError("TCP error: Could not read length prefix");
                return null;
            }

            ushort responseLength = BitConverter.ToUInt16(lenBuffer, 0);
            if (responseLength == 0 || responseLength > 8192)
            {
                _logger.LogError("TCP error: Invalid response length {ResponseLength}", responseLength);
                return null;
            }

            // Read the actual message
            byte[] buffer = new byte[responseLength];
            int totalRead = 0;
            while (totalRead < responseLength)
            {
                int bytesRead = _tcpStream.Read(buffer, totalRead, responseLength - totalRead);
                if (bytesRead <= 0)
                {
                    _logger.LogError("TCP error: Connection closed while reading response");
                    return null;
                }
                totalRead += bytesRead;
            }

            _logger.LogDebug("TCP received {ResponseLength} bytes: {Response}",
                responseLength, BitConverter.ToString(buffer));
            return buffer;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TCP error");
            return null;
        }
    }

    private byte[]? SendUdp(byte[] packet)
    {
        if (_udpClient == null)
        {
            return null;
        }

        for (int retry = 0; retry < TnfsConstants.MaxRetries; retry++)
        {
            try
            {
                _udpClient.Send(packet, packet.Length);

                IPEndPoint? remoteEp = null;
                byte[] response = _udpClient.Receive(ref remoteEp);

                return response;
            }
            catch (SocketException)
            {
                if (retry == TnfsConstants.MaxRetries - 1)
                {
                    throw;
                }
            }
        }

        return null;
    }

    private TnfsDirectoryEntry? ParseDirectoryEntry(byte[] data, int offset)
    {
        // Standard READDIR response format:
        // Bytes 0-4: Header (Session ID, Seq, Cmd, ReturnCode)
        // Byte 5+:   Null-terminated filename (NO attributes, NO size, NO timestamps)

        if (offset >= data.Length)
        {
            return null;
        }

        TnfsDirectoryEntry entry = new TnfsDirectoryEntry();

        // Standard READDIR has NO attributes byte - filename starts immediately
        entry.Attributes = TnfsFileAttributes.None;

        // Read null-terminated filename
        List<byte> nameBytes = new List<byte>();
        while (offset < data.Length && data[offset] != 0)
        {
            nameBytes.Add(data[offset]);
            offset++;
        }
        entry.Name = Encoding.ASCII.GetString(nameBytes.ToArray());

        // Standard READDIR does NOT include size or timestamps
        // Use STAT command to get file metadata if needed
        entry.Size = 0;
        entry.ModifiedTime = 0;

        return entry;
    }

    private TnfsDirectoryEntry? ParseDirectoryEntryExtended(byte[] data, int offset)
    {
        // READDIRX entry format:
        // 1 byte:  flags (TNFS_DIRENTRY)
        // 4 bytes: size (little-endian uint32)
        // 4 bytes: mtime (little-endian uint32)
        // 4 bytes: ctime (little-endian uint32)
        // N bytes: null-terminated filename

        if (offset + 13 >= data.Length)  // Minimum: 1 + 4 + 4 + 4 + 1 (null)
        {
            return null;
        }

        TnfsDirectoryEntry entry = new TnfsDirectoryEntry();

        // Flags byte
        byte flags = data[offset];
        offset++;

        // Convert TNFS_DIRENTRY flags to TnfsFileAttributes
        // TNFS_DIRENTRY_DIR (0x01) = Directory
        // TNFS_DIRENTRY_HIDDEN (0x02) = Hidden
        TnfsFileAttributes attributes = TnfsFileAttributes.None;
        if ((flags & 0x01) != 0)
        {
            attributes |= TnfsFileAttributes.Directory;
        }
        if ((flags & 0x02) != 0)
        {
            attributes |= TnfsFileAttributes.Hidden;
        }
        entry.Attributes = attributes;

        // Size (4 bytes little-endian)
        entry.Size = TnfsEndian.ReadUInt32(data, offset);
        offset += 4;

        // Modified time (4 bytes little-endian)
        entry.ModifiedTime = TnfsEndian.ReadUInt32(data, offset);
        offset += 4;

        // Creation/change time (4 bytes little-endian) - we ignore this for now
        offset += 4;

        // Read null-terminated filename
        List<byte> nameBytes = new List<byte>();
        while (offset < data.Length && data[offset] != 0)
        {
            nameBytes.Add(data[offset]);
            offset++;
        }
        entry.Name = Encoding.ASCII.GetString(nameBytes.ToArray());

        _logger.LogDebug("Parsed entry: name='{Name}', size={Size}, isDir={IsDirectory}",
            entry.Name, entry.Size, entry.IsDirectory);

        return entry;
    }

    private TnfsFileStat ParseFileStat(byte[] data, int offset)
    {
        TnfsFileStat stat = new TnfsFileStat();

        // TNFS 1.2 spec format: mode(2) + uid(2) + gid(2) + size(4) + atime(4) + mtime(4) + ctime(4) + uidstring + gidstring
        stat.Permissions = TnfsEndian.ReadUInt16(data, offset);
        offset += 2;

        // Extract file type from Unix mode bits (S_IFDIR = 0x4000, S_IFLNK = 0xA000)
        const ushort S_IFMT = 0xF000;   // File type mask
        const ushort S_IFDIR = 0x4000;  // Directory
        const ushort S_IFLNK = 0xA000;  // Symbolic link
        ushort fileType = (ushort)(stat.Permissions & S_IFMT);

        stat.Attributes = TnfsFileAttributes.None;
        if (fileType == S_IFDIR)
            stat.Attributes |= TnfsFileAttributes.Directory;
        if (fileType == S_IFLNK)
            stat.Attributes |= TnfsFileAttributes.SymbolicLink;

        // Skip uid(2) + gid(2) - not needed for 8-bit systems
        offset += 4;

        stat.Size = TnfsEndian.ReadUInt32(data, offset);
        offset += 4;

        stat.AccessTime = TnfsEndian.ReadUInt32(data, offset);
        offset += 4;

        stat.ModifiedTime = TnfsEndian.ReadUInt32(data, offset);
        offset += 4;

        stat.CreationTime = TnfsEndian.ReadUInt32(data, offset);
        offset += 4;

        // Note: uidstring and gidstring are optional and typically not present

        return stat;
    }

    /// <summary>
    /// Dispose resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_isMounted)
        {
            Unmount();
        }
        else
        {
            CloseConnection();
        }

        _disposed = true;
    }
}
