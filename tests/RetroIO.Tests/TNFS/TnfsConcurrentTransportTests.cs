//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Protocols.Tnfs.Server;
using RetroIO.Protocols.Tnfs.Server.Handlers;
using RetroIO.Protocols.Tnfs.Server.Transport;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Tests.TNFS;

/// <summary>
/// Tests verifying UDP and TCP transports can operate concurrently on the same server.
/// </summary>
[TestFixture]
[Category("Integration")]
[Category("RealNetwork")]
[Category("Concurrent")]
public class TnfsConcurrentTransportTests
{
    private const int TEST_PORT = TnfsTestConfig.AUTOMATED_TEST_PORT_UDP; // Use port 16666 to avoid conflicts with manually started TNFSD on 16384
    private IHost _host = null!;
    private string _testRoot = null!;

    [OneTimeSetUp]
    public async Task SetupServer()
    {
        // Create test directory structure
        _testRoot = Path.Combine(Path.GetTempPath(), "tnfs_concurrent_test");
        Directory.CreateDirectory(_testRoot);
        Directory.CreateDirectory(Path.Combine(_testRoot, "shared"));
        File.WriteAllText(Path.Combine(_testRoot, "shared", "data.txt"), "Shared Data");

        // Build host with BOTH UDP and TCP transports on same port
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<IFileSystemBackend, LocalFileSystemBackend>();

                services.AddSingleton<SessionManager>(sp =>
                    new SessionManager(
                        sp.GetRequiredService<ILogger<SessionManager>>(),
                        TimeSpan.FromMinutes(10),
                        maxSessionsPerIP: 200)); // High limit for concurrent load testing

                services.AddSingleton<SessionCommandHandler>(sp =>
                    new SessionCommandHandler(
                        sp.GetRequiredService<SessionManager>(),
                        sp.GetRequiredService<ILogger<SessionCommandHandler>>(),
                        _testRoot,
                        sp.GetRequiredService<IFileSystemBackend>()));

                services.AddSingleton<DirectoryCommandHandler>(sp =>
                    new DirectoryCommandHandler(
                        sp.GetRequiredService<SessionManager>(),
                        sp.GetRequiredService<ILogger<DirectoryCommandHandler>>(),
                        sp.GetRequiredService<IFileSystemBackend>()));

                services.AddSingleton<FileCommandHandler>(sp =>
                    new FileCommandHandler(
                        sp.GetRequiredService<SessionManager>(),
                        sp.GetRequiredService<ILogger<FileCommandHandler>>(),
                        sp.GetRequiredService<IFileSystemBackend>()));

                // Both transports on same port
                services.AddSingleton<ITransport>(sp =>
                    new UdpTransport(TEST_PORT,
                        sp.GetRequiredService<ILogger<UdpTransport>>()));

                services.AddSingleton<ITransport>(sp =>
                    new TcpTransport(TEST_PORT,
                        sp.GetRequiredService<ILogger<TcpTransport>>()));

                services.AddHostedService<RetroIO.Protocols.Tnfs.Server.TnfsServerService>();

                services.AddLogging(builder =>
                {
                    builder.AddConsole();
                    builder.SetMinimumLevel(LogLevel.Warning);
                });
            })
            .Build();

        await _host.StartAsync();
        await Task.Delay(500); // Give server time to start listening
    }

    [OneTimeTearDown]
    public async Task TeardownServer()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [Test]
    public async Task Test_UDP_And_TCP_Mount_Concurrently()
    {
        // Create UDP client
        var udpClient = new UdpClient();
        udpClient.Connect(IPAddress.Loopback, TEST_PORT);

        // Create TCP client
        var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, TEST_PORT);
        var tcpStream = tcpClient.GetStream();

        // Create MOUNT requests
        var mountRequest = CreateMountRequest("/");

        // Send both concurrently
        var udpTask = Task.Run(async () =>
        {
            await udpClient.SendAsync(mountRequest, mountRequest.Length);
            var result = await udpClient.ReceiveAsync();
            return result.Buffer;
        });

        var tcpTask = Task.Run(async () =>
        {
            await SendTcpMessageAsync(tcpStream, mountRequest);
            return await ReceiveTcpMessageAsync(tcpStream);
        });

        var results = await Task.WhenAll(udpTask, tcpTask);

        // Both should succeed
        Assert.That((TnfsReturnCode)results[0][4], Is.EqualTo(TnfsReturnCode.SUCCESS), "UDP MOUNT failed");
        Assert.That((TnfsReturnCode)results[1][4], Is.EqualTo(TnfsReturnCode.SUCCESS), "TCP MOUNT failed");

        ushort udpSessionId = BitConverter.ToUInt16(results[0], 0);
        ushort tcpSessionId = BitConverter.ToUInt16(results[1], 0);

        TestContext.Out.WriteLine($"✅ Concurrent MOUNT: UDP Session=0x{udpSessionId:X4}, TCP Session=0x{tcpSessionId:X4}");

        udpClient.Dispose();
        tcpClient.Dispose();
    }

    [Test]
    public async Task Test_10_UDP_10_TCP_Clients_Concurrent()
    {
        var tasks = new Task[20];

        // Create 10 UDP clients
        for (int i = 0; i < 10; i++)
        {
            int clientNum = i;
            tasks[i] = Task.Run(async () =>
            {
                using var udp = new UdpClient();
                udp.Connect(IPAddress.Loopback, TEST_PORT);

                var mountReq = CreateMountRequest("/");
                await udp.SendAsync(mountReq, mountReq.Length);
                var mountResp = await udp.ReceiveAsync();

                Assert.That((TnfsReturnCode)mountResp.Buffer[4], Is.EqualTo(TnfsReturnCode.SUCCESS),
                    $"UDP client {clientNum} MOUNT failed");
            });
        }

        // Create 10 TCP clients
        for (int i = 0; i < 10; i++)
        {
            int clientNum = i;
            tasks[10 + i] = Task.Run(async () =>
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, TEST_PORT);
                var stream = tcp.GetStream();

                var mountReq = CreateMountRequest("/");
                await SendTcpMessageAsync(stream, mountReq);
                var mountResp = await ReceiveTcpMessageAsync(stream);

                Assert.That((TnfsReturnCode)mountResp[4], Is.EqualTo(TnfsReturnCode.SUCCESS),
                    $"TCP client {clientNum} MOUNT failed");
            });
        }

        await Task.WhenAll(tasks);

        TestContext.Out.WriteLine("✅ 10 UDP + 10 TCP clients handled concurrently");
    }

    [Test]
    public async Task Test_Mixed_Protocol_Directory_Operations()
    {
        // UDP client performs OPENDIR
        var udpClient = new UdpClient();
        udpClient.Connect(IPAddress.Loopback, TEST_PORT);

        var udpMountReq = CreateMountRequest("/");
        await udpClient.SendAsync(udpMountReq, udpMountReq.Length);
        var udpMountResp = await udpClient.ReceiveAsync();
        ushort udpSessionId = BitConverter.ToUInt16(udpMountResp.Buffer, 0);

        var udpOpenReq = CreateOpenDirRequest(udpSessionId, "/shared");
        await udpClient.SendAsync(udpOpenReq, udpOpenReq.Length);
        var udpOpenResp = await udpClient.ReceiveAsync();

        Assert.That((TnfsReturnCode)udpOpenResp.Buffer[4], Is.EqualTo(TnfsReturnCode.SUCCESS));
        byte udpHandle = udpOpenResp.Buffer[5];

        // TCP client performs same operation concurrently
        var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, TEST_PORT);
        var tcpStream = tcpClient.GetStream();

        var tcpMountReq = CreateMountRequest("/");
        await SendTcpMessageAsync(tcpStream, tcpMountReq);
        var tcpMountResp = await ReceiveTcpMessageAsync(tcpStream);
        ushort tcpSessionId = BitConverter.ToUInt16(tcpMountResp, 0);

        var tcpOpenReq = CreateOpenDirRequest(tcpSessionId, "/shared");
        await SendTcpMessageAsync(tcpStream, tcpOpenReq);
        var tcpOpenResp = await ReceiveTcpMessageAsync(tcpStream);

        Assert.That((TnfsReturnCode)tcpOpenResp[4], Is.EqualTo(TnfsReturnCode.SUCCESS));
        byte tcpHandle = tcpOpenResp[5];

        // Both protocols can operate on same directory
        TestContext.Out.WriteLine($"✅ Mixed protocols: UDP handle={udpHandle}, TCP handle={tcpHandle}");

        udpClient.Dispose();
        tcpClient.Dispose();
    }

    [Test]
    public async Task Test_Transport_Isolation()
    {
        // Create UDP session
        var udpClient = new UdpClient();
        udpClient.Connect(IPAddress.Loopback, TEST_PORT);

        var udpMountReq = CreateMountRequest("/");
        await udpClient.SendAsync(udpMountReq, udpMountReq.Length);
        var udpMountResp = await udpClient.ReceiveAsync();
        ushort udpSessionId = BitConverter.ToUInt16(udpMountResp.Buffer, 0);

        // Create TCP session
        var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, TEST_PORT);
        var tcpStream = tcpClient.GetStream();

        var tcpMountReq = CreateMountRequest("/");
        await SendTcpMessageAsync(tcpStream, tcpMountReq);
        var tcpMountResp = await ReceiveTcpMessageAsync(tcpStream);
        ushort tcpSessionId = BitConverter.ToUInt16(tcpMountResp, 0);

        // Sessions should be independent
        Assert.That(udpSessionId, Is.Not.EqualTo(tcpSessionId), "Sessions should be unique across transports");

        // Send UDP request, verify response comes via UDP
        var udpOpenReq = CreateOpenDirRequest(udpSessionId, "/");
        await udpClient.SendAsync(udpOpenReq, udpOpenReq.Length);
        var udpOpenResp = await udpClient.ReceiveAsync();
        Assert.That((TnfsReturnCode)udpOpenResp.Buffer[4], Is.EqualTo(TnfsReturnCode.SUCCESS));

        // Send TCP request, verify response comes via TCP
        var tcpOpenReq = CreateOpenDirRequest(tcpSessionId, "/");
        await SendTcpMessageAsync(tcpStream, tcpOpenReq);
        var tcpOpenResp = await ReceiveTcpMessageAsync(tcpStream);
        Assert.That((TnfsReturnCode)tcpOpenResp[4], Is.EqualTo(TnfsReturnCode.SUCCESS));

        TestContext.Out.WriteLine("✅ Transport isolation verified: responses route correctly");

        udpClient.Dispose();
        tcpClient.Dispose();
    }

    [Test]
    public async Task Test_High_Load_Mixed_Transports()
    {
        // Test Expectations:
        // - Server starts on port 16666 with root directory at /tmp/tnfs_concurrent_test
        // - Directory structure: /shared/data.txt
        // - 100 concurrent clients (50 UDP + 50 TCP) each perform: MOUNT → OPENDIR /shared → CLOSEDIR
        // - All clients should succeed without EACCES errors
        // - SessionManager allows up to 200 sessions per IP to support high concurrency
        // - Test should complete in under 10 seconds

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tasks = new Task[100];

        // 50 UDP clients, 50 TCP clients
        for (int i = 0; i < 100; i++)
        {
            int clientNum = i;
            bool isUdp = i < 50;

            tasks[i] = Task.Run(async () =>
            {
                if (isUdp)
                {
                    using var udp = new UdpClient();
                    udp.Connect(IPAddress.Loopback, TEST_PORT);

                    // MOUNT
                    var mountReq = CreateMountRequest("/");
                    await udp.SendAsync(mountReq, mountReq.Length);
                    var mountResp = await udp.ReceiveAsync();
                    ushort sessionId = BitConverter.ToUInt16(mountResp.Buffer, 0);

                    // OPENDIR
                    var openReq = CreateOpenDirRequest(sessionId, "/shared");
                    await udp.SendAsync(openReq, openReq.Length);
                    var openResp = await udp.ReceiveAsync();

                    if (openResp.Buffer.Length < 6 || (TnfsReturnCode)openResp.Buffer[4] != TnfsReturnCode.SUCCESS)
                        throw new Exception($"OPENDIR failed: {(openResp.Buffer.Length >= 5 ? ((TnfsReturnCode)openResp.Buffer[4]).ToString() : "invalid response")}");

                    byte handle = openResp.Buffer[5];

                    // CLOSEDIR
                    var closeReq = CreateCloseDirRequest(sessionId, handle);
                    await udp.SendAsync(closeReq, closeReq.Length);
                    await udp.ReceiveAsync();
                }
                else
                {
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(IPAddress.Loopback, TEST_PORT);
                    var stream = tcp.GetStream();

                    // MOUNT
                    var mountReq = CreateMountRequest("/");
                    await SendTcpMessageAsync(stream, mountReq);
                    var mountResp = await ReceiveTcpMessageAsync(stream);
                    ushort sessionId = BitConverter.ToUInt16(mountResp, 0);

                    // OPENDIR
                    var openReq = CreateOpenDirRequest(sessionId, "/shared");
                    await SendTcpMessageAsync(stream, openReq);
                    var openResp = await ReceiveTcpMessageAsync(stream);

                    if (openResp.Length < 6 || (TnfsReturnCode)openResp[4] != TnfsReturnCode.SUCCESS)
                        throw new Exception($"OPENDIR failed: {(openResp.Length >= 5 ? ((TnfsReturnCode)openResp[4]).ToString() : "invalid response")}");

                    byte handle = openResp[5];

                    // CLOSEDIR
                    var closeReq = CreateCloseDirRequest(sessionId, handle);
                    await SendTcpMessageAsync(stream, closeReq);
                    await ReceiveTcpMessageAsync(stream);
                }
            });
        }

        await Task.WhenAll(tasks);
        sw.Stop();

        TestContext.Out.WriteLine($"✅ High load test: 50 UDP + 50 TCP clients in {sw.ElapsedMilliseconds}ms ({sw.ElapsedMilliseconds / 100.0:F2}ms per client)");
        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(10000), "Should complete in under 10 seconds");
    }

    // Helper methods

    private async Task SendTcpMessageAsync(NetworkStream stream, byte[] message)
    {
        ushort length = (ushort)message.Length;
        byte[] lengthBytes = BitConverter.GetBytes(length);
        await stream.WriteAsync(lengthBytes, 0, 2);
        await stream.WriteAsync(message, 0, message.Length);
    }

    private async Task<byte[]> ReceiveTcpMessageAsync(NetworkStream stream)
    {
        byte[] lengthBytes = new byte[2];
        int bytesRead = await stream.ReadAsync(lengthBytes, 0, 2);
        if (bytesRead != 2)
            throw new Exception("Failed to read length prefix");

        ushort length = BitConverter.ToUInt16(lengthBytes, 0);
        byte[] message = new byte[length];
        bytesRead = 0;
        while (bytesRead < length)
        {
            int read = await stream.ReadAsync(message, bytesRead, length - bytesRead);
            if (read == 0)
                throw new Exception("Connection closed");
            bytesRead += read;
        }

        return message;
    }

    private byte[] CreateMountRequest(string path)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write((ushort)0x0000);
        bw.Write((byte)0x00);
        bw.Write(TnfsConstants.TNFS_MOUNT);
        bw.Write((byte)0x01);
        bw.Write((byte)0x00);
        bw.Write(Encoding.ASCII.GetBytes(path));
        bw.Write((byte)0x00);

        return ms.ToArray();
    }

    private byte[] CreateOpenDirRequest(ushort sessionId, string path)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write((byte)0x01);
        bw.Write(TnfsConstants.TNFS_OPENDIR);
        bw.Write(Encoding.ASCII.GetBytes(path));
        bw.Write((byte)0x00);

        return ms.ToArray();
    }

    private byte[] CreateCloseDirRequest(ushort sessionId, byte handle)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write((byte)0x03);
        bw.Write(TnfsConstants.TNFS_CLOSEDIR);
        bw.Write(handle);

        return ms.ToArray();
    }
}
