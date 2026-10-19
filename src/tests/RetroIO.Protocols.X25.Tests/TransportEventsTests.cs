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

        // ------------------------------------------------------------------ PhysicalLayer XOT framing, more cases

        /// <summary>
        /// A logger that keeps every message, so a test can wait for a line the code under test logs.
        /// </summary>
        private sealed class CapturingLogger : ILogger
        {
            private readonly List<string> _lines = new List<string>();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (_lines)
                {
                    _lines.Add(logLevel + ": " + formatter(state, exception));
                }
            }

            public bool Contains(string text)
            {
                lock (_lines)
                {
                    for (int i = 0; i < _lines.Count; i++)
                    {
                        if (_lines[i].Contains(text, StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
        }

        private PhysicalLayer StartTcpPhysicalLayer(ILogger logger, out int port)
        {
            port = FreePort();
            PhysicalLayerConfig config = new PhysicalLayerConfig
            {
                LocalMachineId = "200",
                TcpServerEnabled = true,
                TcpListenerPort = port,
            };
            PhysicalLayer layer = new PhysicalLayer(logger, config);
            Assert.That(layer.Start(), Is.True);
            return layer;
        }

        /// <summary>
        /// Writes a chunk that is not a Call Request and waits until the layer reports its bytes. The
        /// reader thread handles chunks one after the other, so when this returns every chunk written
        /// before it has been cut into frames completely.
        /// </summary>
        private void SendSentinelAndWait(NetworkStream stream, Func<int> bytesSeen, int expectedTotal)
        {
            // Wait for the earlier bytes first, so the sentinel cannot be read in the same chunk.
            Assert.That(WaitUntil(() => bytesSeen() >= expectedTotal), Is.True, "the bytes written before the sentinel never came back through DataReceived");
            byte[] sentinel = XotFrame(new byte[] { 0x10, 0x01, 0x00 });
            stream.Write(sentinel, 0, sentinel.Length);
            Assert.That(WaitUntil(() => bytesSeen() >= expectedTotal + sentinel.Length), Is.True, "the sentinel never came back through DataReceived");
        }

        /// <summary>
        /// Several whole frames in one read: every Call Request among them is reported, in the order
        /// they came, and the frames that are not Call Requests are skipped.
        /// </summary>
        [Test]
        public void PhysicalLayer_RaisesIncomingCall_ForEveryCallRequestInOneRead()
        {
            PhysicalLayer layer = StartTcpPhysicalLayer(out int port);
            try
            {
                List<PhysicalLayerCallEventArgs> calls = new List<PhysicalLayerCallEventArgs>();
                layer.IncomingCall += (s, e) => { lock (calls) { calls.Add(e); } };
                byte[] first = XotFrame(CallRequestPacket(1, "2001", "1001", Array.Empty<byte>()));
                byte[] data = XotFrame(new byte[] { 0x10, 0x01, 0x00, 0x41 });
                byte[] second = XotFrame(CallRequestPacket(2, "2002", "1002", new byte[] { 0x07 }));
                byte[] all = new byte[first.Length + data.Length + second.Length];
                Buffer.BlockCopy(first, 0, all, 0, first.Length);
                Buffer.BlockCopy(data, 0, all, first.Length, data.Length);
                Buffer.BlockCopy(second, 0, all, first.Length + data.Length, second.Length);

                using (TcpClient client = Connect(port))
                {
                    client.GetStream().Write(all, 0, all.Length);

                    Assert.That(WaitUntil(() => { lock (calls) { return calls.Count == 2; } }), Is.True, "both Call Requests of one read must be reported");
                    Assert.That(calls[0].CalledAddress, Is.EqualTo("2001"));
                    Assert.That(calls[0].CallingAddress, Is.EqualTo("1001"));
                    Assert.That(calls[1].CalledAddress, Is.EqualTo("2002"));
                    Assert.That(calls[1].CallingAddress, Is.EqualTo("1002"));
                    Assert.That(calls[1].CallData, Is.EqualTo(new byte[] { 0x07 }));
                }
            }
            finally
            {
                layer.Dispose();
            }
        }

        /// <summary>
        /// Four header octets that are not a XOT header end the decoding of that connection: the call
        /// that follows is not reported, an error is logged, and the raw bytes still reach DataReceived.
        /// The four cases are a version other than 0, a length under 3, a length of 4097 and the
        /// length 0.
        /// </summary>
        [TestCase(1, 20)]
        [TestCase(0, 2)]
        [TestCase(0, 4097)]
        [TestCase(0, 0)]
        public void PhysicalLayer_StopsDecodingXot_AfterABadHeader(int version, int length)
        {
            CapturingLogger log = new CapturingLogger();
            PhysicalLayer layer = StartTcpPhysicalLayer(log, out int port);
            try
            {
                int calls = 0;
                int bytes = 0;
                layer.IncomingCall += (s, e) => Interlocked.Increment(ref calls);
                layer.DataReceived += (s, e) => Interlocked.Add(ref bytes, e.Data.Length);
                byte[] bad = new byte[] { (byte)(version >> 8), (byte)version, (byte)(length >> 8), (byte)length, 0x10, 0x01, 0x0B };
                byte[] call = XotFrame(CallRequestPacket(3, "200", "100", Array.Empty<byte>()));

                using (TcpClient client = Connect(port))
                {
                    NetworkStream stream = client.GetStream();
                    stream.Write(bad, 0, bad.Length);
                    Assert.That(WaitUntil(() => log.Contains("bad XOT header")), Is.True, "no error was logged for the bad header");

                    stream.Write(call, 0, call.Length);
                    SendSentinelAndWait(stream, () => Volatile.Read(ref bytes), bad.Length + call.Length);

                    Assert.That(Volatile.Read(ref calls), Is.EqualTo(0), "nothing after a bad header can be at a known offset");
                    Assert.That(Volatile.Read(ref bytes), Is.GreaterThanOrEqualTo(bad.Length + call.Length), "DataReceived must still carry the raw bytes");
                }
            }
            finally
            {
                layer.Dispose();
            }
        }

        /// <summary>
        /// A good call that comes before the bad header is reported; the one after it is not.
        /// </summary>
        [Test]
        public void PhysicalLayer_ReportsTheCallsBeforeABadHeader_ButNotTheOnesAfter()
        {
            CapturingLogger log = new CapturingLogger();
            PhysicalLayer layer = StartTcpPhysicalLayer(log, out int port);
            try
            {
                int calls = 0;
                int bytes = 0;
                layer.IncomingCall += (s, e) => Interlocked.Increment(ref calls);
                layer.DataReceived += (s, e) => Interlocked.Add(ref bytes, e.Data.Length);
                byte[] good = XotFrame(CallRequestPacket(1, "200", "100", Array.Empty<byte>()));
                byte[] bad = new byte[] { 0x00, 0x09, 0x00, 0x10 };
                byte[] after = XotFrame(CallRequestPacket(2, "200", "100", Array.Empty<byte>()));
                byte[] all = new byte[good.Length + bad.Length + after.Length];
                Buffer.BlockCopy(good, 0, all, 0, good.Length);
                Buffer.BlockCopy(bad, 0, all, good.Length, bad.Length);
                Buffer.BlockCopy(after, 0, all, good.Length + bad.Length, after.Length);

                using (TcpClient client = Connect(port))
                {
                    NetworkStream stream = client.GetStream();
                    stream.Write(all, 0, all.Length);
                    Assert.That(WaitUntil(() => log.Contains("bad XOT header")), Is.True);
                    SendSentinelAndWait(stream, () => Volatile.Read(ref bytes), all.Length);

                    Assert.That(Volatile.Read(ref calls), Is.EqualTo(1));
                }
            }
            finally
            {
                layer.Dispose();
            }
        }

        /// <summary>
        /// A frame of the largest legal length (4096 octets) is read as one frame, so the Call Request
        /// that follows it is still found.
        /// </summary>
        [Test]
        public void PhysicalLayer_AcceptsAFrameOf4096Octets_AndFindsTheCallAfterIt()
        {
            PhysicalLayer layer = StartTcpPhysicalLayer(out int port);
            try
            {
                List<PhysicalLayerCallEventArgs> calls = new List<PhysicalLayerCallEventArgs>();
                layer.IncomingCall += (s, e) => { lock (calls) { calls.Add(e); } };
                byte[] big = new byte[4096];
                big[0] = 0x10;
                big[1] = 0x01;
                big[2] = 0x00;   // a Data packet
                byte[] bigFrame = XotFrame(big);
                byte[] call = XotFrame(CallRequestPacket(4, "2003", "1003", Array.Empty<byte>()));
                byte[] all = new byte[bigFrame.Length + call.Length];
                Buffer.BlockCopy(bigFrame, 0, all, 0, bigFrame.Length);
                Buffer.BlockCopy(call, 0, all, bigFrame.Length, call.Length);

                using (TcpClient client = Connect(port))
                {
                    client.GetStream().Write(all, 0, all.Length);

                    Assert.That(WaitUntil(() => { lock (calls) { return calls.Count == 1; } }), Is.True, "the call behind a 4096 octet frame was lost");
                    Assert.That(calls[0].CalledAddress, Is.EqualTo("2003"));
                }
            }
            finally
            {
                layer.Dispose();
            }
        }

        /// <summary>
        /// A frame of the smallest legal length (3 octets) that is a Call Request cannot be decoded.
        /// The error is logged, no call is raised, and the next good call on the same connection is.
        /// </summary>
        [Test]
        public void PhysicalLayer_SkipsAnUndecodableCallRequest_AndKeepsReading()
        {
            CapturingLogger log = new CapturingLogger();
            PhysicalLayer layer = StartTcpPhysicalLayer(log, out int port);
            try
            {
                List<PhysicalLayerCallEventArgs> calls = new List<PhysicalLayerCallEventArgs>();
                layer.IncomingCall += (s, e) => { lock (calls) { calls.Add(e); } };
                byte[] shortCall = XotFrame(new byte[] { 0x10, 0x01, 0x0B });
                byte[] good = XotFrame(CallRequestPacket(5, "2004", "1004", Array.Empty<byte>()));
                byte[] all = new byte[shortCall.Length + good.Length];
                Buffer.BlockCopy(shortCall, 0, all, 0, shortCall.Length);
                Buffer.BlockCopy(good, 0, all, shortCall.Length, good.Length);

                using (TcpClient client = Connect(port))
                {
                    client.GetStream().Write(all, 0, all.Length);

                    Assert.That(WaitUntil(() => { lock (calls) { return calls.Count == 1; } }), Is.True);
                    Assert.That(calls[0].CalledAddress, Is.EqualTo("2004"));
                    Assert.That(log.Contains("undecodable Call Request"), Is.True);
                }
            }
            finally
            {
                layer.Dispose();
            }
        }

        /// <summary>
        /// A listener of IncomingCall that throws is logged and does not stop the layer: the next call
        /// on the same connection is still reported.
        /// </summary>
        [Test]
        public void PhysicalLayer_KeepsReading_WhenAnIncomingCallListenerThrows()
        {
            CapturingLogger log = new CapturingLogger();
            PhysicalLayer layer = StartTcpPhysicalLayer(log, out int port);
            try
            {
                int attempts = 0;
                layer.IncomingCall += (s, e) =>
                {
                    Interlocked.Increment(ref attempts);
                    throw new InvalidOperationException("listener failed");
                };
                byte[] two = new byte[0];
                byte[] first = XotFrame(CallRequestPacket(1, "2001", "1001", Array.Empty<byte>()));
                byte[] second = XotFrame(CallRequestPacket(2, "2002", "1002", Array.Empty<byte>()));
                two = new byte[first.Length + second.Length];
                Buffer.BlockCopy(first, 0, two, 0, first.Length);
                Buffer.BlockCopy(second, 0, two, first.Length, second.Length);

                using (TcpClient client = Connect(port))
                {
                    client.GetStream().Write(two, 0, two.Length);

                    Assert.That(WaitUntil(() => Volatile.Read(ref attempts) == 2), Is.True, "the second call was not offered after the listener threw on the first");
                    Assert.That(log.Contains("Error in incoming call event handler"), Is.True);
                }
            }
            finally
            {
                layer.Dispose();
            }
        }

        // ------------------------------------------------------------------ PhysicalLayer serial interface name

        /// <summary>
        /// Bytes that arrive on a configured serial interface are reported with the name of that
        /// interface and the remote machine id configured for it.
        /// </summary>
        [Test]
        public void PhysicalLayer_SerialData_CarriesTheNameOfItsInterface()
        {
            SerialInterfaceConfig first = new SerialInterfaceConfig { Name = "ser0", PortName = "MOCK1", RemoteMachineId = "REMOTE1" };
            SerialInterfaceConfig second = new SerialInterfaceConfig { Name = "ser1", PortName = "MOCK2", RemoteMachineId = "REMOTE2" };
            Runtime.PhysicalLayer.InitializeSerialPorts(new[] { first, second });
            Assert.That(Runtime.PhysicalLayer.Start(), Is.True);
            SerialTransport transport = new SerialTransport(Logger, Runtime, new SerialTransportConfig { Name = "ser0", PortName = "MOCK1" });
            Assert.That(transport.Start(), Is.True);

            List<PhysicalLayerDataEventArgs> seen = new List<PhysicalLayerDataEventArgs>();
            Runtime.PhysicalLayer.DataReceived += (s, e) => { lock (seen) { seen.Add(e); } };

            // In test mode the layer does not start the mock ports itself; start them so they read.
            Assert.That(Runtime.PhysicalLayer.GetMockSerial("ser0")!.Start(), Is.True);
            Assert.That(Runtime.PhysicalLayer.GetMockSerial("ser1")!.Start(), Is.True);
            Runtime.PhysicalLayer.GetMockSerial("ser0")!.InjectIncomingData(new byte[] { 0x11, 0x22 });
            Assert.That(WaitUntil(() => { lock (seen) { return seen.Count == 1; } }), Is.True, "the serial port raised no data event");
            Runtime.PhysicalLayer.GetMockSerial("ser1")!.InjectIncomingData(new byte[] { 0x33 });
            Assert.That(WaitUntil(() => { lock (seen) { return seen.Count == 2; } }), Is.True);

            Assert.That(seen[0].InterfaceName, Is.EqualTo("ser0"));
            Assert.That(seen[0].SourceAddress, Is.EqualTo("REMOTE1"));
            Assert.That(seen[0].TransportType, Is.EqualTo(PhysicalTransportType.Serial));
            Assert.That(seen[0].Data, Is.EqualTo(new byte[] { 0x11, 0x22 }));
            Assert.That(seen[1].InterfaceName, Is.EqualTo("ser1"));
            Assert.That(seen[1].SourceAddress, Is.EqualTo("REMOTE2"));
            Assert.That(Runtime.PhysicalLayer.GetStatistics().SerialBytesReceived, Is.EqualTo(3));
            Assert.That(transport.GetStatistics().BytesReceived, Is.EqualTo(2), "only the bytes of its own interface are the transport's");
        }

        /// <summary>
        /// Bytes injected into a mock port are reported with the name of the interface configured on
        /// that port, and with an empty name when no interface uses the port.
        /// </summary>
        [Test]
        public void PhysicalLayer_InjectedMockData_CarriesTheNameOfTheInterfaceOnThatPort()
        {
            StartSerialTransport("ser0", 1);
            List<PhysicalLayerDataEventArgs> seen = new List<PhysicalLayerDataEventArgs>();
            Runtime.PhysicalLayer.DataReceived += (s, e) => { lock (seen) { seen.Add(e); } };

            Runtime.PhysicalLayer.InjectMockSerialData(1, new byte[] { 0x01 });
            Runtime.PhysicalLayer.InjectMockSerialData(5, new byte[] { 0x02 });

            Assert.That(seen.Count, Is.EqualTo(2));
            Assert.That(seen[0].InterfaceName, Is.EqualTo("ser0"));
            Assert.That(seen[1].InterfaceName, Is.EqualTo(string.Empty), "no interface is configured on MOCK5");
        }

        // ------------------------------------------------------------------ SerialTransport subscription

        /// <summary>
        /// Stopping and starting the transport again does not subscribe it twice: one read gives one
        /// event.
        /// </summary>
        [Test]
        public void SerialTransport_DeliversEachReadOnce_AfterStopAndStart()
        {
            SerialTransport transport = StartSerialTransport("ser0", 1);
            transport.Stop();
            Assert.That(transport.Start(), Is.True);
            int count = 0;
            transport.DataReceived += (s, e) => Interlocked.Increment(ref count);

            Runtime.PhysicalLayer.InjectMockSerialData(1, new byte[] { 0x7E, 0x04, 0x7E });

            Assert.That(count, Is.EqualTo(1));
        }

        /// <summary>
        /// Starting a transport that is already active does not subscribe it a second time.
        /// </summary>
        [Test]
        public void SerialTransport_DeliversEachReadOnce_WhenStartIsCalledTwice()
        {
            SerialTransport transport = StartSerialTransport("ser0", 1);
            Assert.That(transport.Start(), Is.True);
            int count = 0;
            transport.DataReceived += (s, e) => Interlocked.Increment(ref count);

            Runtime.PhysicalLayer.InjectMockSerialData(1, new byte[] { 0x7E, 0x05, 0x7E });

            Assert.That(count, Is.EqualTo(1));
        }

        /// <summary>
        /// The byte count grows even when nobody listens to DataReceived.
        /// </summary>
        [Test]
        public void SerialTransport_CountsBytes_WhenNobodyListens()
        {
            SerialTransport transport = StartSerialTransport("ser0", 1);

            Runtime.PhysicalLayer.InjectMockSerialData(1, new byte[] { 1, 2, 3, 4 });
            Runtime.PhysicalLayer.InjectMockSerialData(1, new byte[] { 5 });

            Assert.That(transport.GetStatistics().BytesReceived, Is.EqualTo(5));
        }

        /// <summary>
        /// A DataReceived listener that throws is logged by the transport; the byte count is already
        /// up and the exception does not reach the physical layer.
        /// </summary>
        [Test]
        public void SerialTransport_LogsAndSwallows_AListenerThatThrows()
        {
            CapturingLogger log = new CapturingLogger();
            SerialInterfaceConfig serial = new SerialInterfaceConfig { Name = "ser0", PortName = "MOCK1", RemoteMachineId = "REMOTE1" };
            Runtime.PhysicalLayer.InitializeSerialPorts(new[] { serial });
            Assert.That(Runtime.PhysicalLayer.Start(), Is.True);
            SerialTransport transport = new SerialTransport(log, Runtime, new SerialTransportConfig { Name = "ser0", PortName = "MOCK1" });
            Assert.That(transport.Start(), Is.True);
            transport.DataReceived += (s, e) => throw new InvalidOperationException("listener failed");

            Runtime.PhysicalLayer.InjectMockSerialData(1, new byte[] { 0x7E, 0x06, 0x7E });

            Assert.That(log.Contains("Error in a DataReceived handler of serial transport ser0"), Is.True);
            Assert.That(log.Contains("Error processing mock serial data"), Is.False, "the transport must catch the exception itself");
            Assert.That(transport.GetStatistics().BytesReceived, Is.EqualTo(3));
        }

        /// <summary>
        /// Bytes that came in over TCP are not the serial transport's: it raises nothing for them.
        /// </summary>
        [Test]
        public void SerialTransport_IgnoresBytesThatCameInOverTcp()
        {
            int port = FreePort();
            Runtime.PhysicalLayer.Config.TcpServerEnabled = true;
            Runtime.PhysicalLayer.Config.TcpListenerPort = port;
            SerialTransport transport = StartSerialTransport("ser0", 1);
            int tcpBytes = 0;
            Runtime.PhysicalLayer.DataReceived += (s, e) =>
            {
                if (e.TransportType != PhysicalTransportType.Serial)
                {
                    Interlocked.Add(ref tcpBytes, e.Data.Length);
                }
            };
            int count = 0;
            transport.DataReceived += (s, e) => Interlocked.Increment(ref count);

            using (TcpClient client = Connect(port))
            {
                byte[] frame = XotFrame(new byte[] { 0x10, 0x01, 0x00 });
                client.GetStream().Write(frame, 0, frame.Length);
                Assert.That(WaitUntil(() => Volatile.Read(ref tcpBytes) == frame.Length), Is.True, "the physical layer never saw the TCP bytes");
            }

            Assert.That(count, Is.EqualTo(0));
            Assert.That(transport.GetStatistics().BytesReceived, Is.EqualTo(0));
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
