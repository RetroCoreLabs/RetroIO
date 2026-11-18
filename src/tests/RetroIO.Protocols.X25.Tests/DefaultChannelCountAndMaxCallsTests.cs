//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using RetroIO.Protocols.X25.Common;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.PacketLayer;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;
using RetroIO.Protocols.X25.Transport;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// A line whose configuration does not say how many logical channels it has gets every channel
    /// X.25 allows, 1..4095, and the switch-wide limit on calls in progress (max_calls).
    /// </summary>
    /// <remarks>
    /// X.25 (10/96) 3.1: a logical channel group number is at most 15 and a logical channel number at
    /// most 255, so the channel number is twelve bits; channel 0 is kept for restart and diagnostic
    /// packets (5.1.3.1). The number of channels in use is agreed at subscription (3.1, Annex A).
    /// Decided 2026-11-18: with nothing agreed in the configuration, a line gets all of them. Until
    /// then the default was 16 channels, and on an XOT line also 16 TCP connections.
    /// </remarks>
    [TestFixture]
    public class DefaultChannelCountAndMaxCallsTests
    {
        private const string Called = "23420001";
        private const string Calling = "23400001";

        // ------------------------------------------------------------------ the packet layer

        [Test]
        public void X25Config_WithNoChannelCount_HasChannels1To4095()
        {
            X25Config config = new X25Config();
            Assert.That(config.LciBase, Is.EqualTo((ushort)1));
            Assert.That(config.LciCount, Is.EqualTo((ushort)4095));
            Assert.DoesNotThrow(config.Validate);
        }

        [Test]
        public void X25Config_WithABaseAndNoCount_RunsFromTheBaseTo4095()
        {
            X25Config config = new X25Config { LciBase = 100 };
            Assert.That(config.LciCount, Is.EqualTo((ushort)3996), "100..4095 is 3996 channels");
            Assert.DoesNotThrow(config.Validate);
        }

        [Test]
        public void X25Config_4096Channels_IsRefused()
        {
            X25Config config = new X25Config { LciBase = 1, LciCount = 4096 };
            Assert.Throws<ArgumentOutOfRangeException>(config.Validate);
        }

        [Test]
        public void X25Layer_WithNoChannelCount_TakesACallOnChannel4095()
        {
            RecordingSink sink = new RecordingSink();
            X25Layer dce = new X25Layer(sink, new X25Config { LocalAddress = Called, IsDte = false });
            ushort seen = 0;
            dce.IncomingCall += (l, lci, called, calling, cud) => seen = lci;

            dce.ProcessPacket(X25PacketBuilder.CreateCallRequest(4095, Called, Calling));

            Assert.That(seen, Is.EqualTo((ushort)4095), "channel 4095 is inside the default range");
            Assert.That(dce.Circuits[4095].State, Is.EqualTo(X25VcState.CallIncoming));
        }

        private sealed class RecordingSink : IX25PacketSink
        {
            public readonly List<byte[]> Sent = new List<byte[]>();

            public X25SendResult SendPacket(ReadOnlySpan<byte> packet)
            {
                Sent.Add(packet.ToArray());
                return X25SendResult.Sent;
            }
        }

        // ------------------------------------------------------------------ the config classes

        [Test]
        public void PortConfig_XotInterfaceConfig_AndPeerConfig_DefaultTo4095Channels()
        {
            Assert.That(new PortConfig().LciCount, Is.EqualTo((ushort)4095));
            Assert.That(new PortConfig { Type = PortType.Trunk }.LciCount, Is.EqualTo((ushort)4095), "a trunk gets the full range too");
            Assert.That(new XotInterfaceConfig().LciCount, Is.EqualTo((ushort)4095));
            Assert.That(new XotPeerConfig().LciCount, Is.EqualTo((ushort)4095));
            Assert.That(new XotPeerConfig().MaxConnections, Is.EqualTo(4095), "one connection slot per channel");
            Assert.That(new XotServerConfig().MaxConnections, Is.EqualTo(4095), "one connection slot per channel");
        }

        [Test]
        public void XotInterfaceConfig_Json_KeepsNotSetAsNotSet_AndReadsASetCount()
        {
            string unset = System.Text.Json.JsonSerializer.Serialize(new XotInterfaceConfig { Name = "a" });
            Assert.That(unset, Does.Not.Contain("LciCount"), "a default is not written as a fixed number");
            XotInterfaceConfig back = System.Text.Json.JsonSerializer.Deserialize<XotInterfaceConfig>(unset)!;
            Assert.That(back.LciCountIsSet, Is.False);
            Assert.That(back.LciCount, Is.EqualTo((ushort)4095));

            XotInterfaceConfig set = System.Text.Json.JsonSerializer.Deserialize<XotInterfaceConfig>("{\"Name\":\"b\",\"LciCount\":128}")!;
            Assert.That(set.LciCountIsSet, Is.True);
            Assert.That(set.LciCount, Is.EqualTo((ushort)128));
            Assert.That(System.Text.Json.JsonSerializer.Serialize(set), Does.Contain("\"LciCount\":128"));
        }

        [Test]
        public void IniSetValue_ChangesAddsAndRemovesOneKey_AndKeepsEverythingElse()
        {
            List<string> lines = new List<string> { "# switch", "[switch]", "dnic = 2340", "", "[port.0]", "# a comment", "type = access" };

            IniConfig.SetValue(lines, "switch", "max_calls", "5");
            Assert.That(lines, Is.EqualTo(new[] { "# switch", "[switch]", "dnic = 2340", "max_calls = 5", "", "[port.0]", "# a comment", "type = access" }));

            IniConfig.SetValue(lines, "switch", "max_calls", "7");
            Assert.That(IniConfig.Parse(lines).Get("switch", "max_calls"), Is.EqualTo("7"));

            IniConfig.SetValue(lines, "port.0", "lci_count", "64");
            Assert.That(lines[lines.Count - 1], Is.EqualTo("lci_count = 64"));

            IniConfig.SetValue(lines, "port.0", "lci_count", null);
            IniConfig.SetValue(lines, "switch", "max_calls", null);
            Assert.That(lines, Is.EqualTo(new[] { "# switch", "[switch]", "dnic = 2340", "", "[port.0]", "# a comment", "type = access" }));
        }

        // ------------------------------------------------------------------ the switch INI loader

        private static SwitchConfig LoadPort(params string[] portLines)
        {
            List<string> lines = new List<string> { "[switch]", "dnic = 2340", "[port.0]" };
            lines.AddRange(portLines);
            return SwitchConfigLoader.Load(IniConfig.Parse(lines));
        }

        [Test]
        public void Loader_AccessAndTrunkPortWithoutLciCount_Get4095Channels()
        {
            SwitchConfig access = LoadPort("type = access", "transport = xot", "mode = dce", "port = 0");
            Assert.That(access.Ports[0].LciBase, Is.EqualTo((ushort)1));
            Assert.That(access.Ports[0].LciCount, Is.EqualTo((ushort)4095));

            SwitchConfig trunk = LoadPort("type = trunk", "transport = xot", "mode = dte", "host = 127.0.0.1", "port = 1998");
            Assert.That(trunk.Ports[0].LciCount, Is.EqualTo((ushort)4095), "trunks carry lots of traffic: the full range");
        }

        [Test]
        public void Loader_ABaseWithoutACount_RunsToChannel4095()
        {
            SwitchConfig sw = LoadPort("type = access", "lci_base = 16");
            Assert.That(sw.Ports[0].LciCount, Is.EqualTo((ushort)4080), "16..4095");
        }

        [Test]
        public void Loader_4096Channels_IsRefusedWithAClearError()
        {
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => LoadPort("type = access", "lci_count = 4096"))!;
            Assert.That(ex.Message, Does.Contain("4095"));
            Assert.That(ex.Message, Does.Contain("lci_count"));
        }

        [Test]
        public void Loader_ABasePlusCountPast4095_IsRefused()
        {
            Assert.Throws<InvalidDataException>(() => LoadPort("type = access", "lci_base = 10", "lci_count = 4090"));
            Assert.Throws<InvalidDataException>(() => LoadPort("type = access", "lci_count = 0"));
        }

        [Test]
        public void Loader_MaxCalls_AbsentOrZeroIsNoLimit_AndANumberIsRead()
        {
            Assert.That(SwitchConfigLoader.Load(IniConfig.Parse(new[] { "[switch]", "dnic = 2340" })).MaxCalls, Is.EqualTo(0));
            Assert.That(SwitchConfigLoader.Load(IniConfig.Parse(new[] { "[switch]", "dnic = 2340", "max_calls = 0" })).MaxCalls, Is.EqualTo(0));
            Assert.That(SwitchConfigLoader.Load(IniConfig.Parse(new[] { "[switch]", "dnic = 2340", "max_calls = 2" })).MaxCalls, Is.EqualTo(2));
            Assert.Throws<InvalidDataException>(() => SwitchConfigLoader.Load(IniConfig.Parse(new[] { "[switch]", "dnic = 2340", "max_calls = -1" })));
        }

        // ------------------------------------------------------------------ a real XOT port

        [TestCase(PortType.Access)]
        [TestCase(PortType.Trunk)]
        public void XotPortWithNoChannelSetting_Serves200CallsAtOnce(PortType type)
        {
            const int Calls = 200;
            using XotSwitchPort port = new XotSwitchPort(new PortConfig { Id = 0, Type = type, Mode = PortMode.Dce, Port = 0 });
            Assert.That(port.Start(), Is.True);
            port.IncomingCall += (p, lci, called, calling, cud) => p.AcceptCall(lci);

            using TcpClient raw = new TcpClient { NoDelay = true };
            raw.Connect(IPAddress.Loopback, port.BoundPort);
            NetworkStream stream = raw.GetStream();

            // One connection asks for 200 circuits on channels 3896..4095, so 4095 itself is among them.
            for (int i = 0; i < Calls; i++)
            {
                WriteXot(stream, X25PacketBuilder.CreateCallRequest((ushort)(4095 - i), Called, Calling));
            }

            int accepted = 0;
            int cleared = 0;
            bool saw4095 = false;
            long deadline = Environment.TickCount64 + 10000;
            stream.ReadTimeout = 200;
            byte[] header = new byte[4];
            while (Environment.TickCount64 < deadline && accepted + cleared < Calls)
            {
                try
                {
                    ReadExactly(stream, header);
                    byte[] body = new byte[(header[2] << 8) | header[3]];
                    ReadExactly(stream, body);
                    if (body[2] == 0x0F)
                    {
                        accepted++;
                        saw4095 |= ((body[0] & 0x0F) << 8 | body[1]) == 4095;
                    }
                    else if (body[2] == 0x13)
                    {
                        cleared++;
                    }
                }
                catch (IOException)
                {
                    // read timeout: go round until the deadline
                }
            }

            Assert.That(cleared, Is.EqualTo(0), "no call may be refused on a line with the default channel count");
            Assert.That(accepted, Is.EqualTo(Calls));
            Assert.That(saw4095, Is.True, "the call on channel 4095 is answered");
        }

        private static void WriteXot(NetworkStream stream, byte[] packet)
        {
            byte[] frame = new byte[4 + packet.Length];
            frame[2] = (byte)(packet.Length >> 8);
            frame[3] = (byte)packet.Length;
            Array.Copy(packet, 0, frame, 4, packet.Length);
            stream.Write(frame, 0, frame.Length);
        }

        private static void ReadExactly(NetworkStream stream, byte[] into)
        {
            int got = 0;
            while (got < into.Length)
            {
                int n = stream.Read(into, got, into.Length - got);
                if (n <= 0)
                {
                    throw new EndOfStreamException();
                }

                got += n;
            }
        }

        // ------------------------------------------------------------------ max_calls

        private sealed class Rig
        {
            public SwitchNode Node = null!;
            public SwitchCore Core = null!;
            public LoopbackSwitchPort In = null!;
            public LoopbackSwitchPort Out = null!;
        }

        private static Rig NewRig(int maxCalls)
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340", MaxCalls = maxCalls };
            cfg.Routing.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = "2341", PrefixLen = 4, DestPort = 1 });
            Rig r = new Rig { Node = new SwitchNode(cfg) };
            r.In = new LoopbackSwitchPort(new PortConfig { Id = 0, LciBase = 1 });
            r.Out = new LoopbackSwitchPort(new PortConfig { Id = 1, LciBase = 1 });
            r.Node.AddPort(r.In);
            r.Node.AddPort(r.Out);
            r.In.Start();
            r.Out.Start();
            r.Core = new SwitchCore(r.Node);
            return r;
        }

        [Test]
        public void MaxCalls2_TheThirdCallIsClearedWithNetworkCongestion_AndAfterOneEndsANewOneGetsThrough()
        {
            Rig r = NewRig(2);

            ushort first = r.In.InjectIncomingCall("23410001", Calling);
            r.In.InjectIncomingCall("23410002", Calling);
            Assert.That(r.Out.PlacedCalls.Count, Is.EqualTo(2), "two calls are under the limit");
            Assert.That(r.Node.ActiveStitchCount, Is.EqualTo(2), "each call counted once, though it uses two ports");

            ushort third = r.In.InjectIncomingCall("23410003", Calling);
            Assert.That(r.Out.PlacedCalls.Count, Is.EqualTo(2), "the third call is never offered onward");
            Assert.That(r.In.ClearedCalls.Count, Is.EqualTo(1));
            Assert.That(r.In.ClearedCalls[0].Lci, Is.EqualTo(third));
            Assert.That(r.In.ClearedCalls[0].Cause, Is.EqualTo(SwitchCore.CauseNetworkCongestion), "X.25 Table 5-7: 0x05 network congestion");
            Assert.That(r.In.ClearedCalls[0].Diag, Is.EqualTo((byte)0), "list after Table C.3, row 36: # 0");

            // One call ends; the next one gets through.
            r.In.InjectClear(first, SwitchCore.CauseDteOriginated, 0);
            Assert.That(r.Node.ActiveStitchCount, Is.EqualTo(1));
            r.In.InjectIncomingCall("23410004", Calling);
            Assert.That(r.Out.PlacedCalls.Count, Is.EqualTo(3));
            Assert.That(r.Node.ActiveStitchCount, Is.EqualTo(2));
        }

        [Test]
        public void MaxCalls0_IsNoLimit()
        {
            Rig r = NewRig(0);
            for (int i = 0; i < 300; i++)
            {
                r.In.InjectIncomingCall("2341" + i.ToString("0000"), Calling);
            }

            Assert.That(r.Out.PlacedCalls.Count, Is.EqualTo(300));
            Assert.That(r.In.ClearedCalls.Count, Is.EqualTo(0));
        }

        [Test]
        public void SwitchConfig_MaxCallsDefault_IsZeroForNoLimit()
        {
            Assert.That(new SwitchConfig().MaxCalls, Is.EqualTo(0));
        }
    }
}
