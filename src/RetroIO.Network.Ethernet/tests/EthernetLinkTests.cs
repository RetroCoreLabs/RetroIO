//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;

namespace RetroIO.Network.Ethernet.Tests
{
    /// <summary>
    /// The links between emulators: TCP link, TCP hub and UDP multicast, on this computer's
    /// loopback. The byte tests talk to a backend with a plain socket and check every byte against
    /// the format the old ND-100 code and nd100x use (host-networking-design.md section 12).
    /// </summary>
    [TestFixture]
    public sealed class EthernetLinkTests
    {
        // ── Constants ──

        private const int WaitMs = 5000;

        // ── Public methods ──

        [Test]
        public void TcpLink_ListenerGreetsWithRethVersion1_AndCarriesLengthPrefixedFramesBothWays()
        {
            TcpLinkBackend link = new TcpLinkBackend(true, string.Empty, 0);
            Collector got = new Collector();
            link.OnFrameReceived += got.Add;
            link.Start();
            try
            {
                using Socket raw = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                raw.Connect(IPAddress.Loopback, link.Port);
                raw.ReceiveTimeout = WaitMs;

                byte[] hello = ReadExactly(raw, 5);
                Assert.That(hello, Is.EqualTo(new byte[] { 0x52, 0x45, 0x54, 0x48, 0x01 }));
                raw.Send(new byte[] { 0x52, 0x45, 0x54, 0x48, 0x01 });

                // Peer -> backend: 00 3C then 60 frame bytes.
                byte[] frame = MakeFrame(60, 0x11);
                byte[] wire = new byte[62];
                wire[0] = 0x00;
                wire[1] = 0x3C;
                Array.Copy(frame, 0, wire, 2, 60);
                raw.Send(wire);
                Assert.That(WaitFor(link, got, 1), Is.True, "the frame never reached the backend");
                Assert.That(got.Last, Is.EqualTo(frame));

                // Backend -> peer: a 300-byte frame is 01 2C then the bytes, nothing else.
                WaitForLinkUp(link);
                byte[] big = MakeFrame(300, 0x22);
                link.Send(big, 0, big.Length);
                byte[] back = ReadExactly(raw, 302);
                Assert.That(back[0], Is.EqualTo(0x01));
                Assert.That(back[1], Is.EqualTo(0x2C));
                Assert.That(new ArraySegment<byte>(back, 2, 300).ToArray(), Is.EqualTo(big));
            }
            finally
            {
                link.Stop();
            }
        }

        [Test]
        public void TcpLink_WrongGreeting_NoFrameIsTaken()
        {
            TcpLinkBackend link = new TcpLinkBackend(true, string.Empty, 0);
            Collector got = new Collector();
            link.OnFrameReceived += got.Add;
            link.Start();
            try
            {
                using Socket raw = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                raw.Connect(IPAddress.Loopback, link.Port);
                raw.ReceiveTimeout = WaitMs;
                ReadExactly(raw, 5);
                // "RETX": one letter wrong.
                raw.Send(new byte[] { 0x52, 0x45, 0x54, 0x58, 0x01 });

                // The backend closes the link: the next read sees the end.
                byte[] one = new byte[1];
                int n = raw.Receive(one);
                Assert.That(n, Is.EqualTo(0), "a link with a wrong greeting must be closed");
                link.Poll();
                Assert.That(got.Count, Is.EqualTo(0));
                Assert.That(link.LinksUp, Is.EqualTo(0L));
            }
            finally
            {
                link.Stop();
            }
        }

        [Test]
        public void TcpLink_ConnectAndListen_ExchangeFramesBothWays()
        {
            TcpLinkBackend server = (TcpLinkBackend)TcpLinkBackend.CreateFromSpec("listen:0")!;
            server.Start();
            TcpLinkBackend client = (TcpLinkBackend)TcpLinkBackend.CreateFromSpec("tcp:127.0.0.1:" + server.Port)!;
            Collector atServer = new Collector();
            Collector atClient = new Collector();
            server.OnFrameReceived += atServer.Add;
            client.OnFrameReceived += atClient.Add;
            client.Start();
            try
            {
                WaitForLinkUp(server);
                WaitForLinkUp(client);
                byte[] a = MakeFrame(64, 0x40);
                byte[] b = MakeFrame(1514, 0x50);
                client.Send(a, 0, a.Length);
                server.Send(b, 0, b.Length);
                Assert.That(WaitFor(server, atServer, 1), Is.True);
                Assert.That(WaitFor(client, atClient, 1), Is.True);
                Assert.That(atServer.Last, Is.EqualTo(a));
                Assert.That(atClient.Last, Is.EqualTo(b));
            }
            finally
            {
                client.Stop();
                server.Stop();
            }
        }

        [Test]
        public void TcpHub_FrameFromOneMember_ReachesTheOthersAndTheHub_NeverTheSender()
        {
            TcpHubBackend hub = (TcpHubBackend)TcpHubBackend.CreateFromSpec("hub:0")!;
            hub.Start();
            TcpLinkBackend m1 = new TcpLinkBackend(false, "127.0.0.1", hub.Port);
            TcpLinkBackend m2 = new TcpLinkBackend(false, "127.0.0.1", hub.Port);
            Collector atHub = new Collector();
            Collector at1 = new Collector();
            Collector at2 = new Collector();
            hub.OnFrameReceived += atHub.Add;
            m1.OnFrameReceived += at1.Add;
            m2.OnFrameReceived += at2.Add;
            m1.Start();
            m2.Start();
            try
            {
                WaitForLinkUp(m1);
                WaitForLinkUp(m2);
                Stopwatch sw = Stopwatch.StartNew();
                while (hub.MemberCount < 2 && sw.ElapsedMilliseconds < WaitMs)
                {
                    Thread.Sleep(5);
                }
                Assert.That(hub.MemberCount, Is.EqualTo(2));

                byte[] f1 = MakeFrame(60, 0x61);
                m1.Send(f1, 0, f1.Length);
                Assert.That(WaitFor(m2, at2, 1), Is.True);
                Assert.That(WaitFor(hub, atHub, 1), Is.True);
                Assert.That(at2.Last, Is.EqualTo(f1));
                Assert.That(atHub.Last, Is.EqualTo(f1));

                byte[] f2 = MakeFrame(100, 0x62);
                hub.Send(f2, 0, f2.Length);
                Assert.That(WaitFor(m1, at1, 1), Is.True);
                Assert.That(WaitFor(m2, at2, 2), Is.True);
                Assert.That(at1.Last, Is.EqualTo(f2));
                Assert.That(at2.Last, Is.EqualTo(f2));
                m1.Poll();
                Assert.That(at1.Count, Is.EqualTo(1), "the hub sent member 1 its own frame back");
            }
            finally
            {
                m1.Stop();
                m2.Stop();
                hub.Stop();
            }
        }

        [Test]
        public void UdpMulticast_OneDatagramIsTheBareFrame_BothWays_AndOwnEchoIsDropped()
        {
            int port = FreeUdpPort();
            UdpMulticastBackend link = new UdpMulticastBackend(EthernetLinkWire.DefaultGroup, port, IPAddress.Loopback);
            Collector got = new Collector();
            link.OnFrameReceived += got.Add;
            link.Start();
            using Socket raw = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                raw.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                raw.Bind(new IPEndPoint(IPAddress.Any, port));
                raw.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Loopback.GetAddressBytes());
                raw.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(EthernetLinkWire.DefaultGroup, IPAddress.Loopback));
                raw.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                raw.ReceiveTimeout = WaitMs;

                byte[] mine = MakeFrame(60, 0x71);
                link.Send(mine, 0, mine.Length);
                byte[] buffer = new byte[2048];
                int n = raw.Receive(buffer);
                Assert.That(n, Is.EqualTo(60), "a datagram must hold the frame and nothing else");
                Assert.That(new ArraySegment<byte>(buffer, 0, n).ToArray(), Is.EqualTo(mine));

                byte[] theirs = MakeFrame(80, 0x72);
                raw.SendTo(theirs, new IPEndPoint(EthernetLinkWire.DefaultGroup, port));
                Assert.That(WaitFor(link, got, 1), Is.True, "the peer's datagram never reached the backend");
                Assert.That(got.Last, Is.EqualTo(theirs));
                Assert.That(link.OwnEchoesDropped, Is.GreaterThanOrEqualTo(1L), "its own frame came back and was not counted as dropped");
                Assert.That(got.Count, Is.EqualTo(1), "its own frame was handed to the guest");
            }
            finally
            {
                link.Stop();
            }
        }

        [Test]
        public void UdpMulticast_TwoBackends_ExchangeFramesBothWays()
        {
            int port = FreeUdpPort();
            UdpMulticastBackend a = new UdpMulticastBackend(EthernetLinkWire.DefaultGroup, port, IPAddress.Loopback);
            UdpMulticastBackend b = new UdpMulticastBackend(EthernetLinkWire.DefaultGroup, port, IPAddress.Loopback);
            Collector atA = new Collector();
            Collector atB = new Collector();
            a.OnFrameReceived += atA.Add;
            b.OnFrameReceived += atB.Add;
            a.Start();
            b.Start();
            try
            {
                byte[] fa = MakeFrame(60, 0x81);
                byte[] fb = MakeFrame(1514, 0x82);
                a.Send(fa, 0, fa.Length);
                b.Send(fb, 0, fb.Length);
                Assert.That(WaitFor(b, atB, 1), Is.True);
                Assert.That(WaitFor(a, atA, 1), Is.True);
                Assert.That(atB.Last, Is.EqualTo(fa));
                Assert.That(atA.Last, Is.EqualTo(fb));
            }
            finally
            {
                a.Stop();
                b.Stop();
            }
        }

        [Test]
        public void Specs_ReadTheSameSpellingsAsTheOldCode()
        {
            TcpLinkBackend c = (TcpLinkBackend)TcpLinkBackend.CreateFromSpec("tcp:gateway")!;
            Assert.That(c.Description, Is.EqualTo("tcp:gateway:3094"));
            TcpLinkBackend l = (TcpLinkBackend)TcpLinkBackend.CreateFromSpec("tcp-listen:4000")!;
            Assert.That(l.Description, Is.EqualTo("listen:4000"));
            Assert.That(TcpLinkBackend.CreateFromSpec("nat"), Is.Null);
            Assert.Throws<ArgumentException>(() => TcpLinkBackend.CreateFromSpec("tcp:host:99999"));

            UdpMulticastBackend u = (UdpMulticastBackend)UdpMulticastBackend.CreateFromSpec("udp")!;
            Assert.That(u.Group.ToString(), Is.EqualTo("239.3.9.4"));
            Assert.That(u.Port, Is.EqualTo(3094));
            UdpMulticastBackend u2 = (UdpMulticastBackend)UdpMulticastBackend.CreateFromSpec("udp-mcast:239.1.2.3:5000")!;
            Assert.That(u2.Group.ToString(), Is.EqualTo("239.1.2.3"));
            Assert.That(u2.Port, Is.EqualTo(5000));
            Assert.Throws<ArgumentException>(() => UdpMulticastBackend.CreateFromSpec("udp:10.0.0.1:5000"));

            TcpHubBackend h = (TcpHubBackend)TcpHubBackend.CreateFromSpec("hub")!;
            Assert.That(h.Port, Is.EqualTo(3094));

            TapEthernetBackend t = (TapEthernetBackend)TapEthernetBackend.CreateFromSpec("tap:Rc0")!;
            Assert.That(t.InterfaceName, Is.EqualTo("Rc0"), "Linux interface names are case-sensitive");
            Assert.That(TapEthernetBackend.CreateFromSpec("tcp:host"), Is.Null);
            Assert.Throws<ArgumentException>(() => TapEthernetBackend.CreateFromSpec("tap:"));
            Assert.Throws<ArgumentException>(() => TapEthernetBackend.CreateFromSpec("tap:a-name-of-16-chr"));
            if (!OperatingSystem.IsLinux())
            {
                Assert.Throws<PlatformNotSupportedException>(() => t.Start());
            }
        }

        // ── Private methods ──

        private static byte[] MakeFrame(int length, byte seed)
        {
            byte[] f = new byte[length];
            for (int i = 0; i < 6; i++)
            {
                f[i] = 0xFF;
            }
            f[6] = 0x02;
            f[7] = seed;
            for (int i = 8; i < length; i++)
            {
                f[i] = (byte)(seed + i);
            }
            return f;
        }

        private static byte[] ReadExactly(Socket socket, int count)
        {
            byte[] buffer = new byte[count];
            Assert.That(EthernetLinkWire.ReceiveAll(socket, buffer), Is.True, "the link closed early");
            return buffer;
        }

        private static bool WaitFor(IEthernetBackend backend, Collector collector, int count)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < WaitMs)
            {
                backend.Poll();
                if (collector.Count >= count)
                {
                    return true;
                }
                Thread.Sleep(2);
            }
            return false;
        }

        private static void WaitForLinkUp(TcpLinkBackend link)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (!link.IsLinkUp && sw.ElapsedMilliseconds < WaitMs)
            {
                Thread.Sleep(2);
            }
            Assert.That(link.IsLinkUp, Is.True, "the TCP link never came up");
        }

        private static int FreeUdpPort()
        {
            using Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)s.LocalEndPoint!).Port;
        }

        // ── Nested types ──

        private sealed class Collector
        {
            public int Count { get; private set; }

            public byte[] Last { get; private set; } = Array.Empty<byte>();

            public void Add(byte[] frame, int offset, int length)
            {
                byte[] copy = new byte[length];
                Array.Copy(frame, offset, copy, 0, length);
                Last = copy;
                Count++;
            }
        }
    }
}
