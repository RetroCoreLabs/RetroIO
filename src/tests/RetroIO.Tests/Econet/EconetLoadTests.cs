//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Many Econet stations at once against one AUN file server (plan Phase 14: load and memory).
/// </summary>
/// <remarks>
/// Each station logs in, loads a file and logs out, many times, all at the same time. Every step
/// must succeed with the right data, and once every station has logged out no session may be
/// left: a session that stayed would be memory never given back on a server that runs for months.
/// AUN takes the station number from the last byte of the sender's address, so station N sends
/// from 127.0.0.N.
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Load")]
[Explicit("Load test; runs many stations at once")]
public sealed class EconetLoadTests
{
    private const int Stations = 16;
    private const int RoundsPerStation = 20;

    [Test]
    public async Task ManyStationsAtOnce_LoginLoadBye_AllSucceed_AndNoSessionIsLeft()
    {
        int port;
        using (Socket probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }

        await using EconetUdpTestServer server = await EconetUdpTestServer.StartAsync(port);

        Stopwatch clock = Stopwatch.StartNew();
        Task<string>[] stations = new Task<string>[Stations];
        for (int i = 0; i < Stations; i++)
        {
            byte station = (byte)(i + 2);
            stations[i] = Task.Run(() => RunStation(port, station));
        }

        string[] results = await Task.WhenAll(stations).WaitAsync(TimeSpan.FromSeconds(180));
        clock.Stop();

        for (int i = 0; i < results.Length; i++)
        {
            Assert.That(results[i], Is.Empty, "station " + (i + 2));
        }

        int steps = Stations * RoundsPerStation * 3;
        TestContext.Out.WriteLine($"{Stations} stations, {steps} steps (login, load, bye) in {clock.ElapsedMilliseconds} ms");
        Assert.That(server.Sessions.GetActiveSessionCount(), Is.EqualTo(0), "every station logged out, so no session may be left");
    }

    /// <summary>
    /// Measurement for the memory item of plan Phase 14: the same load 30 times on one server,
    /// printing the memory in use after a full garbage collection, and the sessions, after each batch.
    /// </summary>
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

        // Warnings and worse only: NUnit keeps every console line in memory, which would be
        // measured as growth (one log line per request, about 250 KB a round).
        await using EconetUdpTestServer server = await EconetUdpTestServer.StartAsync(port, default, Microsoft.Extensions.Logging.LogLevel.Warning);
        for (int batch = 0; batch < 30; batch++)
        {
            Task<string>[] stations = new Task<string>[Stations];
            for (int i = 0; i < Stations; i++)
            {
                byte station = (byte)(i + 2);
                stations[i] = Task.Run(() => RunStation(port, station));
            }

            string[] results = await Task.WhenAll(stations);
            for (int i = 0; i < results.Length; i++)
            {
                Assert.That(results[i], Is.Empty, "batch " + batch + ", station " + (i + 2));
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
            TestContext.Out.WriteLine($"batch {batch}: {GC.GetTotalMemory(false)} bytes in use, heap {info.HeapSizeBytes}, unusable gaps {info.FragmentedBytes}, pinned objects {info.PinnedObjectsCount}, areas gen0/gen1/gen2/large/pinned {info.GenerationInfo[0].SizeAfterBytes}/{info.GenerationInfo[1].SizeAfterBytes}/{info.GenerationInfo[2].SizeAfterBytes}/{info.GenerationInfo[3].SizeAfterBytes}/{info.GenerationInfo[4].SizeAfterBytes}, sessions {server.Sessions.GetActiveSessionCount()}");

            // With MEASURE_DIR set, pause after batches 5 and 29 so a memory dump can be taken from
            // outside (dotnet-gcdump); the test goes on when the file "go<batch>" appears.
            string? pauseDir = Environment.GetEnvironmentVariable("MEASURE_DIR");
            if (pauseDir != null && (batch == 5 || batch == 29))
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(pauseDir, "ready" + batch), Environment.ProcessId.ToString());
                while (!System.IO.File.Exists(System.IO.Path.Combine(pauseDir, "go" + batch)))
                {
                    await Task.Delay(200);
                }
            }
        }
    }

    /// <summary>
    /// One station's rounds. Returns an empty string when every step worked, else what went wrong.
    /// </summary>
    private static string RunStation(int port, byte station)
    {
        using EconetAunTestClient client = new EconetAunTestClient(
            serverPort: port,
            clientPort: 0,
            clientStation: station,
            clientAddress: new IPAddress(new byte[] { 127, 0, 0, station }));

        for (int round = 0; round < RoundsPerStation; round++)
        {
            byte[] login = client.Login("SYST");
            if (login[0] != FileServerOpcodes.LoginSuccessReturnCode)
            {
                return "round " + round + ": login refused " + Convert.ToHexString(login);
            }

            byte[] data = client.LoadFile("HELLO");
            if (!Encoding.ASCII.GetString(data).Contains("Hello from RetroFS Econet", StringComparison.Ordinal))
            {
                return "round " + round + ": HELLO came back wrong (" + data.Length + " bytes)";
            }

            client.Bye();
        }

        return string.Empty;
    }
}
