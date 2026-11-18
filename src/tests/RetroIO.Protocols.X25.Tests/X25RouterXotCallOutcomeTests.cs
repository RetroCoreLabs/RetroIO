//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Transport;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// A call the router places on an XOT_Client route (a route whose destination is an address, dialled on a new XOT
    /// connection) reports what really happened to it: accepted, cleared with the cause and diagnostic the peer gave,
    /// or no answer within T21 (X.25 Annex D, Table D.2: "To transmit a clear request"). Until 2026-11-18 the router
    /// hand-built a Call Request with the addresses in ASCII, sent it and reported the call placed without waiting
    /// ("TODO: Wait for call accepted/cleared response. For now, assume success"). Real sockets on the loopback address;
    /// the peer is a plain listener that answers with hand-written packets.
    /// </summary>
    [TestFixture]
    public class X25RouterXotCallOutcomeTests
    {
        private const string Called = "23420001";
        private const string Calling = "23400001";

        // X.25 5.2.1 and Figure 7: GFI 0001 (modulo 8) + channel 1, packet type Call Request 00001011, then the address
        // length octet (calling length in the high half, called length in the low half) and the addresses in BCD,
        // called first: 2342 0001 2340 0001.
        private static readonly byte[] ExpectedCallRequestStart = { 0x10, 0x01, 0x0B, 0x88, 0x23, 0x42, 0x00, 0x01, 0x23, 0x40, 0x00, 0x01 };

        // Call Connected on channel 1 (X.25 Table 5-1: 00001111).
        private static readonly byte[] CallConnected = { 0x10, 0x01, 0x0F };

        // Clear Indication on channel 1 (00010011), cause 0x0D not obtainable, diagnostic 67 (0x43) unknown number.
        private static readonly byte[] ClearIndication = { 0x10, 0x01, 0x13, 0x0D, 0x43 };

        [Test]
        public void ThePeerAcceptsTheCall_TheOutcomeIsAccepted_AndTheCallRequestIsInX25Form()
        {
            using Peer peer = new Peer();
            Rig rig = new Rig(peer.Port);
            try
            {
                X25CallOutcome? outcome = null;
                Thread caller = new Thread(() => outcome = rig.Router.PlaceOutboundCall(new X25CallRequest(Called, Calling)));
                caller.Start();

                TcpClient seen = peer.Accept();
                byte[] request = ReadFrame(seen);
                Assert.That(request.Length, Is.GreaterThanOrEqualTo(ExpectedCallRequestStart.Length), "the Call Request is too short");
                Assert.That(request.AsSpan(0, ExpectedCallRequestStart.Length).ToArray(), Is.EqualTo(ExpectedCallRequestStart),
                    "the Call Request is not in the X.25 5.2.1 form (BCD addresses after the address length octet)");

                seen.GetStream().Write(Frame(CallConnected));
                Assert.That(caller.Join(10000), Is.True, "the router never reported the call");
                Assert.That(outcome!.Kind, Is.EqualTo(X25CallOutcomeKind.Accepted), outcome.ToString());
                Assert.That(outcome.Call, Is.InstanceOf<XotOutgoingCall>());
                ((XotOutgoingCall)outcome.Call!).Dispose();
            }
            finally
            {
                rig.Transport.Stop();
            }
        }

        [Test]
        public void ThePeerClearsTheCall_TheOutcomeCarriesItsCauseAndDiagnostic_AndTheClearIsConfirmed()
        {
            using Peer peer = new Peer();
            Rig rig = new Rig(peer.Port);
            try
            {
                X25CallOutcome? outcome = null;
                Thread caller = new Thread(() => outcome = rig.Router.PlaceOutboundCall(new X25CallRequest(Called, Calling)));
                caller.Start();

                TcpClient seen = peer.Accept();
                ReadFrame(seen);
                seen.GetStream().Write(Frame(ClearIndication));

                Assert.That(caller.Join(10000), Is.True, "the router never reported the call");
                Assert.That(outcome!.Kind, Is.EqualTo(X25CallOutcomeKind.Cleared), outcome.ToString());
                Assert.That(outcome.Cause, Is.EqualTo((byte)0x0D));
                Assert.That(outcome.Diagnostic, Is.EqualTo((byte)0x43));
                Assert.That(outcome.Succeeded, Is.False);

                // X.25 4.1.7: the DTE answers a Clear Indication with a Clear Confirmation (00010111).
                Assert.That(ReadFrame(seen), Is.EqualTo(new byte[] { 0x10, 0x01, 0x17 }));
            }
            finally
            {
                rig.Transport.Stop();
            }
        }

        [Test]
        public void ThePeerClearsTheCall_RouteOutboundCallSaysTheCallFailed()
        {
            using Peer peer = new Peer();
            Rig rig = new Rig(peer.Port);
            try
            {
                bool placed = true;
                Thread caller = new Thread(() => placed = rig.Router.RouteOutboundCall(new X25CallRequest(Called, Calling)));
                caller.Start();

                TcpClient seen = peer.Accept();
                ReadFrame(seen);
                seen.GetStream().Write(Frame(ClearIndication));

                Assert.That(caller.Join(10000), Is.True, "the router never reported the call");
                Assert.That(placed, Is.False, "a call the peer cleared was reported as placed");
            }
            finally
            {
                rig.Transport.Stop();
            }
        }

        [Test]
        public void NoAnswerWithinT21_TheOutcomeIsTimedOut_AndThisEndSendsTheClearRequest()
        {
            using Peer peer = new Peer();
            // X.25 Annex D note 1: "A DTE may use a timer shorter than the value given for T21 in Table D.2."
            Rig rig = new Rig(peer.Port, callRequestTimeoutMs: 1500);
            try
            {
                X25CallOutcome? outcome = null;
                Thread caller = new Thread(() => outcome = rig.Router.PlaceOutboundCall(new X25CallRequest(Called, Calling)));
                caller.Start();

                TcpClient seen = peer.Accept();
                ReadFrame(seen);

                // Table D.2, T21: "To transmit a clear request". Diagnostic 49 (0x31), time expired for incoming call.
                byte[] clear = ReadFrame(seen);
                Assert.That(clear.Length, Is.GreaterThanOrEqualTo(5));
                Assert.That(clear[2], Is.EqualTo((byte)0x13), "after T21 the DTE must send a Clear Request");
                Assert.That(clear[4], Is.EqualTo((byte)0x31), "the Clear Request after T21 carries diagnostic 49");

                Assert.That(caller.Join(10000), Is.True, "the router never reported the call");
                Assert.That(outcome!.Kind, Is.EqualTo(X25CallOutcomeKind.TimedOut), outcome.ToString());
                Assert.That(outcome.Diagnostic, Is.EqualTo((byte)0x31));
            }
            finally
            {
                rig.Transport.Stop();
            }
        }

        [Test]
        public void TheConnectionClosesBeforeTheAnswer_TheOutcomeIsClearedOutOfOrder()
        {
            using Peer peer = new Peer();
            Rig rig = new Rig(peer.Port);
            try
            {
                X25CallOutcome? outcome = null;
                Thread caller = new Thread(() => outcome = rig.Router.PlaceOutboundCall(new X25CallRequest(Called, Calling)));
                caller.Start();

                TcpClient seen = peer.Accept();
                ReadFrame(seen);
                seen.Close();

                Assert.That(caller.Join(10000), Is.True, "the router never reported the call");
                Assert.That(outcome!.Kind, Is.EqualTo(X25CallOutcomeKind.Cleared), outcome.ToString());
                Assert.That(outcome.Cause, Is.EqualTo((byte)0x09), "cause 9, out of order: the connection carrying the call is gone");
            }
            finally
            {
                rig.Transport.Stop();
            }
        }

        [Test]
        public void NobodyListens_TheOutcomeIsNotPlaced()
        {
            int port;
            using (Peer gone = new Peer())
            {
                port = gone.Port;
            }

            Rig rig = new Rig(port);
            try
            {
                X25CallOutcome outcome = rig.Router.PlaceOutboundCall(new X25CallRequest(Called, Calling));
                Assert.That(outcome.Kind, Is.EqualTo(X25CallOutcomeKind.NotPlaced), outcome.ToString());
            }
            finally
            {
                rig.Transport.Stop();
            }
        }

        [Test]
        public void ADestinationWithAPort_IsSplit_AndOneWithout_GetsPort1998()
        {
            X25Router.SplitXotDestination("10.1.2.3:2001", out string host, out int port);
            Assert.That(host, Is.EqualTo("10.1.2.3"));
            Assert.That(port, Is.EqualTo(2001));

            X25Router.SplitXotDestination("10.1.2.3", out host, out port);
            Assert.That(host, Is.EqualTo("10.1.2.3"));
            Assert.That(port, Is.EqualTo(1998), "RFC 1613 names port 1998 for XOT");
        }

        /// <summary>
        /// A router with an active XOT client transport and one XOT_Client route for the called number to the peer.
        /// </summary>
        private sealed class Rig
        {
            public Rig(int peerPort, int callRequestTimeoutMs = 200000)
            {
                Transport = new XotClientTransport(NullLogger.Instance, new XotClientConfig { Name = "xotc", ConnectTimeout = 2000 });
                Assert.That(Transport.Start(), Is.True);
                Router = new X25Router(NullLogger.Instance);
                Router.OutgoingCallConfig = new RetroIO.Protocols.X25.PacketLayer.X25Config { CallRequestTimeoutMs = callRequestTimeoutMs };
                Router.RegisterInterface(Transport);
                Router.AddRoute(new X25Route
                {
                    Name = "to-peer",
                    X121AddressPattern = "^2342",
                    Type = RouteType.XOT_Client,
                    Destination = "127.0.0.1:" + peerPort,
                    Direction = RouteDirection.Outbound,
                });
            }

            public XotClientTransport Transport { get; }

            public X25Router Router { get; }
        }

        private static byte[] Frame(byte[] packet)
        {
            byte[] frame = new byte[4 + packet.Length];
            frame[2] = (byte)(packet.Length >> 8);
            frame[3] = (byte)packet.Length;
            Buffer.BlockCopy(packet, 0, frame, 4, packet.Length);
            return frame;
        }

        private static byte[] ReadFrame(TcpClient client)
        {
            client.ReceiveTimeout = 5000;
            NetworkStream stream = client.GetStream();
            byte[] header = ReadExact(stream, 4);
            return ReadExact(stream, (header[2] << 8) | header[3]);
        }

        private static byte[] ReadExact(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int done = 0;
            while (done < count)
            {
                int n = stream.Read(buffer, done, count - done);
                if (n == 0)
                {
                    throw new InvalidOperationException("the connection closed after " + done + " of " + count + " octets");
                }

                done += n;
            }

            return buffer;
        }

        /// <summary>
        /// A plain TCP listener standing in for the XOT gateway; it closes every connection it accepted.
        /// </summary>
        private sealed class Peer : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly System.Collections.Generic.List<TcpClient> _accepted = new System.Collections.Generic.List<TcpClient>();

            public Peer()
            {
                _listener.Start();
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            public TcpClient Accept()
            {
                _listener.Server.ReceiveTimeout = 5000;
                if (!_listener.Server.Poll(10_000_000, SelectMode.SelectRead))
                {
                    throw new InvalidOperationException("the router never opened a connection to the peer");
                }

                TcpClient client = _listener.AcceptTcpClient();
                _accepted.Add(client);
                return client;
            }

            public void Dispose()
            {
                for (int i = 0; i < _accepted.Count; i++)
                {
                    _accepted[i].Close();
                }

                _listener.Stop();
            }
        }
    }
}
