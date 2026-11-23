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
