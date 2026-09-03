//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Protocols.Tnfs.Server;
using RetroIO.Protocols.Tnfs.Server.Handlers;
using RetroIO.Protocols.Tnfs.Server.Transport;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Tests.TNFS;

/// <summary>
/// Fast unit tests using InMemoryTransport (no network overhead).
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Mock")]
public class TnfsInMemoryTransportTests
{
    private InMemoryTransport _transport = null!;
    private IHost _host = null!;
    private MockFileSystemBackend _mockFS = null!;

    [SetUp]
    public async Task Setup()
    {
        // Create mock file system
        _mockFS = new MockFileSystemBackend();
        _mockFS.AddDirectory("/tmp/tnfs");
        _mockFS.AddDirectory("/tmp/tnfs/testdir");
        _mockFS.AddFile("/tmp/tnfs/file1.txt", Encoding.ASCII.GetBytes("File 1"));
        _mockFS.AddFile("/tmp/tnfs/file2.txt", Encoding.ASCII.GetBytes("File 2"));
        _mockFS.AddFile("/tmp/tnfs/testdir/nested.txt", Encoding.ASCII.GetBytes("Nested"));

        // Create in-memory transport
        _transport = new InMemoryTransport();

        // Build server with in-memory transport
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<IFileSystemBackend>(_mockFS);

                services.AddSingleton<SessionManager>(sp =>
                    new SessionManager(
                        NullLogger<SessionManager>.Instance,
                        TimeSpan.FromMinutes(10),
                        maxSessionsPerIP: 8));

                services.AddSingleton<SessionCommandHandler>(sp =>
                    new SessionCommandHandler(
                        sp.GetRequiredService<SessionManager>(),
                        NullLogger<SessionCommandHandler>.Instance,
                        "/tmp/tnfs",
                        sp.GetRequiredService<IFileSystemBackend>()));

                services.AddSingleton<DirectoryCommandHandler>(sp =>
                    new DirectoryCommandHandler(
                        sp.GetRequiredService<SessionManager>(),
                        NullLogger<DirectoryCommandHandler>.Instance,
                        sp.GetRequiredService<IFileSystemBackend>()));

                services.AddSingleton<FileCommandHandler>(sp =>
                    new FileCommandHandler(
                        sp.GetRequiredService<SessionManager>(),
                        NullLogger<FileCommandHandler>.Instance,
                        sp.GetRequiredService<IFileSystemBackend>()));

                // Single in-memory transport
                services.AddSingleton<ITransport>(_transport);

                services.AddHostedService<RetroIO.Protocols.Tnfs.Server.TnfsServerService>();
            })
            .Build();

        await _host.StartAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        _transport?.Dispose();
    }

    [Test]
    public async Task Test_InMemory_Mount_Success()
    {
        // Create MOUNT request
        var request = CreateMountRequest("/");

        // Send via in-memory transport
        await _transport.SendTestMessageAsync(request);

        // Wait a bit for processing
        await Task.Delay(100);

        // Receive response
        var response = await _transport.ReceiveResponseAsync(TimeSpan.FromSeconds(5));

        // Verify
        Assert.That((TnfsReturnCode)response.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS));
        Assert.That(response.Header.SessionId, Is.Not.EqualTo(0), "Should have session ID");

        TestContext.Out.WriteLine($"✅ InMemory MOUNT successful, Session ID: 0x{response.Header.SessionId:X4}");
    }

    [Test]
    public async Task Test_InMemory_Complete_Directory_Cycle()
    {
        // MOUNT
        var mountReq = CreateMountRequest("/");
        await _transport.SendTestMessageAsync(mountReq);
        var mountResp = await _transport.ReceiveResponseAsync(TimeSpan.FromSeconds(5));

        Assert.That((TnfsReturnCode)mountResp.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS));
        ushort sessionId = mountResp.Header.SessionId;

        // OPENDIR
        var openReq = CreateOpenDirRequest(sessionId, "/testdir");
        await _transport.SendTestMessageAsync(openReq);
        var openResp = await _transport.ReceiveResponseAsync(TimeSpan.FromSeconds(5));

        Assert.That((TnfsReturnCode)openResp.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS));
        byte handle = openResp.Data.Span[0];

        TestContext.Out.WriteLine($"Directory opened with handle: {handle}");

        // READDIR
        var readReq = CreateReadDirRequest(sessionId, handle);
        await _transport.SendTestMessageAsync(readReq);
        var readResp = await _transport.ReceiveResponseAsync(TimeSpan.FromSeconds(5));

        Assert.That((TnfsReturnCode)readResp.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS));

        // Standard READDIR returns only: name + 0x00 (no flags byte per TNFS 1.2 spec)
        string fileName = ParseNullTerminatedString(readResp.Data.Span);

        TestContext.Out.WriteLine($"First entry: {fileName}");
        Assert.That(fileName, Is.EqualTo("nested.txt"));

        // CLOSEDIR
        var closeReq = CreateCloseDirRequest(sessionId, handle);
        await _transport.SendTestMessageAsync(closeReq);
        var closeResp = await _transport.ReceiveResponseAsync(TimeSpan.FromSeconds(5));

        Assert.That((TnfsReturnCode)closeResp.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS));

        // UNMOUNT
        var unmountReq = CreateUnmountRequest(sessionId);
        await _transport.SendTestMessageAsync(unmountReq);
        var unmountResp = await _transport.ReceiveResponseAsync(TimeSpan.FromSeconds(5));

        Assert.That((TnfsReturnCode)unmountResp.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS));

        TestContext.Out.WriteLine("✅ Complete cycle: MOUNT → OPENDIR → READDIR → CLOSEDIR → UNMOUNT");
    }

    [Test]
    public async Task Test_InMemory_Invalid_Session()
    {
        var request = CreateOpenDirRequest(0xFFFF, "/");
        await _transport.SendTestMessageAsync(request);
        var response = await _transport.ReceiveResponseAsync(TimeSpan.FromSeconds(5));

        Assert.That((TnfsReturnCode)response.Header.Status, Is.EqualTo(TnfsReturnCode.EBADSESSION));
        TestContext.Out.WriteLine("✅ Invalid session correctly rejected");
    }

    [Test]
    public async Task Test_InMemory_Directory_Not_Found()
    {
        // MOUNT first
        var mountReq = CreateMountRequest("/");
        await _transport.SendTestMessageAsync(mountReq);
        var mountResp = await _transport.ReceiveResponseAsync(TimeSpan.FromSeconds(5));
        ushort sessionId = mountResp.Header.SessionId;

        // Try to open non-existent directory
        var openReq = CreateOpenDirRequest(sessionId, "/nonexistent");
        await _transport.SendTestMessageAsync(openReq);
        var openResp = await _transport.ReceiveResponseAsync(TimeSpan.FromSeconds(5));

        Assert.That((TnfsReturnCode)openResp.Header.Status, Is.EqualTo(TnfsReturnCode.ENOENT));
        TestContext.Out.WriteLine("✅ Non-existent directory correctly returns ENOENT");
    }

    [Test]
    public async Task Test_InMemory_Performance_100_Operations()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // MOUNT once
        var mountReq = CreateMountRequest("/");
        await _transport.SendTestMessageAsync(mountReq);
        var mountResp = await _transport.ReceiveResponseAsync(TimeSpan.FromSeconds(5));
        ushort sessionId = mountResp.Header.SessionId;

        // 100 OPENDIR/CLOSEDIR cycles
        for (int i = 0; i < 100; i++)
        {
            var openReq = CreateOpenDirRequest(sessionId, "/testdir");
            await _transport.SendTestMessageAsync(openReq);
            var openResp = await _transport.ReceiveResponseAsync(TimeSpan.FromSeconds(5));
            byte handle = openResp.Data.Span[0];

            var closeReq = CreateCloseDirRequest(sessionId, handle);
            await _transport.SendTestMessageAsync(closeReq);
            await _transport.ReceiveResponseAsync(TimeSpan.FromSeconds(5));
        }

        sw.Stop();

        TestContext.Out.WriteLine($"✅ 100 operations in {sw.ElapsedMilliseconds}ms ({sw.ElapsedMilliseconds / 100.0:F2}ms per op)");
        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(1000), "Should complete in under 1 second");
    }

    // Helper methods

    private TnfsMessage CreateMountRequest(string path)
    {
        var packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes((ushort)0x0000)); // SessionID = 0 for MOUNT
        packet.Add(0x00); // Sequence
        packet.Add(TnfsConstants.TNFS_MOUNT);
        packet.Add(0x01); // Protocol version LSB
        packet.Add(0x00); // Protocol version MSB
        packet.AddRange(Encoding.ASCII.GetBytes(path));
        packet.Add(0x00); // Null terminator

        return TnfsMessage.ParseRequest(packet.ToArray(), IPAddress.Loopback, 50000);
    }

    private TnfsMessage CreateOpenDirRequest(ushort sessionId, string path)
    {
        var packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(sessionId));
        packet.Add(0x01); // Sequence
        packet.Add(TnfsConstants.TNFS_OPENDIR);
        packet.AddRange(Encoding.ASCII.GetBytes(path));
        packet.Add(0x00); // Null terminator

        return TnfsMessage.ParseRequest(packet.ToArray(), IPAddress.Loopback, 50000);
    }

    private TnfsMessage CreateReadDirRequest(ushort sessionId, byte handle)
    {
        var packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(sessionId));
        packet.Add(0x02); // Sequence
        packet.Add(TnfsConstants.TNFS_READDIR);
        packet.Add(handle);

        return TnfsMessage.ParseRequest(packet.ToArray(), IPAddress.Loopback, 50000);
    }

    private TnfsMessage CreateCloseDirRequest(ushort sessionId, byte handle)
    {
        var packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(sessionId));
        packet.Add(0x03); // Sequence
        packet.Add(TnfsConstants.TNFS_CLOSEDIR);
        packet.Add(handle);

        return TnfsMessage.ParseRequest(packet.ToArray(), IPAddress.Loopback, 50000);
    }

    private TnfsMessage CreateUnmountRequest(ushort sessionId)
    {
        var packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(sessionId));
        packet.Add(0x04); // Sequence
        packet.Add(TnfsConstants.TNFS_UMOUNT);

        return TnfsMessage.ParseRequest(packet.ToArray(), IPAddress.Loopback, 50000);
    }

    private string ParseNullTerminatedString(ReadOnlySpan<byte> buffer)
    {
        int nullIndex = buffer.IndexOf((byte)0);
        if (nullIndex < 0) nullIndex = buffer.Length;
        return Encoding.ASCII.GetString(buffer.Slice(0, nullIndex));
    }
}
