//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Protocols.Tnfs.Client;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Protocols.Tnfs.Server;
using RetroIO.Protocols.Tnfs.Server.Handlers;
using RetroIO.Protocols.Tnfs.Server.Transport;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Tests.TNFS;

/// <summary>
/// Integration tests using TnfsClient with TCP transport.
/// Tests the complete client/server stack including length prefix protocol.
/// </summary>
[TestFixture]
[Category("Integration")]
[Category("RealNetwork")]
[Category("TCP")]
[Category("TnfsClient")]
public class TnfsClientTcpIntegrationTests
{
    private const int TEST_TCP_PORT = TnfsTestConfig.AUTOMATED_TEST_PORT_TCP + 1; // Use 16667 to avoid conflicts
    private IHost _host = null!;
    private string _testRoot = null!;

    [OneTimeSetUp]
    public async Task SetupServer()
    {
        // Create test directory structure
        _testRoot = Path.Combine(Path.GetTempPath(), "tnfs_client_tcp_test");
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
                        maxSessionsPerIP: 50));

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
                    builder.SetMinimumLevel(LogLevel.Trace); // Enable trace to see all logging
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
    public void Test_TnfsClient_TCP_Mount_Success()
    {
        // Get logger from DI
        var logger = _host.Services.GetRequiredService<ILogger<TnfsClient>>();

        using var client = new TnfsClient("127.0.0.1", TEST_TCP_PORT, useTcp: true, logger: logger);

        var result = client.Mount("/");

        Assert.That(result.Success, Is.True, "Mount should succeed");
        Assert.That(client.IsMounted, Is.True, "Client should be mounted");
        Assert.That(client.SessionId, Is.Not.EqualTo(0), "Should have session ID");

        TestContext.Out.WriteLine($"✅ TnfsClient TCP MOUNT successful, Session ID: 0x{client.SessionId:X4}");
    }

    [Test]
    public void Test_TnfsClient_TCP_Complete_Directory_Cycle()
    {
        var logger = _host.Services.GetRequiredService<ILogger<TnfsClient>>();

        using var client = new TnfsClient("127.0.0.1", TEST_TCP_PORT, useTcp: true, logger: logger);

        // MOUNT
        var mountResult = client.Mount("/");
        Assert.That(mountResult.Success, Is.True, "Mount should succeed");

        // OPENDIR
        var openResult = client.OpenDir("/testdir");
        Assert.That(openResult.Success, Is.True, "OpenDir should succeed");
        byte handle = openResult.Data;

        TestContext.Out.WriteLine($"Directory opened with handle: {handle}");

        // READDIR
        var readResult = client.ReadDir(handle);
        Assert.That(readResult.Success, Is.True, "ReadDir should succeed");
        Assert.That(readResult.Data, Is.Not.Null, "Should have directory entry");
        Assert.That(readResult.Data!.Name, Is.EqualTo("nested.txt"), "Should read nested.txt");

        TestContext.Out.WriteLine($"First entry: {readResult.Data.Name}");

        // CLOSEDIR
        var closeResult = client.CloseDir(handle);
        Assert.That(closeResult.Success, Is.True, "CloseDir should succeed");

        // UNMOUNT
        var unmountResult = client.Unmount();
        Assert.That(unmountResult.Success, Is.True, "Unmount should succeed");

        TestContext.Out.WriteLine("✅ TnfsClient complete cycle: MOUNT → OPENDIR → READDIR → CLOSEDIR → UNMOUNT");
    }

    [Test]
    public void Test_TnfsClient_TCP_Multiple_Operations()
    {
        var logger = _host.Services.GetRequiredService<ILogger<TnfsClient>>();

        using var client = new TnfsClient("127.0.0.1", TEST_TCP_PORT, useTcp: true, logger: logger);

        // MOUNT
        var mountResult = client.Mount("/");
        Assert.That(mountResult.Success, Is.True);

        // Perform 10 OPENDIR/CLOSEDIR cycles
        for (int i = 0; i < 10; i++)
        {
            var openResult = client.OpenDir("/testdir");
            Assert.That(openResult.Success, Is.True, $"OpenDir failed on iteration {i}");

            var closeResult = client.CloseDir(openResult.Data);
            Assert.That(closeResult.Success, Is.True, $"CloseDir failed on iteration {i}");
        }

        TestContext.Out.WriteLine("✅ TnfsClient: 10 sequential operations on single TCP connection");
    }

    [Test]
    public void Test_TnfsClient_TCP_File_Read()
    {
        var logger = _host.Services.GetRequiredService<ILogger<TnfsClient>>();

        using var client = new TnfsClient("127.0.0.1", TEST_TCP_PORT, useTcp: true, logger: logger);

        // MOUNT
        var mountResult = client.Mount("/");
        Assert.That(mountResult.Success, Is.True);

        // OPEN
        var openResult = client.Open("/file1.txt", TnfsOpenFlags.O_RDONLY);
        Assert.That(openResult.Success, Is.True, "File open should succeed");
        ushort fileHandle = openResult.Data;

        TestContext.Out.WriteLine($"File opened with handle: {fileHandle}");

        // READ
        var readResult = client.Read(fileHandle, 1024);

        TestContext.Out.WriteLine($"READ result: Success={readResult.Success}, ReturnCode={readResult.ReturnCode}, Error={readResult.ErrorMessage}");
        if (!readResult.Success)
        {
            TestContext.Out.WriteLine($"⚠️ READ failed - this suggests server-side issue");
            TestContext.Out.WriteLine($"Expected: Server returns file data");
            TestContext.Out.WriteLine($"Actual: Server returned error code {readResult.ReturnCode}");
        }

        Assert.That(readResult.Success, Is.True, $"File read should succeed. Error: {readResult.ErrorMessage}, Code: {readResult.ReturnCode}");
        Assert.That(readResult.Data, Is.Not.Null);
        Assert.That(readResult.Data!.Length, Is.GreaterThan(0), "Should have read data");

        string content = System.Text.Encoding.ASCII.GetString(readResult.Data);
        Assert.That(content, Is.EqualTo("File 1 Content"), "Should read correct content");

        TestContext.Out.WriteLine($"Read {readResult.Data.Length} bytes: {content}");

        // CLOSE
        var closeResult = client.Close(fileHandle);
        Assert.That(closeResult.Success, Is.True, "File close should succeed");

        TestContext.Out.WriteLine("✅ TnfsClient: File read cycle complete");
    }

    [Test]
    public void Test_TnfsClient_TCP_Stat()
    {
        var logger = _host.Services.GetRequiredService<ILogger<TnfsClient>>();

        using var client = new TnfsClient("127.0.0.1", TEST_TCP_PORT, useTcp: true, logger: logger);

        // MOUNT
        var mountResult = client.Mount("/");
        Assert.That(mountResult.Success, Is.True);

        // STAT
        var statResult = client.Stat("/file1.txt");
        Assert.That(statResult.Success, Is.True, "Stat should succeed");
        Assert.That(statResult.Data, Is.Not.Null);
        Assert.That(statResult.Data!.Size, Is.EqualTo(14), "File size should be 14 bytes");

        TestContext.Out.WriteLine($"✅ TnfsClient STAT: size={statResult.Data.Size}");
    }

    [Test]
    public void Test_TnfsClient_TCP_Invalid_Session_After_Unmount()
    {
        var logger = _host.Services.GetRequiredService<ILogger<TnfsClient>>();

        using var client = new TnfsClient("127.0.0.1", TEST_TCP_PORT, useTcp: true, logger: logger);

        // MOUNT
        client.Mount("/");

        // UNMOUNT
        client.Unmount();

        // Try to use unmounted client
        var openResult = client.OpenDir("/");
        Assert.That(openResult.Success, Is.False, "Should fail after unmount");
        Assert.That(openResult.ReturnCode, Is.EqualTo(TnfsReturnCode.EINVAL), "Should return EINVAL");

        TestContext.Out.WriteLine("✅ TnfsClient correctly rejects operations after unmount");
    }
}
