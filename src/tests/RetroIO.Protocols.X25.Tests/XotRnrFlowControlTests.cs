// =============================================================================
// X.25 RNR (Receiver Not Ready) flow control over XOT.
//
// These tests need NOTHING running - the fake DCE below is the whole peer, so
// they are part of the ordinary suite and not [Category("Integration")].
//
// Why they exist: until 2026-09-05 the runtime handled RR and Reset but had no
// branch for RNR at all, so an RNR fell into the "unhandled packet type"
// warning and we kept sending at a peer that had just asked us to stop. The
// peer's only remaining defence is to discard, and discarding costs a Reset and
// the whole window of data with it - which is precisely how the C reference
// switch loses packets on its trunks (it ignores lapb_send's refusal at
// tools/x25switch.c:2750). There is no reason for us to be the other end of the
// same mistake.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Runtime.Models;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// A hand-built XOT DCE that answers a call and then drives the flow-control
    /// packets the test wants to see.
    /// </summary>
    /// <remarks>
    /// Deliberately not built on <see cref="X25Layer"/>: the point is to emit
    /// exact bytes, including ones a correct peer would not send, so the test
    /// controls the sequence rather than negotiating one.
    /// </remarks>
    internal sealed class FakeXotDce : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private volatile bool _running = true;
        private NetworkStream? _stream;

        /// <summary>Local LCN seen in the Call Request, echoed back on every packet we send.</summary>
        private ushort _lcn;

        /// <summary>DATA packets received from the device under test, in arrival order.</summary>
        public readonly List<byte> ReceivedPs = new List<byte>();

        /// <summary>Set once the Call Request has been answered.</summary>
        public readonly ManualResetEventSlim CallAccepted = new ManualResetEventSlim(false);

        /// <summary>The TCP port the fake DCE listens on.</summary>
        public int Port { get; }

        public FakeXotDce()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _thread = new Thread(Run) { IsBackground = true, Name = "FakeXotDce" };
            _thread.Start();
        }

        /// <summary>Number of DATA packets received so far.</summary>
        public int DataCount
        {
            get { lock (ReceivedPs) { return ReceivedPs.Count; } }
        }

        /// <summary>Sends RNR with the given P(R): "acknowledged up to here, now stop".</summary>
        public void SendRnr(byte pr) => SendSupervisory((byte)(0x05 | ((pr & 0x07) << 5)));

        /// <summary>Sends RR with the given P(R): "acknowledged up to here, carry on".</summary>
        public void SendRr(byte pr) => SendSupervisory((byte)(0x01 | ((pr & 0x07) << 5)));

        private void SendSupervisory(byte pti)
        {
            byte[] packet = new byte[3];
            packet[0] = (byte)(0x10 | ((_lcn >> 8) & 0x0F));
            packet[1] = (byte)(_lcn & 0xFF);
            packet[2] = pti;
            SendXot(packet);
        }

        private void SendXot(byte[] packet)
        {
            NetworkStream? s = _stream;
            if (s == null) return;

            // RFC 1613: version (0) then a big-endian length, then the X.25 packet.
            // One buffer, one Write - two writes can interleave with another thread's.
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
                    if (length < 3) continue;

                    _lcn = (ushort)(((packet[0] & 0x0F) << 8) | packet[1]);
                    byte pti = packet[2];

                    if (pti == 0xFB) // Restart Request -> confirm, as a real DCE does (X.25 4.1.1)
                    {
                        SendXot(new byte[] { 0x10, 0x00, 0xFF });
                    }
                    else if (pti == 0x0B) // Call Request -> answer at once
                    {
                        SendSupervisory(0x0F); // Call Accepted
                        CallAccepted.Set();
                    }
                    else if ((pti & 0x01) == 0) // DATA
                    {
                        lock (ReceivedPs) { ReceivedPs.Add((byte)((pti >> 1) & 0x07)); }
                    }
                }
            }
            catch (Exception)
            {
                // The test closes the runtime first, so a read failing here is the
                // normal way this thread ends.
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
            CallAccepted.Dispose();
        }
    }

    [TestFixture]
    public sealed class XotRnrFlowControlTests
    {
        private const string CALLED_ADDRESS = "23420001";
        private const string CALLING_ADDRESS = "23430001";

        /// <summary>
        /// How long to allow for a packet to cross a loopback socket and be
        /// processed. Generous: the assertions are about ordering, not speed, and
        /// a tight value would only make the test flake on a loaded machine.
        /// </summary>
        private const int SETTLE_MS = 500;

        private ProtocolRuntime CreateRuntime(int port)
        {
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "23430200";
            config.TestMode = true;

            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT_RNR",
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
                Destination = "XOT_RNR",
                Priority = 100,
                Enabled = true
            });

            return new ProtocolRuntime(NullLogger.Instance, config);
        }

        [Test]
        public void Rnr_stops_the_sender_and_a_following_Rr_releases_it()
        {
            using var dce = new FakeXotDce();
            using var runtime = CreateRuntime(dce.Port);

            var pad = runtime.CreatePadClient("RnrPAD");
            var connected = new ManualResetEventSlim(false);
            pad.ConnectionStateChanged += (s, a) =>
            {
                if (a.State == PadConnectionState.Connected) connected.Set();
            };

            pad.Connect(CALLED_ADDRESS, CALLING_ADDRESS);
            Assert.That(connected.Wait(5000), Is.True, "the PAD did not connect to the fake DCE");
            Assert.That(dce.CallAccepted.Wait(5000), Is.True, "the fake DCE never saw a Call Request");

            // One packet before any flow control, to prove the circuit carries data
            // at all - otherwise a later "nothing arrived" would prove nothing.
            Assert.That(pad.SendText("BEFORE\r\n"), Is.True, "the first send was refused");
            Thread.Sleep(SETTLE_MS);
            Assert.That(dce.DataCount, Is.EqualTo(1), "the packet sent before the RNR did not arrive");

            // Peer goes busy. P(R)=1 acknowledges the packet above.
            dce.SendRnr(1);
            Thread.Sleep(SETTLE_MS);

            // The send must now be refused rather than put on the wire. It blocks for
            // the runtime's 2-second window timeout first, which is the intended
            // behaviour - a busy peer is a pause, not an error.
            Assert.That(pad.SendText("DURING\r\n"), Is.False,
                "a send was accepted while the peer had signalled RNR");
            Assert.That(dce.DataCount, Is.EqualTo(1),
                "data reached the peer after it sent RNR");

            // RR both acknowledges and clears the busy condition - X.25 has no
            // separate "ready again" packet.
            dce.SendRr(1);
            Thread.Sleep(SETTLE_MS);

            Assert.That(pad.SendText("AFTER\r\n"), Is.True,
                "the sender was not released by the RR");
            Thread.Sleep(SETTLE_MS);
            Assert.That(dce.DataCount, Is.EqualTo(2),
                "the packet sent after the RR did not arrive");

            pad.Disconnect();
            pad.Dispose();
        }

        [Test]
        public void Rnr_does_not_consume_a_sequence_number()
        {
            using var dce = new FakeXotDce();
            using var runtime = CreateRuntime(dce.Port);

            var pad = runtime.CreatePadClient("RnrSeqPAD");
            var connected = new ManualResetEventSlim(false);
            pad.ConnectionStateChanged += (s, a) =>
            {
                if (a.State == PadConnectionState.Connected) connected.Set();
            };

            pad.Connect(CALLED_ADDRESS, CALLING_ADDRESS);
            Assert.That(connected.Wait(5000), Is.True, "the PAD did not connect to the fake DCE");

            Assert.That(pad.SendText("ONE\r\n"), Is.True);
            Thread.Sleep(SETTLE_MS);

            dce.SendRnr(1);
            Thread.Sleep(SETTLE_MS);
            pad.SendText("BLOCKED\r\n"); // refused, and must not burn a P(S)
            dce.SendRr(1);
            Thread.Sleep(SETTLE_MS);

            Assert.That(pad.SendText("TWO\r\n"), Is.True);
            Thread.Sleep(SETTLE_MS);

            // This is the assertion that matters. A refused send that still advanced
            // P(S) would leave a hole here, and a hole is what makes a correct peer
            // issue Reset cause 05 diagnostic 01 and throw the circuit away - the
            // exact failure measured on the C reference's trunks on 2026-09-05.
            List<byte> ps;
            lock (dce.ReceivedPs) { ps = new List<byte>(dce.ReceivedPs); }

            Assert.That(ps.Count, Is.EqualTo(2), "expected exactly the two accepted packets");
            Assert.That(ps[0], Is.EqualTo(0), "first packet should carry P(S)=0");
            Assert.That(ps[1], Is.EqualTo(1), "second packet should carry P(S)=1, with no hole for the refused send");

            pad.Disconnect();
            pad.Dispose();
        }
    }
}
