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
using NUnit.Framework;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The clear cause the switch gives a call whose XOT connection to the far end cannot be made.
    /// </summary>
    /// <remarks>
    /// X.25 (10/96) Table C.3 a) item 30, "Called DTE out of order", is cause Out of order with diagnostic #0; item 31,
    /// "No logical channel available", is Number busy #71. A far end that refuses the TCP connection is out of order,
    /// not busy (our reading: the text does not name XOT). Before 2026-11-20 the switch said Number busy #71 whenever
    /// the dialling port still had another connection up (RetroIO BUGS 33, RetroX25 PLAN H3-6).
    /// </remarks>
    [TestFixture]
    public class XotDialFailureCauseTests
    {
        [Test]
        public void AFarEndThatRefusesTheConnection_IsClearedOutOfOrder_NotNumberBusy()
        {
            // The far end: a plain listener that takes the port's spare connection, then stops listening, so the
            // next dial (the one for the call) is refused while the spare stays up.
            TcpListener far = new TcpListener(IPAddress.Loopback, 0);
            far.Start();
            ushort farPort = (ushort)((IPEndPoint)far.LocalEndpoint).Port;

            SwitchConfig cfg = new SwitchConfig { Dnic = "2340" };
            cfg.Routing.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = "2342", PrefixLen = 4, DestPort = 1 });
            SwitchNode node = new SwitchNode(cfg);

            LoopbackSwitchPort caller = new LoopbackSwitchPort(new PortConfig { Id = 0, Mode = PortMode.Dce, LciBase = 1, LciCount = 16 });
            using XotSwitchPort xot = new XotSwitchPort(new PortConfig
            {
                Id = 1,
                Mode = PortMode.Dte,
                Host = "127.0.0.1",
                Port = farPort,
                LciBase = 1,
                LciCount = 16,
            });

            TcpClient? spare = null;
            try
            {
                Assert.That(caller.Start(), Is.True);
                Assert.That(xot.Start(), Is.True);
                spare = far.AcceptTcpClient();
                far.Stop();
                Assert.That(SpinWait.SpinUntil(() => xot.PeerConnected, 5000), Is.True, "the spare connection is up");

                node.AddPort(caller);
                node.AddPort(xot);
                SwitchCore core = new SwitchCore(node);

                ushort lci = caller.InjectIncomingCall("23421234", "23400001");

                Assert.That(SpinWait.SpinUntil(() => caller.ClearedCalls.Count == 1, 10000), Is.True, "the call is cleared");
                (ushort clearedLci, byte cause, byte diag) = caller.ClearedCalls[0];
                Assert.That(clearedLci, Is.EqualTo(lci));
                Assert.That(cause, Is.EqualTo(0x09), "Out of order (Table C.3 a) item 30), got 0x" + cause.ToString("X2") + " #" + diag);
                Assert.That(diag, Is.EqualTo(0));
                GC.KeepAlive(core);
            }
            finally
            {
                spare?.Dispose();
                far.Stop();
                caller.Stop();
            }
        }
    }
}
