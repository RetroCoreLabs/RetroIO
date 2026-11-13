//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

// =============================================================================
// Wire pins for ProtocolRuntime - the desktop apps' X.25 engine.
//
// Phase A3 of Migration/X25 one-stack plan.md. ProtocolRuntime today runs the
// old Protocols/X25.cs engine plus its own XotFlowControl; Phase C moves it onto
// the one PacketLayer engine and deletes both. These tests record, byte for
// byte, what the runtime puts on an XOT connection TODAY for call setup, data,
// flow control, reset and clear, so that Phase C is a measured change and not
// a hopeful one. Where today's bytes are wrong the test name says so.
//
// The fake DCE below is the whole peer; nothing has to be running.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.PacketLayer;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Runtime.Models;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// A fake XOT DCE that records every X.25 packet it receives and can send any packet back.
    /// Answers a Call Request with a configurable Call Connected.
    /// </summary>
    internal sealed class RecordingXotDce : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private volatile bool _running = true;
        private NetworkStream? _stream;

        /// <summary>
        /// Every X.25 packet received, in order, without the XOT header.
        /// </summary>
        public readonly List<byte[]> Received = new List<byte[]>();

        /// <summary>
        /// Bytes after GFI/LCN/PTI to put in the Call Connected. Default: the spec form with an
        /// empty address block and no facilities (X.25 5.2.3: the address block octet is only
        /// mandatory when a facility field is present, and here it is not).
        /// </summary>
        public byte[] CallConnectedBody = Array.Empty<byte>();

        public bool AutoAccept = true;

        /// <summary>
        /// When not zero, the channel number the fake answers (and sends everything else) on, whatever number the
        /// Call Request used. RFC 1613 section 6: the number "has no significance"; x25emu answers on its own.
        /// </summary>
        public ushort AnswerChannel;

        /// <summary>
        /// Answer a Restart Request with a Restart Confirmation, as a real DCE does (X.25 4.1.1).
        /// </summary>
        public bool ConfirmRestart = true;

        public readonly ManualResetEventSlim CallSeen = new ManualResetEventSlim(false);

        public ushort Lcn { get; private set; }

        public int Port { get; }

        public RecordingXotDce()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _thread = new Thread(Run) { IsBackground = true, Name = "RecordingXotDce" };
            _thread.Start();
        }

        public int Count
        {
            get { lock (Received) { return Received.Count; } }
        }

        public byte[] At(int i)
        {
            lock (Received) { return Received[i]; }
        }

        public int CountOf(X25PacketType type)
        {
            int n = 0;
            lock (Received)
            {
                for (int i = 0; i < Received.Count; i++)
                {
                    if (Received[i].Length >= 3 && X25PacketParser.ParsePacket(Received[i]).PacketType == type)
                    {
                        n++;
                    }
                }
            }

            return n;
        }

        public bool WaitFor(Func<bool> condition, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(20);
            }

            return condition();
        }

        public void Send(byte[] packet)
        {
            NetworkStream? s = _stream;
            if (s == null)
            {
                return;
            }

            byte[] frame = new byte[4 + packet.Length];
            frame[2] = (byte)((packet.Length >> 8) & 0xFF);
            frame[3] = (byte)(packet.Length & 0xFF);
            Buffer.BlockCopy(packet, 0, frame, 4, packet.Length);
            lock (_listener)
            {
                s.Write(frame, 0, frame.Length);
                s.Flush();
            }
        }

        public void SendOnLcn(byte pti, params byte[] tail)
        {
            byte[] packet = new byte[3 + tail.Length];
            packet[0] = (byte)(0x10 | ((Lcn >> 8) & 0x0F));
            packet[1] = (byte)(Lcn & 0xFF);
            packet[2] = pti;
            Buffer.BlockCopy(tail, 0, packet, 3, tail.Length);
            Send(packet);
        }

        public void SendRr(byte pr) => SendOnLcn((byte)(0x01 | ((pr & 0x07) << 5)));
        public void SendRnr(byte pr) => SendOnLcn((byte)(0x05 | ((pr & 0x07) << 5)));

        public void SendData(byte ps, byte pr, byte[] payload)
        {
            SendOnLcn((byte)(((pr & 0x07) << 5) | ((ps & 0x07) << 1)), payload);
        }

        private void Run()
        {
            try
            {
                using TcpClient client = _listener.AcceptTcpClient();
                _stream = client.GetStream();

                byte[] header = new byte[4];
                while (_running)
                {
                    if (!ReadExact(_stream, header, 4)) break;
                    int length = (header[2] << 8) | header[3];
                    if (length <= 0 || length > 4096) break;

                    byte[] packet = new byte[length];
                    if (!ReadExact(_stream, packet, length)) break;
                    lock (Received) { Received.Add(packet); }
                    if (length < 3) continue;

                    byte pti = packet[2];
                    if (pti == 0xFB)
                    {
                        // A real DCE confirms a restart (X.25 4.1.1); the runtime holds its Call
                        // Request until it does, so the fake must too - unless a test is proving
                        // exactly that hold.
                        if (ConfirmRestart)
                        {
                            Send(new byte[] { 0x10, 0x00, 0xFF });
                        }
                    }
                    else if (pti == 0x0B)
                    {
                        Lcn = AnswerChannel != 0 ? AnswerChannel : (ushort)(((packet[0] & 0x0F) << 8) | packet[1]);
                        CallSeen.Set();
                        if (AutoAccept)
                        {
                            SendOnLcn(0x0F, CallConnectedBody);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // The runtime closing the socket is the normal way this thread ends.
            }
        }

        private static bool ReadExact(NetworkStream stream, byte[] buffer, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = stream.Read(buffer, got, count - got);
                if (n <= 0) return false;
                got += n;
            }

            return true;
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch (Exception) { }
            _thread.Join(2000);
            CallSeen.Dispose();
        }
    }

    [TestFixture]
    public sealed class ProtocolRuntimeWirePinTests
    {
        private const string CALLED_ADDRESS = "23420001";
        private const string CALLING_ADDRESS = "23430001";
        private const int SETTLE_MS = 400;

        private static ProtocolRuntime CreateRuntime(int port, byte requestedWindow = 2)
        {
            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = "23430200";
            config.X25.DefaultPacketWindowSize = requestedWindow;
            config.TestMode = true;

            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT_PIN",
                Enabled = true,
                Mode = XotMode.Client,
                RemoteIPAddress = "127.0.0.1",
                RemotePort = port,
                RemoteMachineId = "fake",
                ConnectTimeoutMs = 5000
            });

            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "FakeRoute",
                X121AddressPattern = "2342.*",
                Type = RetroIO.Protocols.X25.Runtime.RouteType.Interface,
                Destination = "XOT_PIN",
                Priority = 100,
                Enabled = true
            });

            return new ProtocolRuntime(NullLogger.Instance, config);
        }

        private static PadClient ConnectPad(ProtocolRuntime runtime, RecordingXotDce dce, string name)
        {
            PadClient pad = runtime.CreatePadClient(name);
            ManualResetEventSlim connected = new ManualResetEventSlim(false);
            pad.ConnectionStateChanged += (s, a) =>
            {
                if (a.State == PadConnectionState.Connected) connected.Set();
            };

            pad.Connect(CALLED_ADDRESS, CALLING_ADDRESS);
            Assert.That(dce.CallSeen.Wait(10000), Is.True, "the fake DCE never saw a Call Request");
            Assert.That(connected.Wait(10000), Is.True, "the PAD did not connect");
            return pad;
        }

        private static string Hex(byte[] b) => BitConverter.ToString(b).Replace('-', ' ');

        // ------------------------------------------------------------------ per-entry sizes

        private static ProtocolRuntime CreateRuntimeWithTwoEntries(RecordingXotDce a, RecordingXotDce b, byte? windowA, int? packetA, byte? windowB, int? packetB)
        {
            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = "23430200";
            config.TestMode = true;
            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT_A", Enabled = true, Mode = XotMode.Client, RemoteIPAddress = "127.0.0.1", RemotePort = a.Port,
                RemoteMachineId = "fake", RequestedWindowSize = windowA, RequestedPacketSize = packetA
            });
            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT_B", Enabled = true, Mode = XotMode.Both, LocalPort = 0, RemoteIPAddress = "127.0.0.1", RemotePort = b.Port,
                RemoteMachineId = "fake", RequestedWindowSize = windowB, RequestedPacketSize = packetB
            });
            config.Routing.RoutingTable.Add(new X25RouteEntry { Name = "ToA", X121AddressPattern = "2342.*", Type = RetroIO.Protocols.X25.Runtime.RouteType.Interface, Destination = "XOT_A", Priority = 100, Enabled = true });
            config.Routing.RoutingTable.Add(new X25RouteEntry { Name = "ToB", X121AddressPattern = "2343.*", Type = RetroIO.Protocols.X25.Runtime.RouteType.Interface, Destination = "XOT_B", Priority = 100, Enabled = true });
            return new ProtocolRuntime(NullLogger.Instance, config);
        }

        /// <summary>
        /// Each XOT entry has its own window and packet size (Cisco calls this the profile of a peer). The Call Request on
        /// the connection made for each entry carries that entry's sizes: entry A asks for window 3 and 256-byte packets
        /// (facilities 42 08 08 43 03 03), entry B for window 5 and 64-byte packets (42 06 06 43 05 05). Both kinds of
        /// entry, Client and Both, are covered.
        /// </summary>
        [Test]
        public void TwoEntries_WithDifferentSizes_EachCallRequestCarriesItsOwnSizes()
        {
            using RecordingXotDce a = new RecordingXotDce();
            using RecordingXotDce b = new RecordingXotDce();
            using ProtocolRuntime runtime = CreateRuntimeWithTwoEntries(a, b, 3, 256, 5, 64);
            PadClient padA = runtime.CreatePadClient("padA");
            PadClient padB = runtime.CreatePadClient("padB");
            padA.Connect("23420001", CALLING_ADDRESS);
            padB.Connect("23430001", CALLING_ADDRESS);
            Assert.That(a.CallSeen.Wait(10000), Is.True, "entry A saw no Call Request");
            Assert.That(b.CallSeen.Wait(10000), Is.True, "entry B saw no Call Request");

            Assert.That(Hex(X25PacketParser.ParsePacket(a.At(0)).Facilities), Is.EqualTo("42 08 08 43 03 03"), Hex(a.At(0)));
            Assert.That(Hex(X25PacketParser.ParsePacket(b.At(0)).Facilities), Is.EqualTo("42 06 06 43 05 05"), Hex(b.At(0)));
            padA.Disconnect();
            padB.Disconnect();
        }

        /// <summary>
        /// An entry that sets no size uses the X.25 section of the configuration (here window 2, packet 128).
        /// </summary>
        [Test]
        public void AnEntryWithNoSizes_UsesTheGlobalSettings()
        {
            using RecordingXotDce a = new RecordingXotDce();
            using RecordingXotDce b = new RecordingXotDce();
            using ProtocolRuntime runtime = CreateRuntimeWithTwoEntries(a, b, null, null, 4, null);
            PadClient padA = runtime.CreatePadClient("padA");
            PadClient padB = runtime.CreatePadClient("padB");
            padA.Connect("23420001", CALLING_ADDRESS);
            padB.Connect("23430001", CALLING_ADDRESS);
            Assert.That(a.CallSeen.Wait(10000), Is.True);
            Assert.That(b.CallSeen.Wait(10000), Is.True);

            Assert.That(Hex(X25PacketParser.ParsePacket(a.At(0)).Facilities), Is.EqualTo("42 07 07 43 02 02"), "no sizes set: the global 2 and 128");
            Assert.That(Hex(X25PacketParser.ParsePacket(b.At(0)).Facilities), Is.EqualTo("42 07 07 43 04 04"), "only the window set: the packet size stays global");
            padA.Disconnect();
            padB.Disconnect();
        }

        /// <summary>
        /// A size that cannot work is refused, not changed: the entry is not created and the reason is logged.
        /// </summary>
        [Test]
        public void ASizeThatCannotWork_RefusesTheEntry()
        {
            using RecordingXotDce a = new RecordingXotDce();
            using RecordingXotDce b = new RecordingXotDce();
            using ProtocolRuntime runtime = CreateRuntimeWithTwoEntries(a, b, 9, 100, 5, 64);
            Assert.That(runtime.TransportInterfaces.ContainsKey("XOT_A"), Is.False, "window 9 and packet 100 must refuse the entry");
            Assert.That(runtime.TransportInterfaces.ContainsKey("XOT_B"), Is.True, "the other entry is not touched");
        }
        // ------------------------------------------------------------------ call setup

        [Test]
        public void CallRequest_CarriesAddressesFacilitiesAndTheX29Cud()
        {
            using RecordingXotDce dce = new RecordingXotDce();
            using ProtocolRuntime runtime = CreateRuntime(dce.Port);
            using PadClient pad = ConnectPad(runtime, dce, "PinCall");

            // The Call Request is the first packet on the connection: RFC 1613 6.4 says a Restart is not
            // sent over XOT (see the test below). Since Phase C1c (2026-09-06) the one engine picks the
            // channel: as a DTE it takes the highest of the interface's range (X.25 Annex A note 6), 16
            // by default, where the old runtime always sent its local LCN 1.
            Assert.That(dce.Count, Is.GreaterThanOrEqualTo(1));
            byte[] call = dce.At(0);
            X25PacketInfo info = X25PacketParser.ParsePacket(call);

            Assert.That(info.PacketType, Is.EqualTo(X25PacketType.CallRequest), Hex(call));
            Assert.That(info.LCN, Is.EqualTo(16));
            Assert.That(info.CalledAddress, Is.EqualTo(CALLED_ADDRESS));
            Assert.That(info.CallingAddress, Is.EqualTo(CALLING_ADDRESS));

            // RFC 1613 6.1: an XOT Call Request MUST carry the packet size and window size
            // facilities. The engine asks for the configured defaults: 128/128 and 2/2.
            Assert.That(Hex(info.Facilities), Is.EqualTo("42 07 07 43 02 02"));

            // X.29 4.1: protocol identifier 01 00 00 00 in the first four octets of the CUD.
            Assert.That(Hex(info.CallUserData), Is.EqualTo("01 00 00 00"));

            pad.Disconnect();
        }

        [Test]
        public void CallConnected_TakenBeforeThePlacingThreadMovesOn_StillReachesThePad()
        {
            // The order that made ConnectPad fail now and then under load (CI, 2026-10-21): the
            // placing thread leaves the engine lock, the reader thread feeds the engine the DCE's
            // Call Connected, and only then does the placing thread map the runtime's local LCN onto
            // the engine's channel. The Call Connected found no local LCN and was dropped, so the
            // PAD never heard it. The seam holds the placing thread at exactly that point until the
            // engine has taken the answer, which forces that order every time.
            using RecordingXotDce dce = new RecordingXotDce();
            using ProtocolRuntime runtime = CreateRuntime(dce.Port);
            bool answerTaken = false;
            runtime.CallPlacedOnEndpointForTest = (endpoint, lci) =>
            {
                answerTaken = dce.WaitFor(() =>
                {
                    lock (endpoint.Sync)
                    {
                        return endpoint.Layer.Circuits.TryGetValue(lci, out X25Vc? vc) && vc.State == X25VcState.DataTransfer;
                    }
                }, 10000);
            };

            using PadClient pad = ConnectPad(runtime, dce, "PinRace");
            Assert.That(answerTaken, Is.True, "the engine never took the Call Connected while the placing thread was held");

            pad.Disconnect();
        }

        /// <summary>
        /// No Restart Request is sent over an XOT connection, and the Call Request goes out at once even though
        /// the far end never answers a restart.
        /// </summary>
        /// <remarks>
        /// RFC 1613 section 6.4: Restart, Restart Confirm, DTE Reject, Diagnostic and the registration packets
        /// "MUST NOT be sent over the TCP connection" and are silently discarded when received. There is no
        /// link under an XOT circuit to restart. Measured against x25emu (2026-11-12): it sends no Restart
        /// Confirmation, so the old behaviour (send a Restart, hold the call until it is confirmed, T20 =
        /// 180 s) left every call to it waiting for ever.
        /// </remarks>
        [Test]
        public void NoRestartRequest_IsSentOverXot_AndTheCallRequestGoesAtOnce()
        {
            using RecordingXotDce dce = new RecordingXotDce();
            dce.ConfirmRestart = false;
            using ProtocolRuntime runtime = CreateRuntime(dce.Port);
            using PadClient pad = runtime.CreatePadClient("PinNoRestart");
            pad.Connect(CALLED_ADDRESS, CALLING_ADDRESS);

            Assert.That(dce.WaitFor(() => dce.CountOf(X25PacketType.CallRequest) == 1, 3000), Is.True, "the Call Request never went out");
            Assert.That(X25PacketParser.ParsePacket(dce.At(0)).PacketType, Is.EqualTo(X25PacketType.CallRequest), Hex(dce.At(0)));
            Assert.That(dce.CountOf(X25PacketType.RestartRequest), Is.EqualTo(0), "no Restart over XOT");

            pad.Disconnect();
        }

        /// <summary>
        /// A peer that answers on another channel number than the call used (RFC 1613 section 6, x25emu does it):
        /// the call connects, later packets use the peer's number, and the clear goes out on it too.
        /// </summary>
        [Test]
        public void ACallAnsweredOnAnotherChannel_Connects_AndLaterPacketsUseThePeersChannel()
        {
            using RecordingXotDce dce = new RecordingXotDce();
            dce.AnswerChannel = 1;
            using ProtocolRuntime runtime = CreateRuntime(dce.Port);
            using PadClient pad = ConnectPad(runtime, dce, "PinChannel");
            int before = dce.Count;
            Assert.That(X25PacketParser.ParsePacket(dce.At(0)).LCN, Is.EqualTo(16), "the call was placed on 16, so 1 is another number");

            Assert.That(pad.SendText("AB"), Is.True);
            Assert.That(dce.WaitFor(() => dce.Count >= before + 1, 3000), Is.True);
            X25PacketInfo data = X25PacketParser.ParsePacket(dce.At(before));
            Assert.That(data.PacketType, Is.EqualTo(X25PacketType.Data), Hex(dce.At(before)));
            Assert.That(data.LCN, Is.EqualTo(1), "data must use the channel the peer answered on");

            pad.Disconnect();
            Assert.That(dce.WaitFor(() => dce.CountOf(X25PacketType.ClearRequest) == 1, 3000), Is.True);
            Assert.That(X25PacketParser.ParsePacket(dce.At(dce.Count - 1)).LCN, Is.EqualTo(1), "the clear must use the peer's channel");
        }

        // ------------------------------------------------------------------ data

        [Test]
        public void Data_IsSentAsOnePacketWithSequentialPS_AndNoQDMBits()
        {
            using RecordingXotDce dce = new RecordingXotDce();
            using ProtocolRuntime runtime = CreateRuntime(dce.Port);
            using PadClient pad = ConnectPad(runtime, dce, "PinData");
            int before = dce.Count;

            Assert.That(pad.SendText("AB"), Is.True);
            Assert.That(pad.SendText("CD"), Is.True);
            Assert.That(dce.WaitFor(() => dce.Count >= before + 2, 3000), Is.True);

            X25PacketInfo p0 = X25PacketParser.ParsePacket(dce.At(before));
            X25PacketInfo p1 = X25PacketParser.ParsePacket(dce.At(before + 1));

            Assert.That(p0.PacketType, Is.EqualTo(X25PacketType.Data));
            Assert.That(p0.PS, Is.EqualTo(0));
            Assert.That(p0.PR, Is.EqualTo(0));
            Assert.That(p0.QBit, Is.False);
            Assert.That(p0.DBit, Is.False);
            Assert.That(p0.MBit, Is.False);
            Assert.That(Encoding.ASCII.GetString(p0.UserData), Is.EqualTo("AB"));

            Assert.That(p1.PS, Is.EqualTo(1));
            Assert.That(Encoding.ASCII.GetString(p1.UserData), Is.EqualTo("CD"));

            pad.Disconnect();
        }

        [Test]
        public void WindowWithoutNegotiation_IsTwo()
        {
            // X.25 7.3.1.2: "All Administrations will provide a window size of 2" - the default when
            // the Call Connected carries no window facility. Fixed on 2026-09-05 (it was 7).
            using RecordingXotDce dce = new RecordingXotDce();
            using ProtocolRuntime runtime = CreateRuntime(dce.Port);
            using PadClient pad = ConnectPad(runtime, dce, "PinWin2");
            int before = dce.Count;

            Assert.That(pad.SendText("1"), Is.True);
            Assert.That(pad.SendText("2"), Is.True);
            // The third blocks for the runtime's window wait and is then refused.
            Assert.That(pad.SendText("3"), Is.False, "a third packet went out into a window of 2");
            Thread.Sleep(SETTLE_MS);
            Assert.That(dce.Count - before, Is.EqualTo(2));

            pad.Disconnect();
        }

        [Test]
        public void WindowNegotiatedToSevenInCallConnected_IsApplied()
        {
            using RecordingXotDce dce = new RecordingXotDce();
            // Spec-form Call Connected: address block octet 00, facility length 06, packet 128/128,
            // window 7/7 (X.25 5.2.3.1.2, 7.3.1.1, 7.3.1.2).
            dce.CallConnectedBody = new byte[] { 0x00, 0x06, 0x42, 0x07, 0x07, 0x43, 0x07, 0x07 };
            // Table 6-2: the Call Connected may only indicate 2..W(requested), so 7 must be REQUESTED
            // for 7 to be valid - the old runtime accepted any answer, the engine clears an invalid one.
            using ProtocolRuntime runtime = CreateRuntime(dce.Port, requestedWindow: 7);
            using PadClient pad = ConnectPad(runtime, dce, "PinWin7");
            int before = dce.Count;

            for (int i = 0; i < 7; i++)
            {
                Assert.That(pad.SendText(i.ToString()), Is.True, "send " + i + " refused inside a window of 7");
            }

            Assert.That(pad.SendText("8"), Is.False, "an eighth packet went out into a window of 7");
            Thread.Sleep(SETTLE_MS);
            Assert.That(dce.Count - before, Is.EqualTo(7));

            pad.Disconnect();
        }

        [Test]
        public void WindowGrantedAboveTheRequest_ClearsTheCall()
        {
            using RecordingXotDce dce = new RecordingXotDce();
            dce.CallConnectedBody = new byte[] { 0x00, 0x03, 0x43, 0x07, 0x07 };   // 7 granted against a request of 2
            using ProtocolRuntime runtime = CreateRuntime(dce.Port);
            using PadClient pad = runtime.CreatePadClient("PinBadWin");
            pad.Connect(CALLED_ADDRESS, CALLING_ADDRESS);

            // Table 6-2 (X.25 6.12): W(requested) >= 2 -> W(indicated) in 2..W(requested). 7 > 2 is a
            // facility parameter not allowed (Annex E 66) and the caller clears.
            Assert.That(dce.WaitFor(() => dce.CountOf(X25PacketType.ClearRequest) == 1, 3000), Is.True, "no Clear Request");
            X25PacketInfo clear = X25PacketParser.ParsePacket(dce.At(dce.Count - 1));
            Assert.That(clear.DiagnosticCode, Is.EqualTo(66));
        }

        // ------------------------------------------------------------------ receive side

        [Test]
        public void IncomingDataInSequence_IsAcknowledgedWithRR()
        {
            using RecordingXotDce dce = new RecordingXotDce();
            using ProtocolRuntime runtime = CreateRuntime(dce.Port);
            using PadClient pad = ConnectPad(runtime, dce, "PinRx");
            int before = dce.Count;

            dce.SendData(ps: 0, pr: 0, Encoding.ASCII.GetBytes("hi"));
            Assert.That(dce.WaitFor(() => dce.Count > before, 3000), Is.True, "no reply to a data packet");

            X25PacketInfo reply = X25PacketParser.ParsePacket(dce.At(before));
            Assert.That(reply.PacketType, Is.EqualTo(X25PacketType.RR), Hex(dce.At(before)));
            Assert.That(reply.PR, Is.EqualTo(1));

            pad.Disconnect();
        }

        [Test]
        public void IncomingDataWithAGapInPS_ResetsTheCircuit()
        {
            using RecordingXotDce dce = new RecordingXotDce();
            using ProtocolRuntime runtime = CreateRuntime(dce.Port);
            using PadClient pad = ConnectPad(runtime, dce, "PinGap");
            int before = dce.Count;

            // Expected P(S) 0; the DCE sends 3.
            dce.SendData(ps: 3, pr: 0, Encoding.ASCII.GetBytes("x"));
            Assert.That(dce.WaitFor(() => dce.Count > before, 3000), Is.True, "no reply to the out-of-sequence packet");

            X25PacketInfo reply = X25PacketParser.ParsePacket(dce.At(before));

            // FIXED 2026-09-06 (Phase C1c); the old handler acknowledged the gap with an RR P(R)=4.
            // X.25 4.4.1.3: a gap is a local procedure error - Reset, diagnostic 1; cause 0 from a DTE.
            Assert.That(reply.PacketType, Is.EqualTo(X25PacketType.ResetRequest), Hex(dce.At(before)));
            Assert.That(reply.ResetCause, Is.EqualTo(0));
            Assert.That(reply.DiagnosticCode, Is.EqualTo(1));

            pad.Disconnect();
        }

        // ------------------------------------------------------------------ reset and clear

        [Test]
        public void ResetIndication_IsConfirmedAndSequenceNumbersRestart()
        {
            using RecordingXotDce dce = new RecordingXotDce();
            using ProtocolRuntime runtime = CreateRuntime(dce.Port);
            using PadClient pad = ConnectPad(runtime, dce, "PinReset");

            Assert.That(pad.SendText("1"), Is.True);
            Assert.That(pad.SendText("2"), Is.True);
            Thread.Sleep(SETTLE_MS);
            int before = dce.Count;

            dce.SendOnLcn(0x1B, 0x05, 0x01);   // Reset Indication, local procedure error, invalid P(S)
            Assert.That(dce.WaitFor(() => dce.CountOf(X25PacketType.ResetConfirmation) == 1, 3000), Is.True,
                "no Reset Confirmation");

            // X.25 4.4.3: after a reset the window is zeroed in both directions.
            int afterConfirm = dce.Count;
            Assert.That(pad.SendText("3"), Is.True, "send refused after the reset");
            Assert.That(dce.WaitFor(() => dce.Count > afterConfirm, 3000), Is.True);
            X25PacketInfo next = X25PacketParser.ParsePacket(dce.At(afterConfirm));
            Assert.That(next.PacketType, Is.EqualTo(X25PacketType.Data));
            Assert.That(next.PS, Is.EqualTo(0), "P(S) did not restart at 0 after the reset");

            pad.Disconnect();
        }

        [Test]
        public void ClearIndication_IsConfirmedAndThePadIsTold()
        {
            using RecordingXotDce dce = new RecordingXotDce();
            using ProtocolRuntime runtime = CreateRuntime(dce.Port);
            using PadClient pad = ConnectPad(runtime, dce, "PinClear");

            ManualResetEventSlim disconnected = new ManualResetEventSlim(false);
            pad.ConnectionStateChanged += (s, a) =>
            {
                if (a.State == PadConnectionState.Disconnected) disconnected.Set();
            };

            dce.SendOnLcn(0x13, 0x00, 0x00);   // Clear Indication, DTE originated

            // FIXED 2026-09-06 (Phase C1c); nothing used to go back and the PAD was never told.
            // X.25 4.1.8: the DTE responds with a Clear Confirmation.
            Assert.That(dce.WaitFor(() => dce.CountOf(X25PacketType.ClearConfirmation) == 1, 3000), Is.True, "no Clear Confirmation");
            Assert.That(disconnected.Wait(3000), Is.True, "the PAD did not learn about the clear");
        }

        [Test]
        public void PadDisconnect_SendsAClearRequestWithDteCause()
        {
            using RecordingXotDce dce = new RecordingXotDce();
            using ProtocolRuntime runtime = CreateRuntime(dce.Port);
            using PadClient pad = ConnectPad(runtime, dce, "PinDisc");
            int before = dce.Count;

            pad.Disconnect();

            // FIXED 2026-09-06 (Phase C1c); nothing used to reach the wire, so the far DCE kept the
            // call up for ever. X.25 4.1.7: the DTE clears with a Clear Request; Table 5-7 note a:
            // a DTE's clearing cause is 0.
            Assert.That(dce.WaitFor(() => dce.CountOf(X25PacketType.ClearRequest) == 1, 3000), Is.True, "no Clear Request");
            X25PacketInfo clear = X25PacketParser.ParsePacket(dce.At(before));
            Assert.That(clear.PacketType, Is.EqualTo(X25PacketType.ClearRequest), Hex(dce.At(before)));
            Assert.That(clear.ClearingCause, Is.EqualTo(0x00));

            // The DCE confirms and the channel is free again.
            dce.SendOnLcn(0x17);
        }
    }
}
