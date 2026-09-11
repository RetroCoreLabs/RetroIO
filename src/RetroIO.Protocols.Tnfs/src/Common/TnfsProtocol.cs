//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using RetroIO.Protocols.Tnfs.Protocol;

namespace RetroIO.Protocols.Tnfs.Common
{
    /// <summary>
    /// TNFS protocol packet structure and parsing utilities.
    /// Shared between client and server implementations.
    /// </summary>
    public static class TnfsProtocol
    {
        /// <summary>
        /// TNFS packet header size (4 bytes: SessionID, SequenceNumber, Command).
        /// </summary>
        public const int HeaderSize = 4;

        /// <summary>
        /// Minimum valid packet size (header only).
        /// </summary>
        public const int MinPacketSize = HeaderSize;

        /// <summary>
        /// Parse TNFS packet header.
        /// </summary>
        /// <param name="packet">Packet bytes</param>
        /// <returns>Parsed header or null if invalid</returns>
        public static TnfsPacketHeader? ParseHeader(byte[] packet)
        {
            if (packet == null || packet.Length < HeaderSize)
            {
                return null;
            }

            return new TnfsPacketHeader
            {
                SessionId = TnfsEndian.ReadUInt16(packet, 0),
                SequenceNumber = packet[2],
                Command = (TnfsCommand)packet[3]
            };
        }

        /// <summary>
        /// Build TNFS packet with header and optional payload.
        /// </summary>
        /// <param name="sessionId">Session ID</param>
        /// <param name="sequenceNumber">Sequence number</param>
        /// <param name="command">Command code</param>
        /// <param name="payload">Optional payload data</param>
        /// <returns>Complete packet bytes</returns>
        public static byte[] BuildPacket(ushort sessionId, byte sequenceNumber, TnfsCommand command, byte[]? payload = null)
        {
            int payloadLen = payload?.Length ?? 0;
            byte[] packet = new byte[HeaderSize + payloadLen];

            // Header: Session ID (little-endian)
            TnfsEndian.WriteUInt16(packet, 0, sessionId);

            // Header: Sequence number
            packet[2] = sequenceNumber;

            // Header: Command
            packet[3] = (byte)command;

            // Payload
            if (payload != null && payloadLen > 0)
            {
                Array.Copy(payload, 0, packet, HeaderSize, payloadLen);
            }

            return packet;
        }

        /// <summary>
        /// Extract payload from TNFS packet.
        /// </summary>
        /// <param name="packet">Complete packet</param>
        /// <returns>Payload bytes (empty if no payload)</returns>
        public static byte[] GetPayload(byte[] packet)
        {
            if (packet == null || packet.Length <= HeaderSize)
            {
                return Array.Empty<byte>();
            }

            int payloadLen = packet.Length - HeaderSize;
            byte[] payload = new byte[payloadLen];
            Array.Copy(packet, HeaderSize, payload, 0, payloadLen);
            return payload;
        }

        /// <summary>
        /// Get return code from response packet.
        /// </summary>
        /// <param name="packet">Response packet</param>
        /// <returns>Return code or null if invalid</returns>
        public static TnfsReturnCode? GetReturnCode(byte[] packet)
        {
            if (packet == null || packet.Length < HeaderSize + 1)
            {
                return null;
            }

            return (TnfsReturnCode)packet[HeaderSize];
        }

        /// <summary>
        /// Build response packet with return code and optional data.
        /// </summary>
        /// <param name="sessionId">Session ID</param>
        /// <param name="sequenceNumber">Sequence number</param>
        /// <param name="command">Command being responded to</param>
        /// <param name="returnCode">Return code</param>
        /// <param name="data">Optional response data</param>
        /// <returns>Complete response packet</returns>
        public static byte[] BuildResponse(ushort sessionId, byte sequenceNumber, TnfsCommand command, TnfsReturnCode returnCode, byte[]? data = null)
        {
            int dataLen = data?.Length ?? 0;
            byte[] payload = new byte[1 + dataLen];

            // Payload: Return code
            payload[0] = (byte)returnCode;

            // Payload: Response data
            if (data != null && dataLen > 0)
            {
                Array.Copy(data, 0, payload, 1, dataLen);
            }

            return BuildPacket(sessionId, sequenceNumber, command, payload);
        }
    }

    /// <summary>
    /// TNFS packet header structure.
    /// </summary>
    public sealed class TnfsPacketHeader
    {
        /// <summary>Session ID (0 for mount request)</summary>
        public ushort SessionId { get; set; }

        /// <summary>Sequence number (increments per request)</summary>
        public byte SequenceNumber { get; set; }

        /// <summary>Command code</summary>
        public TnfsCommand Command { get; set; }
    }

    /// <summary>
    /// TNFS request message (client to server).
    /// </summary>
    public sealed class TnfsRequest
    {
        /// <summary>Packet header</summary>
        public TnfsPacketHeader Header { get; set; } = new TnfsPacketHeader();

        /// <summary>Request payload (command-specific data)</summary>
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        /// <summary>Parse request from packet bytes</summary>
        public static TnfsRequest? Parse(byte[] packet)
        {
            TnfsPacketHeader? header = TnfsProtocol.ParseHeader(packet);
            if (header == null)
            {
                return null;
            }

            return new TnfsRequest
            {
                Header = header,
                Payload = TnfsProtocol.GetPayload(packet)
            };
        }

        /// <summary>Build packet bytes from request</summary>
        public byte[] ToPacket()
        {
            return TnfsProtocol.BuildPacket(Header.SessionId, Header.SequenceNumber, Header.Command, Payload);
        }
    }

    /// <summary>
    /// TNFS response message (server to client).
    /// </summary>
    public sealed class TnfsResponse
    {
        /// <summary>Packet header</summary>
        public TnfsPacketHeader Header { get; set; } = new TnfsPacketHeader();

        /// <summary>Return code</summary>
        public TnfsReturnCode ReturnCode { get; set; }

        /// <summary>Response data (after return code)</summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();

        /// <summary>Was the operation successful?</summary>
        public bool Success => ReturnCode == TnfsReturnCode.SUCCESS;

        /// <summary>Parse response from packet bytes</summary>
        public static TnfsResponse? Parse(byte[] packet)
        {
            TnfsPacketHeader? header = TnfsProtocol.ParseHeader(packet);
            if (header == null)
            {
                return null;
            }

            TnfsReturnCode? returnCode = TnfsProtocol.GetReturnCode(packet);
            if (returnCode == null)
            {
                return null;
            }

            byte[] payload = TnfsProtocol.GetPayload(packet);
            byte[] data = Array.Empty<byte>();

            if (payload.Length > 1)
            {
                data = new byte[payload.Length - 1];
                Array.Copy(payload, 1, data, 0, data.Length);
            }

            return new TnfsResponse
            {
                Header = header,
                ReturnCode = returnCode.Value,
                Data = data
            };
        }

        /// <summary>Build packet bytes from response</summary>
        public byte[] ToPacket()
        {
            return TnfsProtocol.BuildResponse(Header.SessionId, Header.SequenceNumber, Header.Command, ReturnCode, Data);
        }
    }
}
