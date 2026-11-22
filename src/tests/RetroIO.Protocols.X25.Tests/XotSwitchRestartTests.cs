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
using RetroIO.Protocols.X25.Client;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;
using RetroIO.Protocols.X25.Transport;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// A subscriber behind a switch (the runtime's XOT Client entry with a spare connection, and <see cref="X25Client"/>)
    /// is a DTE: when the switch dies and comes back it must learn of it and serve calls again by itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the standards say (read 2026-11-18, line numbers of the text files in the Specs folder):
    /// X.25 (10/96) 4.6.2 (line 5881): a failure of the physical or data link layer is an out of order condition;
    /// 4.6.3 (line 5904-5914): the DCE then clears "with the cause 'Out of order' for each existing virtual call";
    /// 4.6.5 (line 5938-5941): when it is recovered the DCE sends "a restart indication packet with the cause 'Network
    /// operational'". Over XOT that packet cannot come: RFC 1613 6.4 (line 410-415) says Restart packets "MUST NOT be
    /// sent over the TCP connection". RFC 1613 has one TCP connection per circuit (section 5, line 201), so a lost
    /// connection is the out of order condition of that one circuit, and a connection that can be dialled again is the
    /// link coming back. Neither text gives a time between dials for a virtual call: X.25 2.4.4.1 (line 3348-3350) ends
    /// a failed link set-up with "appropriate higher layer recovery action", unnamed; RFC 1613 6.5 (line 570-571)
    /// suggests 5 minutes, but for a PVC set-up only. So the redial waits 2 seconds (the same as our switch port's
    /// redial), doubling to at most 30 seconds, and starts at 2 seconds again after a dial that works. That timing is our
    /// own choice, not the standard's.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class XotSwitchRestartTests
    {
        private const string Altos = "26245890040004";
        private const string Tad = "26245890040001";

        // X.29 (12/97) 4.2.1: the PAD protocol identifier.
        private static readonly byte[] PadCud = { 0x01, 0x00, 0x00, 0x00 };

        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _disposables.Count - 1; i >= 0; i--)
            {
                try
                {
                    _disposables[i].Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // A switch the test already took down.
                }
            }

            _disposables.Clear();
        }

        // ------------------------------------------------------------------ the redial timing

        /// <summary>
        /// 2 s, then doubling, never above 30 s; a dial that works starts it again at 2 s.
        /// </summary>
        [Test]
        public void TheRedialWait_Doubles_StopsAtTheMost_AndStartsAgainAfterADialThatWorks()
        {
            XotSpareRedial redial = new XotSpareRedial(2000, 30000);
            Assert.That(redial.Next(), Is.EqualTo(2000));
            Assert.That(redial.Next(), Is.EqualTo(4000));
            Assert.That(redial.Next(), Is.EqualTo(8000));
            Assert.That(redial.Next(), Is.EqualTo(16000));
            Assert.That(redial.Next(), Is.EqualTo(30000));
            Assert.That(redial.Next(), Is.EqualTo(30000));
            redial.Reset();
            Assert.That(redial.Next(), Is.EqualTo(2000));
        }

        // ------------------------------------------------------------------ the chat

        /// <summary>
        /// The switch dies (both its ports close every connection, the chat's spare included) and is started again on the
        /// same TCP ports. The runtime's entry dials its spare again by itself, and a new call reaches the chat.
        /// Until 2026-11-18 nothing dialled again and the chat stayed unreachable until the runtime was restarted.
        /// </summary>
        [Test]
        public void TheSwitchRestarts_TheEntryDialsItsSpareAgain_AndANewCallReachesTheChat()
        {
            Network net = BuildSwitch(0, 0);
            ProtocolRuntime runtime = StartRuntime(NullLogger.Instance, net.SubscriberPort.BoundPort);
            Assert.That(SpinWait.SpinUntil(() => net.SubscriberPort.ConnectionCount >= 1, 5000), Is.True, "no spare before the restart");

            Caller before = Dial(net, Altos, "26240001");
            Assert.That(before.WaitFor("+++guest+++\r\n", 10000), Is.True, "no join before the restart: " + before.Text);

            ushort subscriberPort = net.SubscriberPort.BoundPort;
            ushort callerPort = net.CallerPort.BoundPort;
            net.Kill();
            Assert.That(before.WaitForClear(10000), Is.True, "the caller's call outlived its switch");
            Assert.That(SpinWait.SpinUntil(() => runtime.ChatService!.UserCount == 0, 10000), Is.True, "the chat kept a user whose switch died");

            Network again = BuildSwitch(subscriberPort, callerPort);
            Assert.That(SpinWait.SpinUntil(() => again.SubscriberPort.ConnectionCount >= 1, 10000), Is.True, "the entry never dialled its spare again");

            Caller after = Dial(again, Altos, "26240002");
            Assert.That(after.WaitFor("+++guest+++\r\n", 10000), Is.True, "no join after the restart: " + after.Text);
        }

        /// <summary>
        /// The switch is not there when the runtime starts: the spare's dials fail several times, then the switch comes up,
        /// and a call reaches the chat without the runtime being started again.
        /// </summary>
        [Test]
        public void TheSwitchIsDownAtStart_DialsFail_ThenItComesUp_AndACallReachesTheChat()
        {
            ushort subscriberPort = FreePort();
            ProtocolRuntime runtime = StartRuntime(NullLogger.Instance, subscriberPort, redialFirstMs: 100, redialMostMs: 400);
            XotPeerTransport entry = (XotPeerTransport)runtime.TransportInterfaces["Altos"];
            Assert.That(SpinWait.SpinUntil(() => entry.GetPeerReport().DialFailures >= 3, 10000), Is.True,
                "the entry stopped dialling after " + entry.GetPeerReport().DialFailures + " failure(s)");

            Network net = BuildSwitch(subscriberPort, 0);
            Assert.That(SpinWait.SpinUntil(() => net.SubscriberPort.ConnectionCount >= 1, 10000), Is.True, "the entry never reached the switch once it was up");

            Caller caller = Dial(net, Altos, "26240003");
            Assert.That(caller.WaitFor("+++guest+++\r\n", 10000), Is.True, "no join: " + caller.Text);
        }

        /// <summary>
        /// Two switches in front of one chat. A is on switch 2, B on switch 1. Switch 1 dies with B's call up: the chat is
        /// told the call is cleared with cause Out of order, 0x09 (X.25 4.6.3 item 2, Table 5-7), and A sees B leave,
        /// "---guest---", once.
        /// </summary>
        [Test]
        public void ACallInProgress_WhenItsSwitchDies_IsClearedOutOfOrder_AndTheOthersSeeTheUserLeave()
        {
            TrunkListLogger log = new TrunkListLogger();
            Network one = BuildSwitch(0, 0);
            Network two = BuildSwitch(0, 0);
            ProtocolRuntime runtime = StartRuntime(log, one.SubscriberPort.BoundPort, secondSwitchPort: two.SubscriberPort.BoundPort);
            Assert.That(SpinWait.SpinUntil(() => one.SubscriberPort.ConnectionCount >= 1 && two.SubscriberPort.ConnectionCount >= 1, 5000), Is.True);

            Caller a = Dial(two, Altos, "26240011");
            Assert.That(a.WaitFor("+++guest+++\r\n", 10000), Is.True, "A did not join: " + a.Text);
            Caller b = Dial(one, Altos, "26240012");
            Assert.That(b.WaitFor("+++guest+++\r\n", 10000), Is.True, "B did not join: " + b.Text);
            Assert.That(a.WaitForCount("+++guest+++\r\n", 2, 10000), Is.True, "A did not see B join: " + a.Text);

            one.Kill();

            Assert.That(a.WaitFor("---guest---\r\n", 10000), Is.True, "A did not see B leave: " + a.Text);
            Assert.That(log.Contains("cleared, cause 0x09 diagnostic 0"), Is.True, "the chat was not told Out of order:\n" + log.All());

            // A's output is in order: once A sees a later line, every line about B's leaving has arrived.
            Caller c = Dial(two, Altos, "26240013");
            Assert.That(c.WaitFor("+++guest+++\r\n", 10000), Is.True);
            Assert.That(a.WaitForCount("+++guest+++\r\n", 3, 10000), Is.True);
            Assert.That(Count(a.Text, "---guest---\r\n"), Is.EqualTo(1), "a clear must be told once: " + a.Text);
        }

        // ------------------------------------------------------------------ the TAD

        /// <summary>
        /// The same entry carries every service of the runtime: after a switch restart a call to the TAD's address is
        /// answered again.
        /// </summary>
        [Test]
        public void TheSwitchRestarts_ANewCallReachesTheTad()
        {
            Network net = BuildSwitch(0, 0);
            StartRuntime(NullLogger.Instance, net.SubscriberPort.BoundPort);
            Assert.That(SpinWait.SpinUntil(() => net.SubscriberPort.ConnectionCount >= 1, 5000), Is.True);

            Caller before = Dial(net, Tad, "26240021");
            before.Hangup();

            ushort subscriberPort = net.SubscriberPort.BoundPort;
            ushort callerPort = net.CallerPort.BoundPort;
            net.Kill();

            Network again = BuildSwitch(subscriberPort, callerPort);
            Assert.That(SpinWait.SpinUntil(() => again.SubscriberPort.ConnectionCount >= 1, 10000), Is.True, "the entry never dialled its spare again");
            Dial(again, Tad, "26240022");
        }

        // ------------------------------------------------------------------ X25Client (the echo's library)

        /// <summary>
        /// <see cref="X25Client"/> answering calls behind a switch port (what x25echo does): the switch dies and comes back,
        /// the client dials its spare again by itself, and a new call is answered. A call that was up when the switch
        /// died is reported cleared with Out of order, 0x09.
        /// </summary>
        [Test]
        public void X25Client_TheSwitchRestarts_ItDialsItsSpareAgain_AndAnswersANewCall()
        {
            Network net = BuildSwitch(0, 0, subscriberDnicPrefix: "26245890040099");
            X25Client server = new X25Client { ServiceName = "echo-like" };
            _disposables.Add(server);
            int answered = 0;
            byte clearCause = 0xFF;
            using ManualResetEventSlim cleared = new ManualResetEventSlim(false);
            server.SetOnIncoming((c, vc) =>
            {
                Interlocked.Increment(ref answered);
                c.Accept(vc);
            });
            server.SetOnClear((c, vc, cause, diagnostic) =>
            {
                clearCause = cause;
                cleared.Set();
            });
            server.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = net.SubscriberPort.BoundPort,
                LocalAddress = "26245890040099",
                Framing = X25ClientFraming.Xot,
                SpareRedialFirstMs = 100,
                SpareRedialMostMs = 400,
            }, NullLogger.Instance);
            Assert.That(SpinWait.SpinUntil(() => net.SubscriberPort.ConnectionCount >= 1, 5000), Is.True);

            Dial(net, "26245890040099", "26240031");
            Assert.That(Volatile.Read(ref answered), Is.EqualTo(1));

            ushort subscriberPort = net.SubscriberPort.BoundPort;
            ushort callerPort = net.CallerPort.BoundPort;
            net.Kill();
            Assert.That(cleared.Wait(10000), Is.True, "the call up when the switch died was never reported cleared");
            Assert.That(clearCause, Is.EqualTo((byte)0x09), "X.25 4.6.3: a call lost with its link is cleared Out of order");

            // The switch comes back on the same TCP ports.
            Network again = BuildSwitch(subscriberPort, callerPort, subscriberDnicPrefix: "26245890040099");
            Assert.That(SpinWait.SpinUntil(() => again.SubscriberPort.ConnectionCount >= 1, 10000), Is.True, "the client never dialled its spare again");
            Dial(again, "26245890040099", "26240032");
            Assert.That(Volatile.Read(ref answered), Is.EqualTo(2));
        }

        /// <summary>
        /// The switch dies just after it accepted the dial of a new spare, before the client recorded it as the spare.
        /// The client must still dial again (added 2026-11-22: before, that spare was closed and nothing was dialled, the
        /// cause of the test above failing now and then).
        /// </summary>
        [Test]
        public void X25Client_TheSwitchDiesAsANewSpareOpens_ItStillDialsAgain()
        {
            Network net = BuildSwitch(0, 0, subscriberDnicPrefix: "26245890040099");
            X25Client server = new X25Client { ServiceName = "echo-like" };
            _disposables.Add(server);
            server.SetOnIncoming((c, vc) => c.Accept(vc));
            server.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = net.SubscriberPort.BoundPort,
                LocalAddress = "26245890040099",
                Framing = X25ClientFraming.Xot,
                SpareRedialFirstMs = 100,
                SpareRedialMostMs = 400,
            }, NullLogger.Instance);
            Assert.That(SpinWait.SpinUntil(() => net.SubscriberPort.ConnectionCount >= 1, 5000), Is.True);

            ushort subscriberPort = net.SubscriberPort.BoundPort;
            ushort callerPort = net.CallerPort.BoundPort;
            bool dropsSeen = false;

            // The call takes the spare; the next spare is dialled, and the switch dies right after it connected. The hook
            // returns only once the client has seen both connections drop, so the order is forced.
            server.SpareConnectedForTest = () =>
            {
                server.SpareConnectedForTest = null;
                net.Kill();
                dropsSeen = SpinWait.SpinUntil(() => server.ConnectionDropsForTest >= 2, 10000);
            };
            Dial(net, "26245890040099", "26240031");
            Assert.That(SpinWait.SpinUntil(() => server.SpareConnectedForTest == null && server.ConnectionDropsForTest >= 2, 10000), Is.True, "the switch was not killed as the spare opened");
            Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref dropsSeen), 10000), Is.True, "the client never saw the new spare drop");

            Network again = BuildSwitch(subscriberPort, callerPort, subscriberDnicPrefix: "26245890040099");
            Assert.That(SpinWait.SpinUntil(() => again.SubscriberPort.ConnectionCount >= 1, 10000), Is.True, "the client never dialled its spare again");
        }

        // ------------------------------------------------------------------ helpers

        private sealed class Network
        {
            public XotSwitchPort SubscriberPort = null!;
            public XotSwitchPort CallerPort = null!;

            /// <summary>The switch dies: both ports stop and close every connection.</summary>
            public void Kill()
            {
                SubscriberPort.Dispose();
                CallerPort.Dispose();
            }
        }

        /// <summary>
        /// One switch: port 0 is the subscriber line (calls for 2624589004... go there), port 1 is where callers connect.
        /// A TCP port of 0 lets the system choose; a number puts the port back where a switch that died had it.
        /// </summary>
        private Network BuildSwitch(ushort subscriberTcp, ushort callerTcp, string subscriberDnicPrefix = "2624589004")
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340" };
            cfg.Routing.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = subscriberDnicPrefix, PrefixLen = subscriberDnicPrefix.Length, DestPort = 0 });
            SwitchNode node = new SwitchNode(cfg);
            Network net = new Network
            {
                SubscriberPort = new XotSwitchPort(new PortConfig { Id = 0, Mode = PortMode.Dce, Port = subscriberTcp, LciBase = 1, LciCount = 64 }),
                CallerPort = new XotSwitchPort(new PortConfig { Id = 1, Mode = PortMode.Dce, Port = callerTcp, LciBase = 1, LciCount = 64 }),
            };
            Assert.That(net.SubscriberPort.Start(), Is.True, "the subscriber port did not start on TCP port " + subscriberTcp);
            Assert.That(net.CallerPort.Start(), Is.True, "the caller port did not start on TCP port " + callerTcp);
            _disposables.Add(net.SubscriberPort);
            _disposables.Add(net.CallerPort);
            node.AddPort(net.SubscriberPort);
            node.AddPort(net.CallerPort);
            new SwitchCore(node);
            return net;
        }

        /// <summary>
        /// The runtime with the chat on 26245890040004 and the TAD on 26245890040001, as the XOT client of a switch's
        /// subscriber port with a spare connection; optionally a second entry for a second switch.
        /// </summary>
        private ProtocolRuntime StartRuntime(Microsoft.Extensions.Logging.ILogger logger, int switchPort, int redialFirstMs = 100, int redialMostMs = 400, int secondSwitchPort = 0)
        {
            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = Tad;
            config.Chat.Address = Altos;
            config.Chat.MaxUsers = 30;
            config.Chat.WaitPauseMs = 0;
            config.Routing.XotInterfaces.Add(ClientEntry("Altos", switchPort, redialFirstMs, redialMostMs));
            if (secondSwitchPort != 0)
            {
                config.Routing.XotInterfaces.Add(ClientEntry("Altos2", secondSwitchPort, redialFirstMs, redialMostMs));
            }

            config.Routing.RoutingTable.Add(new X25RouteEntry { Name = "Chat", X121AddressPattern = Altos, Type = RetroIO.Protocols.X25.Runtime.RouteType.Local, Destination = "CHAT", Priority = 200 });
            config.Routing.RoutingTable.Add(new X25RouteEntry { Name = "Tad", X121AddressPattern = Tad, Type = RetroIO.Protocols.X25.Runtime.RouteType.Local, Destination = "LocalTAD", Priority = 200 });

            ProtocolRuntime runtime = new ProtocolRuntime(logger, config);
            _disposables.Add(runtime);
            Assert.That(runtime.StartTransportInterfaces(), Is.True, "the runtime's XOT entry did not start");
            return runtime;
        }

        private static XotInterfaceConfig ClientEntry(string name, int switchPort, int redialFirstMs, int redialMostMs)
        {
            return new XotInterfaceConfig
            {
                Name = name,
                Mode = XotMode.Client,
                RemoteIPAddress = "127.0.0.1",
                RemotePort = switchPort,
                ConnectTimeoutMs = 2000,
                KeepConnectionForIncomingCalls = true,
                SpareRedialFirstMs = redialFirstMs,
                SpareRedialMostMs = redialMostMs,
            };
        }

        /// <summary>
        /// A TCP port nothing listens on now: bound to 0, read back, released.
        /// </summary>
        private static ushort FreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            ushort port = (ushort)((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private Caller Dial(Network net, string called, string calling)
        {
            Caller caller = new Caller(net.CallerPort.BoundPort, called, calling);
            _disposables.Add(caller);
            Assert.That(caller.Call(), Is.True, () => "the call from " + calling + " to " + called + " was not connected: " + caller.ClearCause);
            return caller;
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
        /// One PAD user: an X25Client that calls with the PAD protocol identifier and keeps the text it is sent.
        /// </summary>
        private sealed class Caller : IDisposable
        {
            private readonly X25Client _client = new X25Client { ServiceName = "restart-caller" };
            private readonly StringBuilder _text = new StringBuilder();
            private readonly object _gate = new object();
            private readonly ManualResetEventSlim _cleared = new ManualResetEventSlim(false);
            private readonly string _called;
            private X25Vc? _vc;

            public Caller(ushort port, string called, string calling)
            {
                _called = called;
                _client.SetOnData((c, vc, data, length, q, m, d) =>
                {
                    if (q)
                    {
                        return;
                    }

                    lock (_gate)
                    {
                        _text.Append(Encoding.ASCII.GetString(data, 0, length));
                        System.Threading.Monitor.PulseAll(_gate);
                    }
                });
                _client.SetOnClear((c, vc, cause, diagnostic) =>
                {
                    ClearCause = "cause " + cause + " diagnostic " + diagnostic;
                    _cleared.Set();
                });
                _client.Open(new X25ClientConfig { Host = "127.0.0.1", Port = port, LocalAddress = calling, Framing = X25ClientFraming.Xot }, NullLogger.Instance);
            }

            public string ClearCause { get; private set; } = "no clear";

            public string Text
            {
                get
                {
                    lock (_gate)
                    {
                        return _text.ToString();
                    }
                }
            }

            public bool Call()
            {
                _vc = _client.CallWait(_called, 10000, PadCud);
                return _vc != null && _vc.Connected;
            }

            public void Hangup()
            {
                _client.Clear(_vc!);
            }

            public bool WaitFor(string part, int timeoutMs)
            {
                return WaitForCount(part, 1, timeoutMs);
            }

            public bool WaitForCount(string part, int count, int timeoutMs)
            {
                long end = Environment.TickCount64 + timeoutMs;
                lock (_gate)
                {
                    while (Count(_text.ToString(), part) < count)
                    {
                        long left = end - Environment.TickCount64;
                        if (left <= 0)
                        {
                            return false;
                        }

                        System.Threading.Monitor.Wait(_gate, (int)left);
                    }

                    return true;
                }
            }

            public bool WaitForClear(int timeoutMs)
            {
                return _cleared.Wait(timeoutMs);
            }

            public void Dispose()
            {
                _client.Dispose();
                _cleared.Dispose();
            }
        }
    }
}
