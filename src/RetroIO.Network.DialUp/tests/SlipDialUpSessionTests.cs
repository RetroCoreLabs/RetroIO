//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

using NUnit.Framework;

using RetroIO.Network.Nat;
using RetroIO.Network.Router;
using RetroIO.Protocols.Slip;

namespace RetroIO.Network.DialUp.Tests
{
    /// <summary>
    /// A dial-up session driven byte by byte, as a modem line drives it: the login and its check,
    /// then SLIP to a NAT of its own and to a shared router, against a real UDP socket on this
    /// computer. The caller's SLIP is a second <see cref="SlipLink"/>, as a caller's own SLIP
    /// driver would be.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class SlipDialUpSessionTests
    {
        private SlipDialUpSession _session = null!;
        private SlipLink _callerSlip = null!;
        private List<byte[]> _callerPackets = null!;
        private List<string> _ended = null!;
        private IDialUpPacketSide? _side;
        private Socket? _echo;

        [SetUp]
        public void SetUp()
        {
            _callerPackets = new List<byte[]>();
            _ended = new List<string>();
            _callerSlip = new SlipLink();
            _callerSlip.OnPacket += packet => _callerPackets.Add(packet.ToArray());
            _callerSlip.OnLineOutput += bytes => TypeBytes(bytes);
        }

        [TearDown]
        public void TearDown()
        {
            _session?.CallerHungUp();
            _echo?.Close();
        }

        [Test]
        public void TheNameIsEchoedThePasswordIsNotThenSlipStarts()
        {
            Answer(new DialUpSettings(), new NatDialUpSide());
            Assert.That(Text(), Does.EndWith("RetroIO ISP\r\n\r\nlogin: "));

            TypeText("guest\r");
            Assert.That(Text(), Is.EqualTo("guest\r\nPassword: "));
            TypeText("secret\r");
            string reply = Text();

            Assert.That(reply, Does.Not.Contain("secret"));
            Assert.That(reply, Does.Contain("SLIP mode. Your address is 10.0.2.15, the gateway is 10.0.2.2, the name server is 10.0.2.3, MTU 1006."));
            Assert.That(_session.InSlipMode, Is.True);
            Assert.That(_session.UserName, Is.EqualTo("guest"));
        }

        [Test]
        public void BackspaceTakesBackOneCharacterOfTheName()
        {
            Answer(new DialUpSettings(), new NatDialUpSide());
            Text();

            TypeText("gux");
            TypeBytes(new byte[] { 8 });
            TypeText("est\rx\r");

            Assert.That(Text(), Does.StartWith("gux\b \best\r\nPassword: "));
            Assert.That(_session.UserName, Is.EqualTo("guest"));
        }

        [Test]
        public void AWrongLoginPromptsAgainAndTheThirdHangsUp()
        {
            DialUpSettings settings = new DialUpSettings { LoginCheck = (name, password) => name == "ronny" && password == "right" };
            Answer(settings, new NatDialUpSide());
            Text();

            TypeText("ronny\rwrong\r");
            Assert.That(Text(), Does.EndWith("Login incorrect\r\nlogin: "));
            TypeText("ronny\rwrong\r");
            TypeText("ronny\rwrong\r");

            Assert.That(Text(), Does.EndWith("Login incorrect\r\n"), "the last words still reach the caller");
            Assert.That(_session.Ended, Is.True);
            Assert.That(_ended, Is.EqualTo(new[] { "login refused for ronny after 3 tries" }));
            Assert.That(_session.TryTakeFromCaller((byte)'x'), Is.False);
        }

        [Test]
        public void TheRightLoginAfterAWrongOneGetsOn()
        {
            DialUpSettings settings = new DialUpSettings { LoginCheck = (name, password) => password == "right" };
            Answer(settings, new NatDialUpSide());
            TypeText("ronny\rwrong\rronny\rright\r");

            Assert.That(Text(), Does.Contain("SLIP mode."));
            Assert.That(_session.InSlipMode, Is.True);
        }

        [Test]
        public void ASlowCheckLetsTheSessionWaitAndSlipStartsAtTheNextPollAfterYes()
        {
            DialUpLoginAnswer? pending = null;
            Answer(new DialUpSettings { LoginStart = (name, password, answer) => pending = answer }, new NatDialUpSide());
            Text();
            TypeText("ronny\rright\r");
            Text();

            TypeText("typed while waiting");
            _session.Poll();
            Assert.That(Text(), Is.Empty, "nothing echoed while the check runs");
            Assert.That(_session.InSlipMode, Is.False);

            pending!.Accept();
            Assert.That(_session.InSlipMode, Is.False, "the answer is acted on in Poll, on the session's thread");
            _session.Poll();

            Assert.That(_session.InSlipMode, Is.True);
            Assert.That(Text(), Does.Contain("SLIP mode."));
        }

        [Test]
        public void ASlowNoCountsAsAWrongLogin()
        {
            DialUpLoginAnswer? pending = null;
            Answer(new DialUpSettings { LoginStart = (name, password, answer) => pending = answer }, new NatDialUpSide());
            Text();
            TypeText("ronny\rwrong\r");

            pending!.Refuse();
            pending.Accept();
            _session.Poll();

            Assert.That(Text(), Does.EndWith("Login incorrect\r\nlogin: "), "only the first answer counts");
        }

        [Test]
        public void ACheckThatAnswersAtOnceNeedsNoPoll()
        {
            Answer(new DialUpSettings { LoginStart = (name, password, answer) => answer.Accept() }, new NatDialUpSide());
            TypeText("ronny\rright\r");

            Assert.That(_session.InSlipMode, Is.True);
        }

        [Test]
        public void AnAnswerFromAnotherThreadIsTaken()
        {
            Answer(new DialUpSettings { LoginStart = (name, password, answer) => System.Threading.ThreadPool.QueueUserWorkItem(_ => answer.Accept()) }, new NatDialUpSide());
            TypeText("ronny\rright\r");

            Stopwatch clock = Stopwatch.StartNew();
            while (!_session.InSlipMode && clock.ElapsedMilliseconds < 5000)
            {
                _session.Poll();
            }

            Assert.That(_session.InSlipMode, Is.True);
        }

        [Test]
        public void AHangUpWhileTheCheckRunsMakesTheAnswerGoNowhere()
        {
            IpRouter router = SharedRouter();
            DialUpLoginAnswer? pending = null;
            Answer(new DialUpSettings { LoginStart = (name, password, answer) => pending = answer }, new RouterDialUpSide(router, 0));
            TypeText("ronny\rright\r");

            _session.CallerHungUp();
            pending!.Accept();
            _session.Poll();

            Assert.That(_session.Ended, Is.True);
            Assert.That(router.LinkCount, Is.EqualTo(0), "no address taken for a caller who left");
        }

        [Test]
        public void BothChecksAtOnceAreRefused()
        {
            DialUpSettings settings = new DialUpSettings { LoginCheck = (n, p) => true, LoginStart = (n, p, a) => a.Accept() };

            Assert.Throws<ArgumentException>(() => new SlipDialUpSession(settings, new NatDialUpSide()));
        }

        [Test]
        public void WithoutALoginSlipStartsAtOnce()
        {
            Answer(new DialUpSettings { LoginPrompt = false }, new NatDialUpSide());

            Assert.That(Text(), Does.StartWith("\r\nSLIP mode. Your address is 10.0.2.15"));
            Assert.That(_session.InSlipMode, Is.True);
        }

        [Test]
        public void SlipThroughANatOfItsOwnReachesAHostSocketAndBack()
        {
            Answer(new DialUpSettings { LoginPrompt = false }, new NatDialUpSide());
            Text();

            RoundTrip(Ipv4.Parse("10.0.2.15"), Ipv4.Parse("10.0.2.2"));

            Assert.That(_session.SlipErrors, Is.EqualTo(0));
        }

        [Test]
        public void SlipThroughASharedRouterAndItsNatReachesAHostSocketAndBack()
        {
            IpRouter router = SharedRouter();
            NatEthernetBackend nat = new NatEthernetBackend(
                new NatAddressPlan(Ipv4.Parse("10.42.0.0"), 0xFFFFFF00, Ipv4.Parse("10.42.0.2"), Ipv4.Parse("10.42.0.3"), Ipv4.Parse("10.42.0.10"), new byte[] { 0x02, 0x52, 0x43, 0x00, 0x2A, 0x02 }),
                true, null, true);
            router.OnUpstream += p => nat.SendPacket(p.ToArray(), 0, p.Length);
            nat.OnPacketReceived += (packet, offset, length) => router.SendFromUpstream(packet.AsSpan(offset, length));
            nat.Start();
            try
            {
                Answer(new DialUpSettings { LoginPrompt = false }, new RouterDialUpSide(router, Ipv4.Parse("10.42.0.3")));
                Assert.That(Text(), Does.Contain("Your address is 10.42.0.10, the gateway is 10.42.0.1, the name server is 10.42.0.3"));

                RoundTrip(Ipv4.Parse("10.42.0.10"), Ipv4.Parse("10.42.0.2"), nat);
            }
            finally
            {
                nat.Stop();
            }
        }

        [Test]
        public void TheRouterGivesNoAddressUntilTheLoginIsOver()
        {
            IpRouter router = SharedRouter();
            Answer(new DialUpSettings(), new RouterDialUpSide(router, 0));
            TypeText("ronny\r");

            Assert.That(router.LinkCount, Is.EqualTo(0));
            TypeText("pw\r");
            Assert.That(router.LinkCount, Is.EqualTo(1));
        }

        [Test]
        public void AUsersOwnAddressComesFromTheLookup()
        {
            IpRouter router = SharedRouter();
            RouterDialUpSide side = new RouterDialUpSide(router, 0, addressLookup: name => name == "ronny" ? Ipv4.Parse("10.42.0.50") : 0);
            Answer(new DialUpSettings(), side);
            TypeText("ronny\rpw\r");

            Assert.That(Text(), Does.Contain("Your address is 10.42.0.50"));
            Assert.That(side.Link!.Address, Is.EqualTo(Ipv4.Parse("10.42.0.50")));
        }

        [Test]
        public void HangingUpGivesTheAddressBack()
        {
            IpRouter router = SharedRouter();
            Answer(new DialUpSettings { LoginPrompt = false }, new RouterDialUpSide(router, 0));
            Assert.That(router.LinkCount, Is.EqualTo(1));

            _session.CallerHungUp();

            Assert.That(router.LinkCount, Is.EqualTo(0));
            Assert.That(_ended, Is.EqualTo(new[] { "the caller hung up" }));
        }

        [Test]
        public void WithTheRouterFullTheCallerIsToldAndTheSessionHangsUp()
        {
            IpRouter router = new IpRouter(new RouterSettings { PoolFirst = Ipv4.Parse("10.42.0.10"), PoolLast = Ipv4.Parse("10.42.0.10") });
            router.AddLink(p => { });
            Answer(new DialUpSettings { LoginPrompt = false }, new RouterDialUpSide(router, 0));

            Assert.That(Text(), Does.Contain("No line free, please call again later."));
            Assert.That(_session.Ended, Is.True);
            Assert.That(_ended[0], Does.StartWith("the packet side has no room: no free address left in the pool 10.42.0.10 to 10.42.0.10"));
        }

        // The dial-up subnet Ronny chose, with the NAT's gateway and name server as upstream
        // addresses and the callers' pool from 10.42.0.10.
        private static IpRouter SharedRouter()
        {
            return new IpRouter(new RouterSettings
            {
                PoolFirst = Ipv4.Parse("10.42.0.10"),
                UpstreamAddresses = new[] { Ipv4.Parse("10.42.0.2"), Ipv4.Parse("10.42.0.3") },
            });
        }

        private void Answer(DialUpSettings settings, IDialUpPacketSide side)
        {
            _side = side;
            _session = new SlipDialUpSession(settings, side);
            _session.OnEnded += reason => _ended.Add(reason);
            _session.Answer();
        }

        // A UDP datagram from the caller to the gateway reaches a socket on this computer's
        // 127.0.0.1, and the socket's answer comes back to the caller over SLIP.
        private void RoundTrip(uint caller, uint gateway, NatEthernetBackend? sharedNat = null)
        {
            _echo = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _echo.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _echo.ReceiveTimeout = 5000;
            ushort port = (ushort)((IPEndPoint)_echo.LocalEndPoint!).Port;

            byte[] packet = new byte[20 + 8 + 4];
            Encoding.ASCII.GetBytes("ping").CopyTo(packet, 28);
            NatPacket.WriteUdp(packet.AsSpan(20), caller, gateway, 4000, port);
            NatPacket.WriteIpv4(packet, packet.Length, 1, NatPacket.ProtocolUdp, caller, gateway);
            _callerSlip.SendPacket(packet);

            byte[] got = new byte[16];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n = _echo.ReceiveFrom(got, ref from);
            Assert.That(Encoding.ASCII.GetString(got, 0, n), Is.EqualTo("ping"));
            _echo.SendTo(Encoding.ASCII.GetBytes("pong"), from);

            Span<byte> one = stackalloc byte[1];
            Stopwatch clock = Stopwatch.StartNew();
            while (_callerPackets.Count == 0 && clock.ElapsedMilliseconds < 5000)
            {
                _session.Poll();
                sharedNat?.Poll();
                while (_session.TryGiveToCaller(out byte b))
                {
                    one[0] = b;
                    _callerSlip.ReceiveFromLine(one);
                }
            }

            Assert.That(_callerPackets, Has.Count.EqualTo(1), "the answer came back over SLIP");
            byte[] reply = _callerPackets[0];
            Assert.That(Ipv4.Source(reply), Is.EqualTo(gateway));
            Assert.That(Ipv4.Destination(reply), Is.EqualTo(caller));
            Assert.That(Encoding.ASCII.GetString(reply, 28, 4), Is.EqualTo("pong"));
        }

        private void TypeText(string text)
        {
            TypeBytes(Encoding.ASCII.GetBytes(text));
        }

        private void TypeBytes(ReadOnlySpan<byte> bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                _session.TryTakeFromCaller(bytes[i]);
            }
        }

        private string Text()
        {
            StringBuilder sb = new StringBuilder();
            while (_session.TryGiveToCaller(out byte b))
            {
                sb.Append((char)b);
            }

            return sb.ToString();
        }
    }
}
