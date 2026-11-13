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
    /// A peer that answers our Call Request on a different channel number than the one we used, as RFC 1613 allows.
    /// </summary>
    /// <remarks>
    /// RFC 1613 section 6: the LCN "has no significance and has arbitrary values", and a forwarding end sets it to the
    /// number on its own outgoing interface. x25emu answers a call placed on channel 8 with a Call Accepted on channel 1
    /// (measured 2026-11-12: Call Request 10-08-0B..., Call Accepted 10-01-0F...). Before the client translated the
    /// numbers it cleared such a call at once with diagnostic 20.
    /// </remarks>
    [TestFixture]
    public class X25ClientXotChannelNumberTests
    {
        [Test]
        public void ACallAnsweredOnAnotherChannel_IsAccepted_AndLaterPacketsUseThePeersChannel()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            List<byte[]> fromClient = new List<byte[]>();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Thread server = new Thread(() =>
                {
                    try
                    {
                        using TcpClient accepted = listener.AcceptTcpClient();
                        NetworkStream stream = accepted.GetStream();
                        byte[] header = new byte[4];
                        while (Read(stream, header, 4))
                        {
                            byte[] packet = new byte[(header[2] << 8) | header[3]];
                            if (!Read(stream, packet, packet.Length))
                            {
                                break;
                            }

                            lock (fromClient)
                            {
                                fromClient.Add(packet);
                            }

                            if (packet[2] == 0x0B)
                            {
                                // Call Accepted on channel 1, whatever channel the call came in on.
                                byte[] answer = { 0x00, 0x00, 0x00, 0x05, 0x10, 0x01, 0x0F, 0x00, 0x00 };
                                stream.Write(answer, 0, answer.Length);
                            }
                        }
                    }
                    catch (IOException)
                    {
                        // Client closed.
                    }
                    catch (SocketException)
                    {
                        // Listener stopped.
                    }
                })
                { IsBackground = true };
                server.Start();

                using X25Client client = new X25Client { ServiceName = "channel-number" };
                client.Open(new X25ClientConfig { Host = "127.0.0.1", Port = (ushort)port, LocalAddress = "23400099", Framing = X25ClientFraming.Xot }, NullLogger.Instance);
                X25Vc? call = client.CallWait("23420001", 5000);
                Assert.That(call, Is.Not.Null, "the call was not accepted; packets from the client: " + Dump(fromClient));
                Assert.That(client.Send(call!, Encoding.ASCII.GetBytes("HELLO")), Is.True);
                Assert.That(SpinWait.SpinUntil(() => { lock (fromClient) { return fromClient.Count >= 2; } }, 5000), Is.True, "no data packet arrived: " + Dump(fromClient));
                Thread.Sleep(300);

                string dump = Dump(fromClient);
                lock (fromClient)
                {
                    Assert.That(fromClient[0][2], Is.EqualTo(0x0B), "the first packet is the Call Request: " + dump);
                    Assert.That(fromClient[0][1], Is.Not.EqualTo(0x01), "the call must have been placed on a channel other than 1 for this test to mean something: " + dump);
                    Assert.That(fromClient[1][1], Is.EqualTo(0x01), "the data packet must use the channel the peer answered on (1): " + dump);
                    Assert.That((fromClient[1][2] & 0x01), Is.EqualTo(0), "the second packet is a data packet: " + dump);
                    for (int i = 0; i < fromClient.Count; i++)
                    {
                        Assert.That(fromClient[i][2], Is.Not.EqualTo(0x13), "the client cleared the call: " + dump);
                    }
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        private static string Dump(List<byte[]> packets)
        {
            StringBuilder text = new StringBuilder();
            lock (packets)
            {
                for (int i = 0; i < packets.Count; i++)
                {
                    text.Append(BitConverter.ToString(packets[i])).Append(' ');
                }
            }

            return text.ToString();
        }

        private static bool Read(NetworkStream stream, byte[] buffer, int count)
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
