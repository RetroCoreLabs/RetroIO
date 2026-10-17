//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Protocols.Tnfs.Server;
using RetroIO.Protocols.Tnfs.Server.Handlers;

namespace RetroIO.Tests.TNFS;

/// <summary>
/// Unit tests for TNFS directory operations using mock file system.
/// Tests without requiring real file system or network.
/// </summary>
[TestFixture]
public class TnfsDirectoryMockTests
{
    private MockFileSystemBackend _mockFS = null!;
    private SessionManager _sessionManager = null!;
    private DirectoryCommandHandler _directoryHandler = null!;
    private TnfsSession _session = null!;

    [SetUp]
    public void Setup()
    {
        // Create mock file system with test structure
        _mockFS = new MockFileSystemBackend();

        // Root directory
        _mockFS.AddDirectory("/tmp/tnfs");

        // /test directory with files and subdirectory
        _mockFS.AddDirectory("/tmp/tnfs/test");
        _mockFS.AddFile("/tmp/tnfs/test/file1.txt", Encoding.ASCII.GetBytes("File 1 content"));
        _mockFS.AddFile("/tmp/tnfs/test/file2.txt", Encoding.ASCII.GetBytes("File 2 content"));
        _mockFS.AddFile("/tmp/tnfs/test/hello.txt", Encoding.ASCII.GetBytes("Hello TNFS!"));
        _mockFS.AddDirectory("/tmp/tnfs/test/subdir");
        _mockFS.AddFile("/tmp/tnfs/test/subdir/nested.txt", Encoding.ASCII.GetBytes("Nested file"));

        // /empty directory
        _mockFS.AddDirectory("/tmp/tnfs/empty");

        // Create session manager and handler
        _sessionManager = new SessionManager(
            NullLogger<SessionManager>.Instance,
            TimeSpan.FromMinutes(10),
            maxSessionsPerIP: 8);

        _directoryHandler = new DirectoryCommandHandler(
            _sessionManager,
            NullLogger<DirectoryCommandHandler>.Instance,
            _mockFS);

        // Create a test session
        _session = _sessionManager.CreateSession(IPAddress.Loopback, "/tmp/tnfs");
    }

    [TearDown]
    public void TearDown()
    {
        _sessionManager?.Dispose();
    }

    [Test]
    public void Test_OpenDir_Success()
    {
        // Create OPENDIR request
        var request = CreateOpenDirRequest(_session.SessionId, "/test");

        // Handle request
        var response = _directoryHandler.HandleOpenDir(request);

        // Verify response
        Assert.That((TnfsReturnCode)response.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS));
        Assert.That(response.Data.Length, Is.EqualTo(1), "Response should contain handle");

        byte handle = response.Data.Span[0];
        Assert.That(handle, Is.GreaterThanOrEqualTo(0).And.LessThan(16), "Handle should be valid (0-15)");

        TestContext.Out.WriteLine($"✅ OPENDIR succeeded with handle: {handle}");
    }

    [Test]
    public void Test_OpenDir_ReadDir_Complete_Cycle()
    {
        // OPENDIR
        var openRequest = CreateOpenDirRequest(_session.SessionId, "/test");
        var openResponse = _directoryHandler.HandleOpenDir(openRequest);

        Assert.That((TnfsReturnCode)openResponse.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS));
        byte handle = openResponse.Data.Span[0];

        // READDIR loop
        List<string> entries = new List<string>();
        byte sequence = 2;

        while (true)
        {
            var readRequest = CreateReadDirRequest(_session.SessionId, sequence++, handle);
            var readResponse = _directoryHandler.HandleReadDir(readRequest);

            if ((TnfsReturnCode)readResponse.Header.Status == TnfsReturnCode.EOF)
            {
                TestContext.Out.WriteLine($"   EOF - Total entries: {entries.Count}");
                break;
            }

            Assert.That((TnfsReturnCode)readResponse.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS));

            // Parse entry: name + 0x00 (no flags byte per TNFS 1.2 spec)
            string name = ParseNullTerminatedString(readResponse.Data.Span);

            entries.Add(name);
            TestContext.Out.WriteLine($"   {name}");
        }

        // CLOSEDIR
        var closeRequest = CreateCloseDirRequest(_session.SessionId, sequence, handle);
        var closeResponse = _directoryHandler.HandleCloseDir(closeRequest);

        Assert.That((TnfsReturnCode)closeResponse.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS));

        // Verify entries
        Assert.That(entries.Count, Is.EqualTo(4), "Should have 4 entries (3 files + 1 dir)");
        Assert.That(entries, Does.Contain("file1.txt"));
        Assert.That(entries, Does.Contain("file2.txt"));
        Assert.That(entries, Does.Contain("hello.txt"));
        Assert.That(entries, Does.Contain("subdir"));

        TestContext.Out.WriteLine("✅ Complete directory cycle successful");
    }

    [Test]
    public void Test_OpenDir_EmptyDirectory()
    {
        // OPENDIR on empty directory
        var openRequest = CreateOpenDirRequest(_session.SessionId, "/empty");
        var openResponse = _directoryHandler.HandleOpenDir(openRequest);

        Assert.That((TnfsReturnCode)openResponse.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS));
        byte handle = openResponse.Data.Span[0];

        // READDIR should immediately return EOF
        var readRequest = CreateReadDirRequest(_session.SessionId, 1, handle);
        var readResponse = _directoryHandler.HandleReadDir(readRequest);

        Assert.That((TnfsReturnCode)readResponse.Header.Status, Is.EqualTo(TnfsReturnCode.EOF));

        TestContext.Out.WriteLine("✅ Empty directory handled correctly");
    }

    [Test]
    public void Test_OpenDir_InvalidPath()
    {
        var request = CreateOpenDirRequest(_session.SessionId, "/nonexistent");
        var response = _directoryHandler.HandleOpenDir(request);

        Assert.That((TnfsReturnCode)response.Header.Status, Is.EqualTo(TnfsReturnCode.ENOENT));
        TestContext.Out.WriteLine("✅ Invalid path correctly rejected with ENOENT");
    }

    [Test]
    public void Test_ReadDir_InvalidHandle()
    {
        var request = CreateReadDirRequest(_session.SessionId, 1, 0xFF);
        var response = _directoryHandler.HandleReadDir(request);

        Assert.That((TnfsReturnCode)response.Header.Status, Is.EqualTo(TnfsReturnCode.EBADF));
        TestContext.Out.WriteLine("✅ Invalid handle correctly rejected with EBADF");
    }

    [Test]
    public void Test_CloseDir_InvalidHandle()
    {
        var request = CreateCloseDirRequest(_session.SessionId, 1, 0xFF);
        var response = _directoryHandler.HandleCloseDir(request);

        Assert.That((TnfsReturnCode)response.Header.Status, Is.EqualTo(TnfsReturnCode.EBADF));
        TestContext.Out.WriteLine("✅ Invalid handle correctly rejected with EBADF");
    }

    [Test]
    public void Test_ReadDir_ClosedHandle()
    {
        // Open directory
        var openRequest = CreateOpenDirRequest(_session.SessionId, "/test");
        var openResponse = _directoryHandler.HandleOpenDir(openRequest);
        byte handle = openResponse.Data.Span[0];

        // Close immediately
        var closeRequest = CreateCloseDirRequest(_session.SessionId, 1, handle);
        _directoryHandler.HandleCloseDir(closeRequest);

        // Try to read closed handle
        var readRequest = CreateReadDirRequest(_session.SessionId, 2, handle);
        var readResponse = _directoryHandler.HandleReadDir(readRequest);

        Assert.That((TnfsReturnCode)readResponse.Header.Status, Is.EqualTo(TnfsReturnCode.EBADF));
        TestContext.Out.WriteLine("✅ Closed handle correctly rejected with EBADF");
    }

    // Helper methods

    private TnfsMessage CreateOpenDirRequest(ushort sessionId, string path)
    {
        List<byte> packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(sessionId));
        packet.Add(0x00);  // Sequence
        packet.Add(TnfsConstants.TNFS_OPENDIR);
        packet.AddRange(Encoding.ASCII.GetBytes(path));
        packet.Add(0x00);  // Null terminator

        return TnfsMessage.ParseRequest(packet.ToArray(), IPAddress.Loopback, 0);
    }

    private TnfsMessage CreateReadDirRequest(ushort sessionId, byte sequence, byte handle)
    {
        List<byte> packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(sessionId));
        packet.Add(sequence);
        packet.Add(TnfsConstants.TNFS_READDIR);
        packet.Add(handle);

        return TnfsMessage.ParseRequest(packet.ToArray(), IPAddress.Loopback, 0);
    }

    private TnfsMessage CreateCloseDirRequest(ushort sessionId, byte sequence, byte handle)
    {
        List<byte> packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(sessionId));
        packet.Add(sequence);
        packet.Add(TnfsConstants.TNFS_CLOSEDIR);
        packet.Add(handle);

        return TnfsMessage.ParseRequest(packet.ToArray(), IPAddress.Loopback, 0);
    }

    private string ParseNullTerminatedString(ReadOnlySpan<byte> buffer)
    {
        int nullIndex = buffer.IndexOf((byte)0);
        if (nullIndex < 0) nullIndex = buffer.Length;
        return Encoding.ASCII.GetString(buffer.Slice(0, nullIndex));
    }
}
