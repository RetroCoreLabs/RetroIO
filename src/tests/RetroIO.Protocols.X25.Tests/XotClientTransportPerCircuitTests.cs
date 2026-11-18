//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Transport;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// RFC 1613 section 5: "A separate TCP connection MUST be used for each X.25 virtual circuit." <see cref="XotClientTransport"/>
    /// gives every circuit a TCP connection of its own, also two circuits to one peer, sends a circuit's packets on that
    /// connection only, and closes the connection when its call ends (a Clear Confirmation, either way). Until 2026-11-18 it
    /// kept one connection per peer address, sent every call on it and kept it after the calls ended. Real sockets on the
    /// loopback address; the peer is a plain listener.
    /// </summary>
    [TestFixture]
    public class XotClientTransportPerCircuitTests
    {
        // Clear Request on channel 1, cause 0 diagnostic 0 (X.25 Table 5-1: type 00010011); Clear Confirmation (00010111).
        private static readonly byte[] ClearRequest = { 0x10, 0x01, 0x13, 0x00, 0x00 };
        private static readonly byte[] ClearConfirmation = { 0x10, 0x01, 0x17 };
        private static readonly byte[] DataPacket = { 0x10, 0x01, 0x00, 0x41 };

        [Test]
        public void TwoCircuitsToOnePeer_EachHaveTheirOwnConnection_AndAPacketGoesOnlyOnItsOwn()
        {
            using Peer peer = new Peer();
            XotClientTransport transport = Started();
            try
            {
                XotClientCircuit? first = transport.ConnectToXotGateway("127.0.0.1", peer.Port);
                TcpClient firstSeen = peer.Accept();
                XotClientCircuit? second = transport.ConnectToXotGateway("127.0.0.1", peer.Port);
                TcpClient secondSeen = peer.Accept();

                Assert.That(first, Is.Not.Null);
                Assert.That(second, Is.Not.Null);
                Assert.That(second, Is.Not.SameAs(first), "a second circuit to the same peer got the first one's connection");
                Assert.That(transport.GetStatistics().ActiveConnections, Is.EqualTo(2));

                Assert.That(transport.SendX25Packet(second!, DataPacket), Is.True);
                Assert.That(ReadFrame(secondSeen), Is.EqualTo(DataPacket), "the packet did not arrive on its own circuit's connection");
                Thread.Sleep(200);
                Assert.That(firstSeen.Available, Is.EqualTo(0), "the other circuit's connection carried the packet too");
            }
            finally
            {
                transport.Stop();
            }
        }

        [Test]
        public void AClearConfirmationFromThePeer_ClosesTheConnection_AndItIsNeverUsedAgain()
        {
            using Peer peer = new Peer();
            XotClientTransport transport = Started();
            try
            {
                XotClientCircuit? circuit = transport.ConnectToXotGateway("127.0.0.1", peer.Port);
                Assert.That(circuit, Is.Not.Null);
                TcpClient seen = peer.Accept();

                Assert.That(transport.SendX25Packet(circuit!, ClearRequest), Is.True);
                Assert.That(ReadFrame(seen), Is.EqualTo(ClearRequest));
                seen.GetStream().Write(Frame(ClearConfirmation));

                Assert.That(WaitUntil(() => IsClosedByPeer(seen), 5000), Is.True, "the connection stayed open after its call ended");
                Assert.That(transport.GetStatistics().ActiveConnections, Is.EqualTo(0));
                Assert.That(transport.GetStatistics().Disconnects, Is.EqualTo(0), "closing at the end of a call is not the peer dropping the link");
                Assert.That(transport.SendX25Packet(circuit!, DataPacket), Is.False, "a connection whose call has ended was used again");
            }
            finally
            {
                transport.Stop();
            }
        }

        [Test]
        public void AClearConfirmationSent_ClosesTheConnection_AfterItHasGone()
        {
            using Peer peer = new Peer();
            XotClientTransport transport = Started();
            try
            {
                XotClientCircuit? circuit = transport.ConnectToXotGateway("127.0.0.1", peer.Port);
                Assert.That(circuit, Is.Not.Null);
                TcpClient seen = peer.Accept();

                // The peer clears the call (a Clear Indication has the same type as a Clear Request) and the user confirms.
                seen.GetStream().Write(Frame(ClearRequest));
                Assert.That(transport.SendX25Packet(circuit!, ClearConfirmation), Is.True);

                Assert.That(ReadFrame(seen), Is.EqualTo(ClearConfirmation), "the Clear Confirmation must reach the peer before the close");
                Assert.That(WaitUntil(() => IsClosedByPeer(seen), 5000), Is.True, "the connection stayed open after its call ended");
                Assert.That(transport.GetStatistics().ActiveConnections, Is.EqualTo(0));
            }
            finally
            {
                transport.Stop();
            }
        }

        [Test]
        public void DataReceived_NamesTheCircuitThePacketCameOn()
        {
            using Peer peer = new Peer();
            XotClientTransport transport = Started();
            object? seenOn = null;
            transport.DataReceived += (s, e) => Volatile.Write(ref seenOn, e.Connection);
            try
            {
                XotClientCircuit? first = transport.ConnectToXotGateway("127.0.0.1", peer.Port);
                peer.Accept();
                XotClientCircuit? second = transport.ConnectToXotGateway("127.0.0.1", peer.Port);
                TcpClient secondSeen = peer.Accept();

                secondSeen.GetStream().Write(Frame(DataPacket));
                Assert.That(WaitUntil(() => Volatile.Read(ref seenOn) != null, 5000), Is.True, "no packet was raised");
                Assert.That(seenOn, Is.SameAs(second));
                Assert.That(seenOn, Is.Not.SameAs(first));
            }
            finally
            {
                transport.Stop();
            }
        }

        [Test]
        public void APacketWithNoCircuitNamed_IsNotSent()
        {
            using Peer peer = new Peer();
            XotClientTransport transport = Started();
            try
            {
                Assert.That(transport.ConnectToXotGateway("127.0.0.1", peer.Port), Is.Not.Null);
                TcpClient seen = peer.Accept();

                Assert.That(transport.SendPacket(DataPacket), Is.False);
                Thread.Sleep(200);
                Assert.That(seen.Available, Is.EqualTo(0), "a packet with no circuit went out on some connection");
            }
            finally
            {
                transport.Stop();
            }
        }

        private static XotClientTransport Started()
        {
            XotClientTransport transport = new XotClientTransport(NullLogger.Instance, new XotClientConfig { Name = "xotc", ConnectTimeout = 2000 });
            Assert.That(transport.Start(), Is.True);
            return transport;
        }

        private static byte[] Frame(byte[] packet)
        {
            byte[] frame = new byte[4 + packet.Length];
            frame[2] = (byte)(packet.Length >> 8);
            frame[3] = (byte)packet.Length;
            Buffer.BlockCopy(packet, 0, frame, 4, packet.Length);
            return frame;
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

        private static bool IsClosedByPeer(TcpClient client)
        {
            return client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0;
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(20);
            }

            return condition();
        }

        /// <summary>
        /// A plain TCP listener standing in for the XOT gateway; it closes every connection it accepted.
        /// </summary>
        private sealed class Peer : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly System.Collections.Generic.List<TcpClient> _accepted = new System.Collections.Generic.List<TcpClient>();

            public Peer()
            {
                _listener.Start();
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            public TcpClient Accept()
            {
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
