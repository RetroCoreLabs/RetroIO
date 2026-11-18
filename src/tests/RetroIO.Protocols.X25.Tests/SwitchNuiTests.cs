//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using RetroIO.Protocols.X25.Common;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Network user identification at the switch (X.25 6.21, 7.3.7, Annex C; PLAN B1a): the NUI names the account a call
    /// is billed to, an unknown one clears the call, a port can require one, and a port can name a default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One switch, two XOT access ports: the caller on port 0, the callee on port 1, route 2342 to port 1. Accounts:
    /// "lineA" covers the caller's address 23400001 by range, "card7" is reached by NUI "ACCT-7" only. A call naming
    /// ACCT-7 must be billed to card7, not to lineA, "whatever line it dialled in from" - the x25emu rule, and the reading
    /// of 6.21.1 ("information to the network for billing ... on a per call basis").
    /// </para>
    /// <para>
    /// Annex C, Table C.1 (rows 15 and 16): an invalid network user identifier is cleared with "Invalid facility request"
    /// (0x03), diagnostic 84 "NUI problem"; an NUI selection the DCE expects and does not get with "Local procedure error"
    /// (0x13), diagnostic 84.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class SwitchNuiTests
    {
        private const byte InvalidFacilityRequest = 0x03;
        private const byte LocalProcedureError = 0x13;
        private const byte NuiProblem = 84;

        private sealed class OneSwitch : IDisposable
        {
            public SwitchNode Node = null!;
            public SwitchCore Core = null!;
            public XotSwitchPort CallerPort = null!;
            public XotSwitchPort CalleePort = null!;

            // Every record line the switch wrote.
            public readonly System.Collections.Generic.List<Cdr> Records = new System.Collections.Generic.List<Cdr>();

            // The calling address in the last Call Request the callee received.
            public string CalleeSawCalling = string.Empty;

            public void Dispose()
            {
                CallerPort?.Stop();
                CalleePort?.Stop();
            }
        }

        private static OneSwitch Build(bool nuiRequired = false, string defaultNui = "", string callingBase = "")
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340" };
            cfg.Routing.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = "2342", PrefixLen = 4, DestPort = 1 });
            cfg.Accounts.Add(new AccountConfig { Name = "lineA", AddrStart = "23400000", AddrEnd = "23400099" });
            cfg.Accounts.Add(new AccountConfig { Name = "card7", Nui = "ACCT-7" });

            OneSwitch s = new OneSwitch();
            s.Node = new SwitchNode(cfg);
            s.CallerPort = new XotSwitchPort(new PortConfig
            {
                Id = 0, Mode = PortMode.Dce, Host = "127.0.0.1", Port = 0, LciBase = 1, LciCount = 16,
                NuiRequired = nuiRequired, DefaultNui = defaultNui, CallingAddressBase = callingBase,
            });
            s.CalleePort = new XotSwitchPort(new PortConfig { Id = 1, Mode = PortMode.Dce, Host = "127.0.0.1", Port = 0, LciBase = 1, LciCount = 16 });
            s.Node.AddPort(s.CallerPort);
            s.Node.AddPort(s.CalleePort);
            s.Core = new SwitchCore(s.Node);
            s.Core.CdrRecorded += cdr => { lock (s.Records) { s.Records.Add(cdr); } };
            Assert.That(s.CallerPort.Start() && s.CalleePort.Start(), Is.True);
            return s;
        }

        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace('-', ' ');

        private static void SendXot(NetworkStream stream, byte[] packet)
        {
            byte[] frame = new byte[4 + packet.Length];
            frame[2] = (byte)(packet.Length >> 8);
            frame[3] = (byte)packet.Length;
            packet.CopyTo(frame, 4);
            stream.Write(frame, 0, frame.Length);
        }

        private static byte[] ReadXot(NetworkStream stream)
        {
            byte[] header = ReadExactly(stream, 4);
            return ReadExactly(stream, (header[2] << 8) | header[3]);
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

        // Window 2 and packet size 128 both ways, then (when given) NUI selection: C6, length, the identifier.
        private static byte[] Facilities(string? nui)
        {
            byte[] flow = { 0x42, 0x07, 0x07, 0x43, 0x02, 0x02 };
            if (nui == null)
            {
                return flow;
            }

            byte[] id = Encoding.ASCII.GetBytes(nui);
            byte[] f = new byte[flow.Length + 2 + id.Length];
            flow.CopyTo(f, 0);
            f[flow.Length] = 0xC6;
            f[flow.Length + 1] = (byte)id.Length;
            id.CopyTo(f, flow.Length + 2);
            return f;
        }

        private static SwitchAccount Account(OneSwitch s, string name)
        {
            for (int i = 0; i < s.Node.Accounts.Count; i++)
            {
                if (s.Node.Accounts[i].Name == name)
                {
                    return s.Node.Accounts[i];
                }
            }

            throw new InvalidOperationException("no account " + name);
        }

        // Places a call from the caller and answers it at the callee; returns the packet the caller got back.
        private static X25PacketInfo PlaceAndAnswer(OneSwitch s, string? nui, out bool calleeSawIt)
        {
            using TcpClient callee = new TcpClient { NoDelay = true };
            callee.Connect("127.0.0.1", s.CalleePort.BoundPort);
            using TcpClient caller = new TcpClient { NoDelay = true };
            caller.Connect("127.0.0.1", s.CallerPort.BoundPort);
            Assert.That(SpinWait.SpinUntil(() => s.CallerPort.PeerConnected && s.CalleePort.PeerConnected, 5000), Is.True);
            NetworkStream callerStream = caller.GetStream();
            NetworkStream calleeStream = callee.GetStream();
            callerStream.ReadTimeout = 10000;
            calleeStream.ReadTimeout = 2000;

            SendXot(callerStream, X25PacketBuilder.CreateCallRequest(1, "23420001", "23400001", Facilities(nui)));

            calleeSawIt = false;
            try
            {
                X25PacketInfo atCallee = X25PacketParser.ParsePacket(ReadXot(calleeStream));
                calleeSawIt = atCallee.PacketType == X25PacketType.CallRequest;
                if (calleeSawIt)
                {
                    s.CalleeSawCalling = atCallee.CallingAddress;
                    SendXot(calleeStream, X25PacketBuilder.CreateCallAccepted(atCallee.LCN, Facilities(null)));
                }
            }
            catch (IOException)
            {
                // Nothing reached the callee within the timeout: the switch refused the call.
            }

            return X25PacketParser.ParsePacket(ReadXot(callerStream));
        }

        /// <summary>
        /// A call that names a known NUI goes through and is billed to that NUI's account, not to the account its line
        /// belongs to.
        /// </summary>
        [Test]
        public void KnownNui_CallGoesThrough_BilledToTheNuiAccount()
        {
            using OneSwitch s = Build();

            X25PacketInfo back = PlaceAndAnswer(s, "ACCT-7", out bool calleeSawIt);

            Assert.That(calleeSawIt, Is.True);
            Assert.That(back.PacketType, Is.EqualTo(X25PacketType.CallAccepted));
            Assert.That(SpinWait.SpinUntil(() => Account(s, "card7").CallsMade == 1, 5000), Is.True, "billed to the NUI's account");
            Assert.That(Account(s, "lineA").CallsMade + Account(s, "lineA").CallsActive, Is.EqualTo(0u), "not to the line's account");

            // The connections are closed once PlaceAndAnswer returns, which ends the call. Its one record line names
            // the account and the NUI, and the account's totals are that line's figures - the read-out and the record
            // file must never disagree.
            Assert.That(SpinWait.SpinUntil(() => { lock (s.Records) { return s.Records.Count == 1; } }, 5000), Is.True, "one record line");
            Cdr line;
            lock (s.Records)
            {
                line = s.Records[0];
            }

            SwitchAccount card = Account(s, "card7");
            Assert.That(line.Account, Is.EqualTo("card7"));
            Assert.That(line.Nui, Is.EqualTo("ACCT-7"));
            Assert.That(SpinWait.SpinUntil(() => card.CallsActive == 0, 5000), Is.True, "the call is no longer up");
            Assert.That(card.PacketsSent, Is.EqualTo((ulong)line.PacketsForward));
            Assert.That(card.PacketsReceived, Is.EqualTo((ulong)line.PacketsReverse));
            Assert.That(card.BytesSent, Is.EqualTo(line.BytesForward));
            Assert.That(card.BytesReceived, Is.EqualTo(line.BytesReverse));
            Assert.That(card.SegmentsSent, Is.EqualTo(line.SegmentsForward));
            Assert.That(card.SegmentsReceived, Is.EqualTo(line.SegmentsReverse));
            Assert.That(card.TotalDurationMs, Is.EqualTo((ulong)Math.Max(0, line.EndTimeMs - line.StartTimeMs)));
        }

        /// <summary>
        /// A call that names an NUI no account has is cleared back to the caller with Annex C row 15, and never reaches
        /// the callee.
        /// </summary>
        [Test]
        public void UnknownNui_IsCleared_InvalidFacilityRequest_Diagnostic84()
        {
            using OneSwitch s = Build();

            X25PacketInfo back = PlaceAndAnswer(s, "NOSUCH", out bool calleeSawIt);

            Assert.That(calleeSawIt, Is.False, "the call does not go on");
            Assert.That(back.PacketType, Is.EqualTo(X25PacketType.ClearRequest));
            Assert.That(back.ClearingCause, Is.EqualTo(InvalidFacilityRequest));
            Assert.That(back.DiagnosticCode, Is.EqualTo(NuiProblem));
            Assert.That(Account(s, "lineA").CallsMade + Account(s, "lineA").CallsActive + Account(s, "card7").CallsMade + Account(s, "card7").CallsActive, Is.EqualTo(0u), "nothing is billed");
            NoRecordIsWritten(s);
        }

        // A refused call is cleared before it is routed, so it has no record line - not even one of zero length - since
        // nothing was carried that anybody could be charged for. Given time to appear, so a late one would be caught.
        private static void NoRecordIsWritten(OneSwitch s)
        {
            Thread.Sleep(300);
            lock (s.Records)
            {
                Assert.That(s.Records.Count, Is.EqualTo(0), "a refused call wrote a record line");
            }
        }

        /// <summary>
        /// A call the called DTE refuses (it answers the Call Request with a Clear Request) never connected, so nothing is
        /// charged: ITU-T D.11 charges the connected time, and no data can cross before Call Accepted. A record line may
        /// still be written - it is the trace that the call was tried - but it must show it never connected (connect time
        /// 0) and carry nothing; and the account shows no call up and no packets or segments. The attempt does count in
        /// the account's "calls made" (no standard says either way; this matches x25emu, measured).
        /// </summary>
        [Test]
        public void ACallClearedDuringSetUp_IsChargedNothing()
        {
            using OneSwitch s = Build();
            using TcpClient callee = new TcpClient { NoDelay = true };
            callee.Connect("127.0.0.1", s.CalleePort.BoundPort);
            using TcpClient caller = new TcpClient { NoDelay = true };
            caller.Connect("127.0.0.1", s.CallerPort.BoundPort);
            Assert.That(SpinWait.SpinUntil(() => s.CallerPort.PeerConnected && s.CalleePort.PeerConnected, 5000), Is.True);
            NetworkStream callerStream = caller.GetStream();
            NetworkStream calleeStream = callee.GetStream();
            callerStream.ReadTimeout = 10000;
            calleeStream.ReadTimeout = 10000;

            SendXot(callerStream, X25PacketBuilder.CreateCallRequest(1, "23420001", "23400001", Facilities(null)));
            X25PacketInfo atCallee = X25PacketParser.ParsePacket(ReadXot(calleeStream));
            Assert.That(atCallee.PacketType, Is.EqualTo(X25PacketType.CallRequest));
            SendXot(calleeStream, X25PacketBuilder.CreateClearRequest(atCallee.LCN, 0x00, 0x00));
            X25PacketInfo back = X25PacketParser.ParsePacket(ReadXot(callerStream));
            Assert.That(back.PacketType, Is.EqualTo(X25PacketType.ClearRequest), "the caller is told the call was cleared");

            Thread.Sleep(300);
            SwitchAccount line = Account(s, "lineA");
            TestContext.Progress.WriteLine("lineA: made=" + line.CallsMade + " active=" + line.CallsActive + " pkts=" + line.PacketsSent
                + " segs=" + line.SegmentsSent + " ms=" + line.TotalDurationMs);
            Assert.That(line.CallsActive, Is.EqualTo(0u));
            Assert.That(line.PacketsSent + line.PacketsReceived + line.SegmentsSent + line.SegmentsReceived, Is.EqualTo(0ul));
            // The same as x25emu (measured in its call-through-switch test, 2026-11-17): the attempt has one record line,
            // never connected, and it counts in "calls made" - it was a call, routed and refused, just not a charged one.
            Assert.That(line.CallsMade, Is.EqualTo(1u), "the refused attempt counts as a call made, as in x25emu");
            lock (s.Records)
            {
                Assert.That(s.Records.Count, Is.EqualTo(1), "one line, the trace of the attempt, as in x25emu");
                for (int i = 0; i < s.Records.Count; i++)
                {
                    Cdr c = s.Records[i];
                    TestContext.Progress.WriteLine("record: " + CdrFileWriter.Format(c));
                    Assert.That(c.ConnectedTimeMs, Is.EqualTo(0L), "it never connected");
                    Assert.That(c.SegmentsForward + c.SegmentsReverse, Is.EqualTo(0ul));
                    Assert.That(c.PacketsForward + c.PacketsReverse, Is.EqualTo(0u));
                }
            }
        }

        /// <summary>
        /// The called DTE names a NUI in its Call Accepted (X.25 6.21.1, 6.21.3). One no account has is invalid, and the
        /// DCE "will clear the call": Annex E, b) Call accepted packet, row 13 - Invalid facility request, diagnostic 84.
        /// Both ends are told, the call never connected, and nothing is charged. A known one lets the call through.
        /// </summary>
        [TestCase("NOSUCH", false)]
        [TestCase("ACCT-7", true)]
        public void ANuiInTheCallAccepted_IsChecked(string calleeNui, bool known)
        {
            using OneSwitch s = Build();
            using TcpClient callee = new TcpClient { NoDelay = true };
            callee.Connect("127.0.0.1", s.CalleePort.BoundPort);
            using TcpClient caller = new TcpClient { NoDelay = true };
            caller.Connect("127.0.0.1", s.CallerPort.BoundPort);
            Assert.That(SpinWait.SpinUntil(() => s.CallerPort.PeerConnected && s.CalleePort.PeerConnected, 5000), Is.True);
            NetworkStream callerStream = caller.GetStream();
            NetworkStream calleeStream = callee.GetStream();
            callerStream.ReadTimeout = 10000;
            calleeStream.ReadTimeout = 10000;

            SendXot(callerStream, X25PacketBuilder.CreateCallRequest(1, "23420001", "23400001", Facilities(null)));
            X25PacketInfo atCallee = X25PacketParser.ParsePacket(ReadXot(calleeStream));
            Assert.That(atCallee.PacketType, Is.EqualTo(X25PacketType.CallRequest));
            SendXot(calleeStream, X25PacketBuilder.CreateCallAccepted(atCallee.LCN, Facilities(calleeNui)));
            X25PacketInfo back = X25PacketParser.ParsePacket(ReadXot(callerStream));

            if (known)
            {
                Assert.That(back.PacketType, Is.EqualTo(X25PacketType.CallAccepted));
                Assert.That(Hex(back.Facilities), Does.Not.Contain("C6"), "the NUI never reaches the caller (6.21)");

                // It names the called DTE's account (6.21.1, "for billing"): the call is up on card7's side, and when it
                // ends card7 has received one call.
                Assert.That(SpinWait.SpinUntil(() => Account(s, "card7").CallsActive == 1, 5000), Is.True, "the call is up on the NUI's account");
                SendXot(callerStream, X25PacketBuilder.CreateClearRequest(1, 0x00, 0x00));
                Assert.That(SpinWait.SpinUntil(() => Account(s, "card7").CallsReceived == 1 && Account(s, "card7").CallsActive == 0, 5000), Is.True,
                    "card7 received the call: active=" + Account(s, "card7").CallsActive + " received=" + Account(s, "card7").CallsReceived);
                return;
            }

            Assert.That(back.PacketType, Is.EqualTo(X25PacketType.ClearRequest), "the caller is told");
            Assert.That(back.ClearingCause, Is.EqualTo(InvalidFacilityRequest));
            Assert.That(back.DiagnosticCode, Is.EqualTo(NuiProblem));
            X25PacketInfo toCallee = X25PacketParser.ParsePacket(ReadXot(calleeStream));
            Assert.That(toCallee.PacketType, Is.EqualTo(X25PacketType.ClearRequest), "the called DTE is cleared too");
            Assert.That(toCallee.ClearingCause, Is.EqualTo(InvalidFacilityRequest));
            Assert.That(toCallee.DiagnosticCode, Is.EqualTo(NuiProblem));

            Assert.That(SpinWait.SpinUntil(() => { lock (s.Records) { return s.Records.Count == 1; } }, 5000), Is.True);
            lock (s.Records)
            {
                Assert.That(s.Records[0].ConnectedTimeMs, Is.EqualTo(0L), "it never connected, so nothing is charged");
            }
        }

        /// <summary>
        /// A call without an NUI on a port that requires one is cleared with Annex C row 16.
        /// </summary>
        [Test]
        public void MissingNui_OnAPortThatRequiresOne_IsCleared_LocalProcedureError_Diagnostic84()
        {
            using OneSwitch s = Build(nuiRequired: true);

            X25PacketInfo back = PlaceAndAnswer(s, null, out bool calleeSawIt);

            Assert.That(calleeSawIt, Is.False);
            Assert.That(back.PacketType, Is.EqualTo(X25PacketType.ClearRequest));
            Assert.That(back.ClearingCause, Is.EqualTo(LocalProcedureError));
            Assert.That(back.DiagnosticCode, Is.EqualTo(NuiProblem));
            NoRecordIsWritten(s);
        }

        /// <summary>
        /// On a port that requires an NUI, a call that gives a known one goes through.
        /// </summary>
        [Test]
        public void KnownNui_OnAPortThatRequiresOne_GoesThrough()
        {
            using OneSwitch s = Build(nuiRequired: true);

            X25PacketInfo back = PlaceAndAnswer(s, "ACCT-7", out bool calleeSawIt);

            Assert.That(calleeSawIt, Is.True);
            Assert.That(back.PacketType, Is.EqualTo(X25PacketType.CallAccepted));
        }

        /// <summary>
        /// A port's default NUI bills a call that carries none to the account it names (the port's subscription, 6.21),
        /// and satisfies a port that requires an NUI.
        /// </summary>
        [Test]
        public void DefaultNui_BillsACallWithoutOne_ToItsAccount()
        {
            using OneSwitch s = Build(nuiRequired: true, defaultNui: "ACCT-7");

            X25PacketInfo back = PlaceAndAnswer(s, null, out bool calleeSawIt);

            Assert.That(calleeSawIt, Is.True);
            Assert.That(back.PacketType, Is.EqualTo(X25PacketType.CallAccepted));
            Assert.That(SpinWait.SpinUntil(() => Account(s, "card7").CallsMade == 1, 5000), Is.True);
            Assert.That(Account(s, "lineA").CallsMade + Account(s, "lineA").CallsActive, Is.EqualTo(0u));
        }

        /// <summary>
        /// Without an NUI and without a default, the call is billed to the account its calling address belongs to, as
        /// before.
        /// </summary>
        [Test]
        public void NoNui_NoDefault_BilledByCallingAddress()
        {
            using OneSwitch s = Build();

            X25PacketInfo back = PlaceAndAnswer(s, null, out bool calleeSawIt);

            Assert.That(calleeSawIt, Is.True);
            Assert.That(back.PacketType, Is.EqualTo(X25PacketType.CallAccepted));
            Assert.That(SpinWait.SpinUntil(() => Account(s, "lineA").CallsMade == 1, 5000), Is.True);
        }

        /// <summary>
        /// A default NUI that names no account is a configuration error, refused when the file is read rather than found
        /// on the first call.
        /// </summary>
        [Test]
        public void DefaultNui_NamingNoAccount_IsRefusedWhenTheConfigIsRead()
        {
            string[] ini =
            {
                "[switch]", "name = t", "dnic = 2340",
                "[port.0]", "type = access", "mode = dce", "host = 127.0.0.1", "port = 0", "default_nui = NOSUCH",
                "[account.0]", "name = card7", "nui = ACCT-7",
            };

            Assert.Throws<InvalidDataException>(() => SwitchConfigLoader.Load(IniConfig.Parse(ini)));
        }

        /// <summary>
        /// The account's NUI and the port's NUI settings are read from the config file.
        /// </summary>
        [Test]
        public void NuiSettings_AreReadFromTheConfig()
        {
            string[] ini =
            {
                "[switch]", "name = t", "dnic = 2340",
                "[port.0]", "type = access", "mode = dce", "host = 127.0.0.1", "port = 0", "nui_required = yes", "default_nui = ACCT-7",
                "[account.0]", "name = card7", "nui = ACCT-7",
            };

            SwitchConfig cfg = SwitchConfigLoader.Load(IniConfig.Parse(ini));

            Assert.That(cfg.Accounts[0].Nui, Is.EqualTo("ACCT-7"));
            Assert.That(cfg.Ports[0].NuiRequired, Is.True);
            Assert.That(cfg.Ports[0].DefaultNui, Is.EqualTo("ACCT-7"));
        }

        /// <summary>
        /// Without a calling address base the callee gets the address the caller sent, unchanged.
        /// </summary>
        [Test]
        public void NoCallingAddressBase_TheCallersAddressGoesThrough()
        {
            using OneSwitch s = Build();

            PlaceAndAnswer(s, null, out bool calleeSawIt);

            Assert.That(calleeSawIt, Is.True);
            Assert.That(s.CalleeSawCalling, Is.EqualTo("23400001"));
        }

        /// <summary>
        /// With a calling address base the network says where the call comes from: the callee gets base + the call's
        /// channel on the switch's side (the first call on a port with lci_base 1 is channel 1), whatever the caller sent,
        /// and the NUI still decides who pays.
        /// </summary>
        [Test]
        public void CallingAddressBase_ReplacesTheCallersAddress_AndTheNuiStillPays()
        {
            using OneSwitch s = Build(callingBase: "234070000");

            X25PacketInfo back = PlaceAndAnswer(s, "ACCT-7", out bool calleeSawIt);

            Assert.That(calleeSawIt, Is.True);
            Assert.That(back.PacketType, Is.EqualTo(X25PacketType.CallAccepted));
            Assert.That(s.CalleeSawCalling, Is.EqualTo("234070001"));
            Assert.That(SpinWait.SpinUntil(() => Account(s, "card7").CallsMade == 1, 5000), Is.True);
            Assert.That(SpinWait.SpinUntil(() => { lock (s.Records) { return s.Records.Count == 1; } }, 5000), Is.True);
            Assert.That(s.Records[0].Calling, Is.EqualTo("234070001"));
        }

        /// <summary>
        /// The address is the base plus the channel, written with the base's number of digits.
        /// </summary>
        [Test]
        public void CallingAddressFor_AddsTheChannel_KeepingTheLength()
        {
            PortConfig pc = new PortConfig { CallingAddressBase = "0242270000" };

            Assert.That(pc.CallingAddressFor(1), Is.EqualTo("0242270001"));
            Assert.That(pc.CallingAddressFor(64), Is.EqualTo("0242270064"));
            Assert.That(new PortConfig().CallingAddressFor(1), Is.EqualTo(string.Empty));
        }

        /// <summary>
        /// The setting is read from the file; a base that is not digits, is over 15 digits, or has no room for the
        /// highest channel is refused when the file is read.
        /// </summary>
        [TestCase("242270000", true)]
        [TestCase("2422 7000", false)]
        [TestCase("24227A000", false)]
        [TestCase("1234567890123456", false)]
        [TestCase("99999", false)]
        public void CallingAddressBase_IsReadAndChecked(string value, bool good)
        {
            string[] ini =
            {
                "[switch]", "name = t", "dnic = 2340",
                "[port.0]", "type = access", "mode = dce", "host = 127.0.0.1", "port = 0", "lci_count = 64",
                "calling_address_base = " + value,
            };

            if (good)
            {
                Assert.That(SwitchConfigLoader.Load(IniConfig.Parse(ini)).Ports[0].CallingAddressBase, Is.EqualTo(value));
            }
            else
            {
                Assert.Throws<InvalidDataException>(() => SwitchConfigLoader.Load(IniConfig.Parse(ini)));
            }
        }
    }
}
