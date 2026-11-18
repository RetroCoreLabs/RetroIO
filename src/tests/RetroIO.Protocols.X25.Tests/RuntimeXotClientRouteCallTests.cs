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
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Transport;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// A program in the runtime (a module) places a call that the router carries on an XOT_Client route. The module
    /// hears the real answer: an accepted call is connected to it (data both ways, and the far end's clear), and a
    /// refused call reaches it as a clear with the far end's cause and diagnostic. Until 2026-11-18 the router reported
    /// such a call placed the moment a (badly built) Call Request was written, and the runtime never joined the call to
    /// the module (FinalizeOutboundXotCall was empty).
    /// </summary>
    [TestFixture]
    public class RuntimeXotClientRouteCallTests
    {
        private const string Called = "23420001";
        private const string Calling = "23400001";

        [Test]
        public void AnAcceptedCall_IsJoinedToTheModule_DataGoesBothWays_AndTheFarClearReachesIt()
        {
            using Peer peer = new Peer();
            using ProtocolRuntime runtime = NewRuntime(peer.Port, out XotClientTransport transport);
            RecordingModule module = new RecordingModule();
            try
            {
                bool placed = false;
                Thread caller = new Thread(() => placed = runtime.PlaceX25CallWithModule(Called, Calling, null, module));
                caller.Start();

                TcpClient seen = peer.Accept();
                ReadFrame(seen);
                seen.GetStream().Write(Frame(new byte[] { 0x10, 0x01, 0x0F }));
                Assert.That(caller.Join(10000), Is.True, "the runtime never answered");
                Assert.That(placed, Is.True);
                Assert.That(module.WaitFor(X25PacketType.CallAccepted), Is.True, "the module was not told its call is connected");
                ushort lcn = module.Lcn;

                // Far end to the module: Data on channel 1, P(S)=0 P(R)=0.
                byte[] hello = Encoding.ASCII.GetBytes("HELLO");
                byte[] data = new byte[3 + hello.Length];
                data[0] = 0x10;
                data[1] = 0x01;
                data[2] = 0x00;
                Buffer.BlockCopy(hello, 0, data, 3, hello.Length);
                seen.GetStream().Write(Frame(data));
                Assert.That(module.WaitFor(X25PacketType.Data), Is.True, "the far end's data did not reach the module");
                Assert.That(Encoding.ASCII.GetString(module.Find(X25PacketType.Data)!.Packet.Data), Is.EqualTo("HELLO"));

                // Module to the far end.
                Assert.That(runtime.SendX25Data(lcn, Encoding.ASCII.GetBytes("PING")), Is.True);
                byte[] sent = ReadDataFrame(seen);
                Assert.That(Encoding.ASCII.GetString(sent, 3, sent.Length - 3), Is.EqualTo("PING"));

                // The far end clears: cause 0, diagnostic 0 (DTE originated).
                seen.GetStream().Write(Frame(new byte[] { 0x10, 0x01, 0x13, 0x00, 0x00 }));
                Assert.That(module.WaitFor(X25PacketType.ClearRequest), Is.True, "the far end's clear did not reach the module");
            }
            finally
            {
                transport.Stop();
            }
        }

        [Test]
        public void ARefusedCall_ReachesTheModuleAsAClear_WithTheFarCauseAndDiagnostic()
        {
            using Peer peer = new Peer();
            using ProtocolRuntime runtime = NewRuntime(peer.Port, out XotClientTransport transport);
            RecordingModule module = new RecordingModule();
            try
            {
                bool placed = true;
                Thread caller = new Thread(() => placed = runtime.PlaceX25CallWithModule(Called, Calling, null, module));
                caller.Start();

                TcpClient seen = peer.Accept();
                ReadFrame(seen);
                // Clear Indication, cause 0x0D not obtainable, diagnostic 67 (0x43).
                seen.GetStream().Write(Frame(new byte[] { 0x10, 0x01, 0x13, 0x0D, 0x43 }));

                Assert.That(caller.Join(10000), Is.True, "the runtime never answered");
                Assert.That(placed, Is.False, "a refused call was reported as placed");
                X25PacketEventArgs? clear = module.Find(X25PacketType.ClearRequest);
                Assert.That(clear, Is.Not.Null, "the module was not told why its call failed");
                Assert.That(clear!.Packet.Data[0], Is.EqualTo((byte)0x0D), "cause");
                Assert.That(clear.Packet.Data[1], Is.EqualTo((byte)0x43), "diagnostic");
            }
            finally
            {
                transport.Stop();
            }
        }

        private static ProtocolRuntime NewRuntime(int peerPort, out XotClientTransport transport)
        {
            RuntimeConfig config = new RuntimeConfig { HistoryCapacity = 100 };
            config.X25.LocalMachineId = "99999";
            ProtocolRuntime runtime = new ProtocolRuntime(NullLogger<ProtocolRuntime>.Instance, config);
            transport = new XotClientTransport(NullLogger.Instance, new XotClientConfig { Name = "xotc", ConnectTimeout = 2000 });
            Assert.That(transport.Start(), Is.True);
            runtime.Router.RegisterInterface(transport);
            runtime.Router.AddRoute(new X25Route
            {
                Name = "to-peer",
                X121AddressPattern = "^2342",
                Type = RetroIO.Protocols.X25.Transport.RouteType.XOT_Client,
                Destination = "127.0.0.1:" + peerPort,
                Direction = RetroIO.Protocols.X25.Transport.RouteDirection.Outbound,
            });
            return runtime;
        }

        /// <summary>
        /// A module that records every X.25 event it is given.
        /// </summary>
        private sealed class RecordingModule : IProtocolModule
        {
            private readonly object _gate = new object();
            private readonly List<X25PacketEventArgs> _events = new List<X25PacketEventArgs>();

            public string Name => "recorder";

            public string Description => "records X.25 events";

            public string Version => "1";

            public ushort Lcn { get; private set; }

            public bool WaitFor(X25PacketType type)
            {
                DateTime end = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < end)
                {
                    if (Find(type) != null)
                    {
                        return true;
                    }

                    Thread.Sleep(10);
                }

                return Find(type) != null;
            }

            public X25PacketEventArgs? Find(X25PacketType type)
            {
                lock (_gate)
                {
                    for (int i = 0; i < _events.Count; i++)
                    {
                        if (_events[i].Packet.PacketType == type)
                        {
                            return _events[i];
                        }
                    }
                }

                return null;
            }

            public void OnBound(ushort lcn) => Lcn = lcn;

            public void OnUnbound(ushort lcn)
            {
            }

            public void OnX25Data(ushort lcn, byte[] data)
            {
            }

            public void OnX25Event(X25PacketEventArgs args)
            {
                lock (_gate)
                {
                    _events.Add(args);
                }

                if (args.Packet.LCN != 0)
                {
                    Lcn = args.Packet.LCN;
                }
            }

            public void OnX25Reset(ushort lcn, byte cause, byte diagnostic)
            {
            }

            public void OnX25Clear(ushort lcn, byte cause, byte diagnostic)
            {
            }

            public void OnX25Interrupt(ushort lcn, byte[] data)
            {
            }

            public void SendX25Data(ushort lcn, byte[] data)
            {
            }

            public void SendX25Reset(ushort lcn, byte cause, byte diagnostic)
            {
            }

            public void SendX25Clear(ushort lcn, byte cause, byte diagnostic)
            {
            }

            public void SendX25Interrupt(ushort lcn, byte[] data)
            {
            }

            public ModuleStatus GetStatus() => new ModuleStatus();

            public void Initialize()
            {
            }

            public void Shutdown()
            {
            }
        }

        private static byte[] Frame(byte[] packet)
        {
            byte[] frame = new byte[4 + packet.Length];
            frame[2] = (byte)(packet.Length >> 8);
            frame[3] = (byte)packet.Length;
            Buffer.BlockCopy(packet, 0, frame, 4, packet.Length);
            return frame;
        }

        // Skips the flow-control packets (RR, X.25 Table 5-1: low bits 00001) the call may send before its data.
        private static byte[] ReadDataFrame(TcpClient client)
        {
            for (int i = 0; i < 8; i++)
            {
                byte[] packet = ReadFrame(client);
                if ((packet[2] & 0x01) == 0)
                {
                    return packet;
                }
            }

            throw new InvalidOperationException("no data packet arrived");
        }

        private static byte[] ReadFrame(TcpClient client)
        {
            client.ReceiveTimeout = 5000;
            NetworkStream stream = client.GetStream();
            byte[] header = ReadExact(stream, 4);
            return ReadExact(stream, (header[2] << 8) | header[3]);
        }

        private static byte[] ReadExact(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int done = 0;
            while (done < count)
            {
                int n = stream.Read(buffer, done, count - done);
                if (n == 0)
                {
                    throw new InvalidOperationException("the connection closed after " + done + " of " + count + " octets");
                }

                done += n;
            }

            return buffer;
        }

        /// <summary>
        /// A plain TCP listener standing in for the XOT gateway; it closes every connection it accepted.
        /// </summary>
        private sealed class Peer : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly List<TcpClient> _accepted = new List<TcpClient>();

            public Peer()
            {
                _listener.Start();
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            public TcpClient Accept()
            {
                if (!_listener.Server.Poll(10_000_000, SelectMode.SelectRead))
                {
                    throw new InvalidOperationException("the runtime never opened a connection to the peer");
                }

                TcpClient client = _listener.AcceptTcpClient();
                _accepted.Add(client);
                return client;
            }

            public void Dispose()
            {
                for (int i = 0; i < _accepted.Count; i++)
                {
                    _accepted[i].Close();
                }

                _listener.Stop();
            }
        }
    }
}
