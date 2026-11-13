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
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Protocols.X25.Runtime;
using System.Threading;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Two things about the PAD at the end of a call, seen through the telnet PAD with a real socket in
    /// front of a real runtime and its built-in TAD:
    /// 1. exactly ONE "PAD> " prompt comes after the clear message, whoever cleared the call;
    /// 2. the PAD's X.3 parameters go back to their DEFAULTS when a call ends, however it ends. The
    ///    built-in TAD sets parameter 2 (echo) to 0 at the start of a call and the PAD obeys it; if that
    ///    stayed after the call, the next call to a plain host would show nothing the person types.
    /// </summary>
    [TestFixture]
    public class PadPromptAndDefaultsTests
    {
        private const string LocalAddress = "23430200";

        private ProtocolRuntime? _runtime;
        private readonly List<TelnetTestTerminal> _terminals = new List<TelnetTestTerminal>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _terminals.Count; i++)
            {
                _terminals[i].Dispose();
            }

            _terminals.Clear();
            _runtime?.Dispose();
            _runtime = null;
        }

        private static int FreePort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
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

        private static int CountOf(string text, string piece)
        {
            int count = 0;
            int at = 0;
            while ((at = text.IndexOf(piece, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += piece.Length;
            }

            return count;
        }

        private TelnetTestTerminal Start(out ProtocolRuntime runtime)
        {
            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = LocalAddress;
            config.TelnetPad.Enabled = true;
            config.TelnetPad.Port = FreePort();
            _runtime = new ProtocolRuntime(NullLogger.Instance, config);
            runtime = _runtime;
            TelnetTestTerminal t = new TelnetTestTerminal(config.TelnetPad.Port);
            _terminals.Add(t);
            Assert.That(t.WaitFor("PAD> "), Is.True);
            return t;
        }

        private static PadClient PadOf(ProtocolRuntime rt)
        {
            Dictionary<string, IProtocolModule> modules = rt.GetRegisteredModules();
            foreach (KeyValuePair<string, IProtocolModule> kv in modules)
            {
                if (kv.Key.StartsWith("TelnetPad_", StringComparison.Ordinal) && kv.Value is PadClient pad)
                {
                    return pad;
                }
            }

            Assert.Fail("no telnet PAD module registered");
            return null!;
        }

        private static void Call(TelnetTestTerminal t, ProtocolRuntime rt)
        {
            int mark = t.Visible.Length;
            t.TypeLine("CALL " + LocalAddress);
            Assert.That(t.WaitFor("COM", 5000, mark), Is.True, "no COM after the call: " + t.Visible);
            Assert.That(WaitUntil(() => rt.GetLocalTadProtocol().GetActiveSessions().Length > 0), Is.True, "the TAD never saw the call");
        }

        /// <summary>
        /// Waits until the PAD has the TAD's echo-off setting applied, so the tests really start from
        /// a PAD that was changed by the host. If this never happens the later asserts would prove nothing.
        /// </summary>
        private static void WaitForHostEchoOff(PadClient pad)
        {
            Assert.That(WaitUntil(() => pad.X3Protocol.GetParameter(2) == 0), Is.True,
                "the built-in TAD never set the PAD's echo (P2) to 0, so these tests would prove nothing");
        }

        private static void AssertAllDefaults(PadClient pad, string when)
        {
            Dictionary<byte, byte> defaults = new Dictionary<byte, byte>();
            X3.FillDefaultValues(defaults);
            foreach (KeyValuePair<byte, byte> kv in defaults)
            {
                Assert.That(pad.X3Protocol.GetParameter(kv.Key), Is.EqualTo(kv.Value), "X.3 parameter " + kv.Key + " is not its default " + when);
            }
        }

        // ------------------------------------------------------------------ one prompt

        [Test]
        public void HostClear_ByTypingQuitAtTheTad_GivesExactlyOnePrompt()
        {
            ProtocolRuntime rt;
            TelnetTestTerminal t = Start(out rt);
            Call(t, rt);
            int mark = t.Visible.Length;
            t.TypeLine("QUIT");

            Assert.That(t.WaitFor("CLR", 5000, mark), Is.True, "no clear message: " + t.Visible.Substring(mark));
            int clr = t.Visible.IndexOf("CLR", mark, StringComparison.Ordinal);
            Assert.That(t.WaitFor("PAD> ", 5000, clr), Is.True, "no prompt after the clear: " + t.Visible.Substring(mark));
            Thread.Sleep(600);
            string after = t.Visible.Substring(clr);
            Assert.That(CountOf(after, "PAD> "), Is.EqualTo(1), "the prompt must come once after a host clear. Got: " + after);
        }

        [Test]
        public void HostClear_ByTheServerSide_GivesExactlyOnePrompt()
        {
            ProtocolRuntime rt;
            TelnetTestTerminal t = Start(out rt);
            Call(t, rt);
            ushort lcn = rt.GetLocalTadProtocol().GetActiveSessions()[0].LCN;
            int mark = t.Visible.Length;
            rt.GetLocalTadProtocol().DisconnectSession(lcn);

            Assert.That(t.WaitFor("CLR", 5000, mark), Is.True);
            int clr = t.Visible.IndexOf("CLR", mark, StringComparison.Ordinal);
            Assert.That(t.WaitFor("PAD> ", 5000, clr), Is.True);
            Thread.Sleep(600);
            Assert.That(CountOf(t.Visible.Substring(clr), "PAD> "), Is.EqualTo(1), t.Visible.Substring(clr));
        }

        [Test]
        public void UserClear_GivesExactlyOnePrompt()
        {
            ProtocolRuntime rt;
            TelnetTestTerminal t = Start(out rt);
            Call(t, rt);
            int mark = t.Visible.Length;
            t.Send(0x10);
            t.TypeLine("CLR");

            Assert.That(t.WaitFor("CLR CONF", 5000, mark), Is.True);
            int clr = t.Visible.IndexOf("CLR CONF", mark, StringComparison.Ordinal);
            Assert.That(t.WaitFor("PAD> ", 5000, clr), Is.True);
            Thread.Sleep(600);
            Assert.That(CountOf(t.Visible.Substring(clr), "PAD> "), Is.EqualTo(1), t.Visible.Substring(clr));
        }

        // ------------------------------------------------------------------ defaults after a call

        [Test]
        public void HostClear_PutsEveryX3ParameterBackToItsDefault_AndNothingStraysToTheHost()
        {
            ProtocolRuntime rt;
            TelnetTestTerminal t = Start(out rt);
            PadClient pad = PadOf(rt);
            Call(t, rt);
            WaitForHostEchoOff(pad);

            ushort lcn = rt.GetLocalTadProtocol().GetActiveSessions()[0].LCN;
            int mark = t.Visible.Length;
            rt.GetLocalTadProtocol().DisconnectSession(lcn);
            Assert.That(t.WaitFor("PAD> ", 5000, mark), Is.True);

            AssertAllDefaults(pad, "after the host cleared the call");

            // The typed command is echoed by the PAD again.
            mark = t.Visible.Length;
            t.TypeLine("HELP");
            Assert.That(t.WaitFor("PAD commands:", 5000, mark), Is.True);
            Assert.That(t.Visible.Substring(mark), Does.Contain("HELP\r\n"), "the PAD did not echo what was typed after the call");
        }

        [Test]
        public void UserClear_PutsEveryX3ParameterBackToItsDefault()
        {
            ProtocolRuntime rt;
            TelnetTestTerminal t = Start(out rt);
            PadClient pad = PadOf(rt);
            Call(t, rt);
            WaitForHostEchoOff(pad);

            int mark = t.Visible.Length;
            t.Send(0x10);
            t.TypeLine("CLR");
            Assert.That(t.WaitFor("CLR CONF", 5000, mark), Is.True);
            Assert.That(t.WaitFor("PAD> ", 5000, mark), Is.True);

            AssertAllDefaults(pad, "after the user cleared the call");
        }

        [Test]
        public void Disconnect_PutsEveryX3ParameterBackToItsDefault()
        {
            ProtocolRuntime rt;
            TelnetTestTerminal t = Start(out rt);
            PadClient pad = PadOf(rt);
            Call(t, rt);
            WaitForHostEchoOff(pad);

            pad.Disconnect();

            Assert.That(WaitUntil(() => pad.X3Protocol.GetParameter(2) == 1), Is.True, "P2 is not back to its default after Disconnect");
            AssertAllDefaults(pad, "after Disconnect");
        }

        [Test]
        public void ASecondCall_AfterTheDefaultsAreBack_StartsFromTheDefaultsAgain()
        {
            ProtocolRuntime rt;
            TelnetTestTerminal t = Start(out rt);
            PadClient pad = PadOf(rt);
            Call(t, rt);
            WaitForHostEchoOff(pad);
            int mark = t.Visible.Length;
            t.Send(0x10);
            t.TypeLine("CLR");
            Assert.That(t.WaitFor("CLR CONF", 5000, mark), Is.True);
            Assert.That(t.WaitFor("PAD> ", 5000, t.Visible.IndexOf("CLR CONF", mark, StringComparison.Ordinal)), Is.True);
            Assert.That(WaitUntil(() => rt.GetLocalTadProtocol().GetActiveSessions().Length == 0), Is.True);
            AssertAllDefaults(pad, "between the calls");

            Call(t, rt);
            WaitForHostEchoOff(pad);
        }
    }
}
