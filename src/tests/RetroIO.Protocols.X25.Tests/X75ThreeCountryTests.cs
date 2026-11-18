//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.PacketLayer;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Three national networks joined by X.75 links: Norway (DNIC 2422) - England (DNIC 2342, transit) - Germany (DNIC 2624).
    /// A PAD user in Norway types an X.28 selection with the NUI of the University of Tromsø, 242226500000101, and calls the
    /// host Altger at 26245890040004; a TAD on a subscriber port in Germany answers, and data crosses both ways.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every expected value is from CCITT X.75 (11/1988), read 2026-11-18:
    /// </para>
    /// <list type="bullet">
    /// <item><description>5.3.1: the transit list names only transit networks, in the order crossed. Norway originates
    /// (names nobody), England is the transit network (adds 2342), Germany is the destination (adds nothing). The Call
    /// Connected carries the same list back: "always present for each transit network in the call connected packet".
    /// </description></item>
    /// <item><description>5.3.2: the call identifier is "always present in the call request packet", "established by the
    /// originating network" - so Norway sets it and England passes it unchanged.</description></item>
    /// <item><description>5.1: the window and packet sizes cross the links as utilities (5.3.4, 5.3.5), not in the user
    /// facility field.</description></item>
    /// </list>
    /// <para>
    /// And X.25 6.21: "Network user identifier is never transmitted to the remote DTE" - the NUI stops at Norway.
    /// </para>
    /// <para>
    /// The X.75 links run over HDLC on TCP (LAPB, as X.75's link layer is), on loopback. The subscriber lines are XOT.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class X75ThreeCountryTests
    {
        private const string Tromso = "242226500000101";
        private const string Altger = "26245890040004";

        // The PAD's call, captured from the X.28 command interpreter.
        private sealed class CapturingTransport : IPadTransport
        {
            public string Called = string.Empty;
            public byte[]? CallUserData;
            public byte[] Facilities = Array.Empty<byte>();

            public bool PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData)
            {
                return PlaceCall(calledAddress, callingAddress, callUserData, Array.Empty<byte>());
            }

            public bool PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData, byte[] facilities)
            {
                Called = calledAddress;
                CallUserData = callUserData;
                Facilities = facilities;
                return true;
            }

            public bool SendData(ushort lcn, byte[] data, bool mBit, bool qBit, bool dBit) => true;

            public void ForwardData(ushort lcn, byte[] data, int length)
            {
            }

            public void ClearCall(ushort lcn, byte cause, byte diagnostic)
            {
            }
        }

        private sealed class Country
        {
            public SwitchNode Node = null!;
            public SwitchCore Core = null!;
            public readonly List<ISwitchPort> Ports = new List<ISwitchPort>();
        }

        private readonly List<Country> _countries = new List<Country>();
        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _disposables.Count - 1; i >= 0; i--)
            {
                _disposables[i].Dispose();
            }

            for (int c = 0; c < _countries.Count; c++)
            {
                for (int p = 0; p < _countries[c].Ports.Count; p++)
                {
                    _countries[c].Ports[p].Stop();
                }
            }

            _disposables.Clear();
            _countries.Clear();
        }

        private static PortConfig NetworkLink(int id, PortMode mode, ushort dialPort)
        {
            // trunk_kind = network: an X.75 link. The dte end dials and searches channels from the highest, the dce end
            // listens and searches from the lowest (X.75 3.1.4: agreed between the two networks).
            return new PortConfig
            {
                Id = id,
                Type = PortType.Trunk,
                Transport = PortTransport.HdlcTcp,
                BetweenNetworks = true,

                // What the config loader gives a trunk with no called_address key: the full international number,
                // prefix taken off (X.121 7.8.1, 7.12.6-7.12.7).
                CalledAddressForm = X121AddressForm.International,
                Mode = mode,
                Host = "127.0.0.1",
                Port = dialPort,
                LciBase = 1,
                LciCount = 16,
                LapbT1Ms = 500,
            };
        }

        private static PortConfig Subscriber(int id)
        {
            return new PortConfig { Id = id, Mode = PortMode.Dce, Host = "127.0.0.1", Port = 0, LciBase = 1, LciCount = 16 };
        }

        private Country Build(string dnic, string routePrefix, ISwitchPort port0, ISwitchPort port1, string? nuiAccount = null, string? internationalPrefix = null)
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = dnic };

            // Set before the switch is built: the routing table copies it then (X121TableBuilder).
            if (internationalPrefix != null)
            {
                cfg.Routing.InternationalPrefix = internationalPrefix;
            }

            cfg.Routing.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = routePrefix, PrefixLen = routePrefix.Length, DestPort = 1 });
            if (nuiAccount != null)
            {
                cfg.Accounts.Add(new AccountConfig { Name = "uit", Nui = nuiAccount });
            }

            Country c = new Country { Node = new SwitchNode(cfg) };
            c.Ports.Add(port0);
            c.Ports.Add(port1);
            c.Node.AddPort(port0);
            c.Node.AddPort(port1);
            c.Core = new SwitchCore(c.Node);
            _countries.Add(c);
            return c;
        }

        private static void SendXot(NetworkStream stream, byte[] packet)
        {
            byte[] frame = new byte[4 + packet.Length];
            frame[2] = (byte)(packet.Length >> 8);
            frame[3] = (byte)packet.Length;
            packet.CopyTo(frame, 4);
            stream.Write(frame, 0, frame.Length);
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

        // Reads XOT packets until one of the wanted type arrives (flow control packets on the way are skipped).
        private static X25PacketInfo ReadUntil(NetworkStream stream, X25PacketType wanted)
        {
            for (int i = 0; i < 20; i++)
            {
                byte[] header = ReadExactly(stream, 4);
                X25PacketInfo info = X25PacketParser.ParsePacket(ReadExactly(stream, (header[2] << 8) | header[3]));
                if (info.PacketType == wanted)
                {
                    return info;
                }

                Assert.That(info.PacketType, Is.Not.EqualTo(X25PacketType.ClearRequest),
                    "cleared: cause " + info.ClearingCause + " diagnostic " + info.DiagnosticCode);
            }

            throw new AssertionException("no " + wanted + " packet arrived");
        }

        // The X.75 packets of one type a link end captured in one direction, decoded as X.75.
        private static X25PacketInfo Captured(List<byte[]> packets, X25PacketType type, bool x75 = true)
        {
            lock (packets)
            {
                for (int i = 0; i < packets.Count; i++)
                {
                    X25PacketInfo info = X25PacketParser.ParsePacket(packets[i], x75);
                    if (info.PacketType == type)
                    {
                        return info;
                    }
                }
            }

            throw new AssertionException("no " + type + " packet was captured");
        }

        private static void Capture(HdlcSwitchPortBase port, List<byte[]> inbound, List<byte[]> outbound)
        {
            port.PacketCaptured += (id, isIn, packet) =>
            {
                List<byte[]> list = isIn ? inbound : outbound;
                lock (list)
                {
                    list.Add(packet);
                }
            };
        }

        private static X75Utilities Utilities(X25PacketInfo info)
        {
            Assert.That(info.ParseError, Is.Null);
            Assert.That(X75Utilities.TryDecode(info.NetworkUtilities, out X75Utilities u), Is.EqualTo(X75UtilityError.None));
            return u;
        }

        [Test]
        public void APadInNorway_ReachesATadInGermany_ThroughEngland()
        {
            // Germany: X.75 link from England on port 0 (listens), the TAD's subscriber line on port 1.
            HdlcTcpSwitchPort deFromUk = new HdlcTcpSwitchPort(NetworkLink(0, PortMode.Dce, 0), NullLogger.Instance);
            XotSwitchPort deTad = new XotSwitchPort(Subscriber(1));
            Build("2624", "2624589004", deFromUk, deTad);
            Assert.That(deFromUk.Start() && deTad.Start(), Is.True);

            // England: X.75 link from Norway on port 0 (listens), X.75 link to Germany on port 1 (dials).
            HdlcTcpSwitchPort ukFromNo = new HdlcTcpSwitchPort(NetworkLink(0, PortMode.Dce, 0), NullLogger.Instance);
            HdlcTcpSwitchPort ukToDe = new HdlcTcpSwitchPort(NetworkLink(1, PortMode.Dte, deFromUk.BoundPort), NullLogger.Instance);
            Build("2342", "2624", ukFromNo, ukToDe);
            Assert.That(ukFromNo.Start() && ukToDe.Start(), Is.True);

            // Norway: the PAD's subscriber line on port 0 (an NUI is required there), X.75 link to England on port 1.
            PortConfig padLine = Subscriber(0);
            padLine.NuiRequired = true;
            XotSwitchPort noPad = new XotSwitchPort(padLine);
            HdlcTcpSwitchPort noToUk = new HdlcTcpSwitchPort(NetworkLink(1, PortMode.Dte, ukFromNo.BoundPort), NullLogger.Instance);
            Country norway = Build("2422", "2624", noPad, noToUk, nuiAccount: Tromso);
            Assert.That(noPad.Start() && noToUk.Start(), Is.True);

            List<byte[]> noLinkIn = new List<byte[]>();
            List<byte[]> noLinkOut = new List<byte[]>();
            List<byte[]> deLinkIn = new List<byte[]>();
            List<byte[]> deLinkOut = new List<byte[]>();
            Capture(noToUk, noLinkIn, noLinkOut);
            Capture(deFromUk, deLinkIn, deLinkOut);

            Assert.That(SpinWait.SpinUntil(() => noToUk.PeerConnected && ukFromNo.PeerConnected && ukToDe.PeerConnected && deFromUk.PeerConnected, 10000),
                Is.True, "the two X.75 links did not come up");

            // The TAD connects its line in Germany; the PAD user connects in Norway.
            TcpClient tad = new TcpClient { NoDelay = true };
            _disposables.Add(tad);
            tad.Connect("127.0.0.1", deTad.BoundPort);
            TcpClient pad = new TcpClient { NoDelay = true };
            _disposables.Add(pad);
            pad.Connect("127.0.0.1", noPad.BoundPort);
            Assert.That(SpinWait.SpinUntil(() => noPad.PeerConnected && deTad.PeerConnected, 5000), Is.True);
            NetworkStream padStream = pad.GetStream();
            NetworkStream tadStream = tad.GetStream();
            padStream.ReadTimeout = 10000;
            tadStream.ReadTimeout = 10000;

            // The user types the X.28 selection with an NUI (X.28 3.5.15.1.1): N<nui>-<address>.
            CapturingTransport transport = new CapturingTransport();
            PadClient padClient = new PadClient(NullLogger.Instance, transport);
            string selection = "N" + Tromso + "-" + Altger + "\r";
            for (int i = 0; i < selection.Length; i++)
            {
                padClient.X28Protocol.ProcessCharacter(selection[i]);
            }

            Assert.That(transport.Called, Is.EqualTo(Altger));
            byte[] nuiElement = X25CallParameters.NuiSelectionElement(Encoding.ASCII.GetBytes(Tromso));
            Assert.That(transport.Facilities, Is.EqualTo(nuiElement));

            // The PAD's Call Request on channel 1 of its line: window 2/2 and packet 128/128 (42 07 07 43 02 02), then the NUI.
            byte[] facilities = new byte[6 + nuiElement.Length];
            new byte[] { 0x42, 0x07, 0x07, 0x43, 0x02, 0x02 }.CopyTo(facilities, 0);
            nuiElement.CopyTo(facilities, 6);
            SendXot(padStream, X25PacketBuilder.CreateCallRequest(1, Altger, "24220001", facilities, transport.CallUserData));

            // Germany: the TAD is offered the call, without the NUI (X.25 6.21).
            X25PacketInfo offered = ReadUntil(tadStream, X25PacketType.CallRequest);
            Assert.That(offered.CalledAddress, Is.EqualTo(Altger));
            Assert.That(offered.CallUserData, Is.EqualTo(transport.CallUserData));
            Assert.That(X25FacilityContains(offered.Facilities, 0xC6), Is.False, "the NUI must never reach the called DTE");

            // On the Norway - England link: Norway originated the call, so no transit network yet and a call identifier.
            X75Utilities leavingNorway = Utilities(Captured(noLinkOut, X25PacketType.CallRequest));
            Assert.That(leavingNorway.TransitNetworks, Is.Empty);
            Assert.That(leavingNorway.HasCallIdentifier, Is.True);

            // On the England - Germany link: England crossed as transit, the call identifier unchanged.
            X75Utilities enteringGermany = Utilities(Captured(deLinkIn, X25PacketType.CallRequest));
            Assert.That(enteringGermany.TransitNetworks, Is.EqualTo(new[] { "2342" }));
            Assert.That(enteringGermany.HasCallIdentifier, Is.True);
            Assert.That(enteringGermany.CallIdentifier, Is.EqualTo(leavingNorway.CallIdentifier));

            // The TAD answers; the PAD sees its call connected.
            SendXot(tadStream, X25PacketBuilder.CreateCallAccepted(offered.LCN));
            X25PacketInfo connected = ReadUntil(padStream, X25PacketType.CallAccepted);
            Assert.That(connected.LCN, Is.EqualTo((ushort)1));

            // Both Call Connected packets name England as transit (5.3.1), Germany's and the one England sent to Norway.
            Assert.That(Utilities(Captured(deLinkOut, X25PacketType.CallAccepted)).TransitNetworks, Is.EqualTo(new[] { "2342" }));
            Assert.That(Utilities(Captured(noLinkIn, X25PacketType.CallAccepted)).TransitNetworks, Is.EqualTo(new[] { "2342" }));

            // The call is billed to the University of Tromsø's account in Norway.
            SwitchAccount? uit = null;
            for (int i = 0; i < norway.Node.Accounts.Count; i++)
            {
                if (norway.Node.Accounts[i].Name == "uit")
                {
                    uit = norway.Node.Accounts[i];
                }
            }

            Assert.That(uit, Is.Not.Null);
            Assert.That(uit!.CallsActive, Is.EqualTo(1));

            // Data both ways.
            byte[] hello = Encoding.ASCII.GetBytes("HELLO ALTGER");
            SendXot(padStream, X25PacketBuilder.CreateDataPacket(1, 0, 0, hello));
            Assert.That(ReadUntil(tadStream, X25PacketType.Data).UserData, Is.EqualTo(hello));

            byte[] reply = Encoding.ASCII.GetBytes("WELCOME TO ALTGER");
            SendXot(tadStream, X25PacketBuilder.CreateDataPacket(offered.LCN, 0, 1, reply));
            Assert.That(ReadUntil(padStream, X25PacketType.Data).UserData, Is.EqualTo(reply));
        }

        /// <summary>
        /// England as two switches joined by an internal trunk: Norway - England 1 =internal= England 2 - Germany. The
        /// transit list must cross England's internal trunk (in the private facility FE after the marker, x25emu's
        /// coding), so Germany still sees [2342], England is still named exactly once, and the answer still names 2342.
        /// </summary>
        [Test]
        public void TheTransitList_CrossesAnInternalTrunkInsideEngland()
        {
            // Germany: X.75 from England 2 on port 0, the TAD on port 1.
            HdlcTcpSwitchPort deFromUk = new HdlcTcpSwitchPort(NetworkLink(0, PortMode.Dce, 0), NullLogger.Instance);
            XotSwitchPort deTad = new XotSwitchPort(Subscriber(1));
            Build("2624", "2624589004", deFromUk, deTad);
            Assert.That(deFromUk.Start() && deTad.Start(), Is.True);

            // England 2: internal trunk from England 1 on port 0 (listens), X.75 to Germany on port 1 (dials).
            PortConfig internalIn = NetworkLink(0, PortMode.Dce, 0);
            internalIn.BetweenNetworks = false;
            HdlcTcpSwitchPort uk2FromUk1 = new HdlcTcpSwitchPort(internalIn, NullLogger.Instance);
            HdlcTcpSwitchPort uk2ToDe = new HdlcTcpSwitchPort(NetworkLink(1, PortMode.Dte, deFromUk.BoundPort), NullLogger.Instance);
            Build("2342", "2624", uk2FromUk1, uk2ToDe);
            Assert.That(uk2FromUk1.Start() && uk2ToDe.Start(), Is.True);

            // England 1: X.75 from Norway on port 0 (listens), internal trunk to England 2 on port 1 (dials).
            PortConfig internalOut = NetworkLink(1, PortMode.Dte, uk2FromUk1.BoundPort);
            internalOut.BetweenNetworks = false;
            HdlcTcpSwitchPort uk1FromNo = new HdlcTcpSwitchPort(NetworkLink(0, PortMode.Dce, 0), NullLogger.Instance);
            HdlcTcpSwitchPort uk1ToUk2 = new HdlcTcpSwitchPort(internalOut, NullLogger.Instance);
            Build("2342", "2624", uk1FromNo, uk1ToUk2);
            Assert.That(uk1FromNo.Start() && uk1ToUk2.Start(), Is.True);

            // Norway: a subscriber line on port 0, X.75 to England 1 on port 1.
            XotSwitchPort noPad = new XotSwitchPort(Subscriber(0));
            HdlcTcpSwitchPort noToUk = new HdlcTcpSwitchPort(NetworkLink(1, PortMode.Dte, uk1FromNo.BoundPort), NullLogger.Instance);
            Build("2422", "2624", noPad, noToUk);
            Assert.That(noPad.Start() && noToUk.Start(), Is.True);

            List<byte[]> internalCapture = new List<byte[]>();
            List<byte[]> unused = new List<byte[]>();
            List<byte[]> deLinkIn = new List<byte[]>();
            List<byte[]> noLinkIn = new List<byte[]>();
            Capture(uk1ToUk2, unused, internalCapture);
            Capture(deFromUk, deLinkIn, unused);
            Capture(noToUk, noLinkIn, unused);

            Assert.That(SpinWait.SpinUntil(() => noToUk.PeerConnected && uk1FromNo.PeerConnected && uk1ToUk2.PeerConnected
                && uk2FromUk1.PeerConnected && uk2ToDe.PeerConnected && deFromUk.PeerConnected, 10000), Is.True, "the links did not come up");

            TcpClient tad = new TcpClient { NoDelay = true };
            _disposables.Add(tad);
            tad.Connect("127.0.0.1", deTad.BoundPort);
            TcpClient pad = new TcpClient { NoDelay = true };
            _disposables.Add(pad);
            pad.Connect("127.0.0.1", noPad.BoundPort);
            Assert.That(SpinWait.SpinUntil(() => noPad.PeerConnected && deTad.PeerConnected, 5000), Is.True);
            NetworkStream padStream = pad.GetStream();
            NetworkStream tadStream = tad.GetStream();
            padStream.ReadTimeout = 10000;
            tadStream.ReadTimeout = 10000;

            SendXot(padStream, X25PacketBuilder.CreateCallRequest(1, Altger, "24220001",
                new byte[] { 0x42, 0x07, 0x07, 0x43, 0x02, 0x02 }, new byte[] { 0x01, 0x00, 0x00, 0x00 }));

            // The TAD gets the call with no private facility in it: the carried field is the network's own (X.25 7.1).
            X25PacketInfo offered = ReadUntil(tadStream, X25PacketType.CallRequest);
            Assert.That(offered.Facilities, Is.EqualTo(new byte[] { 0x42, 0x07, 0x07, 0x43, 0x02, 0x02 }));

            // On England's internal trunk the field travelled after the marker as FE: 00 00 FE <length> <field>, and it
            // does not name England yet (England has not left its network).
            // An internal trunk is plain X.25: read without the X.75 utility field.
            X25PacketInfo internalCall = Captured(internalCapture, X25PacketType.CallRequest, x75: false);
            byte[] f = internalCall.Facilities ?? Array.Empty<byte>();
            int marker = Array.IndexOf(f, (byte)0xFE);
            Assert.That(marker, Is.GreaterThanOrEqualTo(2), "no carried field on the internal trunk: " + BitConverter.ToString(f));
            Assert.That(f[marker - 2], Is.EqualTo(0x00));
            Assert.That(f[marker - 1], Is.EqualTo(0x00));
            byte[] carried = new byte[f[marker + 1]];
            Array.Copy(f, marker + 2, carried, 0, carried.Length);
            Assert.That(X75Utilities.TryDecode(carried, out X75Utilities onTrunk), Is.EqualTo(X75UtilityError.None));
            Assert.That(onTrunk.TransitNetworks, Is.Empty);
            Assert.That(onTrunk.HasCallIdentifier, Is.True);

            // Into Germany: England named once, the call identifier Norway set.
            X75Utilities enteringGermany = Utilities(Captured(deLinkIn, X25PacketType.CallRequest));
            Assert.That(enteringGermany.TransitNetworks, Is.EqualTo(new[] { "2342" }));
            Assert.That(enteringGermany.CallIdentifier, Is.EqualTo(onTrunk.CallIdentifier));

            // The answer crosses the internal trunk back and still names England to Norway.
            SendXot(tadStream, X25PacketBuilder.CreateCallAccepted(offered.LCN));
            ReadUntil(padStream, X25PacketType.CallAccepted);
            Assert.That(Utilities(Captured(noLinkIn, X25PacketType.CallAccepted)).TransitNetworks, Is.EqualTo(new[] { "2342" }));
        }

        /// <summary>
        /// One PAD caller's XOT line: its stream, its channel and the sequence numbers it keeps (X.25 4.4.1, modulo 8).
        /// </summary>
        private sealed class Caller
        {
            public NetworkStream Stream = null!;
            public ushort Lci;
            public byte SendSeq;
            public byte RecvSeq;
            public readonly StringBuilder Text = new StringBuilder();
        }

        // Reads until the caller's screen holds the given text, acknowledging every data packet with an RR so the
        // server's window stays open (X.25 4.4.1.2), and skipping the Q-bit X.29 messages.
        private static void WaitForText(Caller c, string wanted)
        {
            long deadline = Environment.TickCount64 + 15000;
            while (c.Text.ToString().IndexOf(wanted, StringComparison.Ordinal) < 0)
            {
                Assert.That(Environment.TickCount64, Is.LessThan(deadline), "never saw \"" + wanted + "\"; screen so far:\n" + c.Text);
                byte[] header = ReadExactly(c.Stream, 4);
                X25PacketInfo info = X25PacketParser.ParsePacket(ReadExactly(c.Stream, (header[2] << 8) | header[3]));
                Assert.That(info.PacketType, Is.Not.EqualTo(X25PacketType.ClearRequest),
                    "cleared: cause " + info.ClearingCause + " diagnostic " + info.DiagnosticCode + "; screen so far:\n" + c.Text);
                if (info.PacketType != X25PacketType.Data)
                {
                    continue;
                }

                c.RecvSeq = (byte)((info.PS + 1) & 7);
                SendXot(c.Stream, X25PacketBuilder.CreateRR(c.Lci, c.RecvSeq));
                if (!info.QBit)
                {
                    c.Text.Append(Encoding.ASCII.GetString(info.UserData));
                }
            }
        }

        private static void Type(Caller c, string line)
        {
            SendXot(c.Stream, X25PacketBuilder.CreateDataPacket(c.Lci, c.SendSeq, c.RecvSeq, Encoding.ASCII.GetBytes(line)));
            c.SendSeq = (byte)((c.SendSeq + 1) & 7);
        }

        /// <summary>
        /// Two PAD users at the University of Tromsø dial Altos in Germany, 026245890040004 (0 is Norway's international
        /// prefix), through England, and meet in the Altos CHAT 3.1 conference: what one types, the other reads as
        /// "guest says: ...". The chat is the real one (RetroIO <c>ChatProtocolService</c>, the 1987 program's
        /// behaviour), running in a RetroIO runtime that sits behind Germany's subscriber port as its XOT client.
        /// </summary>
        [Test]
        public void TwoUsersInNorway_MeetInTheAltosChatInGermany()
        {
            // Germany: X.75 from England on port 0; the Altos host's line on port 1, enough channels for the chat's
            // XOT connections (one per call plus the spare it keeps dialled).
            HdlcTcpSwitchPort deFromUk = new HdlcTcpSwitchPort(NetworkLink(0, PortMode.Dce, 0), NullLogger.Instance);
            XotSwitchPort deAltos = new XotSwitchPort(Subscriber(1));
            Build("2624", "2624589004", deFromUk, deAltos);
            Assert.That(deFromUk.Start() && deAltos.Start(), Is.True);

            // The chat, as Altos Germany: 26245890040004, a RetroIO runtime dialling Germany's port 1.
            ProtocolRuntime altos = ChatProtocolServiceTests.StartChatRuntime(deAltos.BoundPort, maxUsers: 30, waitPauseMs: 0);
            _disposables.Add(altos);

            HdlcTcpSwitchPort ukFromNo = new HdlcTcpSwitchPort(NetworkLink(0, PortMode.Dce, 0), NullLogger.Instance);
            HdlcTcpSwitchPort ukToDe = new HdlcTcpSwitchPort(NetworkLink(1, PortMode.Dte, deFromUk.BoundPort), NullLogger.Instance);
            Build("2342", "2624", ukFromNo, ukToDe);
            Assert.That(ukFromNo.Start() && ukToDe.Start(), Is.True);

            // Norway: users dial with the international prefix 0 (X.121 7.8.1); the NUI is required.
            PortConfig padLine = Subscriber(0);
            padLine.NuiRequired = true;
            XotSwitchPort noPad = new XotSwitchPort(padLine);
            HdlcTcpSwitchPort noToUk = new HdlcTcpSwitchPort(NetworkLink(1, PortMode.Dte, ukFromNo.BoundPort), NullLogger.Instance);
            Build("2422", "2624", noPad, noToUk, nuiAccount: Tromso, internationalPrefix: "0");
            Assert.That(noPad.Start() && noToUk.Start(), Is.True);

            Assert.That(SpinWait.SpinUntil(() => noToUk.PeerConnected && ukFromNo.PeerConnected && ukToDe.PeerConnected
                && deFromUk.PeerConnected && deAltos.PeerConnected, 10000), Is.True, "the links or the chat's line did not come up");

            Caller anna = Dial("2422000101");
            Caller bjorn = Dial("2422000102");

            // Both get the 1987 welcome and are in the conference.
            WaitForText(anna, "Conference-utility \"CHAT\"  ver. 3.1");
            WaitForText(anna, "+++guest+++");
            WaitForText(bjorn, "+++guest+++");

            // What Anna types, Bjørn reads.
            Type(anna, "hello from Tromso\r");
            WaitForText(bjorn, "guest says: hello from Tromso");

            // And back.
            Type(bjorn, "hei Anna\r");
            WaitForText(anna, "guest says: hei Anna");

            // Anna leaves with .x; Bjørn sees her go.
            Type(anna, ".x\r");
            WaitForText(bjorn, "---guest---");

            Caller Dial(string calling)
            {
                TcpClient tcp = new TcpClient { NoDelay = true };
                _disposables.Add(tcp);
                tcp.Connect("127.0.0.1", noPad.BoundPort);
                NetworkStream stream = tcp.GetStream();
                stream.ReadTimeout = 15000;

                // A PAD call (CUD 01 00 00 00, X.29) with the University of Tromsø's NUI.
                byte[] nui = X25CallParameters.NuiSelectionElement(Encoding.ASCII.GetBytes(Tromso));
                byte[] facilities = new byte[6 + nui.Length];
                new byte[] { 0x42, 0x07, 0x07, 0x43, 0x02, 0x02 }.CopyTo(facilities, 0);
                nui.CopyTo(facilities, 6);
                SendXot(stream, X25PacketBuilder.CreateCallRequest(1, "0" + Altger, calling, facilities, new byte[] { 0x01, 0x00, 0x00, 0x00 }));

                X25PacketInfo connected = ReadUntil(stream, X25PacketType.CallAccepted);
                return new Caller { Stream = stream, Lci = connected.LCN };
            }
        }

        // True when an X.25 facility field holds an element with the given code before the first marker (X.25 7.1).
        private static bool X25FacilityContains(byte[]? field, byte code)
        {
            X25CallParameters p = new X25CallParameters(2, 2, 128, 128, field);
            return p.TryGetFacility(code, out _);
        }
    }
}
