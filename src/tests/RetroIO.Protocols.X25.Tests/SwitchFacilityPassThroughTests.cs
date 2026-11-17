//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The facilities a switch does not act on cross every switch of a call unchanged, both ways (PLAN B1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Path: caller - access 1 (XOT) - switch 1 - HDLC trunk - switch 2 - XOT trunk - switch 3 - access 3 (XOT) - callee.
    /// Both ends are raw XOT connections, so the test sees the exact facility field that arrives, not what a client library
    /// makes of it.
    /// </para>
    /// <para>
    /// Window and packet size are negotiated end to end and re-encoded at every switch (X.25 6.9 and 6.12); every other
    /// element must arrive byte for byte and after them (X.25 7.1: "requests for X.25 facilities must precede the other
    /// requests"). Until this test, each switch dropped them all: the onward Call Request carried only window and packet
    /// size, so reverse charging, closed user groups, NUI, charging requests and anything after a marker never reached
    /// the called side, and billing and X.75 could not be built on the switch.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class SwitchFacilityPassThroughTests
    {
        // Window 2 and packet size 128 both ways (the network defaults), then: reverse charging requested (01 01), a marker
        // (00 00) and after it a national element of class D with two parameter octets (C3 02 AB CD).
        private static readonly byte[] CallerAsks = { 0x42, 0x07, 0x07, 0x43, 0x02, 0x02, 0x01, 0x01, 0x00, 0x00, 0xC3, 0x02, 0xAB, 0xCD };

        // Window and packet size as asked, then throughput class (02 BB), a class A element.
        private static readonly byte[] CalleeAnswers = { 0x42, 0x07, 0x07, 0x43, 0x02, 0x02, 0x02, 0xBB };

        private sealed class Chain : IDisposable
        {
            public ISwitchPort Access1 = null!;
            public ISwitchPort Trunk1 = null!;
            public ISwitchPort Trunk2 = null!;
            public ISwitchPort XotTrunk2 = null!;
            public ISwitchPort XotTrunk3 = null!;
            public ISwitchPort Access3 = null!;

            public void Dispose()
            {
                ISwitchPort[] all = { Access1, Trunk1, Trunk2, XotTrunk2, XotTrunk3, Access3 };
                for (int i = 0; i < all.Length; i++)
                {
                    all[i]?.Stop();
                }
            }
        }

        private static SwitchConfig ConfigWithRoute(string dnic)
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = dnic };
            cfg.Routing.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = "2342", PrefixLen = 4, DestPort = 1 });

            // The NUI the caller names must be one the network knows, or the first switch clears the call (Annex C row 15).
            cfg.Accounts.Add(new AccountConfig { Name = "card7", Nui = "ACCT-7" });

            // And the one the called DTE names in its Call Accepted, or its switch clears the call (6.21.1; Annex E, b)
            // Call accepted packet, row 13).
            cfg.Accounts.Add(new AccountConfig { Name = "callee", Nui = "B9" });
            return cfg;
        }

        private static PortConfig XotConfig(int id, PortMode mode, string host, ushort port)
        {
            return new PortConfig { Id = id, Mode = mode, Host = host, Port = port, LciBase = 1, LciCount = 16 };
        }

        // The same three-switch chain as SwitchCounterTests: three switches, an HDLC trunk and an XOT trunk.
        private static Chain Build()
        {
            Chain c = new Chain();
            SwitchNode node1 = new SwitchNode(ConfigWithRoute("2340"));
            SwitchNode node2 = new SwitchNode(ConfigWithRoute("2341"));
            SwitchNode node3 = new SwitchNode(ConfigWithRoute("2342"));

            c.XotTrunk3 = new XotSwitchPort(XotConfig(0, PortMode.Dce, "127.0.0.1", 0));
            c.Access3 = new XotSwitchPort(XotConfig(1, PortMode.Dce, "127.0.0.1", 0));
            node3.AddPort(c.XotTrunk3);
            node3.AddPort(c.Access3);
            _ = new SwitchCore(node3);
            Assert.That(c.XotTrunk3.Start() && c.Access3.Start(), Is.True);

            c.Trunk2 = SwitchPortFactory.Create(TrunkPair.Config(0, PortMode.Dce, "127.0.0.1", 0));
            c.XotTrunk2 = new XotSwitchPort(XotConfig(1, PortMode.Dte, "127.0.0.1", ((XotSwitchPort)c.XotTrunk3).BoundPort));
            node2.AddPort(c.Trunk2);
            node2.AddPort(c.XotTrunk2);
            _ = new SwitchCore(node2);
            Assert.That(c.Trunk2.Start() && c.XotTrunk2.Start(), Is.True);

            c.Access1 = new XotSwitchPort(XotConfig(0, PortMode.Dce, "127.0.0.1", 0));
            c.Trunk1 = SwitchPortFactory.Create(TrunkPair.Config(1, PortMode.Dte, "127.0.0.1", ((HdlcTcpSwitchPort)c.Trunk2).BoundPort));
            node1.AddPort(c.Access1);
            node1.AddPort(c.Trunk1);
            _ = new SwitchCore(node1);
            Assert.That(c.Access1.Start() && c.Trunk1.Start(), Is.True);

            Assert.That(SpinWait.SpinUntil(() => c.Trunk1.PeerConnected && c.Trunk2.PeerConnected, 10000), Is.True, "the HDLC trunk comes up");
            return c;
        }

        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace('-', ' ');

        // RFC 1613: a version of 0 (two octets) and the packet length (two octets, network order), then the packet.
        private static void SendXot(NetworkStream stream, byte[] packet)
        {
            byte[] frame = new byte[4 + packet.Length];
            frame[2] = (byte)(packet.Length >> 8);
            frame[3] = (byte)packet.Length;
            packet.CopyTo(frame, 4);
            stream.Write(frame, 0, frame.Length);
        }

        private static byte[] ReadXot(NetworkStream stream)
        {
            byte[] header = ReadExactly(stream, 4);
            return ReadExactly(stream, (header[2] << 8) | header[3]);
        }

        private static byte[] ReadExactly(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int got = 0;
            while (got < count)
            {
                int n = stream.Read(buffer, got, count - got);
                Assert.That(n, Is.GreaterThan(0), "the connection closed");
                got += n;
            }

            return buffer;
        }

        /// <summary>
        /// The caller's other facilities arrive at the callee in its Call Request, and the callee's arrive at the caller in
        /// its Call Connected, each byte for byte after window and packet size, across three switches.
        /// </summary>
        [Test]
        public void OtherFacilities_CrossThreeSwitches_BothWays_ByteForByte()
        {
            using Chain c = Build();

            using TcpClient callee = new TcpClient { NoDelay = true };
            callee.Connect("127.0.0.1", ((XotSwitchPort)c.Access3).BoundPort);
            using TcpClient caller = new TcpClient { NoDelay = true };
            caller.Connect("127.0.0.1", ((XotSwitchPort)c.Access1).BoundPort);
            Assert.That(SpinWait.SpinUntil(() => c.Access1.PeerConnected && c.Access3.PeerConnected, 5000), Is.True);
            NetworkStream callerStream = caller.GetStream();
            NetworkStream calleeStream = callee.GetStream();
            callerStream.ReadTimeout = 10000;
            calleeStream.ReadTimeout = 10000;

            SendXot(callerStream, X25PacketBuilder.CreateCallRequest(1, "23420001", "23400001", CallerAsks));

            X25PacketInfo atCallee = X25PacketParser.ParsePacket(ReadXot(calleeStream));
            Assert.That(atCallee.PacketType, Is.EqualTo(X25PacketType.CallRequest));
            Assert.That(Hex(atCallee.Facilities), Is.EqualTo(Hex(CallerAsks)), "the Call Request at the callee");

            SendXot(calleeStream, X25PacketBuilder.CreateCallAccepted(atCallee.LCN, CalleeAnswers));

            X25PacketInfo atCaller = X25PacketParser.ParsePacket(ReadXot(callerStream));
            Assert.That(atCaller.PacketType, Is.EqualTo(X25PacketType.CallAccepted));
            Assert.That(Hex(atCaller.Facilities), Is.EqualTo(Hex(CalleeAnswers)), "the Call Connected at the caller");
        }

        /// <summary>
        /// The facility field of a Clear Request crosses all three switches unchanged and arrives in the Clear Indication
        /// (X.25 5.2.4.2, extended format). The field here is a segment count (Table 7-2 code C2, 7.3.8.3): 12 segments
        /// sent to the DTE and 34 received, in BCD.
        /// </summary>
        [Test]
        public void ClearFacilities_CrossThreeSwitches_ByteForByte()
        {
            using Chain c = Build();

            using TcpClient callee = new TcpClient { NoDelay = true };
            callee.Connect("127.0.0.1", ((XotSwitchPort)c.Access3).BoundPort);
            using TcpClient caller = new TcpClient { NoDelay = true };
            caller.Connect("127.0.0.1", ((XotSwitchPort)c.Access1).BoundPort);
            Assert.That(SpinWait.SpinUntil(() => c.Access1.PeerConnected && c.Access3.PeerConnected, 5000), Is.True);
            NetworkStream callerStream = caller.GetStream();
            NetworkStream calleeStream = callee.GetStream();
            callerStream.ReadTimeout = 10000;
            calleeStream.ReadTimeout = 10000;

            SendXot(callerStream, X25PacketBuilder.CreateCallRequest(1, "23420001", "23400001", CallerAsks));
            X25PacketInfo atCallee = X25PacketParser.ParsePacket(ReadXot(calleeStream));
            SendXot(calleeStream, X25PacketBuilder.CreateCallAccepted(atCallee.LCN, CalleeAnswers));
            X25PacketInfo connected = X25PacketParser.ParsePacket(ReadXot(callerStream));
            Assert.That(connected.PacketType, Is.EqualTo(X25PacketType.CallAccepted));

            byte[] clearFacilities = { 0xC2, 0x08, 0x00, 0x00, 0x00, 0x12, 0x00, 0x00, 0x00, 0x34 };
            SendXot(calleeStream, X25PacketBuilder.CreateClearRequest(atCallee.LCN, 0x00, 0x00, clearFacilities));

            X25PacketInfo atCaller = X25PacketParser.ParsePacket(ReadXot(callerStream));
            Assert.That(atCaller.PacketType, Is.EqualTo(X25PacketType.ClearRequest), "a Clear Indication reaches the caller");
            Assert.That(atCaller.ParseError, Is.Null.Or.Empty);
            Assert.That(Hex(atCaller.Facilities), Is.EqualTo(Hex(clearFacilities)), "the clear's facilities at the caller");
        }

        /// <summary>
        /// The NUI selection element (code C6, X.25 Table 7-2 and 7.3.7) does not cross the switch, either way: "Network
        /// user identifier is never transmitted to the remote DTE" (X.25 6.21). Everything around it still does, and a C6
        /// after a marker is another facility set's code (7.1), not an NUI, so it stays.
        /// </summary>
        [Test]
        public void NuiSelection_IsRemoved_BothWays_TheRestCrossesUnchanged()
        {
            using Chain c = Build();

            using TcpClient callee = new TcpClient { NoDelay = true };
            callee.Connect("127.0.0.1", ((XotSwitchPort)c.Access3).BoundPort);
            using TcpClient caller = new TcpClient { NoDelay = true };
            caller.Connect("127.0.0.1", ((XotSwitchPort)c.Access1).BoundPort);
            Assert.That(SpinWait.SpinUntil(() => c.Access1.PeerConnected && c.Access3.PeerConnected, 5000), Is.True);
            NetworkStream callerStream = caller.GetStream();
            NetworkStream calleeStream = callee.GetStream();
            callerStream.ReadTimeout = 10000;
            calleeStream.ReadTimeout = 10000;

            // Reverse charging, NUI "ACCT-7" (C6 06 'A' 'C' 'C' 'T' '-' '7'), then a marker and a national C6 element.
            byte[] asks = { 0x42, 0x07, 0x07, 0x43, 0x02, 0x02, 0x01, 0x01, 0xC6, 0x06, 0x41, 0x43, 0x43, 0x54, 0x2D, 0x37, 0x00, 0x00, 0xC6, 0x01, 0x99 };
            byte[] arrives = { 0x42, 0x07, 0x07, 0x43, 0x02, 0x02, 0x01, 0x01, 0x00, 0x00, 0xC6, 0x01, 0x99 };
            SendXot(callerStream, X25PacketBuilder.CreateCallRequest(1, "23420001", "23400001", asks));

            X25PacketInfo atCallee = X25PacketParser.ParsePacket(ReadXot(calleeStream));
            Assert.That(Hex(atCallee.Facilities), Is.EqualTo(Hex(arrives)), "the Call Request at the callee");

            // The called DTE may give an NUI in its Call Accepted too (6.21.3); that one does not reach the caller either.
            byte[] answers = { 0x42, 0x07, 0x07, 0x43, 0x02, 0x02, 0xC6, 0x02, 0x42, 0x39, 0x02, 0xBB };
            byte[] answerArrives = { 0x42, 0x07, 0x07, 0x43, 0x02, 0x02, 0x02, 0xBB };
            SendXot(calleeStream, X25PacketBuilder.CreateCallAccepted(atCallee.LCN, answers));

            X25PacketInfo atCaller = X25PacketParser.ParsePacket(ReadXot(callerStream));
            Assert.That(Hex(atCaller.Facilities), Is.EqualTo(Hex(answerArrives)), "the Call Connected at the caller");
        }
    }
}
