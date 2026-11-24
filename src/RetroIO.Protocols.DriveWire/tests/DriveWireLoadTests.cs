//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RetroIO.Protocols.DriveWire.Drives;
using RetroIO.Protocols.DriveWire.Server;

namespace RetroIO.Protocols.DriveWire.Tests;

/// <summary>
/// Many DriveWire machines at once against one server (plan Phase 14: load and memory).
/// </summary>
/// <remarks>
/// Each client connects over TCP and writes then reads back its own sectors of one shared drive,
/// many times, all at the same time. Every write must be acknowledged, every read must give back
/// exactly what that client wrote (no sector from another client), and once every client has hung
/// up no session may be left in the server.
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Load")]
[Explicit("Load test; runs many clients at once")]
public sealed class DriveWireLoadTests
{
    private const int Clients = 32;
    private const int Rounds = 50;
    private const int SectorsPerClient = 4;

    [Test]
    public async Task ManyClientsAtOnce_WriteAndReadBack_AllCorrect_AndNoSessionIsLeft()
    {
        DriveWireServerOptions options = new DriveWireServerOptions { BindAddress = "127.0.0.1", Port = 0 };
        using DriveWireDriveTable drives = new DriveWireDriveTable();
        drives.Mount(0, new MemoryDrive(Clients * SectorsPerClient));
        using DriveWireServerService server = new DriveWireServerService(options, drives, new DriveWireServerStatistics(), null, null);

        await server.StartAsync(CancellationToken.None);
        try
        {
            IPEndPoint endPoint = await server.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            long allocatedBefore = GC.GetTotalAllocatedBytes(true);
            Stopwatch clock = Stopwatch.StartNew();
            Task<string>[] clients = new Task<string>[Clients];
            for (int i = 0; i < Clients; i++)
            {
                int client = i;
                clients[i] = Task.Run(() => RunClient(endPoint, client));
            }

            string[] results = await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(120));
            clock.Stop();

            for (int i = 0; i < results.Length; i++)
            {
                Assert.That(results[i], Is.Empty, "client " + i);
            }

            TestContext.Out.WriteLine($"{Clients} clients, {Clients * Rounds * 2} sector transfers in {clock.ElapsedMilliseconds} ms"); TestContext.Out.WriteLine($"allocated per transfer, server and test clients together: {(GC.GetTotalAllocatedBytes(true) - allocatedBefore) / (Clients * Rounds * 2)} bytes");

            // A session is removed when its connection's read loop sees the hang-up, a moment later.
            Stopwatch gone = Stopwatch.StartNew();
            while (server.GetSessions().Length != 0 && gone.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(50);
            }

            Assert.That(server.GetSessions().Length, Is.EqualTo(0), "every client hung up, so no session may be left");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Measurement for the memory item of plan Phase 14: the same load 150 times on one server, printing
    /// the memory in use after a full garbage collection, and the sessions, after each batch.
    /// </summary>
    [Test]
    [Explicit("Measurement; prints memory per batch")]
    public async Task Measure_MemoryOverManyBatches()
    {
        DriveWireServerOptions options = new DriveWireServerOptions { BindAddress = "127.0.0.1", Port = 0 };
        using DriveWireDriveTable drives = new DriveWireDriveTable();
        drives.Mount(0, new MemoryDrive(Clients * SectorsPerClient));
        using DriveWireServerService server = new DriveWireServerService(options, drives, new DriveWireServerStatistics(), null, null);
        await server.StartAsync(CancellationToken.None);
        try
        {
            IPEndPoint endPoint = await server.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            for (int batch = 0; batch < 150; batch++)
            {
                Task<string>[] clients = new Task<string>[Clients];
                for (int i = 0; i < Clients; i++)
                {
                    int client = i;
                    clients[i] = Task.Run(() => RunClient(endPoint, client));
                }

                await Task.WhenAll(clients);
                while (server.GetSessions().Length != 0)
                {
                    await Task.Delay(20);
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                TestContext.Out.WriteLine($"batch {batch}: {GC.GetTotalMemory(false)} bytes in use, {Process.GetCurrentProcess().HandleCount} handles");

                // With DW_MEASURE_DIR set, pause after batches 20 and the last, so a memory dump can be
                // taken from outside (dotnet-gcdump); the test goes on when the file "go<batch>" appears.
                string? pauseDir = Environment.GetEnvironmentVariable("DW_MEASURE_DIR");
                if (pauseDir != null && (batch == 20 || batch == 149))
                {
                    File.WriteAllText(Path.Combine(pauseDir, "ready" + batch), Environment.ProcessId.ToString());
                    while (!File.Exists(Path.Combine(pauseDir, "go" + batch)))
                    {
                        await Task.Delay(200);
                    }
                }
            }
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// One client: write a sector of its own, read it back, <see cref="Rounds"/> times. Returns an
    /// empty string when every step worked, else what went wrong.
    /// </summary>
    private static async Task<string> RunClient(IPEndPoint endPoint, int client)
    {
        using TcpClient tcp = new TcpClient();
        await tcp.ConnectAsync(endPoint);
        NetworkStream stream = tcp.GetStream();

        for (int round = 0; round < Rounds; round++)
        {
            uint lsn = (uint)(client * SectorsPerClient + round % SectorsPerClient);
            byte[] data = new byte[256];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(client * 7 + round * 3 + i);
            }

            byte written = await DriveWireTestClient.WriteAsync(stream, 0, lsn, data);
            if (written != 0)
            {
                return "round " + round + ": write answered " + written;
            }

            (byte status, byte[] back) = await DriveWireTestClient.ReadExtendedAsync(stream, 0, lsn);
            if (status != 0)
            {
                return "round " + round + ": read answered " + status;
            }

            if (!back.AsSpan().SequenceEqual(data))
            {
                return "round " + round + ": sector " + lsn + " came back with other data";
            }
        }

        return string.Empty;
    }
}
