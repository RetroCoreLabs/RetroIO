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
using System.Threading.Tasks;

using RetroIO.Protocols.Xmsg.Hub.Server;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Hub.Tests
{
    /// <summary>
    /// The buffer of recent frames, the pcap it exports, and the hosted service - checked with the
    /// real frames of the LIST-NAMES capture of 2026-11-10 (D102 asks D100, 152 frames, taken in
    /// the machine-to-hub direction from a Wireshark capture of the standalone hub on port 5010).
    /// </summary>
    /// <remarks>
    /// Every hub here listens on port 0 (a free port). Port 5010 is in live use on this box.
    /// </remarks>
    public sealed class HubFrameLogTests
    {
        private static readonly byte[] Magic = { 0x52, 0x45, 0x54, 0x48 };

        [Fact]
        public void TheCaptureFramesGoThroughTheHub_EachOnceInTheBuffer_WithTheirSystems()
        {
            byte[][] frames = LoadCaptureFrames();
            Assert.Equal(152, frames.Length);

            HubServer hub = new HubServer(IPAddress.Loopback, 0);
            hub.FrameLog = new HubFrameRing(1000);
            hub.Start();
            try
            {
                using Machine d100 = new Machine(hub.Port);
                using Machine d102 = new Machine(hub.Port);
                WaitForMembers(hub, 2);

                // Each frame is sent by the machine its source address names, so the other one
                // receives it - just as on the real segment.
                int toD100 = 0;
                int toD102 = 0;
                for (int i = 0; i < frames.Length; i++)
                {
                    ushort source = SourceSystem(frames[i]);
                    if (source == 100)
                    {
                        d100.Send(frames[i]);
                        Assert.Equal(frames[i], d102.Receive(5000));
                        toD102++;
                    }
                    else
                    {
                        Assert.Equal(102, source);
                        d102.Send(frames[i]);
                        Assert.Equal(frames[i], d100.Receive(5000));
                        toD100++;
                    }
                }

                Assert.Equal(76, toD100);
                Assert.Equal(76, toD102);

                // Wait until the last frame is recorded: the record goes in after the copies are
                // queued, which can be a moment after the receiver already has its copy.
                SpinWait.SpinUntil(() => hub.FrameLog.LastSequence == 152, 5000);
                HubFrameRecord[] records = hub.FrameLog.GetAfter(0, 1000);

                Assert.Equal(152, records.Length);
                for (int i = 0; i < records.Length; i++)
                {
                    Assert.Equal(i + 1, records[i].Sequence);
                    Assert.Equal(frames[i], records[i].Frame);
                    Assert.True(records[i].IsNdFrame);
                    Assert.Equal(HubFrameOutcome.Forwarded, records[i].Outcome);
                    Assert.Equal(1, records[i].CopiesSent);
                    Assert.Equal(0, records[i].CopiesDropped);
                    Assert.True(
                        (records[i].SourceSystem == 100 && records[i].DestinationSystem == 102)
                        || (records[i].SourceSystem == 102 && records[i].DestinationSystem == 100));
                }

                // The first frame of the capture: D102 to D100.
                Assert.Equal(102, records[0].SourceSystem);
                Assert.Equal(100, records[0].DestinationSystem);
            }
            finally
            {
                hub.Stop();
            }
        }

        [Fact]
        public void ThePcapOfTheBufferHoldsTheExactFrames_AndTheirArrivalTimes()
        {
            byte[][] frames = LoadCaptureFrames();
            HubFrameRing ring = new HubFrameRing(200);
            DateTime start = new DateTime(2026, 11, 10, 10, 42, 39, 169, DateTimeKind.Utc);
            for (int i = 0; i < frames.Length; i++)
            {
                ring.Add(start.AddMilliseconds(i), "m", frames[i], HubFrameOutcome.Forwarded, 1, 0);
            }

            using MemoryStream pcap = new MemoryStream();
            HubFrameRing.WritePcap(ring.GetAfter(0, 1000), pcap);
            byte[] bytes = pcap.ToArray();

            // Classic pcap header: magic A1B2C3D4 little-endian, version 2.4, link type 1.
            Assert.Equal(0xA1B2C3D4u, BitConverter.ToUInt32(bytes, 0));
            Assert.Equal(2, BitConverter.ToUInt16(bytes, 4));
            Assert.Equal(4, BitConverter.ToUInt16(bytes, 6));
            Assert.Equal(1u, BitConverter.ToUInt32(bytes, 20));

            int at = 24;
            for (int i = 0; i < frames.Length; i++)
            {
                uint seconds = BitConverter.ToUInt32(bytes, at);
                uint micros = BitConverter.ToUInt32(bytes, at + 4);
                int captured = (int)BitConverter.ToUInt32(bytes, at + 8);
                int original = (int)BitConverter.ToUInt32(bytes, at + 12);
                DateTime expected = start.AddMilliseconds(i);
                long expectedTicks = expected.Ticks - DateTime.UnixEpoch.Ticks;

                Assert.Equal((uint)(expectedTicks / TimeSpan.TicksPerSecond), seconds);
                Assert.Equal((uint)((expectedTicks % TimeSpan.TicksPerSecond) / 10), micros);
                Assert.Equal(frames[i].Length, captured);
                Assert.Equal(frames[i].Length, original);
                Assert.Equal(frames[i], bytes.AsSpan(at + 16, captured).ToArray());
                at += 16 + captured;
            }

            Assert.Equal(bytes.Length, at);
        }

        [Fact]
        public void AFullBufferDropsTheOldest_AndTheNumbersShowTheGap()
        {
            HubFrameRing ring = new HubFrameRing(3);
            for (int i = 0; i < 5; i++)
            {
                ring.Add(DateTime.UtcNow, "m", new byte[] { (byte)i }, HubFrameOutcome.Forwarded, 0, 0);
            }

            HubFrameRecord[] all = ring.GetAfter(0, 10);
            Assert.Equal(3, all.Length);
            Assert.Equal(3, all[0].Sequence);
            Assert.Equal(5, all[2].Sequence);
            Assert.Equal(5, ring.LastSequence);

            HubFrameRecord[] after4 = ring.GetAfter(4, 10);
            Assert.Single(after4);
            Assert.Equal(5, after4[0].Sequence);

            HubFrameRecord[] firstTwo = ring.GetAfter(0, 2);
            Assert.Equal(3, firstTwo[0].Sequence);
            Assert.Equal(4, firstTwo[1].Sequence);

            ring.Clear();
            Assert.Empty(ring.GetAfter(0, 10));
            ring.Add(DateTime.UtcNow, "m", new byte[] { 9 }, HubFrameOutcome.Forwarded, 0, 0);
            Assert.Equal(6, ring.GetAfter(0, 10)[0].Sequence);
        }

        [Fact]
        public void AFrameThatIsNotNd_IsKeptWithItsBytes_ButNoSystems()
        {
            HubFrameRing ring = new HubFrameRing(4);
            byte[] junk = { 1, 2, 3, 4, 5 };

            HubFrameRecord record = ring.Add(DateTime.UtcNow, "m", junk, HubFrameOutcome.Forwarded, 0, 0);

            Assert.False(record.IsNdFrame);
            Assert.Equal(0, record.SourceSystem);
            Assert.Equal(junk, record.Frame);
        }

        [Fact]
        public void TheMemberListCountsEachMembersFrames()
        {
            HubServer hub = new HubServer(IPAddress.Loopback, 0);
            hub.Start();
            try
            {
                using Machine a = new Machine(hub.Port);
                using Machine b = new Machine(hub.Port);
                WaitForMembers(hub, 2);

                a.Send(new byte[] { 1, 2, 3 });
                Assert.NotNull(b.Receive(5000));

                SpinWait.SpinUntil(() => Total(hub.GetMembers()) == 1, 5000);
                HubMemberInfo[] members = hub.GetMembers();
                Assert.Equal(2, members.Length);
                Assert.Equal(1, Total(members));
                for (int i = 0; i < members.Length; i++)
                {
                    Assert.False(members[i].IsHub);
                    Assert.False(members[i].IsUplink);
                    Assert.Equal(members[i].FramesFromMember == 1 ? 0 : 1, members[i].FramesToMember);
                }
            }
            finally
            {
                hub.Stop();
            }
        }

        [Fact]
        public async Task TheServiceRunsTheHub_KeepsItsLog_AndItsFrames()
        {
            XmsgHubService service = new XmsgHubService(new XmsgHubOptions { Port = 0, BindAddress = "127.0.0.1", FrameBufferSize = 16 });
            using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await service.StartAsync(timeout.Token);
            try
            {
                IPEndPoint endPoint = await service.WaitUntilListeningAsync(timeout.Token);
                Assert.Equal(XmsgHubState.Running, service.State);
                Assert.Equal(IPAddress.Loopback, endPoint.Address);

                using Machine a = new Machine(endPoint.Port);
                using Machine b = new Machine(endPoint.Port);
                SpinWait.SpinUntil(() => service.GetMembers().Length == 2, 5000);

                byte[] frame = LoadCaptureFrames()[0];
                a.Send(frame);
                Assert.Equal(frame, b.Receive(5000));
                SpinWait.SpinUntil(() => service.Frames.LastSequence == 1, 5000);

                Assert.Equal(frame, service.Frames.GetAfter(0, 10)[0].Frame);
                Assert.Contains(service.LogLines.GetAfter(0, 100), line => line.Message.Contains("member joined", StringComparison.Ordinal));
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }

            Assert.Equal(XmsgHubState.Stopped, service.State);
            Assert.Null(service.LocalEndPoint);
            Assert.Contains(service.LogLines.GetAfter(0, 100), line => line.Message.StartsWith("stopped.", StringComparison.Ordinal));

            // The buffers outlive the run, so a stop and start from the admin UI keeps them.
            Assert.Equal(1, service.Frames.LastSequence);
        }

        [Fact]
        public async Task APortInUse_LeavesTheServiceFailed_WithTheReason_AndDoesNotThrow()
        {
            TcpListener holder = new TcpListener(IPAddress.Loopback, 0);
            holder.Start();
            try
            {
                int taken = ((IPEndPoint)holder.LocalEndpoint).Port;
                XmsgHubService service = new XmsgHubService(new XmsgHubOptions { Port = taken, BindAddress = "127.0.0.1" });

                await service.StartAsync(CancellationToken.None);
                await Assert.ThrowsAsync<SocketException>(() => service.WaitUntilListeningAsync(CancellationToken.None));
                await service.StopAsync(CancellationToken.None);

                Assert.Equal(XmsgHubState.Failed, service.State);
                Assert.Contains(taken.ToString(System.Globalization.CultureInfo.InvariantCulture), service.FailureReason, StringComparison.Ordinal);
                Assert.Contains(service.LogLines.GetAfter(0, 10), line => line.Message.StartsWith("FAILED:", StringComparison.Ordinal));
            }
            finally
            {
                holder.Stop();
            }
        }

        [Fact]
        public async Task ABadUplink_LeavesTheServiceFailed()
        {
            XmsgHubService service = new XmsgHubService(new XmsgHubOptions { Port = 0, Uplink = "no-port-here" });

            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(XmsgHubState.Failed, service.State);
            Assert.Contains("Uplink", service.FailureReason, StringComparison.Ordinal);
        }

        private static long Total(HubMemberInfo[] members)
        {
            long n = 0;
            for (int i = 0; i < members.Length; i++)
            {
                n += members[i].FramesFromMember;
            }

            return n;
        }

        private static void WaitForMembers(HubServer hub, int count)
        {
            Assert.True(SpinWait.SpinUntil(() => hub.MemberCount == count, 5000), "members did not join");
        }

        private static ushort SourceSystem(byte[] frame)
        {
            // An ND MAC address is 08 00 26 then the system number, low byte first.
            return (ushort)(frame[9] | (frame[10] << 8));
        }

        private static byte[][] LoadCaptureFrames()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "TestData", "list-names-2026-11-10.hex");
            string[] lines = File.ReadAllLines(path);
            List<byte[]> frames = new List<byte[]>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length > 0)
                {
                    frames.Add(Convert.FromHexString(lines[i].Trim()));
                }
            }

            return frames.ToArray();
        }

        /// <summary>
        /// A test member that joins the way a machine does (role 1).
        /// </summary>
        private sealed class Machine : IDisposable
        {
            private readonly TcpClient _client;
            private readonly NetworkStream _stream;

            public Machine(int port)
            {
                _client = new TcpClient();
                _client.Connect(IPAddress.Loopback, port);
                _client.NoDelay = true;
                _stream = _client.GetStream();
                byte[] hello = { Magic[0], Magic[1], Magic[2], Magic[3], HubServer.RoleMachine };
                _stream.Write(hello, 0, hello.Length);
                byte[] peer = new byte[5];
                Assert.True(ReadExactly(peer, 5, 5000));
            }

            public void Send(byte[] frame)
            {
                byte[] buffer = new byte[2 + frame.Length];
                buffer[0] = (byte)(frame.Length >> 8);
                buffer[1] = (byte)frame.Length;
                Buffer.BlockCopy(frame, 0, buffer, 2, frame.Length);
                _stream.Write(buffer, 0, buffer.Length);
            }

            public byte[]? Receive(int timeoutMs)
            {
                byte[] prefix = new byte[2];
                if (!ReadExactly(prefix, 2, timeoutMs))
                {
                    return null;
                }

                byte[] body = new byte[(prefix[0] << 8) | prefix[1]];
                return ReadExactly(body, body.Length, timeoutMs) ? body : null;
            }

            public void Dispose()
            {
                _client.Dispose();
            }

            private bool ReadExactly(byte[] buffer, int count, int timeoutMs)
            {
                _client.ReceiveTimeout = timeoutMs;
                int got = 0;
                try
                {
                    while (got < count)
                    {
                        int n = _stream.Read(buffer, got, count - got);
                        if (n <= 0)
                        {
                            return false;
                        }

                        got += n;
                    }
                }
                catch (IOException)
                {
                    return false;
                }

                return true;
            }
        }
    }
}
