//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Client;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Services;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The Altos CHAT 3.1 service in the runtime (step 2 of RetroX25 docs/altos-chat-port-plan.md). The runtime runs
    /// in this process as the XOT client of a switch's subscriber port on 127.0.0.1 at a port chosen at run time, and
    /// the chat answers 26245890040004, the Altos address. Callers are X25Client users on another port of the same
    /// switch: their calls cross the switch to the runtime, as a PAD's would.
    /// </summary>
    [TestFixture]
    public class ChatProtocolServiceTests
    {
        private const string Altos = "26245890040004";

        // X.29 (12/97) 4.2.1: the PAD protocol identifier.
        private static readonly byte[] PadCud = { 0x01, 0x00, 0x00, 0x00 };

        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        [TearDown]
        public void TearDown()
        {
            // Last made, first gone: the callers, then the runtime, then the switch ports.
            for (int i = _disposables.Count - 1; i >= 0; i--)
            {
                _disposables[i].Dispose();
            }

            _disposables.Clear();
        }

        // ------------------------------------------------------------------ test 21, the X.29 set PAD message

        /// <summary>
        /// Test 21 of the plan: the X.29 set PAD message, byte for byte. Octet 1 0x02 (X.29 Table 2, set); then the
        /// parameter pairs 2=1 echo, 3=2 forward on CR, 13=4 LF after CR in the echo, 15=1 editing (X.3 3.2, 3.3, 3.13, 3.15), 16=127 DEL, 17=24 CAN,
        /// 18=18 DC2 (X.3 3.16-3.18 with the values of X.28 Table 1).
        /// </summary>
        [Test]
        public void TheSetPadMessage_IsExactlyTheseOctets()
        {
            Assert.That(ChatProtocolService.SetPadMessage, Is.EqualTo(new byte[] { 0x02, 0x02, 0x01, 0x03, 0x02, 0x0D, 0x04, 0x0F, 0x01, 0x10, 0x7F, 0x11, 0x18, 0x12, 0x12 }));
        }

        /// <summary>
        /// On an accepted call the first thing the service asks for is the Call Accepted, then the set PAD message in a
        /// data packet with the Q bit (X.29 1.5.2), and only then the banner as plain data.
        /// </summary>
        [Test]
        public void AnAcceptedCall_GetsTheSetPadMessageWithTheQBit_BeforeTheBanner()
        {
            RecordingRuntime runtime = new RecordingRuntime();
            using ChatProtocolService chat = new ChatProtocolService(NullLogger.Instance, runtime.DeliverServiceOutput, Altos,
                new AltosChatSettings { WaitPause = TimeSpan.Zero }, () => new DateTime(1987, 5, 29, 12, 0, 0));

            Assert.That(chat.HandleIncomingCall(7, Altos, "24220001", PadCud, Array.Empty<byte>()), Is.True);

            Assert.That(runtime.Outputs.Count, Is.GreaterThanOrEqualTo(3));
            Assert.That(runtime.Outputs[0].Kind, Is.EqualTo(TadOutputKind.CallAccepted));
            Assert.That(runtime.Outputs[1].Kind, Is.EqualTo(TadOutputKind.Data));
            Assert.That(runtime.Outputs[1].QBit, Is.True, "the set PAD message is a PAD message: Q bit set");
            Assert.That(runtime.Outputs[1].Data, Is.EqualTo(ChatProtocolService.SetPadMessage));
            Assert.That(runtime.Outputs[2].QBit, Is.False, "the banner is user data");
            Assert.That(Encoding.ASCII.GetString(runtime.Outputs[2].Data), Does.StartWith("Welcome to the original, evergreen\r\n"));
        }

        /// <summary>
        /// The chat takes PAD calls (X.29 protocol identifier, or no call user data) and nothing else; a PAD message
        /// from the PAD (Q bit) is never read as text.
        /// </summary>
        [Test]
        public void TheChat_TakesPadCallsOnly_AndNeverReadsAPadMessageAsText()
        {
            RecordingRuntime runtime = new RecordingRuntime();
            using ChatProtocolService chat = new ChatProtocolService(NullLogger.Instance, runtime.DeliverServiceOutput, Altos,
                new AltosChatSettings { WaitPause = TimeSpan.Zero }, () => new DateTime(1987, 5, 29, 12, 0, 0));

            Assert.That(chat.CanHandleCUD(PadCud), Is.True);
            Assert.That(chat.CanHandleCUD(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x41 }), Is.True, "a PAD call with call data");
            Assert.That(chat.CanHandleCUD(Array.Empty<byte>()), Is.True);
            Assert.That(chat.CanHandleCUD(new byte[] { 0xCC }), Is.False, "IP");
            Assert.That(chat.CanHandleCUD(new byte[] { 0x4C, 0x03, 0, 0, 0x10, 0x00 }), Is.False, "XMSG");

            Assert.That(chat.HandleIncomingCall(1, Altos, "24220001", PadCud, Array.Empty<byte>()), Is.True);
            Assert.That(chat.HandleIncomingCall(2, Altos, "24220002", PadCud, Array.Empty<byte>()), Is.True);
            runtime.Outputs.Clear();

            // A parameter indication PAD message (X.29 Table 2: 0000 0000) with the Q bit: not a line of text.
            chat.HandleData(1, new byte[] { 0x00, 0x02, 0x01, 0x0D }, true);
            Assert.That(runtime.Outputs.Count, Is.EqualTo(0), "a PAD message reached the chat as text");
        }

        // ------------------------------------------------------------------ through the runtime and a switch

        /// <summary>
        /// REQUIRED for X75ThreeCountryTests: the runtime starts in this process as the XOT client of a switch
        /// subscriber port on 127.0.0.1 (port chosen at run time), keeps a connection open for arriving calls, and the
        /// chat answers 26245890040004. A caller on the switch reaches it and sees the banner, the pause, and the join.
        /// The code a test needs is <see cref="StartChatRuntime"/>.
        /// </summary>
        [Test]
        public void TheRuntime_AsXotClientOfASwitchSubscriberPort_AnswersTheAltosAddress()
        {
            Network net = BuildSwitch();
            ProtocolRuntime runtime = StartChatRuntime(net.ChatPort.BoundPort, maxUsers: 30, waitPauseMs: 300);
            _disposables.Add(runtime);
            Assert.That(SpinWait.SpinUntil(() => net.ChatPort.ConnectionCount >= 1, 5000), Is.True, "the runtime never opened its connection for arriving calls");

            Caller a = Dial(net, "26240001");
            Assert.That(a.WaitFor("+++guest+++\r\n", 10000), Is.True, "no join; got: " + a.Text);

            // The PAD was set first, then the banner and the join, exactly (CHAT 3.1 chat.c:248-250, 277, 270; serv.c:253).
            Assert.That(a.FirstQData, Is.EqualTo(ChatProtocolService.SetPadMessage));
            Assert.That(a.Text, Is.EqualTo(
                "Welcome to the original, evergreen\r\n" +
                "Conference-utility \"CHAT\"  ver. 3.1\r\n" +
                "Copyright (C) 1987 Hans Korneder, Muenchen\r\n" +
                "Waiting for connection.\r\n" +
                "Connection established.\r\n" +
                "Get help with:    .h\r\n" +
                "+++guest+++\r\n"));
            Assert.That(runtime.ChatService, Is.Not.Null);
            Assert.That(runtime.ChatService!.UserCount, Is.EqualTo(1));
        }

        /// <summary>
        /// Two callers: each sees the other join, text crosses to the other and not back to the sender, and a cleared
        /// call is seen as "---guest---" exactly once.
        /// </summary>
        [Test]
        public void TwoCallers_TextCrosses_AndAClearIsSeenOnce()
        {
            Network net = BuildSwitch();
            ProtocolRuntime runtime = StartChatRuntime(net.ChatPort.BoundPort, maxUsers: 30, waitPauseMs: 0);
            _disposables.Add(runtime);
            Assert.That(SpinWait.SpinUntil(() => net.ChatPort.ConnectionCount >= 1, 5000), Is.True);

            Caller a = Dial(net, "26240001");
            Assert.That(a.WaitFor("+++guest+++\r\n", 10000), Is.True, "A did not join: " + a.Text);
            Caller b = Dial(net, "26240002");
            Assert.That(b.WaitFor("+++guest+++\r\n", 10000), Is.True, "B did not join: " + b.Text);
            Assert.That(a.WaitForCount("+++guest+++\r\n", 2, 10000), Is.True, "A did not see B join: " + a.Text);

            b.Type("hello from b\r");
            Assert.That(a.WaitFor("guest says: hello from b\r\n", 10000), Is.True, "B's text did not reach A: " + a.Text);
            a.Type("hello from a\r");
            Assert.That(b.WaitFor("guest says: hello from a\r\n", 10000), Is.True, "A's text did not reach B: " + b.Text);

            // B's own line never came back to B, and A's never to A (CHAT 3.1 serv.c: "says" goes to the others).
            Assert.That(b.Text, Does.Not.Contain("hello from b"));
            Assert.That(a.Text, Does.Not.Contain("hello from a"));

            // B's call is cleared by the caller: A sees ---guest--- once. A third caller joins afterwards; A's output is
            // in order, so once A sees that join every line about B's leaving has arrived.
            b.Hangup();
            Assert.That(a.WaitFor("---guest---\r\n", 10000), Is.True, "A did not see B leave: " + a.Text);
            Caller c = Dial(net, "26240003");
            Assert.That(c.WaitFor("+++guest+++\r\n", 10000), Is.True);
            Assert.That(a.WaitForCount("+++guest+++\r\n", 3, 10000), Is.True, "A did not see C join: " + a.Text);
            Assert.That(Count(a.Text, "---guest---\r\n"), Is.EqualTo(1), "a clear must be told once: " + a.Text);
            Assert.That(runtime.ChatService!.UserCount, Is.EqualTo(2));
        }

        /// <summary>
        /// With the limit at 30, caller 31 sees the banner, "Waiting for connection.", then "open of answerpipe
        /// failed." and the call is cleared (CHAT 3.1 serv.c:225-229, chat.c:261-269); the 30 inside never hear of it.
        /// </summary>
        [Test]
        public void TheThirtyFirstCaller_AtLimitThirty_IsRefused()
        {
            Network net = BuildSwitch();
            ProtocolRuntime runtime = StartChatRuntime(net.ChatPort.BoundPort, maxUsers: 30, waitPauseMs: 0);
            _disposables.Add(runtime);
            Assert.That(SpinWait.SpinUntil(() => net.ChatPort.ConnectionCount >= 1, 5000), Is.True);

            Caller[] users = new Caller[30];
            for (int i = 0; i < users.Length; i++)
            {
                users[i] = Dial(net, "262410" + i.ToString("D2"));
                Assert.That(users[i].WaitFor("+++guest+++\r\n", 10000), Is.True, "user " + (i + 1) + " did not join: " + users[i].Text);
            }

            Assert.That(users[0].WaitForCount("+++guest+++\r\n", 30, 10000), Is.True, "user 1 did not see all 30 join");

            Caller late = Dial(net, "26241099");
            Assert.That(late.WaitForClear(10000), Is.True, "caller 31 was not cleared: " + late.Text);
            Assert.That(late.Text, Is.EqualTo(
                "Welcome to the original, evergreen\r\n" +
                "Conference-utility \"CHAT\"  ver. 3.1\r\n" +
                "Copyright (C) 1987 Hans Korneder, Muenchen\r\n" +
                "Waiting for connection.\r\n" +
                "open of answerpipe failed.\r\n"));
            Assert.That(runtime.ChatService!.UserCount, Is.EqualTo(30));

            // The 30 inside never saw a 31st join: user 1 says something, and when user 2 has it, user 2's output up to
            // that line is complete.
            users[0].Type("still thirty\r");
            Assert.That(users[1].WaitFor("guest says: still thirty\r\n", 10000), Is.True);
            Assert.That(Count(users[1].Text, "+++guest+++\r\n"), Is.EqualTo(29), "user 2 joined second-to-first and saw 29 joins");
        }

        /// <summary>
        /// Forty callers (limit raised to 40) through the runtime: every one joins, every one says two lines, and every
        /// one gets exactly the 78 lines of the other 39, each sender's lines in the order sent.
        /// </summary>
        [Test]
        public void FortyCallers_ThroughTheRuntime_EachGetsEveryOtherLineInOrder()
        {
            const int Users = 40;
            const int Lines = 2;
            Network net = BuildSwitch();
            ProtocolRuntime runtime = StartChatRuntime(net.ChatPort.BoundPort, maxUsers: Users, waitPauseMs: 0);
            _disposables.Add(runtime);
            Assert.That(SpinWait.SpinUntil(() => net.ChatPort.ConnectionCount >= 1, 5000), Is.True);

            Caller[] users = new Caller[Users];
            for (int i = 0; i < Users; i++)
            {
                users[i] = Dial(net, "262420" + i.ToString("D2"));
                Assert.That(users[i].WaitFor("+++guest+++\r\n", 10000), Is.True, "user " + i + " did not join: " + users[i].Text);
            }

            Assert.That(users[0].WaitForCount("+++guest+++\r\n", Users, 10000), Is.True, "user 0 did not see all join");
            Assert.That(runtime.ChatService!.UserCount, Is.EqualTo(Users));

            for (int line = 0; line < Lines; line++)
            {
                for (int i = 0; i < Users; i++)
                {
                    users[i].Type("u" + i.ToString("D2") + " line " + line + "\r");
                }
            }

            for (int r = 0; r < Users; r++)
            {
                Assert.That(users[r].WaitForCount(" says: ", (Users - 1) * Lines, 20000), Is.True,
                    "user " + r + " got " + Count(users[r].Text, " says: ") + " lines of " + ((Users - 1) * Lines));
            }

            for (int r = 0; r < Users; r++)
            {
                string text = users[r].Text;
                for (int s = 0; s < Users; s++)
                {
                    string first = "guest says: u" + s.ToString("D2") + " line 0\r\n";
                    string second = "guest says: u" + s.ToString("D2") + " line 1\r\n";
                    if (s == r)
                    {
                        Assert.That(text, Does.Not.Contain(first), "user " + r + " got its own line back");
                        continue;
                    }

                    Assert.That(Count(text, first), Is.EqualTo(1), "user " + r + ", line 0 of user " + s);
                    Assert.That(Count(text, second), Is.EqualTo(1), "user " + r + ", line 1 of user " + s);
                    Assert.That(text.IndexOf(first, StringComparison.Ordinal), Is.LessThan(text.IndexOf(second, StringComparison.Ordinal)),
                        "user " + r + " got user " + s + "'s lines out of order");
                }
            }
        }

        /// <summary>
        /// A channel that carries a call held by a service (the TAD, XMSG, the chat) is not free. Until 2026-11-18 the
        /// runtime counted only channels bound to a module as busy, so the second of two callers held by services at
        /// the same time was given the first one's local channel: the chat refused it ("local channel 1 still carries
        /// a call"), and the TAD would have mixed the two callers.
        /// </summary>
        [Test]
        public void AChannelHeldByAService_IsNotGivenToTheNextCall()
        {
            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = "26245890040001";
            using ProtocolRuntime runtime = new ProtocolRuntime(NullLogger<ProtocolRuntime>.Instance, config);
            RecordingRuntime sink = new RecordingRuntime();
            using ChatProtocolService chat = new ChatProtocolService(NullLogger.Instance, sink.DeliverServiceOutput, Altos,
                new AltosChatSettings(), () => new DateTime(1987, 5, 29, 12, 0, 0));

            ushort first = runtime.FindAvailableLCN();
            runtime.BindServiceToLcn(first, chat);
            ushort second = runtime.FindAvailableLCN();

            Assert.That(second, Is.Not.EqualTo(first), "a channel held by a service was handed out again");
        }

        /// <summary>
        /// The "Chat" setting: no address (the default) means no chat; the TAD's own address is refused, because one
        /// address would then send every PAD call to the chat and none to the TAD.
        /// </summary>
        [Test]
        public void TheChat_IsOffWithoutAnAddress_AndRefusesTheTadAddress()
        {
            RuntimeConfig off = new RuntimeConfig();
            off.X25.LocalMachineId = "26245890040001";
            using (ProtocolRuntime runtime = new ProtocolRuntime(NullLogger<ProtocolRuntime>.Instance, off))
            {
                Assert.That(runtime.ChatService, Is.Null, "a chat was started with no address");
            }

            RuntimeConfig clash = new RuntimeConfig();
            clash.X25.LocalMachineId = "26245890040001";
            clash.Chat.Address = "26245890040001";
            using (ProtocolRuntime runtime = new ProtocolRuntime(NullLogger<ProtocolRuntime>.Instance, clash))
            {
                Assert.That(runtime.ChatService, Is.Null, "the chat took the TAD's address");
            }
        }

        // ------------------------------------------------------------------ the code a test needs

        /// <summary>
        /// Starts the runtime in this process with the chat on 26245890040004, as the XOT client of a switch
        /// subscriber port at 127.0.0.1:<paramref name="switchPort"/>. This is the whole recipe: the "Chat" section
        /// names the address, one Local route lets calls to it in, and one Client entry with
        /// <c>KeepConnectionForIncomingCalls</c> keeps a connection open for the calls the switch puts on it. <see cref="ProtocolRuntime.Dispose"/> stops it.
        /// </summary>
        /// <param name="switchPort">The TCP port the switch's subscriber port listens on.</param>
        /// <param name="maxUsers">The chat's user limit.</param>
        /// <param name="waitPauseMs">The pause after "Waiting for connection." in milliseconds.</param>
        /// <returns>The started runtime.</returns>
        public static ProtocolRuntime StartChatRuntime(int switchPort, int maxUsers, int waitPauseMs)
        {
            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = "26245890040001";
            config.Chat.Address = Altos;
            config.Chat.MaxUsers = maxUsers;
            config.Chat.WaitPauseMs = waitPauseMs;
            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "Altos",
                Mode = XotMode.Client,
                RemoteIPAddress = "127.0.0.1",
                RemotePort = switchPort,
                KeepConnectionForIncomingCalls = true,
            });

            // A call that arrives is let in only if an incoming route takes its number (X25Router.DecideIncomingCall);
            // a Local route hands it to the services of this runtime, where the chat takes it by its address.
            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "Chat",
                X121AddressPattern = Altos,
                Type = RetroIO.Protocols.X25.Runtime.RouteType.Local,
                Destination = "CHAT",
                Priority = 200,
            });

            ProtocolRuntime runtime = new ProtocolRuntime(NullLogger<ProtocolRuntime>.Instance, config);
            Assert.That(runtime.StartTransportInterfaces(), Is.True, "the runtime's XOT entry did not start");
            return runtime;
        }

        // ------------------------------------------------------------------ helpers

        private sealed class Network
        {
            public XotSwitchPort ChatPort = null!;
            public XotSwitchPort CallerPort = null!;
        }

        /// <summary>
        /// One switch, network 2624: port 0 is the chat's subscriber line (calls for 2624589004... go there), port 1 is
        /// where the callers connect.
        /// </summary>
        private Network BuildSwitch()
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340" };
            cfg.Routing.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = "2624589004", PrefixLen = 10, DestPort = 0 });
            SwitchNode node = new SwitchNode(cfg);
            Network net = new Network
            {
                ChatPort = new XotSwitchPort(new PortConfig { Id = 0, Mode = PortMode.Dce, Port = 0, LciBase = 1, LciCount = 64 }),
                // A switch port takes one TCP connection per channel, and every X25Client caller holds two: its call and
                // the spare it keeps open for arriving calls. 64 channels stopped the 33rd of 40 callers at the accept.
                CallerPort = new XotSwitchPort(new PortConfig { Id = 1, Mode = PortMode.Dce, Port = 0, LciBase = 1, LciCount = 128 }),
            };
            Assert.That(net.ChatPort.Start(), Is.True);
            Assert.That(net.CallerPort.Start(), Is.True);
            _disposables.Add(net.ChatPort);
            _disposables.Add(net.CallerPort);
            node.AddPort(net.ChatPort);
            node.AddPort(net.CallerPort);
            new SwitchCore(node);
            return net;
        }

        private Caller Dial(Network net, string calling)
        {
            Caller caller = new Caller(net.CallerPort.BoundPort, calling);
            _disposables.Add(caller);
            Assert.That(caller.Call(), Is.True, () => "the call from " + calling + " to the chat was not connected: " + caller.ClearCause);
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
        /// One chat user: an X25Client that calls the chat with the PAD protocol identifier and keeps everything the
        /// chat sends it. It types like a PAD that forwards on CR (parameter 3 = 2): a whole line, then CR, in one packet.
        /// </summary>
        private sealed class Caller : IDisposable
        {
            private readonly X25Client _client = new X25Client { ServiceName = "chat-caller" };
            private readonly StringBuilder _text = new StringBuilder();
            private readonly object _gate = new object();
            private readonly ManualResetEventSlim _cleared = new ManualResetEventSlim(false);
            private X25Vc? _vc;
            private byte[]? _firstQData;

            public Caller(ushort port, string calling)
            {
                _client.SetOnData((c, vc, data, length, q, m, d) =>
                {
                    lock (_gate)
                    {
                        if (q)
                        {
                            _firstQData ??= data.AsSpan(0, length).ToArray();
                            return;
                        }

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

            public byte[]? FirstQData
            {
                get
                {
                    lock (_gate)
                    {
                        return _firstQData;
                    }
                }
            }

            public bool Call()
            {
                _vc = _client.CallWait(Altos, 10000, PadCud);
                return _vc != null && _vc.Connected;
            }

            public void Type(string line)
            {
                Assert.That(_client.Send(_vc!, Encoding.ASCII.GetBytes(line)), Is.True, "the line was not sent: " + line);
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

        /// <summary>
        /// Stands in for the runtime in the tests that drive the service directly: keeps every request in order.
        /// </summary>
        private sealed class RecordingRuntime
        {
            public readonly List<TadOutputEventArgs> Outputs = new List<TadOutputEventArgs>();

            public void DeliverServiceOutput(TadOutputEventArgs output)
            {
                Outputs.Add(output);
            }
        }
    }
}
