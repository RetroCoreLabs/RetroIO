using System;
using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Protocols.X25.Monitor;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Round-trip tests for the X25MON/1 binary codec: every message type must encode and decode back to an
    /// equal value, unknown fields must be skipped, and the frame must carry the version and type header.
    /// </summary>
    [TestFixture]
    public class MonitorBinaryCodecTests
    {
        // Encodes then decodes, stripping the 4-byte length prefix the way the frame reader does.
        private static MonitorMessage? Roundtrip(MonitorMessage message)
        {
            byte[] frame = MonitorBinaryCodec.Encode(message);
            // The first 4 bytes are the big-endian payload length; the payload is version+type+body.
            int payloadLen = (frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3];
            Assert.That(payloadLen, Is.EqualTo(frame.Length - 4), "length prefix matches payload size");
            Assert.That(frame[4], Is.EqualTo(MonitorBinaryCodec.Version), "version byte present");
            return MonitorBinaryCodec.DecodeBody(new ReadOnlySpan<byte>(frame, 4, frame.Length - 4));
        }

        [Test]
        public void Capture_RoundTrips_WithRawFrame()
        {
            byte[] raw = { 0x10, 0x01, 0x0B, 0xFB, 0x00, 0x00, 0x7A };
            CaptureRecord r = new CaptureRecord
            {
                Seq = 42,
                Port = 3,
                Direction = CaptureDirection.Tx,
                TimestampMs = 1700000000123,
                Frame = raw
            };

            MonitorMessage? decoded = Roundtrip(new MonitorMessage { Type = MonitorProtocol.TypeCapture, Capture = r });

            Assert.That(decoded, Is.Not.Null);
            Assert.That(decoded!.Type, Is.EqualTo(MonitorProtocol.TypeCapture));
            CaptureRecord got = decoded.Capture!;
            Assert.That(got.Seq, Is.EqualTo(42));
            Assert.That(got.Port, Is.EqualTo(3));
            Assert.That(got.Direction, Is.EqualTo(CaptureDirection.Tx));
            Assert.That(got.TimestampMs, Is.EqualTo(1700000000123));
            Assert.That(got.Frame, Is.EqualTo(raw));
            Assert.That(got.TotalLength, Is.EqualTo(raw.Length));
        }

        [Test]
        public void Status_RoundTrips_AllPortFields()
        {
            StatusSnapshot s = new StatusSnapshot { TimestampMs = 5000, ActiveCircuits = 7 };
            s.Ports.Add(new PortStatus
            {
                Id = 2,
                Mode = "Dte",
                Routes = "prefix 2352, default",
                Active = true,
                PeerConnected = true,
                CallsIn = 11,
                CallsOut = 22,
                CallsCleared = 3,
                PacketsIn = 100,
                PacketsOut = 99,
                ConnectAttempts = 5,
                LastConnectedMs = 123456,
                LastDisconnectedMs = 120000,
                BytesSent = 8888,
                BytesReceived = 7777
            });

            MonitorMessage? decoded = Roundtrip(new MonitorMessage { Type = MonitorProtocol.TypeStatus, Status = s });

            Assert.That(decoded!.Type, Is.EqualTo(MonitorProtocol.TypeStatus));
            StatusSnapshot got = decoded.Status!;
            Assert.That(got.TimestampMs, Is.EqualTo(5000));
            Assert.That(got.ActiveCircuits, Is.EqualTo(7));
            Assert.That(got.Ports.Count, Is.EqualTo(1));
            PortStatus p = got.Ports[0];
            Assert.That(p.Id, Is.EqualTo(2));
            Assert.That(p.Mode, Is.EqualTo("Dte"));
            Assert.That(p.Routes, Is.EqualTo("prefix 2352, default"));
            Assert.That(p.Active, Is.True);
            Assert.That(p.PeerConnected, Is.True);
            Assert.That(p.CallsIn, Is.EqualTo(11u));
            Assert.That(p.CallsOut, Is.EqualTo(22u));
            Assert.That(p.CallsCleared, Is.EqualTo(3u));
            Assert.That(p.PacketsIn, Is.EqualTo(100u));
            Assert.That(p.PacketsOut, Is.EqualTo(99u));
            Assert.That(p.ConnectAttempts, Is.EqualTo(5));
            Assert.That(p.LastConnectedMs, Is.EqualTo(123456));
            Assert.That(p.LastDisconnectedMs, Is.EqualTo(120000));
            Assert.That(p.BytesSent, Is.EqualTo(8888));
            Assert.That(p.BytesReceived, Is.EqualTo(7777));
        }

        [Test]
        public void Topology_RoundTrips_WithNeighbours()
        {
            TopologySnapshot t = new TopologySnapshot
            {
                NodeId = "2352",
                Name = "Switch-C",
                Dnic = "2352",
                MonitorPort = 4003,
                MgmtPort = 9103,
                UptimeMs = 42000
            };
            t.Neighbours.Add(new NeighbourInfo
            {
                Name = "Switch-B",
                MonitorHost = "127.0.0.1",
                MonitorPort = 4002,
                ViaPortId = 0,
                Up = true
            });
            t.Neighbours.Add(new NeighbourInfo
            {
                Name = "WSL-Reference",
                MonitorHost = string.Empty,
                MonitorPort = 0,
                ViaPortId = 2,
                Up = false
            });

            MonitorMessage? decoded = Roundtrip(new MonitorMessage { Type = MonitorProtocol.TypeTopology, Topology = t });

            TopologySnapshot got = decoded!.Topology!;
            Assert.That(got.Name, Is.EqualTo("Switch-C"));
            Assert.That(got.MonitorPort, Is.EqualTo(4003));
            Assert.That(got.MgmtPort, Is.EqualTo(9103));
            Assert.That(got.UptimeMs, Is.EqualTo(42000));
            Assert.That(got.Neighbours.Count, Is.EqualTo(2));
            Assert.That(got.Neighbours[0].Name, Is.EqualTo("Switch-B"));
            Assert.That(got.Neighbours[0].MonitorPort, Is.EqualTo(4002));
            Assert.That(got.Neighbours[0].Up, Is.True);
            Assert.That(got.Neighbours[1].Name, Is.EqualTo("WSL-Reference"));
            Assert.That(got.Neighbours[1].MonitorPort, Is.EqualTo(0));
            Assert.That(got.Neighbours[1].Up, Is.False);
        }

        [Test]
        public void Log_RoundTrips_LevelAndText()
        {
            LogRecord l = new LogRecord
            {
                TimestampMs = 1700000000456,
                Level = "Warning",
                Category = "RetroX25.Switch.XotSwitchPort",
                Message = "Port 2: connection down (remote closed the connection)"
            };

            MonitorMessage? decoded = Roundtrip(new MonitorMessage { Type = MonitorProtocol.TypeLog, Log = l });

            LogRecord got = decoded!.Log!;
            Assert.That(got.TimestampMs, Is.EqualTo(1700000000456));
            Assert.That(got.Level, Is.EqualTo("Warning"));
            Assert.That(got.Category, Is.EqualTo("RetroX25.Switch.XotSwitchPort"));
            Assert.That(got.Message, Does.Contain("connection down"));
        }

        [Test]
        public void Filter_RoundTrips_AllDimensions()
        {
            MonitorFilter f = new MonitorFilter
            {
                Ports = new List<int> { 0, 2, 5 },
                Src = "2340",
                Dst = "2352",
                PacketTypes = new List<string> { "CallRequest", "Data" }
            };

            MonitorMessage? decoded = Roundtrip(new MonitorMessage { Type = MonitorProtocol.TypeFilter, Filter = f });

            MonitorFilter got = decoded!.Filter!;
            Assert.That(got.Ports, Is.EqualTo(new List<int> { 0, 2, 5 }));
            Assert.That(got.Src, Is.EqualTo("2340"));
            Assert.That(got.Dst, Is.EqualTo("2352"));
            Assert.That(got.PacketTypes, Is.EqualTo(new List<string> { "CallRequest", "Data" }));
        }

        [Test]
        public void UnknownField_IsSkipped_ForwardCompatible()
        {
            // Encode a log message, then splice in an unknown varint field (field 99) before decoding to prove
            // old readers skip new fields.
            LogRecord l = new LogRecord { TimestampMs = 10, Level = "Error", Category = "c", Message = "m" };
            byte[] frame = MonitorBinaryCodec.Encode(new MonitorMessage { Type = MonitorProtocol.TypeLog, Log = l });

            // Append an extra field 99 (varint) to the body: key = (99<<3)|0 = 792 -> varint 0x98 0x06, value 1.
            byte[] extended = new byte[frame.Length + 3];
            Array.Copy(frame, extended, frame.Length);
            extended[frame.Length] = 0x98;
            extended[frame.Length + 1] = 0x06;
            extended[frame.Length + 2] = 0x01;
            // Fix the length prefix to include the 3 extra body bytes.
            int newPayload = (frame[0] << 24 | frame[1] << 16 | frame[2] << 8 | frame[3]) + 3;
            extended[0] = (byte)((newPayload >> 24) & 0xFF);
            extended[1] = (byte)((newPayload >> 16) & 0xFF);
            extended[2] = (byte)((newPayload >> 8) & 0xFF);
            extended[3] = (byte)(newPayload & 0xFF);

            MonitorMessage? decoded = MonitorBinaryCodec.DecodeBody(new ReadOnlySpan<byte>(extended, 4, extended.Length - 4));

            Assert.That(decoded, Is.Not.Null);
            Assert.That(decoded!.Log!.Message, Is.EqualTo("m"), "known fields still decode with an unknown field present");
        }

        [Test]
        public void EmptyStrings_And_EmptyFrame_RoundTrip()
        {
            CaptureRecord r = new CaptureRecord { Seq = 1, Port = 0, Frame = Array.Empty<byte>() };
            MonitorMessage? decoded = Roundtrip(new MonitorMessage { Type = MonitorProtocol.TypeCapture, Capture = r });
            Assert.That(decoded!.Capture!.Frame.Length, Is.EqualTo(0));

            TopologySnapshot t = new TopologySnapshot { Name = string.Empty, Dnic = string.Empty };
            MonitorMessage? d2 = Roundtrip(new MonitorMessage { Type = MonitorProtocol.TypeTopology, Topology = t });
            Assert.That(d2!.Topology!.Name, Is.EqualTo(string.Empty));
        }
    }
}
