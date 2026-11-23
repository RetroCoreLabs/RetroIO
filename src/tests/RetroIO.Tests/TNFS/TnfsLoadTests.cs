//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Protocols.Tnfs.Server;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Tests.TNFS;

/// <summary>
/// Many TNFS clients at once against one in-process server (plan Phase 14: load and memory).
/// </summary>
/// <remarks>
/// Every client mounts, unmounts and mounts again, many times, all at the same time. A MOUNT that
/// is resent because its answer was late makes a second session that nobody unmounts. tnfsd does
/// the same (session.c tnfs_mount allocates a new session per MOUNT) and throws such sessions out
/// after SESSION_TIMEOUT seconds without contact; RetroIO's SessionManager does the same on its
/// one-minute cleanup pass. So the rule tested is: once the session timeout has passed, no session
/// is left. A session that stayed for ever would be memory never given back on a server that runs
/// for months. Takes up to about a minute because of the cleanup pass, so it runs only when asked.
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Load")]
[Explicit("Load test; up to about a minute while the session cleanup pass runs")]
public sealed class TnfsLoadTests
{
    private const int Clients = 32;
    private const int RoundsPerClient = 50;

    // What the last failing client saw, for the assert message.
    private static volatile string LastFailure = string.Empty;

    // How many requests were sent more than once.
    private static int Resends;

    [Test]
    public async Task ManyClientsAtOnce_MountAndUnmountRepeatedly_AllAnswered_AndNoSessionIsLeft()
    {
        int port;
        using (Socket probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }

        MockFileSystemBackend files = new MockFileSystemBackend();
        files.AddDirectory("/tmp/tnfs");

        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IFileSystemBackend>(files);
        builder.Services.AddTnfsServer(o =>
        {
            o.EnableUdp = true;
            o.EnableTcp = false;
            o.UdpPort = port;
            o.RootDirectory = "/tmp/tnfs";
            o.SessionTimeoutSeconds = 1;
        });
        using IHost host = builder.Build();
        await host.StartAsync();

        try
        {
            SessionManager sessions = host.Services.GetRequiredService<SessionManager>();
            Stopwatch clock = Stopwatch.StartNew();
            Task<int>[] clients = new Task<int>[Clients];
            for (int i = 0; i < Clients; i++)
            {
                clients[i] = Task.Run(() => RunClient(port));
            }

            int[] answered = await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(120));
            clock.Stop();

            int total = 0;
            for (int i = 0; i < answered.Length; i++)
            {
                Assert.That(answered[i], Is.EqualTo(RoundsPerClient * 2), "client " + i + ": every MOUNT and UMOUNT gets an OK answer; " + LastFailure);
                total += answered[i];
            }

            TestContext.Out.WriteLine($"{Clients} clients, {total} requests answered in {clock.ElapsedMilliseconds} ms ({total * 1000L / Math.Max(1, clock.ElapsedMilliseconds)} a second)");
            TestContext.Out.WriteLine($"sessions left right after the load: {sessions.SessionCount}, requests resent: {Resends}");

            // The cleanup pass runs once a minute; give it one full pass after the timeout.
            Stopwatch cleanup = Stopwatch.StartNew();
            while (sessions.SessionCount != 0 && cleanup.Elapsed < TimeSpan.FromSeconds(75))
            {
                await Task.Delay(500);
            }

            Assert.That(sessions.SessionCount, Is.EqualTo(0), "no session may be left once the session timeout has passed; resends " + Resends);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// Measurement for the memory item of plan Phase 14: the same load 30 times on one server,
    /// printing the memory in use after a full garbage collection, and the sessions, after each
    /// batch. Sessions left by resent MOUNTs go on the one-minute cleanup pass, so the count can
    /// rise between passes and must fall again.
    /// </summary>
    /// <returns>The test.</returns>
    [Test]
    [Explicit("Measurement; prints memory per batch")]
    public async Task Measure_MemoryOverManyBatches()
    {
        int port;
        using (Socket probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }

        MockFileSystemBackend files = new MockFileSystemBackend();
        files.AddDirectory("/tmp/tnfs");
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IFileSystemBackend>(files);
        builder.Services.AddTnfsServer(o =>
        {
            o.EnableUdp = true;
            o.EnableTcp = false;
            o.UdpPort = port;
            o.RootDirectory = "/tmp/tnfs";
            o.SessionTimeoutSeconds = 1;
        });
        using IHost host = builder.Build();
        await host.StartAsync();
        try
        {
            SessionManager sessions = host.Services.GetRequiredService<SessionManager>();
            for (int batch = 0; batch < 30; batch++)
            {
                Task<int>[] clients = new Task<int>[Clients];
                for (int i = 0; i < Clients; i++)
                {
                    clients[i] = Task.Run(() => RunClient(port));
                }

                int[] answered = await Task.WhenAll(clients);
                for (int i = 0; i < answered.Length; i++)
                {
                    Assert.That(answered[i], Is.EqualTo(RoundsPerClient * 2), "batch " + batch + ", client " + i + "; " + LastFailure);
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                TestContext.Out.WriteLine($"batch {batch}: {GC.GetTotalMemory(false)} bytes in use, sessions {sessions.SessionCount}");
            }

            Stopwatch cleanup = Stopwatch.StartNew();
            while (sessions.SessionCount != 0 && cleanup.Elapsed < TimeSpan.FromSeconds(75))
            {
                await Task.Delay(500);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            TestContext.Out.WriteLine($"after cleanup: {GC.GetTotalMemory(false)} bytes in use, sessions {sessions.SessionCount}");
            Assert.That(sessions.SessionCount, Is.EqualTo(0));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// One client: MOUNT then UMOUNT, <see cref="RoundsPerClient"/> times. Returns how many
    /// requests were answered with status OK.
    /// </summary>
    private static int RunClient(int port)
    {
        using UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        udp.Connect(IPAddress.Loopback, port);
        udp.Client.ReceiveTimeout = 500;
        byte sequence = 0;
        int ok = 0;

        for (int round = 0; round < RoundsPerClient; round++)
        {
            // MOUNT: session 0, sequence, command, version, mount point "/", no user, no password.
            byte[]? mounted = Ask(udp, new byte[] { 0, 0, 0, TnfsConstants.TNFS_MOUNT, TnfsConstants.PROTOVERSION_LSB, TnfsConstants.PROTOVERSION_MSB, (byte)'/', 0, 0, 0 }, ref sequence);
            if (mounted == null || mounted[4] != 0)
            {
                LastFailure = "round " + round + " MOUNT: " + (mounted == null ? "no answer" : Convert.ToHexString(mounted));
                return ok;
            }

            ok++;

            // UMOUNT: the session number the server gave.
            byte[]? unmounted = Ask(udp, new byte[] { mounted[0], mounted[1], 0, TnfsConstants.TNFS_UMOUNT }, ref sequence);
            if (unmounted == null || unmounted[4] != 0)
            {
                LastFailure = "round " + round + " UMOUNT: " + (unmounted == null ? "no answer" : Convert.ToHexString(unmounted));
                return ok;
            }

            ok++;
        }

        return ok;
    }

    /// <summary>
    /// Sends a request and waits for the answer with the same sequence number, resending with
    /// the same number when no answer comes (as a TNFS client does). Null after 5 seconds.
    /// </summary>
    /// <remarks>
    /// On Windows a packet sent to a port nobody has opened yet comes back at once as "connection
    /// reset" (an ICMP port-unreachable), not as a timeout. The server opens its port a moment after
    /// the host has started, so the client waits a little before sending again rather than burning
    /// its tries in a few milliseconds.
    /// </remarks>
    private static byte[]? Ask(UdpClient udp, byte[] request, ref byte sequence)
    {
        request[2] = sequence++;
        IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
        Stopwatch waited = Stopwatch.StartNew();
        bool first = true;
        while (waited.ElapsedMilliseconds < 5000)
        {
            if (!first)
            {
                Interlocked.Increment(ref Resends);
            }

            first = false;
            udp.Send(request, request.Length);
            try
            {
                while (true)
                {
                    byte[] answer = udp.Receive(ref from);
                    if (answer.Length >= 5 && answer[2] == request[2] && answer[3] == request[3])
                    {
                        return answer;
                    }
                }
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                // The port is not open yet: wait, then send the same request again.
                Thread.Sleep(50);
            }
            catch (SocketException)
            {
                // No answer within the receive timeout: send the same request again.
            }
        }

        return null;
    }
}
