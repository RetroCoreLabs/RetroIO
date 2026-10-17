//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.X25.Monitor
{
    /// <summary>
    /// The monitoring wire protocol (X25MON/1): each message is a compact binary
    /// <see cref="MonitorMessage"/> in the protobuf wire format, preceded by a 4-byte big-endian length.
    /// Encoding and decoding are delegated to <see cref="MonitorBinaryCodec"/>; this type carries the shared
    /// message-type constants and the frame-size guard. New fields can be added without breaking existing
    /// clients (unknown field tags are skipped).
    /// </summary>
    public static class MonitorProtocol
    {
        /// <summary>
        /// Message type for a server-to-client capture record.
        /// </summary>
        public const string TypeCapture = "capture";

        /// <summary>
        /// Message type for a client-to-server filter update.
        /// </summary>
        public const string TypeFilter = "filter";

        /// <summary>
        /// Message type for a server-to-client link/port status snapshot.
        /// </summary>
        public const string TypeStatus = "status";

        /// <summary>
        /// Message type for a server-to-client log record.
        /// </summary>
        public const string TypeLog = "log";

        /// <summary>
        /// Message type for a server-to-client topology snapshot (self identity plus neighbours), used to
        /// discover and map the network on the management plane.
        /// </summary>
        public const string TypeTopology = "topology";

        /// <summary>
        /// Message type for a client greeting.
        /// </summary>
        public const string TypeHello = "hello";

        /// <summary>
        /// Maximum encoded message size accepted by the reader, guarding against a runaway length prefix.
        /// </summary>
        public const int MaxMessageSize = 1 << 20;

        /// <summary>
        /// Encodes a message into a length-prefixed X25MON/1 binary frame ready to write to the wire.
        /// </summary>
        /// <param name="message">
        /// The message to encode.
        /// </param>
        /// <returns>
        /// The framed bytes: a 4-byte big-endian payload length, the version and type bytes, then the body.
        /// </returns>
        public static byte[] Encode(MonitorMessage message)
        {
            return MonitorBinaryCodec.Encode(message);
        }

        /// <summary>
        /// Decodes a frame payload (the bytes after the 4-byte length prefix) into a message.
        /// </summary>
        /// <param name="body">
        /// The payload bytes of one message: <c>[u8 version][u8 msg_type][protobuf body]</c>.
        /// </param>
        /// <returns>
        /// The decoded message, or null when the payload is not valid.
        /// </returns>
        public static MonitorMessage? DecodeBody(ReadOnlySpan<byte> body)
        {
            return MonitorBinaryCodec.DecodeBody(body);
        }
    }

    /// <summary>
    /// A streaming reader that reassembles length-prefixed monitoring frames from an arbitrary byte stream
    /// (partial reads and several frames per read are handled) and raises a decoded message for each.
    /// </summary>
    public sealed class MonitorFrameReader
    {
        private const int HeaderSize = 4;

        private byte[] _buffer = new byte[4096];
        private int _length;

        /// <summary>
        /// Raised for each fully received and decoded message.
        /// </summary>
        /// <param name="message">
        /// The decoded message.
        /// </param>
        public delegate void MessageHandler(MonitorMessage message);

        /// <summary>
        /// Raised for each fully received and decoded message.
        /// </summary>
        public event MessageHandler? MessageReceived;

        /// <summary>
        /// Feeds received bytes into the reader, raising <see cref="MessageReceived"/> for every complete
        /// frame contained in the accumulated buffer.
        /// </summary>
        /// <param name="data">
        /// The freshly received bytes.
        /// </param>
        public void ProcessBytes(ReadOnlySpan<byte> data)
        {
            EnsureCapacity(_length + data.Length);
            data.CopyTo(_buffer.AsSpan(_length));
            _length += data.Length;

            int offset = 0;
            while (_length - offset >= HeaderSize)
            {
                int size = (_buffer[offset] << 24) | (_buffer[offset + 1] << 16)
                    | (_buffer[offset + 2] << 8) | _buffer[offset + 3];

                if (size < 0 || size > MonitorProtocol.MaxMessageSize)
                {
                    // Corrupt length — discard everything and resynchronize.
                    _length = 0;
                    return;
                }

                if (_length - offset - HeaderSize < size)
                {
                    // Body not fully arrived yet.
                    break;
                }

                MonitorMessage? message = MonitorProtocol.DecodeBody(_buffer.AsSpan(offset + HeaderSize, size));
                if (message != null)
                {
                    MessageReceived?.Invoke(message);
                }

                offset += HeaderSize + size;
            }

            if (offset > 0)
            {
                int remain = _length - offset;
                if (remain > 0)
                {
                    _buffer.AsSpan(offset, remain).CopyTo(_buffer.AsSpan(0));
                }
                _length = remain;
            }
        }

        private void EnsureCapacity(int needed)
        {
            if (needed <= _buffer.Length)
            {
                return;
            }

            int size = _buffer.Length;
            while (size < needed)
            {
                size *= 2;
            }

            byte[] grown = new byte[size];
            Array.Copy(_buffer, 0, grown, 0, _length);
            _buffer = grown;
        }
    }
}
