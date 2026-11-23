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

            TestContext.Out.WriteLine($"{Clients} clients, {Clients * Rounds * 2} sector transfers in {clock.ElapsedMilliseconds} ms");

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
