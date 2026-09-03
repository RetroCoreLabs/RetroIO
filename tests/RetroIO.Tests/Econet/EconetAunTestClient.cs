//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.Protocol;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet;

/// <summary>
/// UDP AUN test client — one datagram out, ACK + FS reply in (same as BeebEm/DNFS).
/// </summary>
internal sealed class EconetAunTestClient : IDisposable
{
    private readonly Socket _socket;
    private readonly IPEndPoint _server;
    private readonly IPAddress _clientAddress = IPAddress.Loopback;
    private readonly byte _clientNet;
    private readonly byte _clientStation;

    private uint _sequence;
    private byte _replyPort = 0x80;
    private byte _dataPort = 0x40;
    private byte _urd;
    private byte _cwd;
    private byte _lib;
    private bool _loggedIn;

    public EconetAunTestClient(
        int serverPort = EconetTestPorts.UnitTestServerUdp,
        int clientPort = EconetTestPorts.UnitTestClientUdp,
        byte clientNet = 1,
        byte clientStation = 1)
    {
        _clientNet = clientNet;
        _clientStation = clientStation;
        _server = new IPEndPoint(IPAddress.Loopback, serverPort);

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Bind(new IPEndPoint(_clientAddress, clientPort));
        _socket.ReceiveTimeout = 5000;
    }

    public bool IsLoggedIn => _loggedIn;

    public byte WireUrd => _urd;
    public byte WireCwd => _cwd;
    public byte WireLib => _lib;

    public byte[] Login(string username, string? password = null)
    {
        string paddedUser = AcornPath.PadUsername(username.Trim());
        string paddedPass = AcornPath.PadPassword(password ?? string.Empty);
        byte[] payload = SendOsCli($"I AM {paddedUser} {paddedPass}\r", loggedIn: false);
        ApplyLoginIfSuccess(payload);
        return payload;
    }

    public byte[] Bye(bool sendWireHandles = false)
    {
        byte[] payload = SendOsCli("BYE\r", loggedIn: sendWireHandles && _loggedIn);
        if (payload.Length >= 2 && payload[0] == 0 && payload[1] == 0)
        {
            _loggedIn = false;
            _urd = 0;
            _cwd = 0;
            _lib = 0;
        }

        return payload;
    }

    public byte[] SendOsCli(string command, bool loggedIn)
    {
        byte[] parameters = Encoding.ASCII.GetBytes(command);
        return SendFileServerCommand(FileServerOpcodes.OsCli, parameters, loggedIn);
    }

    public byte[] LoadFile(string filename)
    {
        if (!_loggedIn)
            throw new InvalidOperationException("Not logged in.");

        byte[] nameBytes = Encoding.ASCII.GetBytes(filename + "\r");
        byte[] body = new byte[3 + nameBytes.Length];
        body[0] = _dataPort;
        body[1] = _cwd;
        body[2] = 0;
        nameBytes.CopyTo(body, 3);

        uint requestSequence = _sequence + 1;
        byte[] metadata = SendBulkFileServerCommand(FileServerOpcodes.Load, body);
        if (metadata.Length > 1 && metadata[1] != 0)
            throw new IOException($"Load failed (0x{metadata[1]:X2}).");

        SendAck(requestSequence);

        List<byte> fileData = new List<byte>();
        bool loadComplete = false;
        while (!loadComplete)
        {
            EconetMessage packet = ReceiveMessage();
            if (packet.PacketType == AunPacketType.Ack)
                continue;

            if (packet.EconetPort == _dataPort)
            {
                for (int i = 0; i < packet.Payload.Length; i++)
                    fileData.Add(packet.Payload[i]);

                SendAck(packet.Sequence);
                continue;
            }

            if (packet.EconetPort == _replyPort &&
                packet.Payload.Length >= 2 &&
                packet.Payload[0] == 0 &&
                packet.Payload[1] == 0)
            {
                SendReplyAck(packet.Sequence, packet.EconetPort);
                loadComplete = true;
            }
        }

        return fileData.ToArray();
    }

    /// <summary>
    /// Perform an Acorn SAVE (fsop 0x01): request → setup reply (bulk port) → ACK-paced data burst →
    /// 21-byte close packet. Returns the server's completion reply payload.
    /// </summary>
    public byte[] SaveFile(string filename, byte[] data, uint load = 0, uint exec = 0)
    {
        if (!_loggedIn)
            throw new InvalidOperationException("Not logged in.");

        byte[] nameBytes = Encoding.ASCII.GetBytes(filename + "\r");
        byte[] body = new byte[14 + nameBytes.Length];
        body[0] = _dataPort;              // raw[2] = ack port
        WriteUInt32Le(body, 3, load);     // raw[5..8]
        WriteUInt32Le(body, 7, exec);     // raw[9..12]
        body[11] = (byte)data.Length;     // raw[13]
        body[12] = (byte)(data.Length >> 8);
        body[13] = (byte)(data.Length >> 16);
        nameBytes.CopyTo(body, 14);       // raw[16..]

        byte[] setup = SendBulkFileServerCommand(FileServerOpcodes.Save, body);
        if (setup.Length < 5 || setup[1] != 0)
            throw new IOException($"Save setup failed (0x{(setup.Length > 1 ? setup[1] : 0):X2}).");

        byte bulkPort = setup[2];
        byte[] completion = [];

        int offset = 0;
        while (offset < data.Length)
        {
            int chunk = Math.Min(1280, data.Length - offset);
            byte[] payload = new byte[chunk];
            Array.Copy(data, offset, payload, 0, chunk);
            offset += chunk;

            _sequence++;
            EconetMessage dataPacket = new EconetMessage
            {
                PacketType = AunPacketType.Unicast,
                EconetPort = bulkPort,
                Control = 0x80,
                Sequence = _sequence,
                Payload = payload,
                ClientAddress = _clientAddress,
                ClientPort = ((IPEndPoint)_socket.LocalEndPoint!).Port,
                ClientNet = _clientNet,
                ClientStation = _clientStation,
            };
            SendMessage(dataPacket);

            EconetMessage response = ReceiveNonAck();
            if (response.EconetPort == _replyPort)
            {
                completion = response.Payload;
                SendReplyAck(response.Sequence, response.EconetPort);
                break;
            }

            // Otherwise an intermediate ACK ([0,0] on the ack port): continue the burst.
        }

        return completion;
    }

    public void Dispose() => _socket.Dispose();

    private EconetMessage ReceiveNonAck()
    {
        while (true)
        {
            EconetMessage message = ReceiveMessage();
            if (message.PacketType != AunPacketType.Ack)
                return message;
        }
    }

    private static void WriteUInt32Le(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private byte[] SendBulkFileServerCommand(byte opcode, ReadOnlySpan<byte> bodyFromByte2)
    {
        byte[] fsPayload = new byte[2 + bodyFromByte2.Length];
        fsPayload[0] = _replyPort;
        fsPayload[1] = opcode;
        bodyFromByte2.CopyTo(fsPayload.AsSpan(2));
        return SendFileServerPayload(fsPayload);
    }

    private void SendAck(uint sequence)
    {
        EconetMessage ack = new EconetMessage
        {
            PacketType = AunPacketType.Ack,
            EconetPort = _dataPort,
            Control = 0x80,
            Sequence = sequence,
            Payload = [],
            ClientAddress = _clientAddress,
            ClientPort = ((IPEndPoint)_socket.LocalEndPoint!).Port,
            ClientNet = _clientNet,
            ClientStation = _clientStation,
        };

        SendMessage(ack);
    }

    private void SendMessage(EconetMessage message)
    {
        byte[] buffer = new byte[AunPacket.HeaderSize + message.Payload.Length];
        AunPacket.Encode(
            buffer,
            message.PacketType,
            message.EconetPort,
            message.Control,
            message.Sequence,
            message.Payload);

        int sent = _socket.SendTo(buffer, _server);
        if (sent != buffer.Length)
            throw new IOException("Incomplete AUN send.");
    }

    private EconetMessage ReceiveMessage()
    {
        byte[] buffer = new byte[4096];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        int received = _socket.ReceiveFrom(buffer, ref remote);
        if (!AunPacket.TryDecode(
                buffer.AsSpan(0, received),
                out AunPacketType type,
                out byte port,
                out byte control,
                out uint sequence,
                out int payloadOffset))
        {
            throw new IOException("Invalid AUN datagram.");
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
            ClientPort = ((IPEndPoint)_socket.LocalEndPoint!).Port,
            ClientNet = _clientNet,
            ClientStation = _clientStation,
        };
    }

    private byte[] SendFileServerCommand(byte opcode, byte[] parameters, bool loggedIn)
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

    private byte[] SendFileServerPayload(byte[] fsPayload)
    {
        _sequence++;
        byte[] datagram = new byte[AunPacket.HeaderSize + fsPayload.Length];
        AunPacket.Encode(
            datagram,
            AunPacketType.Unicast,
            EconetPorts.FileServer,
            0x80,
            _sequence,
            fsPayload);

        int sent = _socket.SendTo(datagram, _server);
        if (sent != datagram.Length)
            throw new IOException("Incomplete AUN send.");

        return ReceiveFsPayload();
    }

    private byte[] ReceiveFsPayload()
    {
        while (true)
        {
            EconetMessage message = ReceiveMessage();
            if (message.PacketType == AunPacketType.Ack)
                continue;

            // Acknowledge the FS reply so the server's reliable-delivery loop completes and stops
            // retransmitting (mirrors a real AUN client / BeebEm in AUN mode).
            SendReplyAck(message.Sequence, message.EconetPort);
            return message.Payload;
        }
    }

    private void SendReplyAck(uint sequence, byte port)
    {
        EconetMessage ack = new EconetMessage
        {
            PacketType = AunPacketType.Ack,
            EconetPort = port,
            Control = 0x80,
            Sequence = sequence,
            Payload = [],
            ClientAddress = _clientAddress,
            ClientPort = ((IPEndPoint)_socket.LocalEndPoint!).Port,
            ClientNet = _clientNet,
            ClientStation = _clientStation,
        };

        SendMessage(ack);
    }

    private void ApplyLoginIfSuccess(byte[] payload)
    {
        if (payload.Length < 6 || payload[0] != FileServerOpcodes.LoginSuccessReturnCode || payload[1] != 0)
            return;

        _urd = payload[2];
        _cwd = payload[3];
        _lib = payload[4];
        _loggedIn = true;
    }
}
