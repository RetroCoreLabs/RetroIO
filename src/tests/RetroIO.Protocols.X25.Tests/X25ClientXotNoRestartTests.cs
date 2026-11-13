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
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Client;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// What the XOT client puts on a fresh TCP connection, read from the real bytes: nothing but the call.
    /// </summary>
    /// <remarks>
    /// RFC 1613 section 6.4: Restart, Restart Confirm, DTE Reject, Diagnostic, Registration Request and
    /// Registration Confirmation "MUST NOT be sent over the TCP connection. If one of these packets is received, then it
    /// MUST be silently discarded." So nobody answers a Restart Request over XOT, and a client that sends one and then
    /// waits for the answer (X.25 4.1.1 holds every call while a restart is unanswered) never places its call. The
    /// x25emu switch behaves exactly so: measured against it, a Call Request on a fresh connection is accepted at once
    /// and a Restart Request is never answered.
    /// </remarks>
    [TestFixture]
    public class X25ClientXotNoRestartTests
    {
        /// <summary>
        /// A server that reads XOT frames and never answers anything, like a peer that discards what it must not receive.
        /// The client's first packet must be its Call Request, sent at once.
        /// </summary>
        [Test]
        public void TheFirstPacketOnAFreshXotConnection_IsTheCallRequest_AndNoRestartIsSent()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                List<byte[]> packets = new List<byte[]>();
                ManualResetEventSlim firstPacket = new ManualResetEventSlim(false);
                Thread server = new Thread(() =>
                {
                    try
                    {
                        using TcpClient accepted = listener.AcceptTcpClient();
                        NetworkStream stream = accepted.GetStream();
                        byte[] header = new byte[4];
                        while (ReadFully(stream, header, 4))
                        {
                            int length = (header[2] << 8) | header[3];
                            byte[] packet = new byte[length];
                            if (!ReadFully(stream, packet, length))
                            {
                                break;
                            }

                            lock (packets)
                            {
                                packets.Add(packet);
                            }

                            firstPacket.Set();
                        }
                    }
                    catch (IOException)
                    {
                        // The client closed.
                    }
                    catch (SocketException)
                    {
                        // The listener was stopped.
                    }
                })
                { IsBackground = true };
                server.Start();

                using X25Client client = new X25Client { ServiceName = "no-restart" };
                client.Open(new X25ClientConfig { Host = "127.0.0.1", Port = (ushort)port, LocalAddress = "23400099", Framing = X25ClientFraming.Xot }, NullLogger.Instance);

                // The call is placed on its own thread: CallWait blocks until an answer that this server never gives.
                Thread caller = new Thread(() => client.CallWait("23420001", 3000)) { IsBackground = true };
                caller.Start();

                Assert.That(firstPacket.Wait(5000), Is.True, "nothing at all arrived on the connection");
                Thread.Sleep(500);
                StringBuilder seen = new StringBuilder();
                lock (packets)
                {
                    for (int i = 0; i < packets.Count; i++)
                    {
                        seen.Append(BitConverter.ToString(packets[i])).Append('\n');
                    }

                    Assert.That(packets[0][2], Is.EqualTo(0x0B), "the first packet must be a Call Request (type 0x0B), not a Restart Request (0xFB). Packets seen, in order:\n" + seen);
                    for (int i = 0; i < packets.Count; i++)
                    {
                        Assert.That(packets[i][2], Is.Not.EqualTo(0xFB), "a Restart Request was sent over XOT (RFC 1613 6.4 forbids it). Packets seen, in order:\n" + seen);
                    }
                }

                caller.Join(6000);
            }
            finally
            {
                listener.Stop();
            }
        }

        private static bool ReadFully(NetworkStream stream, byte[] buffer, int count)
        {
            int done = 0;
            while (done < count)
            {
                int n = stream.Read(buffer, done, count - done);
                if (n <= 0)
                {
                    return false;
                }

                done += n;
            }

            return true;
        }
    }
}
