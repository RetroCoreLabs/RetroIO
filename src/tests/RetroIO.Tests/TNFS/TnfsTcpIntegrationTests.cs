//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
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
/// Integration tests for TCP transport using real TCP connections.
/// </summary>
[TestFixture]
[Category("Integration")]
[Category("RealNetwork")]
[Category("TCP")]
public class TnfsTcpIntegrationTests
{
    private const int TEST_TCP_PORT = TnfsTestConfig.AUTOMATED_TEST_PORT_TCP; // Use port 16666 to avoid conflicts with manually started TNFSD on 16384
    private IHost _host = null!;
    private string _testRoot = null!;

    [OneTimeSetUp]
    public async Task SetupServer()
    {
        // Create test directory structure
        _testRoot = Path.Combine(Path.GetTempPath(), "tnfs_tcp_test");
        Directory.CreateDirectory(_testRoot);
        Directory.CreateDirectory(Path.Combine(_testRoot, "testdir"));
        File.WriteAllText(Path.Combine(_testRoot, "file1.txt"), "File 1 Content");
        File.WriteAllText(Path.Combine(_testRoot, "file2.txt"), "File 2 Content");
        File.WriteAllText(Path.Combine(_testRoot, "testdir", "nested.txt"), "Nested Content");

        // Build host with TCP transport only
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<IFileSystemBackend, LocalFileSystemBackend>();

                services.AddSingleton<SessionManager>(sp =>
                    new SessionManager(
                        sp.GetRequiredService<ILogger<SessionManager>>(),
                        TimeSpan.FromMinutes(10),
                        maxSessionsPerIP: 50)); // Higher limit for integration tests

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

                // TCP transport only
                services.AddSingleton<ITransport>(sp =>
                    new TcpTransport(TEST_TCP_PORT,
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
    public async Task Test_TCP_Mount_Success()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, TEST_TCP_PORT);
        var stream = client.GetStream();

        // Create MOUNT request
        var request = CreateMountRequest("/");

        // Send with length prefix
        await SendTcpMessageAsync(stream, request);

        // Receive response
        var response = await ReceiveTcpMessageAsync(stream);

        Assert.That((TnfsReturnCode)response[4], Is.EqualTo(TnfsReturnCode.SUCCESS));
        ushort sessionId = BitConverter.ToUInt16(response, 0);
        Assert.That(sessionId, Is.Not.EqualTo(0), "Should have session ID");

        TestContext.Out.WriteLine($"✅ TCP MOUNT successful, Session ID: 0x{sessionId:X4}");
    }

    [Test]
    public async Task Test_TCP_Complete_Directory_Cycle()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, TEST_TCP_PORT);
        var stream = client.GetStream();

        // MOUNT
        var mountReq = CreateMountRequest("/");
        await SendTcpMessageAsync(stream, mountReq);
        var mountResp = await ReceiveTcpMessageAsync(stream);

        Assert.That((TnfsReturnCode)mountResp[4], Is.EqualTo(TnfsReturnCode.SUCCESS));
        ushort sessionId = BitConverter.ToUInt16(mountResp, 0);

        // OPENDIR
        var openReq = CreateOpenDirRequest(sessionId, "/testdir");
        await SendTcpMessageAsync(stream, openReq);
        var openResp = await ReceiveTcpMessageAsync(stream);

        Assert.That((TnfsReturnCode)openResp[4], Is.EqualTo(TnfsReturnCode.SUCCESS));
        byte handle = openResp[5];

        TestContext.Out.WriteLine($"Directory opened with handle: {handle}");

        // READDIR
        var readReq = CreateReadDirRequest(sessionId, handle);
        await SendTcpMessageAsync(stream, readReq);
        var readResp = await ReceiveTcpMessageAsync(stream);

        Assert.That((TnfsReturnCode)readResp[4], Is.EqualTo(TnfsReturnCode.SUCCESS));

        // Standard READDIR returns only: name + 0x00 (no flags byte per TNFS 1.2 spec)
        string fileName = ParseNullTerminatedString(readResp.AsSpan(5));

        TestContext.Out.WriteLine($"First entry: {fileName}");
        Assert.That(fileName, Is.EqualTo("nested.txt"));

        // CLOSEDIR
        var closeReq = CreateCloseDirRequest(sessionId, handle);
        await SendTcpMessageAsync(stream, closeReq);
        var closeResp = await ReceiveTcpMessageAsync(stream);

        Assert.That((TnfsReturnCode)closeResp[4], Is.EqualTo(TnfsReturnCode.SUCCESS));

        // UNMOUNT
        var unmountReq = CreateUnmountRequest(sessionId);
        await SendTcpMessageAsync(stream, unmountReq);
        var unmountResp = await ReceiveTcpMessageAsync(stream);

        Assert.That((TnfsReturnCode)unmountResp[4], Is.EqualTo(TnfsReturnCode.SUCCESS));

        TestContext.Out.WriteLine("✅ Complete TCP cycle: MOUNT → OPENDIR → READDIR → CLOSEDIR → UNMOUNT");
    }

    [Test]
    public async Task Test_TCP_Multiple_Sequential_Operations()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, TEST_TCP_PORT);
        var stream = client.GetStream();

        // MOUNT
        var mountReq = CreateMountRequest("/");
        await SendTcpMessageAsync(stream, mountReq);
        var mountResp = await ReceiveTcpMessageAsync(stream);
        ushort sessionId = BitConverter.ToUInt16(mountResp, 0);

        // Perform 10 OPENDIR/CLOSEDIR cycles on same connection
        for (int i = 0; i < 10; i++)
        {
            var openReq = CreateOpenDirRequest(sessionId, "/testdir");
            await SendTcpMessageAsync(stream, openReq);
            var openResp = await ReceiveTcpMessageAsync(stream);

            Assert.That(openResp.Length, Is.GreaterThanOrEqualTo(6), $"OPENDIR response too short on iteration {i}");
            Assert.That((TnfsReturnCode)openResp[4], Is.EqualTo(TnfsReturnCode.SUCCESS), $"OPENDIR failed on iteration {i}");
            byte handle = openResp[5];

            var closeReq = CreateCloseDirRequest(sessionId, handle);
            await SendTcpMessageAsync(stream, closeReq);
            await ReceiveTcpMessageAsync(stream);
        }

        TestContext.Out.WriteLine("✅ 10 sequential operations on single TCP connection");
    }

    [Test]
    public async Task Test_TCP_Connection_Persistence()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, TEST_TCP_PORT);
        var stream = client.GetStream();

        // MOUNT
        var mountReq = CreateMountRequest("/");
        await SendTcpMessageAsync(stream, mountReq);
        var mountResp = await ReceiveTcpMessageAsync(stream);
        ushort sessionId = BitConverter.ToUInt16(mountResp, 0);

        // Wait 2 seconds - connection should stay open
        await Task.Delay(2000);

        // Should still be able to use the connection
        var openReq = CreateOpenDirRequest(sessionId, "/");
        await SendTcpMessageAsync(stream, openReq);
        var openResp = await ReceiveTcpMessageAsync(stream);

        Assert.That((TnfsReturnCode)openResp[4], Is.EqualTo(TnfsReturnCode.SUCCESS));
        TestContext.Out.WriteLine("✅ TCP connection persists across idle periods");
    }

    [Test]
    public async Task Test_TCP_Invalid_Session()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, TEST_TCP_PORT);
        var stream = client.GetStream();

        var request = CreateOpenDirRequest(0xFFFF, "/");
        await SendTcpMessageAsync(stream, request);
        var response = await ReceiveTcpMessageAsync(stream);

        Assert.That((TnfsReturnCode)response[4], Is.EqualTo(TnfsReturnCode.EBADSESSION));
        TestContext.Out.WriteLine("✅ TCP: Invalid session correctly rejected");
    }

    // Helper methods

    private async Task SendTcpMessageAsync(NetworkStream stream, byte[] message)
    {
        // Write 2-byte length prefix (little-endian)
        ushort length = (ushort)message.Length;
        byte[] lengthBytes = BitConverter.GetBytes(length);
        await stream.WriteAsync(lengthBytes, 0, 2);

        // Write message
        await stream.WriteAsync(message, 0, message.Length);
    }

    private async Task<byte[]> ReceiveTcpMessageAsync(NetworkStream stream)
    {
        // Read 2-byte length prefix
        byte[] lengthBytes = new byte[2];
        int bytesRead = await stream.ReadAsync(lengthBytes, 0, 2);
        if (bytesRead != 2)
            throw new Exception("Failed to read length prefix");

        ushort length = BitConverter.ToUInt16(lengthBytes, 0);

        // Read message
        byte[] message = new byte[length];
        bytesRead = 0;
        while (bytesRead < length)
        {
            int read = await stream.ReadAsync(message, bytesRead, length - bytesRead);
            if (read == 0)
                throw new Exception("Connection closed while reading message");
            bytesRead += read;
        }

        return message;
    }

    private byte[] CreateMountRequest(string path)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write((ushort)0x0000); // SessionID = 0 for MOUNT
        bw.Write((byte)0x00); // Sequence
        bw.Write(TnfsConstants.TNFS_MOUNT);
        bw.Write((byte)0x01); // Protocol version LSB
        bw.Write((byte)0x00); // Protocol version MSB
        bw.Write(Encoding.ASCII.GetBytes(path));
        bw.Write((byte)0x00); // Null terminator

        return ms.ToArray();
    }

    private byte[] CreateOpenDirRequest(ushort sessionId, string path)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write((byte)0x01); // Sequence
        bw.Write(TnfsConstants.TNFS_OPENDIR);
        bw.Write(Encoding.ASCII.GetBytes(path));
        bw.Write((byte)0x00); // Null terminator

        return ms.ToArray();
    }

    private byte[] CreateReadDirRequest(ushort sessionId, byte handle)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write((byte)0x02); // Sequence
        bw.Write(TnfsConstants.TNFS_READDIR);
        bw.Write(handle);

        return ms.ToArray();
    }

    private byte[] CreateCloseDirRequest(ushort sessionId, byte handle)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write((byte)0x03); // Sequence
        bw.Write(TnfsConstants.TNFS_CLOSEDIR);
        bw.Write(handle);

        return ms.ToArray();
    }

    private byte[] CreateUnmountRequest(ushort sessionId)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write((byte)0x04); // Sequence
        bw.Write(TnfsConstants.TNFS_UMOUNT);

        return ms.ToArray();
    }

    private string ParseNullTerminatedString(ReadOnlySpan<byte> buffer)
    {
        int nullIndex = buffer.IndexOf((byte)0);
        if (nullIndex < 0) nullIndex = buffer.Length;
        return Encoding.ASCII.GetString(buffer.Slice(0, nullIndex));
    }
}
