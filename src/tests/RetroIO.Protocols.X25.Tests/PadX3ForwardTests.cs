//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Runtime.Models;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// A parameter the user changes on the PAD must reach the host: the PAD sends an X.29 control
    /// message (an X.25 data packet with the Q bit set) and the host side keeps its own copy of the
    /// session's X.3 parameters. Real PAD, real runtime, real call to the built-in TAD server.
    /// </summary>
    [TestFixture]
    public class PadX3ForwardTests
    {
        private ProtocolRuntime _runtime;

        [SetUp]
        public void Setup()
        {
            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = "200";
            _runtime = new ProtocolRuntime(NullLogger.Instance, config);
        }

        [TearDown]
        public void TearDown()
        {
            _runtime?.Dispose();
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

        private Dictionary<byte, byte> ServerParameters()
        {
            Protocols.LocalTadProtocol tad = _runtime.GetLocalTadProtocol();
            ushort lcn = tad.GetActiveSessions().First().LCN;
            return tad.GetSessionX3ParametersByLCN(lcn) ?? new Dictionary<byte, byte>();
        }

        /// <summary>
        /// Changing a parameter in the PAD session shows up in the host's copy for that session, and a
        /// second change overwrites the first.
        /// </summary>
        [Test]
        public void ParameterChangedOnThePad_IsSeenByTheServer()
        {
            PadClient pad = _runtime.CreatePadClient("X3PAD");
            Assert.That(pad.Connect("200", "100"), Is.True);
            Assert.That(WaitUntil(() => _runtime.GetLocalTadProtocol().GetActiveSessions().Any(), 3000), Is.True);

            // The value the PAD starts with, so the test cannot pass by accident.
            byte before = ServerParameters().GetValueOrDefault((byte)12);
            byte wanted = (byte)(before == 0 ? 1 : 0);

            Assert.That(pad.SetParameter(12, wanted), Is.True);
            Assert.That(WaitUntil(() => ServerParameters().GetValueOrDefault((byte)12) == wanted, 3000), Is.True,
                "the server never saw the new value of parameter 12 (was " + before + ")");

            Assert.That(pad.SetParameter(12, before), Is.True);
            Assert.That(WaitUntil(() => ServerParameters().GetValueOrDefault((byte)12) == before, 3000), Is.True,
                "the second change was not seen");

            pad.Disconnect();
            pad.Dispose();
        }

        /// <summary>
        /// The X.29 control message must not be taken for text: nothing of it may be echoed back to the PAD.
        /// </summary>
        [Test]
        public void ParameterMessage_IsNotEchoedAsText()
        {
            PadClient pad = _runtime.CreatePadClient("X3PAD2");
            List<string> received = new List<string>();
            pad.DataReceived += (s, e) => received.Add(e.Data);
            Assert.That(pad.Connect("200", "100"), Is.True);
            Assert.That(WaitUntil(() => _runtime.GetLocalTadProtocol().GetActiveSessions().Any(), 3000), Is.True);
            Thread.Sleep(300);
            int before = received.Count;

            pad.SetParameter(12, 0);
            Thread.Sleep(500);

            Assert.That(received.Count, Is.EqualTo(before), "the control message was answered as if it were text");
            pad.Disconnect();
            pad.Dispose();
        }

        /// <summary>
        /// X.3 parameter 4 is a data forwarding delay. A call must stay up when it is set, however long
        /// the line is quiet (it used to clear the call when the delay ran out).
        /// </summary>
        [Test]
        public void IdleTimerParameter_DoesNotClearTheCall()
        {
            PadClient pad = _runtime.CreatePadClient("X3PAD3");
            Assert.That(pad.SetParameter(4, 5), Is.True);
            Assert.That(pad.Connect("200", "100"), Is.True);
            Assert.That(WaitUntil(() => _runtime.GetLocalTadProtocol().GetActiveSessions().Any(), 3000), Is.True);

            Thread.Sleep(1000);

            Assert.That(pad.ConnectionState, Is.EqualTo(PadConnectionState.Connected), "the call was cleared by the idle timer");
            Assert.That(_runtime.GetLocalTadProtocol().GetActiveSessions().Any(), Is.True);
            pad.Disconnect();
            pad.Dispose();
        }

        /// <summary>
        /// Disconnect tells the terminal "CLR CONF" once and gives the PAD prompt back once, reports the
        /// disconnect once, and RedrawPrompt writes the prompt again only while the PAD waits for a command.
        /// </summary>
        [Test]
        public void Disconnect_WritesOneClearAndOnePrompt_AndRedrawPromptOnlyInCommandMode()
        {
            PadClient pad = _runtime.CreatePadClient("X3PAD4");
            List<string> received = new List<string>();
            int disconnected = 0;
            pad.DataReceived += (s, e) => received.Add(e.Data);
            pad.ConnectionStateChanged += (s, e) =>
            {
                if (e.State == PadConnectionState.Disconnected)
                {
                    disconnected++;
                }
            };
            Assert.That(pad.Connect("200", "100"), Is.True);
            Assert.That(WaitUntil(() => _runtime.GetLocalTadProtocol().GetActiveSessions().Any(), 3000), Is.True);
            Thread.Sleep(300);

            // In a call the PAD has no prompt of its own to draw.
            int beforeRedraw = received.Count;
            pad.RedrawPrompt();
            Assert.That(received.Count, Is.EqualTo(beforeRedraw), "the prompt was drawn during a call");

            int beforeDisconnect = received.Count;
            pad.Disconnect();
            Thread.Sleep(500);
            string afterDisconnect = string.Concat(received.GetRange(beforeDisconnect, received.Count - beforeDisconnect));
            Assert.That(afterDisconnect, Is.EqualTo("\r\nCLR CONF\r\nPAD> "));
            Assert.That(disconnected, Is.EqualTo(1));
            Assert.That(pad.ConnectionState, Is.EqualTo(PadConnectionState.Disconnected));

            // Waiting for a command, the prompt is drawn again.
            int beforeSecond = received.Count;
            pad.RedrawPrompt();
            Assert.That(string.Concat(received.GetRange(beforeSecond, received.Count - beforeSecond)), Is.EqualTo("PAD> "));
            pad.Dispose();
        }
    }
}
