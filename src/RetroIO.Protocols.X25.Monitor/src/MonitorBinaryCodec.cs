//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;

namespace RetroIO.Protocols.X25.Monitor
{
    /// <summary>
    /// The X25MON/1 binary codec: encodes and decodes <see cref="MonitorMessage"/>s using the protobuf wire
    /// format (varint-tagged fields), hand-rolled with no protobuf dependency and no code generation.
    /// </summary>
    /// <remarks>
    /// The frame layout is <c>[u32 BE payload_len][u8 version][u8 msg_type][protobuf body]</c>. The leading
    /// <see cref="Version"/> and message-type byte let a reader dispatch the capture hot-path without parsing
    /// cold fields. Unknown field tags are skipped, so new fields never break old readers. See
    /// <c>docs/MONITOR_PROTOCOL_V1.md</c> for the full schema.
    /// </remarks>
    public static class MonitorBinaryCodec
    {
        /// <summary>
        /// The protocol version encoded in every frame.
        /// </summary>
        public const byte Version = 1;

        /// <summary>
        /// Message-type byte for a capture record.
        /// </summary>
        public const byte MsgCapture = 1;

        /// <summary>
        /// Message-type byte for a status snapshot.
        /// </summary>
        public const byte MsgStatus = 2;

        /// <summary>
        /// Message-type byte for a topology snapshot.
        /// </summary>
        public const byte MsgTopology = 3;

        /// <summary>
        /// Message-type byte for a log record.
        /// </summary>
        public const byte MsgLog = 4;

        /// <summary>
        /// Message-type byte for a filter update.
        /// </summary>
        public const byte MsgFilter = 5;

        /// <summary>
        /// Message-type byte for a client hello.
        /// </summary>
        public const byte MsgHello = 6;

        // protobuf wire types used here.
        private const int WtVarint = 0;
        private const int WtLen = 2;

        private const int HeaderSize = 4;

        /// <summary>
        /// Encodes a message into a length-prefixed X25MON/1 frame ready to write to the wire.
        /// </summary>
        /// <param name="message">
        /// The message to encode.
        /// </param>
        /// <returns>
        /// The framed bytes: a 4-byte big-endian payload length, the version and type bytes, then the body.
        /// </returns>
        public static byte[] Encode(MonitorMessage message)
        {
            PbWriter body = new PbWriter();
            byte type = EncodeBody(message, body);

            int payloadLen = 2 + body.Length; // version + type + body
            byte[] frame = new byte[HeaderSize + payloadLen];
            frame[0] = (byte)((payloadLen >> 24) & 0xFF);
            frame[1] = (byte)((payloadLen >> 16) & 0xFF);
            frame[2] = (byte)((payloadLen >> 8) & 0xFF);
            frame[3] = (byte)(payloadLen & 0xFF);
            frame[4] = Version;
            frame[5] = type;
            body.CopyTo(frame, 6);
            return frame;
        }

        /// <summary>
        /// Decodes a frame payload (the bytes after the 4-byte length prefix) into a message.
        /// </summary>
        /// <param name="payload">
        /// The payload bytes: <c>[u8 version][u8 msg_type][protobuf body]</c>.
        /// </param>
        /// <returns>
        /// The decoded message, or null when the payload is too short or the type is unknown.
        /// </returns>
        public static MonitorMessage? DecodeBody(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 2)
            {
                return null;
            }

            byte type = payload[1];
            ReadOnlySpan<byte> body = payload.Slice(2);

            switch (type)
            {
                case MsgCapture: return DecodeCapture(body);
                case MsgStatus: return DecodeStatus(body);
                case MsgTopology: return DecodeTopology(body);
                case MsgLog: return DecodeLog(body);
                case MsgFilter: return DecodeFilter(body);
                case MsgHello: return DecodeHello(body);
                default: return null;
            }
        }

        // ----- Encoders -----

        private static byte EncodeBody(MonitorMessage message, PbWriter body)
        {
            if (string.Equals(message.Type, MonitorProtocol.TypeCapture, StringComparison.Ordinal) && message.Capture != null)
            {
                EncodeCapture(message.Capture, body);
                return MsgCapture;
            }
            if (string.Equals(message.Type, MonitorProtocol.TypeStatus, StringComparison.Ordinal) && message.Status != null)
            {
                EncodeStatus(message.Status, body);
                return MsgStatus;
            }
            if (string.Equals(message.Type, MonitorProtocol.TypeTopology, StringComparison.Ordinal) && message.Topology != null)
            {
                EncodeTopology(message.Topology, body);
                return MsgTopology;
            }
            if (string.Equals(message.Type, MonitorProtocol.TypeLog, StringComparison.Ordinal) && message.Log != null)
            {
                EncodeLog(message.Log, body);
                return MsgLog;
            }
            if (string.Equals(message.Type, MonitorProtocol.TypeFilter, StringComparison.Ordinal) && message.Filter != null)
            {
                EncodeFilter(message.Filter, body);
                return MsgFilter;
            }
            // Anything else is treated as a hello (the greeting carries no payload today).
            return MsgHello;
        }

        private static void EncodeCapture(CaptureRecord r, PbWriter w)
        {
            w.VarintField(1, (ulong)r.Seq);
            w.VarintField(2, (ulong)(uint)r.Port);
            w.VarintField(3, (ulong)(int)r.Direction);
            w.VarintField(4, (ulong)r.TimestampMs);
            w.BytesField(5, r.Frame);
        }

        private static void EncodeStatus(StatusSnapshot s, PbWriter w)
        {
            w.VarintField(1, (ulong)s.TimestampMs);
            w.VarintField(2, (ulong)(uint)s.ActiveCircuits);
            for (int i = 0; i < s.Ports.Count; i++)
            {
                PbWriter sub = new PbWriter();
                EncodePortStatus(s.Ports[i], sub);
                w.MessageField(3, sub);
            }
        }

        private static void EncodePortStatus(PortStatus p, PbWriter w)
        {
            int flags = (p.Active ? 1 : 0) | (p.PeerConnected ? 2 : 0);
            w.VarintField(1, (ulong)(uint)p.Id);
            w.VarintField(2, ModeToInt(p.Mode));
            w.StringField(3, p.Routes);
            w.VarintField(4, (ulong)(uint)flags);
            w.VarintField(5, p.CallsIn);
            w.VarintField(6, p.CallsOut);
            w.VarintField(7, p.CallsCleared);
            w.VarintField(8, p.PacketsIn);
            w.VarintField(9, p.PacketsOut);
            w.VarintField(10, (ulong)(uint)p.ConnectAttempts);
            w.VarintField(11, (ulong)p.LastConnectedMs);
            w.VarintField(12, (ulong)p.LastDisconnectedMs);
            w.VarintField(13, (ulong)p.BytesSent);
            w.VarintField(14, (ulong)p.BytesReceived);
        }

        private static void EncodeTopology(TopologySnapshot t, PbWriter w)
        {
            w.StringField(1, t.NodeId);
            w.StringField(2, t.Name);
            w.StringField(3, t.Dnic);
            w.VarintField(4, (ulong)(uint)t.MonitorPort);
            w.VarintField(5, (ulong)(uint)t.MgmtPort);
            w.VarintField(6, (ulong)t.UptimeMs);
            for (int i = 0; i < t.Neighbours.Count; i++)
            {
                PbWriter sub = new PbWriter();
                EncodeNeighbour(t.Neighbours[i], sub);
                w.MessageField(7, sub);
            }
        }

        private static void EncodeNeighbour(NeighbourInfo n, PbWriter w)
        {
            w.StringField(1, n.Name);
            w.StringField(2, n.MonitorHost);
            w.VarintField(3, (ulong)(uint)n.MonitorPort);
            w.VarintField(4, (ulong)(uint)n.ViaPortId);
            w.VarintField(5, n.Up ? 1UL : 0UL);
        }

        private static void EncodeLog(LogRecord l, PbWriter w)
        {
            w.VarintField(1, (ulong)l.TimestampMs);
            w.VarintField(2, LevelToInt(l.Level));
            w.StringField(3, l.Category);
            w.StringField(4, l.Message);
        }

        private static void EncodeFilter(MonitorFilter f, PbWriter w)
        {
            for (int i = 0; i < f.Ports.Count; i++)
            {
                w.VarintField(1, (ulong)(uint)f.Ports[i]);
            }
            w.StringField(2, f.Src);
            w.StringField(3, f.Dst);
            for (int i = 0; i < f.PacketTypes.Count; i++)
            {
                w.StringField(4, f.PacketTypes[i]);
            }
        }

        // ----- Decoders -----

        private static MonitorMessage DecodeCapture(ReadOnlySpan<byte> body)
        {
            CaptureRecord r = new CaptureRecord();
            PbReader reader = new PbReader(body);
            while (!reader.End)
            {
                int field = reader.ReadTag(out int wt);
                switch (field)
                {
                    case 1: r.Seq = (long)reader.ReadVarint(); break;
                    case 2: r.Port = (int)reader.ReadVarint(); break;
                    case 3: r.Direction = (CaptureDirection)(int)reader.ReadVarint(); break;
                    case 4: r.TimestampMs = (long)reader.ReadVarint(); break;
                    case 5: r.Frame = reader.ReadBytes(); r.TotalLength = r.Frame.Length; break;
                    default: reader.Skip(wt); break;
                }
            }

            return new MonitorMessage { Type = MonitorProtocol.TypeCapture, Capture = r };
        }

        private static MonitorMessage DecodeStatus(ReadOnlySpan<byte> body)
        {
            StatusSnapshot s = new StatusSnapshot();
            PbReader reader = new PbReader(body);
            while (!reader.End)
            {
                int field = reader.ReadTag(out int wt);
                switch (field)
                {
                    case 1: s.TimestampMs = (long)reader.ReadVarint(); break;
                    case 2: s.ActiveCircuits = (int)reader.ReadVarint(); break;
                    case 3: s.Ports.Add(DecodePortStatus(reader.ReadLenDelimited())); break;
                    default: reader.Skip(wt); break;
                }
            }

            return new MonitorMessage { Type = MonitorProtocol.TypeStatus, Status = s };
        }

        private static PortStatus DecodePortStatus(ReadOnlySpan<byte> body)
        {
            PortStatus p = new PortStatus();
            PbReader reader = new PbReader(body);
            while (!reader.End)
            {
                int field = reader.ReadTag(out int wt);
                switch (field)
                {
                    case 1: p.Id = (int)reader.ReadVarint(); break;
                    case 2: p.Mode = IntToMode(reader.ReadVarint()); break;
                    case 3: p.Routes = reader.ReadString(); break;
                    case 4:
                    {
                        ulong flags = reader.ReadVarint();
                        p.Active = (flags & 1) != 0;
                        p.PeerConnected = (flags & 2) != 0;
                        break;
                    }
                    case 5: p.CallsIn = (uint)reader.ReadVarint(); break;
                    case 6: p.CallsOut = (uint)reader.ReadVarint(); break;
                    case 7: p.CallsCleared = (uint)reader.ReadVarint(); break;
                    case 8: p.PacketsIn = (uint)reader.ReadVarint(); break;
                    case 9: p.PacketsOut = (uint)reader.ReadVarint(); break;
                    case 10: p.ConnectAttempts = (int)reader.ReadVarint(); break;
                    case 11: p.LastConnectedMs = (long)reader.ReadVarint(); break;
                    case 12: p.LastDisconnectedMs = (long)reader.ReadVarint(); break;
                    case 13: p.BytesSent = (long)reader.ReadVarint(); break;
                    case 14: p.BytesReceived = (long)reader.ReadVarint(); break;
                    default: reader.Skip(wt); break;
                }
            }

            return p;
        }

        private static MonitorMessage DecodeTopology(ReadOnlySpan<byte> body)
        {
            TopologySnapshot t = new TopologySnapshot();
            PbReader reader = new PbReader(body);
            while (!reader.End)
            {
                int field = reader.ReadTag(out int wt);
                switch (field)
                {
                    case 1: t.NodeId = reader.ReadString(); break;
                    case 2: t.Name = reader.ReadString(); break;
                    case 3: t.Dnic = reader.ReadString(); break;
                    case 4: t.MonitorPort = (int)reader.ReadVarint(); break;
                    case 5: t.MgmtPort = (int)reader.ReadVarint(); break;
                    case 6: t.UptimeMs = (long)reader.ReadVarint(); break;
                    case 7: t.Neighbours.Add(DecodeNeighbour(reader.ReadLenDelimited())); break;
                    default: reader.Skip(wt); break;
                }
            }

            return new MonitorMessage { Type = MonitorProtocol.TypeTopology, Topology = t };
        }

        private static NeighbourInfo DecodeNeighbour(ReadOnlySpan<byte> body)
        {
            NeighbourInfo n = new NeighbourInfo();
            PbReader reader = new PbReader(body);
            while (!reader.End)
            {
                int field = reader.ReadTag(out int wt);
                switch (field)
                {
                    case 1: n.Name = reader.ReadString(); break;
                    case 2: n.MonitorHost = reader.ReadString(); break;
                    case 3: n.MonitorPort = (int)reader.ReadVarint(); break;
                    case 4: n.ViaPortId = (int)reader.ReadVarint(); break;
                    case 5: n.Up = reader.ReadVarint() != 0; break;
                    default: reader.Skip(wt); break;
                }
            }

            return n;
        }

        private static MonitorMessage DecodeLog(ReadOnlySpan<byte> body)
        {
            LogRecord l = new LogRecord();
            PbReader reader = new PbReader(body);
            while (!reader.End)
            {
                int field = reader.ReadTag(out int wt);
                switch (field)
                {
                    case 1: l.TimestampMs = (long)reader.ReadVarint(); break;
                    case 2: l.Level = IntToLevel(reader.ReadVarint()); break;
                    case 3: l.Category = reader.ReadString(); break;
                    case 4: l.Message = reader.ReadString(); break;
                    default: reader.Skip(wt); break;
                }
            }

            return new MonitorMessage { Type = MonitorProtocol.TypeLog, Log = l };
        }

        private static MonitorMessage DecodeFilter(ReadOnlySpan<byte> body)
        {
            MonitorFilter f = new MonitorFilter();
            PbReader reader = new PbReader(body);
            while (!reader.End)
            {
                int field = reader.ReadTag(out int wt);
                switch (field)
                {
                    case 1: f.Ports.Add((int)reader.ReadVarint()); break;
                    case 2: f.Src = reader.ReadString(); break;
                    case 3: f.Dst = reader.ReadString(); break;
                    case 4: f.PacketTypes.Add(reader.ReadString()); break;
                    default: reader.Skip(wt); break;
                }
            }

            return new MonitorMessage { Type = MonitorProtocol.TypeFilter, Filter = f };
        }

        private static MonitorMessage DecodeHello(ReadOnlySpan<byte> body)
        {
            PbReader reader = new PbReader(body);
            while (!reader.End)
            {
                int field = reader.ReadTag(out int wt);
                // Fields (1 proto_version, 2 client_name) are accepted but not surfaced today.
                reader.Skip(wt);
                _ = field;
            }

            return new MonitorMessage { Type = MonitorProtocol.TypeHello };
        }

        // ----- Enum mapping -----

        private static ulong ModeToInt(string mode)
        {
            // 0 = DCE, 1 = DTE.
            return string.Equals(mode, "Dte", StringComparison.OrdinalIgnoreCase) ? 1UL : 0UL;
        }

        private static string IntToMode(ulong v)
        {
            return v == 1 ? "Dte" : "Dce";
        }

        private static ulong LevelToInt(string level)
        {
            if (string.Equals(level, "Trace", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(level, "Debug", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(level, "Information", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(level, "Warning", StringComparison.OrdinalIgnoreCase)) return 3;
            if (string.Equals(level, "Error", StringComparison.OrdinalIgnoreCase)) return 4;
            if (string.Equals(level, "Critical", StringComparison.OrdinalIgnoreCase)) return 5;
            return 2; // default Information
        }

        private static string IntToLevel(ulong v)
        {
            switch (v)
            {
                case 0: return "Trace";
                case 1: return "Debug";
                case 2: return "Information";
                case 3: return "Warning";
                case 4: return "Error";
                case 5: return "Critical";
                default: return "Information";
            }
        }

        /// <summary>
        /// A minimal growable writer that emits protobuf wire-format fields.
        /// </summary>
        private sealed class PbWriter
        {
            private byte[] _buffer = new byte[64];
            private int _len;

            /// <summary>
            /// The number of bytes written so far.
            /// </summary>
            public int Length => _len;

            /// <summary>
            /// Writes a varint-encoded field key (field number and wire type).
            /// </summary>
            /// <param name="field">
            /// The field number.
            /// </param>
            /// <param name="wireType">
            /// The wire type.
            /// </param>
            public void Tag(int field, int wireType)
            {
                WriteVarint((ulong)((field << 3) | wireType));
            }

            /// <summary>
            /// Writes a varint field.
            /// </summary>
            /// <param name="field">
            /// The field number.
            /// </param>
            /// <param name="value">
            /// The value.
            /// </param>
            public void VarintField(int field, ulong value)
            {
                Tag(field, WtVarint);
                WriteVarint(value);
            }

            /// <summary>
            /// Writes a UTF-8 string as a length-delimited field.
            /// </summary>
            /// <param name="field">
            /// The field number.
            /// </param>
            /// <param name="value">
            /// The string; null is treated as empty.
            /// </param>
            public void StringField(int field, string value)
            {
                Tag(field, WtLen);
                if (string.IsNullOrEmpty(value))
                {
                    WriteVarint(0);
                    return;
                }

                int count = Encoding.UTF8.GetByteCount(value);
                WriteVarint((ulong)count);
                EnsureCapacity(count);
                Encoding.UTF8.GetBytes(value, 0, value.Length, _buffer, _len);
                _len += count;
            }

            /// <summary>
            /// Writes raw bytes as a length-delimited field.
            /// </summary>
            /// <param name="field">
            /// The field number.
            /// </param>
            /// <param name="value">
            /// The bytes; null is treated as empty.
            /// </param>
            public void BytesField(int field, byte[] value)
            {
                Tag(field, WtLen);
                int count = value != null ? value.Length : 0;
                WriteVarint((ulong)count);
                if (count > 0)
                {
                    EnsureCapacity(count);
                    Array.Copy(value!, 0, _buffer, _len, count);
                    _len += count;
                }
            }

            /// <summary>
            /// Writes a nested message as a length-delimited field.
            /// </summary>
            /// <param name="field">
            /// The field number.
            /// </param>
            /// <param name="sub">
            /// The writer holding the nested message body.
            /// </param>
            public void MessageField(int field, PbWriter sub)
            {
                Tag(field, WtLen);
                WriteVarint((ulong)sub._len);
                EnsureCapacity(sub._len);
                Array.Copy(sub._buffer, 0, _buffer, _len, sub._len);
                _len += sub._len;
            }

            /// <summary>
            /// Copies the written bytes into a destination buffer.
            /// </summary>
            /// <param name="dst">
            /// The destination buffer.
            /// </param>
            /// <param name="offset">
            /// The offset to copy to.
            /// </param>
            public void CopyTo(byte[] dst, int offset)
            {
                Array.Copy(_buffer, 0, dst, offset, _len);
            }

            private void WriteVarint(ulong value)
            {
                // 7 bits per byte, high bit set while more bytes follow.
                while (value >= 0x80)
                {
                    EnsureCapacity(1);
                    _buffer[_len++] = (byte)(value | 0x80);
                    value >>= 7;
                }
                EnsureCapacity(1);
                _buffer[_len++] = (byte)value;
            }

            private void EnsureCapacity(int extra)
            {
                int needed = _len + extra;
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
                Array.Copy(_buffer, 0, grown, 0, _len);
                _buffer = grown;
            }
        }

        /// <summary>
        /// A forward-only reader over a protobuf wire-format body.
        /// </summary>
        private ref struct PbReader
        {
            private readonly ReadOnlySpan<byte> _data;
            private int _pos;

            /// <summary>
            /// Creates a reader over the given body.
            /// </summary>
            /// <param name="data">
            /// The body bytes.
            /// </param>
            public PbReader(ReadOnlySpan<byte> data)
            {
                _data = data;
                _pos = 0;
            }

            /// <summary>
            /// True when all bytes have been consumed.
            /// </summary>
            public bool End => _pos >= _data.Length;

            /// <summary>
            /// Reads a field key, returning the field number and the wire type.
            /// </summary>
            /// <param name="wireType">
            /// Receives the wire type.
            /// </param>
            /// <returns>
            /// The field number.
            /// </returns>
            public int ReadTag(out int wireType)
            {
                ulong key = ReadVarint();
                wireType = (int)(key & 0x7);
                return (int)(key >> 3);
            }

            /// <summary>
            /// Reads a varint value.
            /// </summary>
            /// <returns>
            /// The decoded unsigned value.
            /// </returns>
            public ulong ReadVarint()
            {
                ulong result = 0;
                int shift = 0;
                while (_pos < _data.Length && shift < 64)
                {
                    byte b = _data[_pos++];
                    result |= (ulong)(b & 0x7F) << shift;
                    if ((b & 0x80) == 0)
                    {
                        break;
                    }
                    shift += 7;
                }

                return result;
            }

            /// <summary>
            /// Reads a length-delimited byte range without copying.
            /// </summary>
            /// <returns>
            /// A span over the length-delimited bytes.
            /// </returns>
            public ReadOnlySpan<byte> ReadLenDelimited()
            {
                int len = (int)ReadVarint();
                if (len < 0 || _pos + len > _data.Length)
                {
                    len = _data.Length - _pos;
                }

                ReadOnlySpan<byte> slice = _data.Slice(_pos, len);
                _pos += len;
                return slice;
            }

            /// <summary>
            /// Reads a length-delimited field as a copied byte array.
            /// </summary>
            /// <returns>
            /// The copied bytes.
            /// </returns>
            public byte[] ReadBytes()
            {
                return ReadLenDelimited().ToArray();
            }

            /// <summary>
            /// Reads a length-delimited field as a UTF-8 string.
            /// </summary>
            /// <returns>
            /// The decoded string.
            /// </returns>
            public string ReadString()
            {
                ReadOnlySpan<byte> slice = ReadLenDelimited();
                return slice.Length == 0 ? string.Empty : Encoding.UTF8.GetString(slice);
            }

            /// <summary>
            /// Skips a field of the given wire type.
            /// </summary>
            /// <param name="wireType">
            /// The wire type to skip.
            /// </param>
            public void Skip(int wireType)
            {
                if (wireType == WtVarint)
                {
                    ReadVarint();
                }
                else if (wireType == WtLen)
                {
                    ReadLenDelimited();
                }
                else if (wireType == 5)
                {
                    _pos += 4;
                }
                else if (wireType == 1)
                {
                    _pos += 8;
                }
            }
        }
    }
}
