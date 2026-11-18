//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Runtime;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Two PADs talk through the runtime's built-in Altos chat (step 3 of RetroX25 docs/altos-chat-port-plan.md). Each
    /// PAD is the runtime's own <see cref="PadClient"/>, the one the Desktop PAD window uses; it obeys the chat's X.29
    /// set, so it echoes and edits the line itself and the chat only ever gets finished lines.
    /// </summary>
    [TestFixture]
    public class PadToChatEndToEndTests
    {
        private const string Altos = "26245890040004";
        private const char Del = (char)127;
        private const char Can = (char)24;

        private ProtocolRuntime _runtime = null!;

        [SetUp]
        public void SetUp()
        {
            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = "26245890040001";
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
            _runtime = new ProtocolRuntime(NullLogger<ProtocolRuntime>.Instance, config);
        }

        [TearDown]
        public void TearDown()
        {
            _runtime.Dispose();
        }

        /// <summary>One PAD and everything its terminal shows.</summary>
        private sealed class Terminal : IDisposable
        {
            private readonly StringBuilder _screen = new StringBuilder();
            private readonly object _gate = new object();

            public Terminal(ProtocolRuntime runtime, string name)
            {
                Pad = runtime.CreatePadClient(name);
                Pad.DataReceived += (s, e) => { lock (_gate) { _screen.Append(e.Data); } };
                Pad.CharacterEcho += (s, e) => { lock (_gate) { _screen.Append((char)e.Character); } };
            }

            public PadClient Pad { get; }

            public string Screen
            {
                get { lock (_gate) { return _screen.ToString(); } }
            }

            public void Type(string keys)
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    Pad.OnKey(keys[i]);
                }
            }

            public bool WaitFor(string part, int timeoutMs)
            {
                return SpinWait.SpinUntil(() => Screen.Contains(part, StringComparison.Ordinal), timeoutMs);
            }

            public void Dispose()
            {
                Pad.Dispose();
            }
        }

        private static int Count(string text, string part)
        {
            int n = 0;
            int at = text.IndexOf(part, StringComparison.Ordinal);
            while (at >= 0)
            {
                n++;
                at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal);
            }

            return n;
        }

        /// <summary>
        /// A and B call the chat. Each PAD takes the chat's set (echo, forward on CR, editing, DEL, CAN, DC2). A types
        /// "helo DEL lo CR": A's own screen shows it as typed, with the deleted-character signal, and B is told "guest
        /// says: hello" - the chat never saw the DEL. A types "oops CAN fine CR": B gets "fine" only.
        /// </summary>
        [Test]
        public void TwoPads_TalkThroughTheChat_WithTheLineEditedInThePad()
        {
            using Terminal a = new Terminal(_runtime, "PadA");
            using Terminal b = new Terminal(_runtime, "PadB");

            Assert.That(a.Pad.Connect(Altos, "26240001"), Is.True);
            Assert.That(a.WaitFor("+++guest+++\r\n", 5000), Is.True, "A did not join: " + a.Screen);
            Assert.That(b.Pad.Connect(Altos, "26240002"), Is.True);
            Assert.That(b.WaitFor("+++guest+++\r\n", 5000), Is.True, "B did not join: " + b.Screen);
            Assert.That(SpinWait.SpinUntil(() => Count(a.Screen, "+++guest+++\r\n") == 2, 5000), Is.True, "A did not see B join: " + a.Screen);

            // The chat's set reached both PADs (X.29 Table 1: set, no answer; the values are now the PAD's).
            Assert.That(a.Pad.GetParameter(2), Is.EqualTo((byte)1));
            Assert.That(a.Pad.GetParameter(3), Is.EqualTo((byte)2));
            Assert.That(a.Pad.GetParameter(15), Is.EqualTo((byte)1));
            Assert.That(b.Pad.GetParameter(15), Is.EqualTo((byte)1));

            int aBefore = a.Screen.Length;
            a.Type("helo" + Del + "lo\r");
            Assert.That(b.WaitFor("guest says: hello\r\n", 5000), Is.True, "B did not get A's line: " + b.Screen);
            Assert.That(a.Screen.Substring(aBefore), Does.StartWith("helo" + Del + "\\lo\r\n"), "A's own echo, with the LF the chat's parameter 13 = 4 asks for");

            a.Type("oops" + Can + "fine\r");
            Assert.That(b.WaitFor("guest says: fine\r\n", 5000), Is.True, "B did not get A's second line: " + b.Screen);
            Assert.That(b.Screen, Does.Not.Contain("oops"), "a deleted line reached the chat");
            Assert.That(b.Screen, Does.Not.Contain("helo"), "an edited-away character reached the chat");
            Assert.That(a.Screen, Does.Not.Contain("guest says: hello"), "the chat sends a line to the others only");

            b.Type("hi a\r");
            Assert.That(a.WaitFor("guest says: hi a\r\n", 5000), Is.True, "A did not get B's line: " + a.Screen);
        }
    }
}
