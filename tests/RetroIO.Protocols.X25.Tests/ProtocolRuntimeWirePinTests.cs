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

        /// <summary>Every X.25 packet received, in order, without the XOT header.</summary>
        public readonly List<byte[]> Received = new List<byte[]>();

        /// <summary>
        /// Bytes after GFI/LCN/PTI to put in the Call Connected. Default: the spec form with an
        /// empty address block and no facilities (X.25 5.2.3: the address block octet is only
        /// mandatory when a facility field is present, and here it is not).
        /// </summary>
        public byte[] CallConnectedBody = Array.Empty<byte>();

        public bool AutoAccept = true;

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
                    if (pti == 0x0B)
                    {
                        Lcn = (ushort)(((packet[0] & 0x0F) << 8) | packet[1]);
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

        private static ProtocolRuntime CreateRuntime(int port)
        {
            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = "23430200";
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
            Assert.That(dce.CallSeen.Wait(5000), Is.True, "the fake DCE never saw a Call Request");
            Assert.That(connected.Wait(5000), Is.True, "the PAD did not connect");
            return pad;
        }

        private static string Hex(byte[] b) => BitConverter.ToString(b).Replace('-', ' ');

        // ------------------------------------------------------------------ call setup

        [Test]
        public void CallRequest_CarriesAddressesFacilitiesAndTheX29Cud()
        {
            using RecordingXotDce dce = new RecordingXotDce();
            using ProtocolRuntime runtime = CreateRuntime(dce.Port);
            using PadClient pad = ConnectPad(runtime, dce, "PinCall");

            // MEASURED 2026-09-06: two packets on connect - a Restart Request, then the Call
            // Request: 10 01 0B 88 23 42 00 01 23 43 00 01 06 42 07 07 43 07 07 01 00 00 00.
            Assert.That(dce.Count, Is.GreaterThanOrEqualTo(2));
            byte[] call = dce.At(1);
            X25PacketInfo info = X25PacketParser.ParsePacket(call);

            Assert.That(info.PacketType, Is.EqualTo(X25PacketType.CallRequest), Hex(call));
            Assert.That(info.LCN, Is.EqualTo(1));
            Assert.That(info.CalledAddress, Is.EqualTo(CALLED_ADDRESS));
            Assert.That(info.CallingAddress, Is.EqualTo(CALLING_ADDRESS));

            // RFC 1613 6.1: an XOT Call Request MUST carry the packet size and window size
            // facilities. Since 2026-09-05 it does: 128/128 and 7/7 (verified on the wire against
            // the C switch that day).
            Assert.That(Hex(info.Facilities), Is.EqualTo("42 07 07 43 07 07"));

            // X.29 4.1: protocol identifier 01 00 00 00 in the first four octets of the CUD.
            Assert.That(Hex(info.CallUserData), Is.EqualTo("01 00 00 00"));

            pad.Disconnect();
        }

        [Test]
        public void RestartRequest_IsSentOnLinkUp_ButTheCallDoesNotWaitForItsConfirmation_WHICH_IS_WRONG()
        {
            using RecordingXotDce dce = new RecordingXotDce();
            using ProtocolRuntime runtime = CreateRuntime(dce.Port);
            using PadClient pad = ConnectPad(runtime, dce, "PinRestart");

            // MEASURED 2026-09-06: the first packet is a Restart Request, cause 0 diag 0 - good,
            // X.25 4.1 asks for it. The fake DCE never confirms it, and the Call Request follows
            // at once anyway. X.25 4.1.1: the DTE must not send call set-up packets until the
            // restart is confirmed (state r1 is only left on Restart Confirmation or a Restart
            // Indication); T20 (180 s) would retransmit. The C emulator has the same gap
            // (audit F-02-06: "restarted set before any reply"). Plan R6.
            Assert.That(Hex(dce.At(0)), Is.EqualTo("10 00 FB 00 00"));
            Assert.That(X25PacketParser.ParsePacket(dce.At(1)).PacketType, Is.EqualTo(X25PacketType.CallRequest),
                "behaviour changed: holding the Call Request until Restart Confirmation is the FIX - rewrite this test");

            pad.Disconnect();
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
            using ProtocolRuntime runtime = CreateRuntime(dce.Port);
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
        public void IncomingDataWithAGapInPS_IsAcknowledgedAsIfInSequence_WHICH_IS_WRONG()
        {
            using RecordingXotDce dce = new RecordingXotDce();
            using ProtocolRuntime runtime = CreateRuntime(dce.Port);
            using PadClient pad = ConnectPad(runtime, dce, "PinGap");
            int before = dce.Count;

            // Expected P(S) 0; the DCE sends 3.
            dce.SendData(ps: 3, pr: 0, Encoding.ASCII.GetBytes("x"));
            Assert.That(dce.WaitFor(() => dce.Count > before, 3000), Is.True, "no reply to the out-of-sequence packet");

            X25PacketInfo reply = X25PacketParser.ParsePacket(dce.At(before));

            // MEASURED 2026-09-06: the reply is 10 01 81 - an RR with P(R)=4. The gap is neither
            // noticed nor reported. X.25 4.4.1.3 makes a P(S) gap a local procedure error (Reset,
            // cause 05 diag 01). Plan R1. (The old Protocols/X25.cs engine would have sent a REJ,
            // which RFC 1613 6.4 forbids over XOT; the XOT path does not go through that code.)
            Assert.That(reply.PacketType, Is.EqualTo(X25PacketType.RR),
                "behaviour changed: a Reset Request (cause 05, diag 01) is the FIX - rewrite this test. Got " + Hex(dce.At(before)));
            Assert.That(reply.PR, Is.EqualTo(4));

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
        public void ClearIndication_IsNeverConfirmed_WHICH_IS_WRONG()
        {
            using RecordingXotDce dce = new RecordingXotDce();
            using ProtocolRuntime runtime = CreateRuntime(dce.Port);
            using PadClient pad = ConnectPad(runtime, dce, "PinClear");
            int before = dce.Count;

            ManualResetEventSlim disconnected = new ManualResetEventSlim(false);
            pad.ConnectionStateChanged += (s, a) =>
            {
                if (a.State == PadConnectionState.Disconnected) disconnected.Set();
            };

            dce.SendOnLcn(0x13, 0x00, 0x00);   // Clear Indication, DTE originated
            bool padSawIt = disconnected.Wait(3000);
            Thread.Sleep(SETTLE_MS);

            // MEASURED 2026-09-06: nothing at all goes back. X.25 4.1.8: "the DTE ... shall
            // respond by transferring a DTE clear confirmation packet"; without it the DCE holds
            // the channel in p7 until T23 (180 s) expires and then clears again. Plan R5/R17.
            Assert.That(dce.Count - before, Is.EqualTo(0),
                "behaviour changed: a DTE Clear Confirmation is the FIX - rewrite this test. Got " + (dce.Count > before ? Hex(dce.At(before)) : ""));
            // And the PAD is not told: it still believes it is connected to a call the network
            // has already torn down.
            Assert.That(padSawIt, Is.False,
                "behaviour changed: the PAD now learns about a Clear Indication - that is a FIX - rewrite this test");
        }

        [Test]
        public void PadDisconnect_SendsNothingOnTheWire_WHICH_IS_WRONG()
        {
            using RecordingXotDce dce = new RecordingXotDce();
            using ProtocolRuntime runtime = CreateRuntime(dce.Port);
            using PadClient pad = ConnectPad(runtime, dce, "PinDisc");
            int before = dce.Count;

            pad.Disconnect();
            Thread.Sleep(3000);

            // MEASURED 2026-09-06: no Clear Request, no anything. The far DCE keeps the virtual
            // call up on LCI 1 for ever (or until its own idle timer); the next call from this
            // side allocates LCI 1 again and collides with the ghost. X.25 4.1.7: the DTE clears
            // by sending a Clear Request. Plan R5.
            Assert.That(dce.Count - before, Is.EqualTo(0),
                "behaviour changed: a Clear Request on disconnect is the FIX - rewrite this test. Got " + (dce.Count > before ? Hex(dce.At(before)) : ""));
        }
    }
}
