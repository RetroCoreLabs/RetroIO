//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
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
/// Integration tests for TNFS server with real client-server communication, using raw
/// hand-built packets (not TnfsClient) against a self-hosted in-process server.
///
/// Covers every opcode the server actually implements: MOUNT, UMOUNT, OPENDIR, READDIR,
/// CLOSEDIR, MKDIR, RMDIR, OPEN, READ, WRITE, LSEEK, CLOSE, STAT, UNLINK, RENAME.
///
/// NOT covered, and cannot be covered here: CHMOD, TELLDIR, SEEKDIR, OPENDIRX, OPENFILE_OLD.
/// These are server feature gaps, not test gaps — TnfsServerService's opcode dispatch switch
/// has no case for any of them, so there is nothing on the wire to assert against. READDIRX
/// is exercised indirectly (TnfsClient.ReadDir tries it before falling back to READDIR), not
/// as a standalone opcode in this raw-packet file.
/// </summary>
[TestFixture]
[Category("Integration")]
[Category("RealNetwork")]
public class TnfsIntegrationTests
{
    private IHost? _host;
    private UdpClient? _client;
    private IPEndPoint? _serverEndpoint;
    private ushort _sessionId;
    private const int TEST_PORT = TnfsTestConfig.AUTOMATED_TEST_PORT_UDP;
    private const string TEST_ROOT = TnfsTestConfig.AUTOMATED_TEST_ROOT;

    [OneTimeSetUp]
    public async Task SetupServer()
    {
        // Create test directory structure
        Directory.CreateDirectory(TEST_ROOT);
        Directory.CreateDirectory(Path.Combine(TEST_ROOT, "testdir"));
        Directory.CreateDirectory(Path.Combine(TEST_ROOT, "emptydir"));
        File.WriteAllText(Path.Combine(TEST_ROOT, "file1.txt"), "Content of file 1");
        File.WriteAllText(Path.Combine(TEST_ROOT, "file2.txt"), "Content of file 2");
        File.WriteAllText(Path.Combine(TEST_ROOT, "testdir", "nested.txt"), "Nested file");

        // Start TNFS server
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                // File system backend
                services.AddSingleton<IFileSystemBackend, LocalFileSystemBackend>();

                // Core services
                services.AddSingleton<SessionManager>(sp =>
                    new SessionManager(
                        sp.GetRequiredService<ILogger<SessionManager>>(),
                        TimeSpan.FromSeconds(600),
                        maxSessionsPerIP: 8));

                // Handlers
                services.AddSingleton<SessionCommandHandler>(sp =>
                    new SessionCommandHandler(
                        sp.GetRequiredService<SessionManager>(),
                        sp.GetRequiredService<ILogger<SessionCommandHandler>>(),
                        TEST_ROOT,
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

                // Transport - UDP only for this test
                services.AddSingleton<ITransport>(sp =>
                    new UdpTransport(TEST_PORT, sp.GetRequiredService<ILogger<UdpTransport>>()));

                // Server (receives IEnumerable<ITransport>)
                services.AddHostedService<RetroIO.Protocols.Tnfs.Server.TnfsServerService>();

                // Logging
                services.AddLogging(builder =>
                {
                    builder.AddConsole();
                    builder.SetMinimumLevel(LogLevel.Warning); // Reduce noise
                });
            })
            .Build();

        // Start server in background
        _ = _host.RunAsync();

        // Wait for server to start
        await Task.Delay(500);

        // Create client
        _client = new UdpClient();
        _serverEndpoint = new IPEndPoint(IPAddress.Loopback, TEST_PORT);
        _client.Connect(_serverEndpoint);
        _client.Client.ReceiveTimeout = 5000;

        TestContext.Out.WriteLine($"✅ TNFS Integration Test Server started on port {TEST_PORT}");
        TestContext.Out.WriteLine($"   Test root: {TEST_ROOT}");
    }

    [OneTimeTearDown]
    public async Task TearDownServer()
    {
        _client?.Dispose();

        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        // Cleanup test directory
        try
        {
            if (Directory.Exists(TEST_ROOT))
                Directory.Delete(TEST_ROOT, recursive: true);
        }
        catch { }

        TestContext.Out.WriteLine("✅ TNFS Integration Test Server stopped");
    }

    [Test, Order(1)]
    public async Task Test_01_Mount_Success()
    {
        TestContext.Out.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        TestContext.Out.WriteLine("TEST 1: MOUNT Command");
        TestContext.Out.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

        // Build MOUNT request
        byte[] mountRequest = new byte[] {
            0x00, 0x00,  // Session ID = 0
            0x00,        // Sequence = 0
            TnfsConstants.TNFS_MOUNT,  // Command = MOUNT
            0x01, 0x00,  // Protocol version
            0x2F, 0x00   // "/" + null terminator
        };

        await _client!.SendAsync(mountRequest, mountRequest.Length);
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = _client.Receive(ref remoteEP);

        // Parse response
        _sessionId = BitConverter.ToUInt16(response, 0);
        byte status = response[4];

        TestContext.Out.WriteLine($"   Request:  SID=0x0000, Seq=0x00, Cmd=MOUNT, Path='/'");
        TestContext.Out.WriteLine($"   Response: SID=0x{_sessionId:X4}, Status=0x{status:X2}");

        Assert.That(status, Is.EqualTo(0x00), "MOUNT should succeed");
        Assert.That(_sessionId, Is.Not.EqualTo(0), "Session ID should be non-zero");

        TestContext.Out.WriteLine($"   ✅ MOUNT successful, Session ID: 0x{_sessionId:X4}");
    }

    [Test, Order(2)]
    public async Task Test_02_OpenDir_Success()
    {
        TestContext.Out.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        TestContext.Out.WriteLine("TEST 2: OPENDIR Command");
        TestContext.Out.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

        // Build OPENDIR request
        List<byte> packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(_sessionId));
        packet.Add(0x01);  // Sequence
        packet.Add(TnfsConstants.TNFS_OPENDIR);
        packet.AddRange(Encoding.ASCII.GetBytes("/testdir"));
        packet.Add(0x00);

        await _client!.SendAsync(packet.ToArray(), packet.Count);
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = _client.Receive(ref remoteEP);

        byte status = response[4];
        byte handle = response.Length > 5 ? response[5] : (byte)0xFF;

        TestContext.Out.WriteLine($"   Request:  SID=0x{_sessionId:X4}, Seq=0x01, Cmd=OPENDIR, Path='/testdir'");
        TestContext.Out.WriteLine($"   Response: SID=0x{BitConverter.ToUInt16(response, 0):X4}, Status=0x{status:X2}, Handle={handle}");

        Assert.That(status, Is.EqualTo(0x00), "OPENDIR should succeed");
        Assert.That(handle, Is.LessThan(16), "Handle should be valid (0-15)");

        TestContext.Out.WriteLine($"   ✅ OPENDIR successful, Handle: {handle}");
    }

    [Test, Order(3)]
    public async Task Test_03_ReadDir_Success()
    {
        TestContext.Out.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        TestContext.Out.WriteLine("TEST 3: READDIR Command (Full Directory Listing)");
        TestContext.Out.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

        // OPENDIR /
        List<byte> openPacket = new List<byte>();
        openPacket.AddRange(BitConverter.GetBytes(_sessionId));
        openPacket.Add(0x02);
        openPacket.Add(TnfsConstants.TNFS_OPENDIR);
        openPacket.AddRange(Encoding.ASCII.GetBytes("/"));
        openPacket.Add(0x00);

        await _client!.SendAsync(openPacket.ToArray(), openPacket.Count);
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] openResponse = _client.Receive(ref remoteEP);

        byte handle = openResponse[5];
        TestContext.Out.WriteLine($"   Opened root directory, handle={handle}");

        // READDIR loop
        List<string> entries = new List<string>();
        byte sequence = 0x03;

        while (true)
        {
            List<byte> readPacket = new List<byte>();
            readPacket.AddRange(BitConverter.GetBytes(_sessionId));
            readPacket.Add(sequence++);
            readPacket.Add(TnfsConstants.TNFS_READDIR);
            readPacket.Add(handle);

            await _client.SendAsync(readPacket.ToArray(), readPacket.Count);
            byte[] readResponse = _client.Receive(ref remoteEP);

            byte status = readResponse[4];

            if (status == 0x21)  // EOF
            {
                TestContext.Out.WriteLine($"   EOF - Total entries: {entries.Count}");
                break;
            }

            Assert.That(status, Is.EqualTo(0x00), "READDIR should succeed");

            // Standard READDIR returns only: name + 0x00 (no flags byte per TNFS 1.2 spec)
            string name = ParseNullTerminatedString(readResponse, 5);

            entries.Add(name);
            TestContext.Out.WriteLine($"   📄 {name}");
        }

        Assert.That(entries.Count, Is.GreaterThan(0), "Should have read some entries");
        Assert.That(entries, Does.Contain("file1.txt"));
        Assert.That(entries, Does.Contain("file2.txt"));
        Assert.That(entries, Does.Contain("testdir"));

        TestContext.Out.WriteLine($"   ✅ READDIR successful, {entries.Count} entries");
    }

    [Test, Order(4)]
    public async Task Test_04_CloseDir_Success()
    {
        TestContext.Out.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        TestContext.Out.WriteLine("TEST 4: CLOSEDIR Command");
        TestContext.Out.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

        // OPENDIR
        List<byte> openPacket = new List<byte>();
        openPacket.AddRange(BitConverter.GetBytes(_sessionId));
        openPacket.Add(0x10);
        openPacket.Add(TnfsConstants.TNFS_OPENDIR);
        openPacket.AddRange(Encoding.ASCII.GetBytes("/emptydir"));
        openPacket.Add(0x00);

        await _client!.SendAsync(openPacket.ToArray(), openPacket.Count);
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] openResponse = _client.Receive(ref remoteEP);

        byte handle = openResponse[5];
        TestContext.Out.WriteLine($"   Opened directory /emptydir, handle={handle}");

        // CLOSEDIR
        List<byte> closePacket = new List<byte>();
        closePacket.AddRange(BitConverter.GetBytes(_sessionId));
        closePacket.Add(0x11);
        closePacket.Add(TnfsConstants.TNFS_CLOSEDIR);
        closePacket.Add(handle);

        await _client.SendAsync(closePacket.ToArray(), closePacket.Count);
        byte[] closeResponse = _client.Receive(ref remoteEP);

        byte status = closeResponse[4];

        TestContext.Out.WriteLine($"   Request:  SID=0x{_sessionId:X4}, Seq=0x11, Cmd=CLOSEDIR, Handle={handle}");
        TestContext.Out.WriteLine($"   Response: Status=0x{status:X2}");

        Assert.That(status, Is.EqualTo(0x00), "CLOSEDIR should succeed");

        TestContext.Out.WriteLine($"   ✅ CLOSEDIR successful");
    }

    [Test, Order(5)]
    public async Task Test_05_OpenDir_InvalidPath()
    {
        TestContext.Out.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        TestContext.Out.WriteLine("TEST 5: OPENDIR with Invalid Path (Error Handling)");
        TestContext.Out.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

        List<byte> packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(_sessionId));
        packet.Add(0x20);
        packet.Add(TnfsConstants.TNFS_OPENDIR);
        packet.AddRange(Encoding.ASCII.GetBytes("/nonexistent"));
        packet.Add(0x00);

        await _client!.SendAsync(packet.ToArray(), packet.Count);
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = _client.Receive(ref remoteEP);

        byte status = response[4];

        TestContext.Out.WriteLine($"   Request:  Path='/nonexistent'");
        TestContext.Out.WriteLine($"   Response: Status=0x{status:X2} (ENOENT={TnfsReturnCode.ENOENT:X})");

        Assert.That(status, Is.EqualTo((byte)TnfsReturnCode.ENOENT), "Should return ENOENT");

        TestContext.Out.WriteLine($"   ✅ Invalid path correctly rejected with ENOENT");
    }

    [Test, Order(6)]
    public async Task Test_06_ReadDir_InvalidHandle()
    {
        TestContext.Out.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        TestContext.Out.WriteLine("TEST 6: READDIR with Invalid Handle (Error Handling)");
        TestContext.Out.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

        List<byte> packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(_sessionId));
        packet.Add(0x30);
        packet.Add(TnfsConstants.TNFS_READDIR);
        packet.Add(0xFF);  // Invalid handle

        await _client!.SendAsync(packet.ToArray(), packet.Count);
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = _client.Receive(ref remoteEP);

        byte status = response[4];

        TestContext.Out.WriteLine($"   Request:  Handle=0xFF (invalid)");
        TestContext.Out.WriteLine($"   Response: Status=0x{status:X2} (EBADF={TnfsReturnCode.EBADF:X})");

        Assert.That(status, Is.EqualTo((byte)TnfsReturnCode.EBADF), "Should return EBADF");

        TestContext.Out.WriteLine($"   ✅ Invalid handle correctly rejected with EBADF");
    }

    [Test, Order(7)]
    public async Task Test_07_DirectoryTraversal_Security()
    {
        TestContext.Out.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        TestContext.Out.WriteLine("TEST 7: Directory Traversal Attack Prevention");
        TestContext.Out.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

        List<byte> packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(_sessionId));
        packet.Add(0x40);
        packet.Add(TnfsConstants.TNFS_OPENDIR);
        packet.AddRange(Encoding.ASCII.GetBytes("/../../../etc"));  // Attempt to escape
        packet.Add(0x00);

        await _client!.SendAsync(packet.ToArray(), packet.Count);
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = _client.Receive(ref remoteEP);

        byte status = response[4];

        TestContext.Out.WriteLine($"   Request:  Path='/../../../etc' (traversal attack)");
        TestContext.Out.WriteLine($"   Response: Status=0x{status:X2}");

        // Should be rejected with either EACCES (access denied) or ENOENT (not found)
        Assert.That(status, Is.EqualTo((byte)TnfsReturnCode.EACCES).Or.EqualTo((byte)TnfsReturnCode.ENOENT),
            "Should reject directory traversal attempt");

        TestContext.Out.WriteLine($"   ✅ Directory traversal attack blocked");
    }

    // ====================================================================
    // FILE I/O + MKDIR/RMDIR TESTS
    //
    // These 10 opcodes (MKDIR, RMDIR, OPEN, READ, WRITE, LSEEK, CLOSE, STAT, UNLINK, RENAME) are
    // all dispatched by TnfsServerService (see its opcode switch) and were previously completely
    // untested here despite the class doc comment claiming "all commands" — this was the real,
    // severe part of that false claim (>75% of the protocol surface). TELLDIR/SEEKDIR/OPENDIRX/
    // OPENFILE_OLD are NOT in TnfsServerService's opcode switch at all (server doesn't implement
    // them), and CHMOD is a confirmed empty stub (see TnfsClient.Chmod's comment) — none of those
    // five can be tested honestly here; adding raw-packet tests for them would just prove "the
    // server never responds", not that a real feature works.
    // ====================================================================

    [Test, Order(10)]
    public async Task Test_10_MkDir_Success()
    {
        List<byte> packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(_sessionId));
        packet.Add(0x60);
        packet.Add(TnfsConstants.TNFS_MKDIR);
        packet.AddRange(Encoding.ASCII.GetBytes("/newdir"));
        packet.Add(0x00);

        await _client!.SendAsync(packet.ToArray(), packet.Count);
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = _client.Receive(ref remoteEP);

        byte status = response[4];
        Assert.That(status, Is.EqualTo(0x00), "MKDIR should succeed");
        Assert.That(Directory.Exists(Path.Combine(TEST_ROOT, "newdir")), Is.True, "Directory should exist on disk");
    }

    [Test, Order(11)]
    public async Task Test_11_RmDir_Success()
    {
        // MKDIR first (this test must not depend on Test_10's ordering/side effects).
        Directory.CreateDirectory(Path.Combine(TEST_ROOT, "rmdirtarget"));

        List<byte> packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(_sessionId));
        packet.Add(0x61);
        packet.Add(TnfsConstants.TNFS_RMDIR);
        packet.AddRange(Encoding.ASCII.GetBytes("/rmdirtarget"));
        packet.Add(0x00);

        await _client!.SendAsync(packet.ToArray(), packet.Count);
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = _client.Receive(ref remoteEP);

        byte status = response[4];
        Assert.That(status, Is.EqualTo(0x00), "RMDIR should succeed");
        Assert.That(Directory.Exists(Path.Combine(TEST_ROOT, "rmdirtarget")), Is.False, "Directory should be gone on disk");
    }

    [Test, Order(12)]
    public async Task Test_12_Open_ForRead_Success()
    {
        // OPEN payload: flags(2 LE) + mode(2 LE) + path(null-terminated). O_RDONLY = 0.
        List<byte> packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(_sessionId));
        packet.Add(0x62);
        packet.Add(TnfsConstants.TNFS_OPEN);
        packet.AddRange(BitConverter.GetBytes((ushort)0x0000)); // O_RDONLY
        packet.AddRange(BitConverter.GetBytes((ushort)0x01A4)); // mode 0644 (ignored for read)
        packet.AddRange(Encoding.ASCII.GetBytes("/file1.txt"));
        packet.Add(0x00);

        await _client!.SendAsync(packet.ToArray(), packet.Count);
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = _client.Receive(ref remoteEP);

        byte status = response[4];
        byte handle = response[5];
        Assert.That(status, Is.EqualTo(0x00), "OPEN should succeed");

        // Cleanup: CLOSE the handle so it doesn't leak across tests.
        List<byte> closePacket = new List<byte>();
        closePacket.AddRange(BitConverter.GetBytes(_sessionId));
        closePacket.Add(0x63);
        closePacket.Add(TnfsConstants.TNFS_CLOSE);
        closePacket.Add(handle);
        await _client.SendAsync(closePacket.ToArray(), closePacket.Count);
        _client.Receive(ref remoteEP);
    }

    [Test, Order(13)]
    public async Task Test_13_Read_Success()
    {
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);

        // OPEN /file1.txt (content: "Content of file 1", written in SetupServer).
        List<byte> openPacket = new List<byte>();
        openPacket.AddRange(BitConverter.GetBytes(_sessionId));
        openPacket.Add(0x64);
        openPacket.Add(TnfsConstants.TNFS_OPEN);
        openPacket.AddRange(BitConverter.GetBytes((ushort)0x0000));
        openPacket.AddRange(BitConverter.GetBytes((ushort)0x01A4));
        openPacket.AddRange(Encoding.ASCII.GetBytes("/file1.txt"));
        openPacket.Add(0x00);
        await _client!.SendAsync(openPacket.ToArray(), openPacket.Count);
        byte[] openResponse = _client.Receive(ref remoteEP);
        byte handle = openResponse[5];

        // READ payload: handle(1) + length(2 LE).
        List<byte> readPacket = new List<byte>();
        readPacket.AddRange(BitConverter.GetBytes(_sessionId));
        readPacket.Add(0x65);
        readPacket.Add(TnfsConstants.TNFS_READ);
        readPacket.Add(handle);
        readPacket.AddRange(BitConverter.GetBytes((ushort)100));
        await _client.SendAsync(readPacket.ToArray(), readPacket.Count);
        byte[] readResponse = _client.Receive(ref remoteEP);

        byte status = readResponse[4];
        Assert.That(status, Is.EqualTo(0x00), "READ should succeed");
        ushort bytesRead = BitConverter.ToUInt16(readResponse, 5);
        string content = Encoding.ASCII.GetString(readResponse, 7, bytesRead);
        Assert.That(content, Is.EqualTo("Content of file 1"));

        // CLOSE
        List<byte> closePacket = new List<byte>();
        closePacket.AddRange(BitConverter.GetBytes(_sessionId));
        closePacket.Add(0x66);
        closePacket.Add(TnfsConstants.TNFS_CLOSE);
        closePacket.Add(handle);
        await _client.SendAsync(closePacket.ToArray(), closePacket.Count);
        _client.Receive(ref remoteEP);
    }

    [Test, Order(14)]
    public async Task Test_14_Write_Success()
    {
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] data = Encoding.ASCII.GetBytes("Written via raw WRITE test");

        // OPEN for write, create+truncate. Server decodes these via FileCommandHandler's
        // ConvertToFileAccess/ConvertToFileMode, which use TnfsOpenFlags bit values
        // (O_WRONLY=0x0001, O_CREAT=0x0100, O_TRUNC=0x0800) — NOT TnfsConstants.TNFS_O_*,
        // which are unused by the server and would produce the wrong file mode if used here.
        ushort flags = (ushort)(TnfsOpenFlags.O_WRONLY | TnfsOpenFlags.O_CREAT | TnfsOpenFlags.O_TRUNC);
        List<byte> openPacket = new List<byte>();
        openPacket.AddRange(BitConverter.GetBytes(_sessionId));
        openPacket.Add(0x67);
        openPacket.Add(TnfsConstants.TNFS_OPEN);
        openPacket.AddRange(BitConverter.GetBytes(flags));
        openPacket.AddRange(BitConverter.GetBytes((ushort)0x01A4));
        openPacket.AddRange(Encoding.ASCII.GetBytes("/writetest.txt"));
        openPacket.Add(0x00);
        await _client!.SendAsync(openPacket.ToArray(), openPacket.Count);
        byte[] openResponse = _client.Receive(ref remoteEP);
        Assert.That(openResponse[4], Is.EqualTo(0x00), "OPEN for write should succeed");
        byte handle = openResponse[5];

        // WRITE payload: handle(1) + size(2 LE) + data(N).
        List<byte> writePacket = new List<byte>();
        writePacket.AddRange(BitConverter.GetBytes(_sessionId));
        writePacket.Add(0x68);
        writePacket.Add(TnfsConstants.TNFS_WRITE);
        writePacket.Add(handle);
        writePacket.AddRange(BitConverter.GetBytes((ushort)data.Length));
        writePacket.AddRange(data);
        await _client.SendAsync(writePacket.ToArray(), writePacket.Count);
        byte[] writeResponse = _client.Receive(ref remoteEP);

        Assert.That(writeResponse[4], Is.EqualTo(0x00), "WRITE should succeed");
        ushort bytesWritten = BitConverter.ToUInt16(writeResponse, 5);
        Assert.That(bytesWritten, Is.EqualTo((ushort)data.Length));

        // CLOSE, then verify on disk.
        List<byte> closePacket = new List<byte>();
        closePacket.AddRange(BitConverter.GetBytes(_sessionId));
        closePacket.Add(0x69);
        closePacket.Add(TnfsConstants.TNFS_CLOSE);
        closePacket.Add(handle);
        await _client.SendAsync(closePacket.ToArray(), closePacket.Count);
        _client.Receive(ref remoteEP);

        string writtenPath = Path.Combine(TEST_ROOT, "writetest.txt");
        Assert.That(File.Exists(writtenPath), Is.True);
        Assert.That(File.ReadAllText(writtenPath), Is.EqualTo("Written via raw WRITE test"));
        File.Delete(writtenPath);
    }

    [Test, Order(15)]
    public async Task Test_15_Lseek_Success()
    {
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);

        List<byte> openPacket = new List<byte>();
        openPacket.AddRange(BitConverter.GetBytes(_sessionId));
        openPacket.Add(0x6A);
        openPacket.Add(TnfsConstants.TNFS_OPEN);
        openPacket.AddRange(BitConverter.GetBytes((ushort)0x0000));
        openPacket.AddRange(BitConverter.GetBytes((ushort)0x01A4));
        openPacket.AddRange(Encoding.ASCII.GetBytes("/file1.txt"));
        openPacket.Add(0x00);
        await _client!.SendAsync(openPacket.ToArray(), openPacket.Count);
        byte[] openResponse = _client.Receive(ref remoteEP);
        byte handle = openResponse[5];

        // LSEEK payload: handle(1) + whence(1) + offset(4 LE signed). whence 0 = SEEK_SET.
        List<byte> seekPacket = new List<byte>();
        seekPacket.AddRange(BitConverter.GetBytes(_sessionId));
        seekPacket.Add(0x6B);
        seekPacket.Add(TnfsConstants.TNFS_LSEEK);
        seekPacket.Add(handle);
        seekPacket.Add(0x00); // SEEK_SET
        seekPacket.AddRange(BitConverter.GetBytes(8)); // offset 8
        await _client.SendAsync(seekPacket.ToArray(), seekPacket.Count);
        byte[] seekResponse = _client.Receive(ref remoteEP);
        Assert.That(seekResponse[4], Is.EqualTo(0x00), "LSEEK should succeed");

        // Read from the sought position and confirm it landed where expected ("Content of file 1"
        // -> seeking 8 bytes in lands on "of file 1").
        List<byte> readPacket = new List<byte>();
        readPacket.AddRange(BitConverter.GetBytes(_sessionId));
        readPacket.Add(0x6C);
        readPacket.Add(TnfsConstants.TNFS_READ);
        readPacket.Add(handle);
        readPacket.AddRange(BitConverter.GetBytes((ushort)20));
        await _client.SendAsync(readPacket.ToArray(), readPacket.Count);
        byte[] readResponse = _client.Receive(ref remoteEP);
        ushort bytesRead = BitConverter.ToUInt16(readResponse, 5);
        string content = Encoding.ASCII.GetString(readResponse, 7, bytesRead);
        Assert.That(content, Is.EqualTo("of file 1"), "LSEEK must actually move the read position");

        List<byte> closePacket = new List<byte>();
        closePacket.AddRange(BitConverter.GetBytes(_sessionId));
        closePacket.Add(0x6D);
        closePacket.Add(TnfsConstants.TNFS_CLOSE);
        closePacket.Add(handle);
        await _client.SendAsync(closePacket.ToArray(), closePacket.Count);
        _client.Receive(ref remoteEP);
    }

    [Test, Order(16)]
    public async Task Test_16_Close_Success()
    {
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);

        List<byte> openPacket = new List<byte>();
        openPacket.AddRange(BitConverter.GetBytes(_sessionId));
        openPacket.Add(0x6E);
        openPacket.Add(TnfsConstants.TNFS_OPEN);
        openPacket.AddRange(BitConverter.GetBytes((ushort)0x0000));
        openPacket.AddRange(BitConverter.GetBytes((ushort)0x01A4));
        openPacket.AddRange(Encoding.ASCII.GetBytes("/file1.txt"));
        openPacket.Add(0x00);
        await _client!.SendAsync(openPacket.ToArray(), openPacket.Count);
        byte[] openResponse = _client.Receive(ref remoteEP);
        byte handle = openResponse[5];

        List<byte> closePacket = new List<byte>();
        closePacket.AddRange(BitConverter.GetBytes(_sessionId));
        closePacket.Add(0x6F);
        closePacket.Add(TnfsConstants.TNFS_CLOSE);
        closePacket.Add(handle);
        await _client.SendAsync(closePacket.ToArray(), closePacket.Count);
        byte[] closeResponse = _client.Receive(ref remoteEP);

        Assert.That(closeResponse[4], Is.EqualTo(0x00), "CLOSE should succeed");
    }

    [Test, Order(17)]
    public async Task Test_17_Stat_Success()
    {
        List<byte> packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(_sessionId));
        packet.Add(0x70);
        packet.Add(TnfsConstants.TNFS_STAT);
        packet.AddRange(Encoding.ASCII.GetBytes("/file1.txt"));
        packet.Add(0x00);

        await _client!.SendAsync(packet.ToArray(), packet.Count);
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = _client.Receive(ref remoteEP);

        Assert.That(response[4], Is.EqualTo(0x00), "STAT should succeed");
        Assert.That(response.Length, Is.GreaterThanOrEqualTo(20), "STAT response should carry file metadata");
    }

    [Test, Order(18)]
    public async Task Test_18_Unlink_Success()
    {
        string targetPath = Path.Combine(TEST_ROOT, "unlinktarget.txt");
        File.WriteAllText(targetPath, "delete me");

        List<byte> packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(_sessionId));
        packet.Add(0x71);
        packet.Add(TnfsConstants.TNFS_UNLINK);
        packet.AddRange(Encoding.ASCII.GetBytes("/unlinktarget.txt"));
        packet.Add(0x00);

        await _client!.SendAsync(packet.ToArray(), packet.Count);
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = _client.Receive(ref remoteEP);

        Assert.That(response[4], Is.EqualTo(0x00), "UNLINK should succeed");
        Assert.That(File.Exists(targetPath), Is.False, "File should be gone on disk");
    }

    [Test, Order(19)]
    public async Task Test_19_Rename_Success()
    {
        string oldPath = Path.Combine(TEST_ROOT, "renamesource.txt");
        string newPath = Path.Combine(TEST_ROOT, "renamedest.txt");
        File.WriteAllText(oldPath, "rename me");
        if (File.Exists(newPath))
            File.Delete(newPath);

        // RENAME payload: sourcePath(null-terminated) + destPath(null-terminated).
        List<byte> packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(_sessionId));
        packet.Add(0x72);
        packet.Add(TnfsConstants.TNFS_RENAME);
        packet.AddRange(Encoding.ASCII.GetBytes("/renamesource.txt"));
        packet.Add(0x00);
        packet.AddRange(Encoding.ASCII.GetBytes("/renamedest.txt"));
        packet.Add(0x00);

        await _client!.SendAsync(packet.ToArray(), packet.Count);
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = _client.Receive(ref remoteEP);

        Assert.That(response[4], Is.EqualTo(0x00), "RENAME should succeed");
        Assert.That(File.Exists(oldPath), Is.False, "Old name should be gone");
        Assert.That(File.Exists(newPath), Is.True, "New name should exist");
        File.Delete(newPath);
    }

    [Test, Order(99)]
    public async Task Test_99_Unmount_Success()
    {
        TestContext.Out.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        TestContext.Out.WriteLine("TEST 8: UNMOUNT Command");
        TestContext.Out.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

        byte[] unmountRequest = new byte[] {
            (byte)(_sessionId & 0xFF),
            (byte)((_sessionId >> 8) & 0xFF),
            0x50,  // Sequence
            TnfsConstants.TNFS_UMOUNT
        };

        await _client!.SendAsync(unmountRequest, unmountRequest.Length);
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = _client.Receive(ref remoteEP);

        byte status = response[4];

        TestContext.Out.WriteLine($"   Request:  SID=0x{_sessionId:X4}, Seq=0x50, Cmd=UNMOUNT");
        TestContext.Out.WriteLine($"   Response: Status=0x{status:X2}");

        Assert.That(status, Is.EqualTo(0x00), "UNMOUNT should succeed");

        TestContext.Out.WriteLine($"   ✅ UNMOUNT successful");

        // Try to use session after unmount (should fail)
        TestContext.Out.WriteLine($"   Verifying session is invalid after UNMOUNT...");

        List<byte> testPacket = new List<byte>();
        testPacket.AddRange(BitConverter.GetBytes(_sessionId));
        testPacket.Add(0x51);
        testPacket.Add(TnfsConstants.TNFS_OPENDIR);
        testPacket.AddRange(Encoding.ASCII.GetBytes("/"));
        testPacket.Add(0x00);

        await _client.SendAsync(testPacket.ToArray(), testPacket.Count);
        byte[] testResponse = _client.Receive(ref remoteEP);

        byte testStatus = testResponse[4];
        TestContext.Out.WriteLine($"   Post-unmount request status: 0x{testStatus:X2}");

        Assert.That(testStatus, Is.Not.EqualTo(0x00), "Commands should fail after UNMOUNT");

        TestContext.Out.WriteLine($"   ✅ Session correctly invalidated after UNMOUNT");
    }

    // Helper methods

    private string ParseNullTerminatedString(byte[] buffer, int offset)
    {
        int length = 0;
        while (offset + length < buffer.Length && buffer[offset + length] != 0)
            length++;

        return Encoding.ASCII.GetString(buffer, offset, length);
    }
}
