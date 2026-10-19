//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Runtime.Layers;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Runtime.Transport;
using RetroIO.Protocols.X25.Transport;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Pins that the events of the serial transport, the physical layer and the XOT client transport
    /// are raised when the wire delivers something. Every wire here is a loopback: the mock serial port
    /// of the physical layer (test mode), or a TCP listener on 127.0.0.1.
    /// </summary>
    [TestFixture]
    public class TransportEventsTests : TestBase
    {
        private const int WaitMs = 5000;

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Builds one X.25 Call Request packet: GFI and LCN, PTI 0x0B, then the body the existing
        /// X25CallRequest encoder writes (address block, facilities, call user data).
        /// </summary>
        private static byte[] CallRequestPacket(ushort lcn, string called, string calling, byte[] cud)
        {
            byte[] body = new X25CallRequest(called, calling, null, cud).BuildPacketData();
            byte[] packet = new byte[3 + body.Length];
            packet[0] = (byte)(0x10 | ((lcn >> 8) & 0x0F));
            packet[1] = (byte)(lcn & 0xFF);
            packet[2] = 0x0B;
            Buffer.BlockCopy(body, 0, packet, 3, body.Length);
            return packet;
        }

        /// <summary>
        /// Wraps one X.25 packet in the RFC 1613 header: version 0x0000, then the length, big endian.
        /// </summary>
        private static byte[] XotFrame(byte[] x25Packet)
        {
            byte[] frame = new byte[4 + x25Packet.Length];
            frame[2] = (byte)(x25Packet.Length >> 8);
            frame[3] = (byte)(x25Packet.Length & 0xFF);
            Buffer.BlockCopy(x25Packet, 0, frame, 4, x25Packet.Length);
            return frame;
        }

        private static int FreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private bool WaitUntil(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(WaitMs);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(5);
            }
            return condition();
        }

        private SerialTransport StartSerialTransport(string name, int mockPort)
        {
            SerialInterfaceConfig serial = new SerialInterfaceConfig { Name = name, PortName = "MOCK" + mockPort, RemoteMachineId = "REMOTE" + mockPort };
            Runtime.PhysicalLayer.InitializeSerialPorts(new[] { serial });
            Assert.That(Runtime.PhysicalLayer.Start(), Is.True, "the physical layer must start its mock serial port");
            SerialTransport transport = new SerialTransport(Logger, Runtime, new SerialTransportConfig { Name = name, PortName = "MOCK" + mockPort });
            Assert.That(transport.Start(), Is.True, "the serial transport must become active");
            return transport;
        }

        // ------------------------------------------------------------------ SerialTransport.DataReceived

        [Test]
        public void SerialTransport_RaisesDataReceived_ForBytesOnItsOwnInterface()
        {
            SerialTransport transport = StartSerialTransport("ser0", 1);
            List<TransportDataEventArgs> seen = new List<TransportDataEventArgs>();
            transport.DataReceived += (s, e) => { lock (seen) { seen.Add(e); } };

            byte[] wire = new byte[] { 0x7E, 0x01, 0x3F, 0xAB, 0xCD, 0x7E };
            Runtime.PhysicalLayer.InjectMockSerialData(1, wire);

            Assert.That(WaitUntil(() => { lock (seen) { return seen.Count == 1; } }), Is.True, "no DataReceived for bytes that arrived on the serial line");
            Assert.That(seen[0].Data, Is.EqualTo(wire));
            Assert.That(seen[0].Length, Is.EqualTo(wire.Length));
            Assert.That(seen[0].Transport, Is.SameAs(transport));
            Assert.That(transport.GetStatistics().BytesReceived, Is.EqualTo(wire.Length));
        }

        [Test]
        public void SerialTransport_DoesNotRaiseDataReceived_ForAnotherInterface()
        {
            SerialTransport transport = StartSerialTransport("ser0", 1);
            SerialInterfaceConfig other = new SerialInterfaceConfig { Name = "ser1", PortName = "MOCK2", RemoteMachineId = "REMOTE2" };
            Runtime.PhysicalLayer.InitializeSerialPorts(new[] { other });
            int count = 0;
            transport.DataReceived += (s, e) => Interlocked.Increment(ref count);

            Runtime.PhysicalLayer.InjectMockSerialData(2, new byte[] { 0x7E, 0x02, 0x7E });

            // Inject is synchronous: the layer has raised its own event by the time it returns.
            Assert.That(count, Is.EqualTo(0), "bytes from MOCK2 must not show up on the transport of MOCK1");
        }

        [Test]
        public void SerialTransport_DoesNotRaiseDataReceived_AfterStop()
        {
            SerialTransport transport = StartSerialTransport("ser0", 1);
            int count = 0;
            transport.DataReceived += (s, e) => Interlocked.Increment(ref count);
            transport.Stop();

            Runtime.PhysicalLayer.InjectMockSerialData(1, new byte[] { 0x7E, 0x03, 0x7E });

            Assert.That(count, Is.EqualTo(0), "a stopped transport must not deliver data");
        }

        // ------------------------------------------------------------------ PhysicalLayer.IncomingCall

        private PhysicalLayer StartTcpPhysicalLayer(out int port)
        {
            port = FreePort();
            PhysicalLayerConfig config = new PhysicalLayerConfig
            {
                LocalMachineId = "200",
                TcpServerEnabled = true,
                TcpListenerPort = port,
            };
            PhysicalLayer layer = new PhysicalLayer(Logger, config);
            Assert.That(layer.Start(), Is.True);
            return layer;
        }

        private static TcpClient Connect(int port)
        {
            TcpClient client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            client.NoDelay = true;
            return client;
        }

        [Test]
        public void PhysicalLayer_RaisesIncomingCall_ForXotCallRequest()
        {
            PhysicalLayer layer = StartTcpPhysicalLayer(out int port);
            try
            {
                List<PhysicalLayerCallEventArgs> calls = new List<PhysicalLayerCallEventArgs>();
                layer.IncomingCall += (s, e) => { lock (calls) { calls.Add(e); } };
                byte[] cud = new byte[] { 0x01, 0xAA, 0x55 };

                using (TcpClient client = Connect(port))
                {
                    client.GetStream().Write(XotFrame(CallRequestPacket(5, "200", "100", cud)));

                    Assert.That(WaitUntil(() => { lock (calls) { return calls.Count == 1; } }), Is.True, "a Call Request on the XOT stream raised no IncomingCall");
                    Assert.That(calls[0].CalledAddress, Is.EqualTo("200"));
                    Assert.That(calls[0].CallingAddress, Is.EqualTo("100"));
                    Assert.That(calls[0].CallData, Is.EqualTo(cud));
                    Assert.That(calls[0].TransportType, Is.EqualTo(PhysicalTransportType.XOT_Client));
                }
            }
            finally
            {
                layer.Dispose();
            }
        }

        [Test]
        public void PhysicalLayer_RaisesIncomingCall_WhenTheFrameArrivesInPieces()
        {
            PhysicalLayer layer = StartTcpPhysicalLayer(out int port);
            try
            {
                List<PhysicalLayerCallEventArgs> calls = new List<PhysicalLayerCallEventArgs>();
                layer.IncomingCall += (s, e) => { lock (calls) { calls.Add(e); } };
                byte[] frame = XotFrame(CallRequestPacket(1, "2001", "1002", Array.Empty<byte>()));

                using (TcpClient client = Connect(port))
                {
                    NetworkStream stream = client.GetStream();
                    // TCP gives no frame boundaries: cut inside the XOT header, then inside the packet.
                    stream.Write(frame, 0, 2);
                    stream.Flush();
                    stream.Write(frame, 2, 4);
                    stream.Flush();
                    stream.Write(frame, 6, frame.Length - 6);

                    Assert.That(WaitUntil(() => { lock (calls) { return calls.Count == 1; } }), Is.True);
                    Assert.That(calls[0].CalledAddress, Is.EqualTo("2001"));
                    Assert.That(calls[0].CallingAddress, Is.EqualTo("1002"));
                }
            }
            finally
            {
                layer.Dispose();
            }
        }

        [Test]
        public void PhysicalLayer_DoesNotRaiseIncomingCall_ForAnX25DataPacket_ButStillRaisesDataReceived()
        {
            PhysicalLayer layer = StartTcpPhysicalLayer(out int port);
            try
            {
                int calls = 0;
                int bytes = 0;
                layer.IncomingCall += (s, e) => Interlocked.Increment(ref calls);
                layer.DataReceived += (s, e) => Interlocked.Add(ref bytes, e.Data.Length);
                // A Data packet: GFI, LCN 5, PTI with P(R)=0 M=0 P(S)=0 and a type bit of 0, then user data.
                byte[] data = XotFrame(new byte[] { 0x10, 0x05, 0x00, 0x41, 0x42 });
                // A Call Request after it proves the first frame was read and skipped, not lost.
                byte[] call = XotFrame(CallRequestPacket(6, "200", "100", Array.Empty<byte>()));

                using (TcpClient client = Connect(port))
                {
                    client.GetStream().Write(data);
                    client.GetStream().Write(call);

                    Assert.That(WaitUntil(() => Volatile.Read(ref calls) == 1), Is.True);
                    Assert.That(WaitUntil(() => Volatile.Read(ref bytes) == data.Length + call.Length), Is.True, "DataReceived must still carry the raw bytes");
                    Assert.That(calls, Is.EqualTo(1), "only the Call Request is an incoming call");
                }
            }
            finally
            {
                layer.Dispose();
            }
        }

        // ------------------------------------------------------------------ XotClientTransport.DataReceived

        [Test]
        public void XotClientTransport_RaisesDataReceived_ForPacketsFromTheGateway()
        {
            TcpListener gateway = new TcpListener(IPAddress.Loopback, 0);
            gateway.Start();
            try
            {
                int port = ((IPEndPoint)gateway.LocalEndpoint).Port;
                XotClientTransport transport = new XotClientTransport(Logger, new XotClientConfig { Name = "xotc", ConnectTimeout = 5000 });
                List<TransportDataEventArgs> seen = new List<TransportDataEventArgs>();
                transport.DataReceived += (s, e) => { lock (seen) { seen.Add(e); } };
                Assert.That(transport.ConnectToXotGateway("127.0.0.1", port), Is.True);

                using (TcpClient peer = gateway.AcceptTcpClient())
                {
                    byte[] packet = new byte[] { 0x10, 0x01, 0x0F, 0xAA };
                    peer.GetStream().Write(XotFrame(packet));

                    Assert.That(WaitUntil(() => { lock (seen) { return seen.Count == 1; } }), Is.True, "a packet from the gateway raised no DataReceived");
                    Assert.That(seen[0].Data, Is.EqualTo(packet));
                    Assert.That(seen[0].Transport, Is.SameAs(transport));
                    Assert.That(transport.GetStatistics().BytesReceived, Is.EqualTo(packet.Length));
                }
            }
            finally
            {
                gateway.Stop();
            }
        }
    }
}
