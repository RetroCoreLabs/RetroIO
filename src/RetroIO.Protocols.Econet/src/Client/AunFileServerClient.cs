//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Net;
using System.Net.Sockets;
using System.Text;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.Protocol;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Protocols.Econet.Server.Transport;

namespace RetroIO.Protocols.Econet.Client;

/// <summary>
/// UDP AUN client for Level-3 Econet file server operations.
/// Supports <c>*I AM</c> login, directory examine, and file load (read).
/// </summary>
public sealed class AunFileServerClient : IDisposable
{
  private const int ReceiveTimeoutMs = 5000;
  private const int MaxDatagramSize = 4096;

  private readonly InMemoryAunTransport? _inMemoryTransport;
  private readonly Socket? _udpSocket;
  private readonly IPEndPoint? _serverEndPoint;
  private readonly IPAddress _clientAddress;
  private readonly int _clientPort;
  private readonly byte _clientNet;
  private readonly byte _clientStation;

  private uint _sequence;
  private byte _replyPort = EconetClientConstants.DefaultReplyPort;
  private byte _dataPort = EconetClientConstants.DefaultDataPort;
  private byte _urd;
  private byte _cwd;
  private byte _lib;
  private bool _loggedIn;
  private bool _disposed;

  public AunFileServerClient(
    string host,
    int port = EconetClientConstants.DefaultAunUdpPort,
    byte clientNet = 1,
    byte clientStation = 1,
    int clientPort = EconetClientConstants.DefaultClientPort)
  {
    if (string.IsNullOrWhiteSpace(host))
      throw new ArgumentException("Host cannot be null or empty.", nameof(host));

    _clientNet = clientNet;
    _clientStation = clientStation;
    _clientPort = clientPort;
    _clientAddress = IPAddress.Loopback;

    _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    _udpSocket.Bind(new IPEndPoint(_clientAddress, _clientPort));
    _udpSocket.ReceiveTimeout = ReceiveTimeoutMs;

    IPAddress[] addresses = Dns.GetHostAddresses(host);
    IPAddress? serverAddress = null;
    for (int i = 0; i < addresses.Length; i++)
    {
      if (addresses[i].AddressFamily == AddressFamily.InterNetwork)
      {
        serverAddress = addresses[i];
        break;
      }
    }

    if (serverAddress == null)
      throw new ArgumentException($"Could not resolve host '{host}'.", nameof(host));

    _serverEndPoint = new IPEndPoint(serverAddress, port);
  }

  /// <summary>
  /// Creates a client bound to an in-memory transport for unit tests.
  /// </summary>
  public AunFileServerClient(
    InMemoryAunTransport transport,
    byte clientNet = 1,
    byte clientStation = 1,
    int clientPort = EconetClientConstants.DefaultClientPort)
  {
    _inMemoryTransport = transport ?? throw new ArgumentNullException(nameof(transport));
    _clientNet = clientNet;
    _clientStation = clientStation;
    _clientPort = clientPort;
    _clientAddress = IPAddress.Loopback;
  }

  public bool IsLoggedIn => _loggedIn;

  public byte UrdHandle => _urd;

  public byte CwdHandle => _cwd;

  public byte LibHandle => _lib;

  /// <summary>
  /// Performs <c>*I AM username password</c> login on the file server.
  /// </summary>
  public void Login(string username, string? password = null)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);

    string paddedUser = AcornPath.PadUsername(username.Trim());
    string paddedPass = AcornPath.PadPassword(password ?? string.Empty);
    string command = "I AM " + paddedUser + " " + paddedPass + "\r";
    byte[] parameters = Encoding.ASCII.GetBytes(command);

    EconetMessage response = SendFileServerCommand(FileServerOpcodes.OsCli, parameters, loggedIn: false);
    ValidateFsResponse(response);

    if (response.Payload[0] != FileServerOpcodes.LoginSuccessReturnCode)
    {
      byte errorCode = response.Payload.Length > 1 ? response.Payload[1] : FileServerErrors.BadCommand;
      throw new IOException($"Econet login failed (0x{errorCode:X2}).");
    }

    if (response.Payload.Length < 6)
      throw new IOException("Econet login response was too short.");

    _urd = response.Payload[2];
    _cwd = response.Payload[3];
    _lib = response.Payload[4];
    _loggedIn = true;
  }

  /// <summary>
  /// Logs out the current session (<c>*BYE</c>).
  /// </summary>
  public void Logout()
  {
    if (!_loggedIn || _disposed)
      return;

    byte[] parameters = Encoding.ASCII.GetBytes("BYE\r");
    try
    {
      EconetMessage response = SendFileServerCommand(FileServerOpcodes.OsCli, parameters, loggedIn: true);
      ValidateFsResponse(response);
    }
    catch (IOException)
    {
      // Best-effort logout.
    }

    _loggedIn = false;
    _urd = 0;
    _cwd = 0;
    _lib = 0;
  }

  /// <summary>
  /// Lists directory contents via examine (opcode <c>0x03</c>).
  /// </summary>
  public AunDirectoryEntry[] ListDirectory(string path)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    EnsureLoggedIn();

    string acornPath = ToAcornPath(path);
    byte[] pathBytes = Encoding.ASCII.GetBytes(acornPath + "\r");
    byte[] parameters = new byte[3 + pathBytes.Length];
    parameters[0] = 0;
    parameters[1] = 0;
    parameters[2] = 0xFF;
    for (int i = 0; i < pathBytes.Length; i++)
      parameters[3 + i] = pathBytes[i];

    EconetMessage response = SendFileServerCommand(FileServerOpcodes.Examine, parameters, loggedIn: true);
    ValidateFsResponse(response);

    if (response.Payload.Length > 1 && response.Payload[1] != 0)
      throw new IOException($"Econet examine failed (0x{response.Payload[1]:X2}).");

    if (response.Payload.Length < 4)
      return Array.Empty<AunDirectoryEntry>();

    int entryCount = response.Payload[2];
    int entryBytes = response.Payload.Length - 4;
    if (response.Payload[^1] == 0x80)
      entryBytes--;

    return ParseCatalogEntries(response.Payload.AsSpan(4, entryBytes), entryCount);
  }

  /// <summary>
  /// Reads an entire file via load (opcode <c>0x02</c>) including bulk transfer.
  /// </summary>
  public byte[] ReadFile(string path)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    EnsureLoggedIn();

    string acornPath = ToAcornFilePath(path);
    byte[] nameBytes = Encoding.ASCII.GetBytes(acornPath + "\r");
    byte[] body = new byte[3 + nameBytes.Length];
    body[0] = _dataPort;
    body[1] = _cwd;
    body[2] = 0;
    nameBytes.CopyTo(body, 3);

    uint requestSequence = _sequence + 1;
    EconetMessage metadata = SendBulkFileServerCommand(FileServerOpcodes.Load, body);
    ValidateFsResponse(metadata);

    if (metadata.Payload.Length > 1 && metadata.Payload[1] != 0)
      throw new FileNotFoundException($"Econet file not found: {path}", path);

    int expectedLength = 0;
    if (metadata.Payload.Length >= 13)
    {
      expectedLength = metadata.Payload[10]
        | (metadata.Payload[11] << 8)
        | (metadata.Payload[12] << 16);
    }

    SendAck(requestSequence);

    List<byte> fileData = new List<byte>(expectedLength > 0 ? expectedLength : 256);
    bool loadComplete = false;

    while (!loadComplete)
    {
      EconetMessage packet = ReceiveMessage();
      if (packet.PacketType == AunPacketType.Ack)
        continue;

      if (packet.EconetPort == _dataPort)
      {
        if (packet.Payload.Length > 0)
        {
          for (int i = 0; i < packet.Payload.Length; i++)
            fileData.Add(packet.Payload[i]);
        }

        SendAck(packet.Sequence);
        continue;
      }

      if (packet.EconetPort == _replyPort)
      {
        if (packet.Payload.Length >= 2 && packet.Payload[0] == 0 && packet.Payload[1] == 0)
        {
          loadComplete = true;
          continue;
        }

        ValidateFsResponse(packet);
        if (packet.Payload.Length > 1 && packet.Payload[1] != 0)
          throw new IOException($"Econet load failed (0x{packet.Payload[1]:X2}).");
      }
    }

    return fileData.ToArray();
  }

  public void Dispose()
  {
    if (_disposed)
      return;

    Logout();
    _udpSocket?.Dispose();
    _disposed = true;
  }

  private void EnsureLoggedIn()
  {
    if (!_loggedIn)
      throw new InvalidOperationException("Not logged in. Call Login() first.");
  }

  private EconetMessage SendFileServerCommand(byte opcode, byte[] parameters, bool loggedIn)
  {
    byte[] fsPayload = new byte[5 + parameters.Length];
    fsPayload[0] = _replyPort;
    fsPayload[1] = opcode;
    if (loggedIn)
    {
      fsPayload[2] = _urd;
      fsPayload[3] = _cwd;
      fsPayload[4] = _lib;
    }

    for (int i = 0; i < parameters.Length; i++)
      fsPayload[5 + i] = parameters[i];

    return SendFileServerPayload(fsPayload);
  }

  /// <summary>
  /// SAVE/LOAD opcodes repurpose bytes 2–4; body is written from offset 2.
  /// </summary>
  private EconetMessage SendBulkFileServerCommand(byte opcode, ReadOnlySpan<byte> bodyFromByte2)
  {
    byte[] fsPayload = new byte[2 + bodyFromByte2.Length];
    fsPayload[0] = _replyPort;
    fsPayload[1] = opcode;
    bodyFromByte2.CopyTo(fsPayload.AsSpan(2));
    return SendFileServerPayload(fsPayload);
  }

  private EconetMessage SendFileServerPayload(byte[] fsPayload)
  {
    _sequence++;
    EconetMessage request = new EconetMessage
    {
      PacketType = AunPacketType.Unicast,
      EconetPort = EconetPorts.FileServer,
      Control = 0x80,
      Sequence = _sequence,
      Payload = fsPayload,
      ClientAddress = _clientAddress,
      ClientPort = _clientPort,
      ClientNet = _clientNet,
      ClientStation = _clientStation,
    };

    SendMessage(request);
    return ReceiveFsResponse();
  }

  private void SendMessage(EconetMessage message)
  {
    if (_inMemoryTransport != null)
    {
      _inMemoryTransport.SendTestMessageAsync(message).GetAwaiter().GetResult();
      return;
    }

    byte[] buffer = new byte[AunPacket.HeaderSize + message.Payload.Length];
    AunPacket.Encode(
      buffer,
      message.PacketType,
      message.EconetPort,
      message.Control,
      message.Sequence,
      message.Payload);

    int sent = _udpSocket!.SendTo(buffer, _serverEndPoint!);
    if (sent != buffer.Length)
      throw new IOException("Failed to send complete AUN datagram.");
  }

  private EconetMessage ReceiveFsResponse()
  {
    while (true)
    {
      EconetMessage message = ReceiveMessage();
      if (message.PacketType == AunPacketType.Ack)
        continue;

      return message;
    }
  }

  private EconetMessage ReceiveMessage()
  {
    if (_inMemoryTransport != null)
    {
      using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
      return _inMemoryTransport.ReceiveResponseAsync(timeout.Token).GetAwaiter().GetResult();
    }

    byte[] buffer = new byte[MaxDatagramSize];
    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
    int received = _udpSocket!.ReceiveFrom(buffer, ref remote);

    if (!AunPacket.TryDecode(
          buffer.AsSpan(0, received),
          out AunPacketType type,
          out byte port,
          out byte control,
          out uint sequence,
          out int payloadOffset))
    {
      throw new IOException("Invalid AUN datagram received.");
    }

    int payloadLength = received - payloadOffset;
    byte[] payload = new byte[payloadLength];
    buffer.AsSpan(payloadOffset, payloadLength).CopyTo(payload);

    return new EconetMessage
    {
      PacketType = type,
      EconetPort = port,
      Control = control,
      Sequence = sequence,
      Payload = payload,
      ClientAddress = _clientAddress,
      ClientPort = _clientPort,
      ClientNet = _clientNet,
      ClientStation = _clientStation,
    };
  }

  private void SendAck(uint sequence)
  {
    EconetMessage ack = new EconetMessage
    {
      PacketType = AunPacketType.Ack,
      EconetPort = _dataPort,
      Control = 0x80,
      Sequence = sequence,
      Payload = Array.Empty<byte>(),
      ClientAddress = _clientAddress,
      ClientPort = _clientPort,
      ClientNet = _clientNet,
      ClientStation = _clientStation,
    };

    SendMessage(ack);
  }

  private static void ValidateFsResponse(EconetMessage response)
  {
    if (response.Payload == null || response.Payload.Length < 2)
      throw new IOException("Empty Econet file server response.");
  }

  private static AunDirectoryEntry[] ParseCatalogEntries(ReadOnlySpan<byte> entryData, int entryCount)
  {
    if (entryCount <= 0 || entryData.Length < 27)
      return Array.Empty<AunDirectoryEntry>();

    AunDirectoryEntry[] entries = new AunDirectoryEntry[entryCount];
    int offset = 0;

    for (int i = 0; i < entryCount; i++)
    {
      if (offset + 27 > entryData.Length)
        break;

      ReadOnlySpan<byte> entry = entryData.Slice(offset, 27);
      string name = Encoding.ASCII.GetString(entry.Slice(0, 10)).TrimEnd();
      bool isDirectory = entry[24] == 0x02;
      long size = 0;
      if (!isDirectory)
      {
        size = entry[24] | (entry[25] << 8) | (entry[26] << 16);
      }

      entries[i] = new AunDirectoryEntry
      {
        Name = name,
        IsDirectory = isDirectory,
        Size = size,
      };

      offset += 27;
    }

    return entries;
  }

  private static string ToAcornPath(string path)
  {
    if (string.IsNullOrWhiteSpace(path) || path == "/" || path == ".")
      return ".";

    string normalized = path.Replace('\\', '/').Trim('/');
    if (normalized.Length == 0)
      return ".";

    return normalized.Replace('/', '.');
  }

  private static string ToAcornFilePath(string path)
  {
    if (string.IsNullOrWhiteSpace(path))
      throw new ArgumentException("Path cannot be empty.", nameof(path));

    string normalized = path.Replace('\\', '/').TrimStart('/');
    if (normalized.StartsWith("$.", StringComparison.Ordinal))
      return normalized;

    if (normalized.StartsWith('$'))
      return "$." + normalized.Substring(1).Replace('/', '.');

    return normalized.Replace('/', '.');
  }
}
