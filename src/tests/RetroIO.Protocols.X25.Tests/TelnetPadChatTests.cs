//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Runtime;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The runtime's telnet PAD obeys the Altos chat's X.29 set (step 3 of RetroX25 docs/altos-chat-port-plan.md): two
    /// telnet terminals call the chat, the PAD echoes and edits each line, the chat gets finished lines only.
    /// </summary>
    [TestFixture]
    public class TelnetPadChatTests
    {
        private const string Altos = "26245890040004";

        private ProtocolRuntime? _runtime;
        private TelnetTestTerminal? _a;
        private TelnetTestTerminal? _b;

        [TearDown]
        public void TearDown()
        {
            _a?.Dispose();
            _b?.Dispose();
            _runtime?.Dispose();
        }

        private static int FreePort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        [Test]
        public void TwoTelnetTerminals_TalkThroughTheChat_WithTheLineEditedInThePad()
        {
            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = "26245890040001";
            config.TelnetPad.Enabled = true;
            config.TelnetPad.Port = FreePort();
            config.Chat.Address = Altos;
            config.Chat.WaitPauseMs = 0;
            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "Chat",
                X121AddressPattern = Altos,
                Type = RetroIO.Protocols.X25.Runtime.RouteType.Local,
                Destination = "CHAT",
                Priority = 200,
            });
            _runtime = new ProtocolRuntime(NullLogger.Instance, config);

            _a = new TelnetTestTerminal(config.TelnetPad.Port);
            _b = new TelnetTestTerminal(config.TelnetPad.Port);
            Assert.That(_a.WaitFor("PAD> "), Is.True, "A: no prompt: " + _a.Visible);
            Assert.That(_b.WaitFor("PAD> "), Is.True, "B: no prompt: " + _b.Visible);

            _a.TypeLine("CALL " + Altos);
            Assert.That(_a.WaitFor("+++guest+++\r\n"), Is.True, "A did not join: " + _a.Visible);
            _b.TypeLine("CALL " + Altos);
            Assert.That(_b.WaitFor("+++guest+++\r\n"), Is.True, "B did not join: " + _b.Visible);

            int mark = _a.Visible.Length;
            _a.Send("helo");
            _a.Send(127);
            _a.Send("lo");
            _a.Send(13, 0);

            Assert.That(_b.WaitFor("guest says: hello\r\n"), Is.True, "B did not get A's edited line: " + _b.Visible);
            Assert.That(_a.WaitFor("helo" + (char)127 + "\\lo\r\n", 5000, mark), Is.True,
                "A's PAD did not echo the line with the character deleted signal: " + _a.Visible.Substring(mark));
            Assert.That(_b.Visible, Does.Not.Contain("helo"), "the chat got the unedited line");
        }
    }
}
