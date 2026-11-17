//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using RetroIO.Protocols.X25.PacketLayer;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// An XOT port as the RFC 1613 6.5 responder, driven over a real TCP socket by a test that plays the far XOT and
    /// writes the bytes itself: the XOT header (version 0, length, RFC 1613 section 4) and the setup packet.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class XotPvcPortTests
    {
        private static XotSwitchPort Responder()
        {
            XotSwitchPort port = new XotSwitchPort(new PortConfig
            {
                Id = 0, Mode = PortMode.Dce, Host = "127.0.0.1", Port = 0, LciBase = 16, LciCount = 16,
                PvcBase = 1, PvcCount = 8, InterfaceName = "Serial1", DefaultWindowSize = 2, DefaultPacketSize = 128,
            });
            Assert.That(port.AddPvc(5), Is.True);
            Assert.That(port.Start(), Is.True);
            return port;
        }

        private static void Send(NetworkStream s, byte[] packet)
        {
            byte[] f = new byte[4 + packet.Length];
            f[2] = (byte)(packet.Length >> 8);
            f[3] = (byte)packet.Length;
            packet.CopyTo(f, 4);
            s.Write(f, 0, f.Length);
        }

        private static byte[]? Receive(NetworkStream s)
        {
            byte[] h = new byte[4];
            if (!ReadAll(s, h))
            {
                return null;
            }

            byte[] p = new byte[(h[2] << 8) | h[3]];
            return ReadAll(s, p) ? p : null;
        }

        private static bool ReadAll(NetworkStream s, byte[] b)
        {
            int got = 0;
            while (got < b.Length)
            {
                int n = s.Read(b, got, b.Length - got);
                if (n <= 0)
                {
                    return false;
                }

                got += n;
            }

            return true;
        }

        private static byte[] Setup(ushort responderLci)
        {
            return new XotPvcSetup
            {
                GfiLcn = 0x1001, InitiatorInterface = "Serial0", InitiatorLci = 1, ResponderInterface = "Serial1",
                ResponderLci = responderLci, IncomingWindow = 2, OutgoingWindow = 2, IncomingPacketSize = 128, OutgoingPacketSize = 128,
            }.Build();
        }

        [Test]
        public void AGoodSetup_IsAnswered0x00_ThenThePvcIsResetRemoteDteOperational_AndCarriesData()
        {
            using XotSwitchPort port = Responder();
            List<(ushort Lci, byte[] Data)> got = new List<(ushort, byte[])>();
            port.DataReceived += (p, lci, bytes, length, q, m, d) =>
            {
                byte[] c = new byte[length];
                Array.Copy(bytes, c, length);
                lock (got)
                {
                    got.Add((lci, c));
                }
            };

            using TcpClient far = new TcpClient("127.0.0.1", port.BoundPort);
            far.ReceiveTimeout = 5000;
            NetworkStream s = far.GetStream();
            Send(s, Setup(5));

            byte[]? reply = Receive(s);
            Assert.That(reply, Is.Not.Null);
            Assert.That(XotPvcSetup.TryParse(reply!, out XotPvcSetup r), Is.True);
            Assert.That(r.Status, Is.EqualTo((byte)0x00));

            // "each XOT MUST complete a Reset procedure on the local interface": a Reset Request (type 0x1B) on the far
            // end's channel 1, cause 0x09.
            byte[]? reset = Receive(s);
            Assert.That(reset, Is.Not.Null);
            Assert.That(reset![1], Is.EqualTo((byte)1));
            Assert.That(reset[2], Is.EqualTo((byte)0x1B));
            Assert.That(reset[3], Is.EqualTo((byte)0x09));

            // Confirm the reset (0x1F), then a data packet, P(S)=0 P(R)=0 with no M bit, on channel 1.
            Send(s, new byte[] { 0x10, 0x01, 0x1F });
            Send(s, new byte[] { 0x10, 0x01, 0x00, 0x41, 0x42 });
            Assert.That(SpinWait.SpinUntil(() => { lock (got) { return got.Count == 1; } }, 5000), Is.True);
            Assert.That(got[0].Lci, Is.EqualTo((ushort)5), "the far end's channel 1 is this port's PVC 5");
            Assert.That(got[0].Data, Is.EqualTo(new byte[] { 0x41, 0x42 }));
        }

        private static XotSwitchPort Initiator()
        {
            XotSwitchPort port = new XotSwitchPort(new PortConfig
            {
                Id = 1, Mode = PortMode.Dce, Host = "127.0.0.1", Port = 0, LciBase = 16, LciCount = 16,
                PvcBase = 1, PvcCount = 8, InterfaceName = "Serial0", DefaultWindowSize = 2, DefaultPacketSize = 128,
                XotPvcInitiator = true,
            });
            Assert.That(port.AddPvc(1), Is.True);
            Assert.That(port.Start(), Is.True);
            return port;
        }

        private static List<(ushort Lci, byte[] Data)> Collect(XotSwitchPort port)
        {
            List<(ushort Lci, byte[] Data)> got = new List<(ushort, byte[])>();
            port.DataReceived += (p, lci, bytes, length, q, m, d) =>
            {
                byte[] c = new byte[length];
                Array.Copy(bytes, c, length);
                lock (got)
                {
                    got.Add((lci, c));
                }
            };
            return got;
        }

        private static void SendWhenOpen(XotSwitchPort port, ushort lci, byte[] data)
        {
            Assert.That(SpinWait.SpinUntil(() => port.SendData(lci, data, false, false, false) == PortSendResult.Sent, 5000), Is.True,
                "PVC " + lci + " accepted data");
        }

        [Test]
        public void CSharpInitiatorToCSharpResponder_JoinsThePvc_AndDataFlowsBothWays()
        {
            using XotSwitchPort responder = Responder();
            using XotSwitchPort initiator = Initiator();
            List<(ushort Lci, byte[] Data)> atResponder = Collect(responder);
            List<(ushort Lci, byte[] Data)> atInitiator = Collect(initiator);

            initiator.StartPvcSetup(1, "127.0.0.1:" + responder.BoundPort, "Serial1", 5);

            SendWhenOpen(initiator, 1, new byte[] { 1, 2 });
            Assert.That(SpinWait.SpinUntil(() => { lock (atResponder) { return atResponder.Count == 1; } }, 5000), Is.True);
            Assert.That(atResponder[0].Lci, Is.EqualTo((ushort)5));

            SendWhenOpen(responder, 5, new byte[] { 3 });
            Assert.That(SpinWait.SpinUntil(() => { lock (atInitiator) { return atInitiator.Count == 1; } }, 5000), Is.True);
            Assert.That(atInitiator[0].Lci, Is.EqualTo((ushort)1));
            Assert.That(atInitiator[0].Data, Is.EqualTo(new byte[] { 3 }));
        }

        [Test]
        public void AWholeSwitch_CarriesAPvcFromAFarXotToAnotherPort()
        {
            // The switch: port 0 an XOT responder (PVC channel 5), port 1 a loopback port (PVC channel 2), joined.
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340" };
            cfg.Pvcs.Add(new PvcConfig { SourcePort = 0, SourceLci = 5, DestPort = 1, DestLci = 2 });
            SwitchNode node = new SwitchNode(cfg);
            using XotSwitchPort xot = new XotSwitchPort(new PortConfig
            {
                Id = 0, Mode = PortMode.Dce, Host = "127.0.0.1", Port = 0, LciBase = 16, LciCount = 16,
                PvcBase = 1, PvcCount = 8, InterfaceName = "Serial1", DefaultWindowSize = 2, DefaultPacketSize = 128,
            });
            LoopbackSwitchPort local = new LoopbackSwitchPort(new PortConfig { Id = 1, LciBase = 16 });
            node.AddPort(xot);
            node.AddPort(local);
            Assert.That(xot.Start(), Is.True);
            local.Start();
            SwitchCore core = new SwitchCore(node);

            // The far XOT asks for Serial1 channel 5 and sends on its own channel 1.
            using XotSwitchPort far = Initiator();
            far.StartPvcSetup(1, "127.0.0.1:" + xot.BoundPort, "Serial1", 5);
            SendWhenOpen(far, 1, new byte[] { 7, 8, 9 });

            Assert.That(SpinWait.SpinUntil(() => local.SentData.Count == 1, 5000), Is.True, "the data crossed the switch");
            Assert.That(local.SentData[0].Lci, Is.EqualTo((ushort)2));
            Assert.That(local.SentData[0].Data, Is.EqualTo(new byte[] { 7, 8, 9 }));
            GC.KeepAlive(core);
        }

        [Test]
        public void AWholeSwitch_Answers0x14_WhileThePvcsOtherPortIsDown()
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340" };
            cfg.Pvcs.Add(new PvcConfig { SourcePort = 0, SourceLci = 5, DestPort = 1, DestLci = 2 });
            SwitchNode node = new SwitchNode(cfg);
            using XotSwitchPort xot = new XotSwitchPort(new PortConfig
            {
                Id = 0, Mode = PortMode.Dce, Host = "127.0.0.1", Port = 0, LciBase = 16, LciCount = 16,
                PvcBase = 1, PvcCount = 8, InterfaceName = "Serial1", DefaultWindowSize = 2, DefaultPacketSize = 128,
            });
            LoopbackSwitchPort local = new LoopbackSwitchPort(new PortConfig { Id = 1, LciBase = 16 });
            node.AddPort(xot);
            node.AddPort(local);
            Assert.That(xot.Start(), Is.True);
            local.Start();
            SwitchCore core = new SwitchCore(node);
            local.Stop();

            using TcpClient far = new TcpClient("127.0.0.1", xot.BoundPort);
            far.ReceiveTimeout = 5000;
            NetworkStream s = far.GetStream();
            Send(s, Setup(5));
            Assert.That(XotPvcSetup.TryParse(Receive(s)!, out XotPvcSetup r), Is.True);
            Assert.That(r.Status, Is.EqualTo((byte)0x14), "Destination interface is not up");
            GC.KeepAlive(core);
        }

        [Test]
        public void ARefusedSetup_IsTriedAgainAfterTheWait()
        {
            using XotSwitchPort responder = Responder();
            using XotSwitchPort initiator = Initiator();
            initiator.PvcSetupRetryMs = 200;
            List<(ushort Lci, byte[] Data)> atResponder = Collect(responder);

            // Channel 6 is no PVC on the responder: 0x16 every time, so the initiator keeps trying and data never flows.
            initiator.StartPvcSetup(1, "127.0.0.1:" + responder.BoundPort, "Serial1", 6);
            Thread.Sleep(1000);
            Assert.That(initiator.SendData(1, new byte[] { 1 }, false, false, false), Is.Not.EqualTo(PortSendResult.Sent));
            Assert.That(responder.Counters.AllPacketsOut, Is.GreaterThanOrEqualTo(2ul), "more than one refusal was sent");
        }

        [Test]
        public void ASetupForAChannelThatIsNoPvc_IsAnswered0x16_AndTheConnectionClosed()
        {
            using XotSwitchPort port = Responder();
            using TcpClient far = new TcpClient("127.0.0.1", port.BoundPort);
            far.ReceiveTimeout = 5000;
            NetworkStream s = far.GetStream();
            Send(s, Setup(6));

            Assert.That(XotPvcSetup.TryParse(Receive(s)!, out XotPvcSetup r), Is.True);
            Assert.That(r.Status, Is.EqualTo((byte)0x16));
            Assert.That(Receive(s), Is.Null, "the responder closes the TCP connection after a refusal");
        }
    }
}
