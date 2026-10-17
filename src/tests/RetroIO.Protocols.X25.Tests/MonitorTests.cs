//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;
using RetroIO.Protocols.X25.Monitor;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Unit tests for the monitoring library: server-side X.25 decode, length-prefixed JSON framing (including
    /// split and coalesced reads), and server-side filter matching.
    /// </summary>
    [TestFixture]
    public class MonitorTests
    {
        [Test]
        public void Decoder_CallRequest_ProducesLayeredFields()
        {
            byte[] packet = X25PacketBuilder.CreateCallRequest(1, "23410001", "23400001", null, null);

            CaptureRecord record = X25CaptureDecoder.Decode(packet, 0, CaptureDirection.Rx, 7, 12345);

            Assert.That(record.Seq, Is.EqualTo(7));
            Assert.That(record.Port, Is.EqualTo(0));
            Assert.That(record.Direction, Is.EqualTo(CaptureDirection.Rx));
            Assert.That(record.PacketType, Is.EqualTo("CallRequest"));
            Assert.That(record.Called, Is.EqualTo("23410001"));
            Assert.That(record.Calling, Is.EqualTo("23400001"));
            Assert.That(record.Layers.Count, Is.EqualTo(1));
            Assert.That(record.Layers[0].Name, Is.EqualTo("X.25"));
            Assert.That(record.Hex.Length, Is.GreaterThan(0));
        }

        [Test]
        public void Framing_RoundTrips_AcrossSplitReads()
        {
            MonitorMessage a = new MonitorMessage
            {
                Type = MonitorProtocol.TypeCapture,
                Capture = new CaptureRecord { Seq = 1, Port = 3, Frame = new byte[] { 0x10, 0x01, 0x05 } }
            };
            MonitorMessage b = new MonitorMessage
            {
                Type = MonitorProtocol.TypeFilter,
                Filter = new MonitorFilter { Ports = new List<int> { 0, 1 }, Dst = "2341" }
            };

            byte[] frameA = MonitorProtocol.Encode(a);
            byte[] frameB = MonitorProtocol.Encode(b);
            byte[] both = new byte[frameA.Length + frameB.Length];
            System.Array.Copy(frameA, 0, both, 0, frameA.Length);
            System.Array.Copy(frameB, 0, both, frameA.Length, frameB.Length);

            List<MonitorMessage> received = new List<MonitorMessage>();
            MonitorFrameReader reader = new MonitorFrameReader();
            reader.MessageReceived += m => received.Add(m);

            // Feed one byte at a time to prove partial-read reassembly.
            for (int i = 0; i < both.Length; i++)
            {
                reader.ProcessBytes(both.AsSpan(i, 1));
            }

            Assert.That(received.Count, Is.EqualTo(2));
            Assert.That(received[0].Type, Is.EqualTo(MonitorProtocol.TypeCapture));
            Assert.That(received[0].Capture!.Frame, Is.EqualTo(new byte[] { 0x10, 0x01, 0x05 }));
            Assert.That(received[1].Type, Is.EqualTo(MonitorProtocol.TypeFilter));
            Assert.That(received[1].Filter!.Ports, Is.EqualTo(new List<int> { 0, 1 }));
            Assert.That(received[1].Filter!.Dst, Is.EqualTo("2341"));
        }

        [Test]
        public void Filter_FromText_ParsesFields()
        {
            MonitorFilter filter = MonitorFilter.FromText(" 0, 1 ", "2340", "2341", "CallRequest, Data");

            Assert.That(filter.Ports, Is.EqualTo(new List<int> { 0, 1 }));
            Assert.That(filter.Src, Is.EqualTo("2340"));
            Assert.That(filter.Dst, Is.EqualTo("2341"));
            Assert.That(filter.PacketTypes, Is.EqualTo(new List<string> { "CallRequest", "Data" }));
        }

        [Test]
        public void Filter_FromText_BlankIsMatchAll()
        {
            MonitorFilter filter = MonitorFilter.FromText("", "", "", "");

            Assert.That(filter.Ports.Count, Is.EqualTo(0));
            Assert.That(filter.PacketTypes.Count, Is.EqualTo(0));
            Assert.That(filter.Matches(new CaptureRecord { Port = 5, PacketType = "Data" }), Is.True);
        }

        [Test]
        public void Framing_StatusMessage_RoundTrips()
        {
            StatusSnapshot snapshot = new StatusSnapshot
            {
                TimestampMs = 500,
                ActiveCircuits = 3
            };
            snapshot.Ports.Add(new PortStatus
            {
                Id = 1,
                Mode = "Dce",
                Active = true,
                CallsIn = 4,
                CallsOut = 2,
                CallsCleared = 1,
                PacketsIn = 10,
                PacketsOut = 9
            });

            byte[] frame = MonitorProtocol.Encode(new MonitorMessage
            {
                Type = MonitorProtocol.TypeStatus,
                Status = snapshot
            });

            MonitorMessage? decoded = null;
            MonitorFrameReader reader = new MonitorFrameReader();
            reader.MessageReceived += m => decoded = m;
            reader.ProcessBytes(frame);

            Assert.That(decoded, Is.Not.Null);
            Assert.That(decoded!.Type, Is.EqualTo(MonitorProtocol.TypeStatus));
            Assert.That(decoded.Status!.ActiveCircuits, Is.EqualTo(3));
            Assert.That(decoded.Status.Ports.Count, Is.EqualTo(1));
            Assert.That(decoded.Status.Ports[0].Id, Is.EqualTo(1));
            Assert.That(decoded.Status.Ports[0].Active, Is.True);
            Assert.That(decoded.Status.Ports[0].PacketsIn, Is.EqualTo(10u));
        }

        [Test]
        public void Framing_LogMessage_RoundTrips()
        {
            LogRecord record = new LogRecord
            {
                TimestampMs = 1700000000123,
                Level = "Warning",
                Category = "RetroX25.Switch.XotSwitchPort",
                Message = "Port 2: peer disconnected (remote closed the connection (read 0))"
            };

            byte[] frame = MonitorProtocol.Encode(new MonitorMessage
            {
                Type = MonitorProtocol.TypeLog,
                Log = record
            });

            MonitorMessage? decoded = null;
            MonitorFrameReader reader = new MonitorFrameReader();
            reader.MessageReceived += m => decoded = m;
            reader.ProcessBytes(frame);

            Assert.That(decoded, Is.Not.Null);
            Assert.That(decoded!.Type, Is.EqualTo(MonitorProtocol.TypeLog));
            Assert.That(decoded.Log!.Level, Is.EqualTo("Warning"));
            Assert.That(decoded.Log.TimestampMs, Is.EqualTo(1700000000123));
            Assert.That(decoded.Log.Message, Does.Contain("peer disconnected"));
        }

        [Test]
        public void Framing_TopologyMessage_RoundTrips()
        {
            TopologySnapshot topology = new TopologySnapshot
            {
                NodeId = "2352",
                Name = "SwitchA",
                Dnic = "2352",
                MonitorPort = 9101,
                MgmtPort = 9001,
                UptimeMs = 42000
            };
            topology.Neighbours.Add(new NeighbourInfo
            {
                Name = "SwitchB",
                MonitorHost = "192.168.1.181",
                MonitorPort = 9102,
                ViaPortId = 4,
                Up = true
            });

            byte[] frame = MonitorProtocol.Encode(new MonitorMessage
            {
                Type = MonitorProtocol.TypeTopology,
                Topology = topology
            });

            MonitorMessage? decoded = null;
            MonitorFrameReader reader = new MonitorFrameReader();
            reader.MessageReceived += m => decoded = m;
            reader.ProcessBytes(frame);

            Assert.That(decoded, Is.Not.Null);
            Assert.That(decoded!.Type, Is.EqualTo(MonitorProtocol.TypeTopology));
            Assert.That(decoded.Topology!.Name, Is.EqualTo("SwitchA"));
            Assert.That(decoded.Topology.MonitorPort, Is.EqualTo(9101));
            Assert.That(decoded.Topology.Neighbours.Count, Is.EqualTo(1));
            Assert.That(decoded.Topology.Neighbours[0].Name, Is.EqualTo("SwitchB"));
            Assert.That(decoded.Topology.Neighbours[0].MonitorHost, Is.EqualTo("192.168.1.181"));
            Assert.That(decoded.Topology.Neighbours[0].MonitorPort, Is.EqualTo(9102));
            Assert.That(decoded.Topology.Neighbours[0].ViaPortId, Is.EqualTo(4));
            Assert.That(decoded.Topology.Neighbours[0].Up, Is.True);
        }

        [Test]
        public void Filter_Empty_MatchesEverything()
        {
            MonitorFilter filter = new MonitorFilter();
            CaptureRecord record = new CaptureRecord { Port = 3, Called = "9999", Calling = "1111", PacketType = "Data" };
            Assert.That(filter.Matches(record), Is.True);
        }

        [Test]
        public void Filter_ByPort_And_PacketType_And_Address()
        {
            CaptureRecord onPort0Call = new CaptureRecord
            {
                Port = 0,
                Called = "23410001",
                Calling = "23400001",
                PacketType = "CallRequest"
            };
            CaptureRecord onPort1Data = new CaptureRecord
            {
                Port = 1,
                Called = "23410001",
                Calling = "23400001",
                PacketType = "Data"
            };

            MonitorFilter portOnly = new MonitorFilter { Ports = new List<int> { 0 } };
            Assert.That(portOnly.Matches(onPort0Call), Is.True);
            Assert.That(portOnly.Matches(onPort1Data), Is.False);

            MonitorFilter dataOnly = new MonitorFilter { PacketTypes = new List<string> { "Data" } };
            Assert.That(dataOnly.Matches(onPort0Call), Is.False);
            Assert.That(dataOnly.Matches(onPort1Data), Is.True);

            MonitorFilter dstPrefix = new MonitorFilter { Dst = "2341" };
            Assert.That(dstPrefix.Matches(onPort0Call), Is.True);

            MonitorFilter dstMiss = new MonitorFilter { Dst = "9999" };
            Assert.That(dstMiss.Matches(onPort0Call), Is.False);

            MonitorFilter srcPrefix = new MonitorFilter { Src = "2340" };
            Assert.That(srcPrefix.Matches(onPort0Call), Is.True);
        }
    }
}
