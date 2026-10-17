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
using NUnit.Framework;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Server.Core;

namespace RetroIO.Tests.TNFS;

/// <summary>
/// Low-level TCP protocol tests for TNFS against reference C TNFSD server.
/// Tests hand-crafted TNFS protocol packets over TCP to validate protocol implementation.
///
/// SERVER REQUIREMENTS:
/// ===================
///
/// 1. C TNFSD Server Configuration:
///    - Running on: localhost:16384
///    - TCP transport: ENABLED
///    - Server root: Specified via TNFS_TEST_ROOT environment variable
///
/// 2. Environment Variable (REQUIRED):
///    Set TNFS_TEST_ROOT to the directory the C TNFSD server will serve:
///
///    Linux/Unix/WSL:
///      export TNFS_TEST_ROOT=/tmp/tnfs
///
///    Windows (PowerShell):
///      $env:TNFS_TEST_ROOT = "C:\tmp\tnfs"
///
///    Windows (Command Prompt):
///      set TNFS_TEST_ROOT=C:\tmp\tnfs
///
/// 3. Test Setup:
///    - Tests will automatically create required directory structure:
///      {TNFS_TEST_ROOT}/test/           (read-only test files)
///      {TNFS_TEST_ROOT}/test/subdir/    (subdirectory with files)
///      {TNFS_TEST_ROOT}/writable/       (read-write test area)
///
/// 4. Start C TNFSD Server:
///    tnfsd $TNFS_TEST_ROOT --port 16384 --tcp
///
///    OR on Windows:
///    tnfsd %TNFS_TEST_ROOT% --port 16384 --tcp
///
/// 5. Run Tests:
///    dotnet test --filter Category=ExternalTNFSD
///
/// </summary>
[TestFixture]
[Category("Manual")]
[Category("ExternalTNFSD")]
[Category("TCP")]
[Explicit("Requires manually started C TNFSD server on localhost:16384 with TCP transport")]
public class TnfsTcpProtocolTests
{
    private const int SERVER_PORT = TnfsTestConfig.REMOTE_SERVER_PORT;
    private static readonly string SERVER_HOST = TnfsTestConfig.REMOTE_SERVER_ADDRESS;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private ushort _sessionId;
    private byte _sequenceNumber = 0;  // Auto-incrementing sequence number for shared connection
    private string _testRoot = null!;

    // Default test root if TNFS_TEST_ROOT env var is not set (for where tnfsd version 24.0522.1 is pointing at)
    private string WIN_DEFAULT_ROOT_FOLDER = "F:\\ROMS\\c64\\D64\\Z"; // ← CHANGE THIS PATH AS NEEDED
    private string X_DEFAULT_ROOT_FOLDER = "/tmp/tnfs"; // ← CHANGE THIS PATH AS NEEDED

    [OneTimeSetUp]
    public async Task ConnectAndMount()
    {
        string def_folder = ".";

        // Get test root - hardcoded default or override with environment variable
        if (OperatingSystem.IsWindows())
        {
            def_folder = WIN_DEFAULT_ROOT_FOLDER;
        }
        else

        {
            def_folder = X_DEFAULT_ROOT_FOLDER;
        }

        _testRoot = Environment.GetEnvironmentVariable("TNFS_TEST_ROOT")
                ?? def_folder;  // ← CHANGE THIS PATH AS NEEDED

        TestContext.Out.WriteLine($"Using TNFS test root: {_testRoot}");

        // Create test directory structure
        CreateTestDirectoryStructure();

        // Connect to C TNFSD server
        _client = new TcpClient();
        await _client.ConnectAsync(IPAddress.Parse(SERVER_HOST), SERVER_PORT);
        _stream = _client.GetStream();

        // MOUNT
        var mountReq = CreateMountRequest("/");
        await SendTcpMessageAsync(mountReq);
        var mountResp = await ReceiveTcpMessageAsync();

        Assert.That(mountResp.Length, Is.GreaterThanOrEqualTo(8), "MOUNT response too short");
        Assert.That(mountResp[4], Is.EqualTo(0x00), "MOUNT should succeed");

        _sessionId = BitConverter.ToUInt16(mountResp, 0);
        TestContext.Out.WriteLine($"✅ Connected via TCP, Session ID: 0x{_sessionId:X4}");
    }

    private void CreateTestDirectoryStructure()
    {
        try
        {
            // Create main test directories
            string testDir = Path.Combine(_testRoot, "test");
            string subDir = Path.Combine(testDir, "subdir");
            string writableDir = Path.Combine(_testRoot, "writable");

            Directory.CreateDirectory(testDir);
            Directory.CreateDirectory(subDir);
            Directory.CreateDirectory(writableDir);

            // Create test files
            string file1 = Path.Combine(testDir, "file1.txt");
            string file2 = Path.Combine(testDir, "file2.txt");
            string nested = Path.Combine(subDir, "nested.txt");

            File.WriteAllText(file1, "File 1 content - at least 20 bytes for LSEEK test");
            File.WriteAllText(file2, "File 2 content");
            File.WriteAllText(nested, "Nested file content");

            TestContext.Out.WriteLine($"✅ Created test directory structure in {_testRoot}");
            TestContext.Out.WriteLine($"   - {testDir}/");
            TestContext.Out.WriteLine($"   - {testDir}/file1.txt");
            TestContext.Out.WriteLine($"   - {testDir}/file2.txt");
            TestContext.Out.WriteLine($"   - {subDir}/");
            TestContext.Out.WriteLine($"   - {subDir}/nested.txt");
            TestContext.Out.WriteLine($"   - {writableDir}/ (empty, for write tests)");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to create test directory structure in {_testRoot}. " +
                $"Ensure the directory exists and has write permissions. Error: {ex.Message}", ex);
        }
    }

    [OneTimeTearDown]
    public async Task DisconnectAndCleanup()
    {
        if (_stream != null)
        {
            // UNMOUNT
            var unmountReq = CreateUnmountRequest(_sessionId);
            await SendTcpMessageAsync(unmountReq);
            await ReceiveTcpMessageAsync();
        }

        _stream?.Dispose();
        _client?.Dispose();

        // Clean up test directory structure
        CleanupTestDirectoryStructure();
    }

    private void CleanupTestDirectoryStructure()
    {
        if (string.IsNullOrEmpty(_testRoot))
            return;

        try
        {
            string writableDir = Path.Combine(_testRoot, "writable");

            // Only clean up the writable directory (remove any test files created during tests)
            if (Directory.Exists(writableDir))
            {
                var files = Directory.GetFiles(writableDir);
                var dirs = Directory.GetDirectories(writableDir);

                foreach (var file in files)
                    File.Delete(file);

                foreach (var dir in dirs)
                    Directory.Delete(dir, recursive: true);

                TestContext.Out.WriteLine($"✅ Cleaned up writable test directory: {writableDir}");
            }

            // Leave test/ directory intact for inspection if needed
            // User can manually delete {_testRoot}/test if desired
        }
        catch (Exception ex)
        {
            TestContext.Out.WriteLine($"⚠️  Cleanup warning: {ex.Message}");
        }
    }

    #region Session Commands

    [Test, Order(1)]
    public void Test_01_MOUNT_Already_Tested_In_Setup()
    {
        // MOUNT was tested in OneTimeSetUp
        Assert.That(_sessionId, Is.Not.EqualTo(0));
        TestContext.Out.WriteLine("✅ MOUNT command validated in setup");
    }

    [Test, Order(2)]
    public async Task Test_02_MOUNT_InvalidPath()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Parse(SERVER_HOST), SERVER_PORT);
        var stream = client.GetStream();

        var mountReq = CreateMountRequest("/nonexistent_path_12345");
        await SendTcpMessageAsync(stream, mountReq);
        var mountResp = await ReceiveTcpMessageAsync(stream);

        byte status = mountResp[4];
        TestContext.Out.WriteLine($"MOUNT invalid path status: 0x{status:X2}");

        // Should return error (not 0x00 SUCCESS)
        Assert.That(status, Is.Not.EqualTo(0x00), "Invalid path should fail");
        TestContext.Out.WriteLine("✅ MOUNT invalid path correctly rejected");
    }

    #endregion

    #region Directory Commands - Basic

    [Test, Order(10)]
    public async Task Test_10_OPENDIR_Success()
    {
        var openReq = CreateOpenDirRequest(_sessionId, NextSequence(), "/test");
        await SendTcpMessageAsync(openReq);
        var openResp = await ReceiveTcpMessageAsync();

        // Log response for debugging
        TestContext.Out.WriteLine($"OPENDIR response: {BitConverter.ToString(openResp)}");
        TestContext.Out.WriteLine($"Response length: {openResp.Length} bytes");

        ValidateResponse(openResp, 6, "OPENDIR");
        byte handle = openResp[5];
        TestContext.Out.WriteLine($"✅ OPENDIR successful, handle: {handle}");

        // Cleanup - CLOSEDIR
        var closeReq = CreateCloseDirRequest(_sessionId, NextSequence(), handle);
        await SendTcpMessageAsync(closeReq);
        await ReceiveTcpMessageAsync();
    }

    [Test, Order(11)]
    public async Task Test_11_OPENDIR_InvalidPath()
    {
        var openReq = CreateOpenDirRequest(_sessionId, NextSequence(), "/nonexistent");
        await SendTcpMessageAsync(openReq);
        var openResp = await ReceiveTcpMessageAsync();

        byte status = openResp[4];
        Assert.That(status, Is.EqualTo(0x02), "Should return ENOENT (0x02)");
        TestContext.Out.WriteLine("✅ OPENDIR invalid path correctly rejected with ENOENT");
    }

    [Test, Order(12)]
    public async Task Test_12_READDIR_Complete_Cycle()
    {
        // OPENDIR
        var openReq = CreateOpenDirRequest(_sessionId, NextSequence(), "/test");
        await SendTcpMessageAsync(openReq);
        var openResp = await ReceiveTcpMessageAsync();
        byte handle = openResp[5];

        // READDIR loop
        int entryCount = 0;
        while (true)
        {
            var readReq = CreateReadDirRequest(_sessionId, NextSequence(), handle);
            await SendTcpMessageAsync(readReq);
            var readResp = await ReceiveTcpMessageAsync();

            byte status = readResp[4];
            if (status == 0x21) // EOF
            {
                TestContext.Out.WriteLine($"Reached EOF after {entryCount} entries");
                break;
            }

            Assert.That(status, Is.EqualTo(0x00), "READDIR should succeed");
            string name = ParseNullTerminatedString(readResp, 5);
            TestContext.Out.WriteLine($"  Entry {entryCount + 1}: {name}");
            entryCount++;

            if (entryCount > 100)
            {
                Assert.Fail("Too many entries - possible infinite loop");
                break;
            }
        }

        Assert.That(entryCount, Is.GreaterThan(0), "Should read at least one entry");

        // CLOSEDIR
        var closeReq = CreateCloseDirRequest(_sessionId, NextSequence(), handle);
        await SendTcpMessageAsync(closeReq);
        var closeResp = await ReceiveTcpMessageAsync();
        Assert.That(closeResp[4], Is.EqualTo(0x00), "CLOSEDIR should succeed");

        TestContext.Out.WriteLine("✅ READDIR complete cycle successful");
    }

    [Test, Order(13)]
    public async Task Test_13_CLOSEDIR_InvalidHandle()
    {
        var closeReq = CreateCloseDirRequest(_sessionId, NextSequence(), 0xFF);
        await SendTcpMessageAsync(closeReq);
        var closeResp = await ReceiveTcpMessageAsync();

        byte status = closeResp[4];
        Assert.That(status, Is.EqualTo(0x09), "Should return EBADF (0x09)");
        TestContext.Out.WriteLine("✅ CLOSEDIR invalid handle correctly rejected with EBADF");
    }

    #endregion

    #region File Commands - Basic

    [Test, Order(20)]
    public async Task Test_20_OPEN_READ_CLOSE_Cycle()
    {
        // OPEN for reading
        var openReq = CreateOpenFileRequest(_sessionId, NextSequence(), "/test/file1.txt", 0x0001, 0x0000); // O_RDONLY
        await SendTcpMessageAsync(openReq);
        var openResp = await ReceiveTcpMessageAsync();

        Assert.That(openResp.Length, Is.GreaterThanOrEqualTo(6), "OPEN response too short");
        Assert.That(openResp[4], Is.EqualTo(0x00), "OPEN should succeed");
        byte fileHandle = openResp[5];
        TestContext.Out.WriteLine($"Opened file, handle: {fileHandle}");

        // READ
        var readReq = CreateReadRequest(_sessionId, NextSequence(), fileHandle, 256);
        await SendTcpMessageAsync(readReq);
        var readResp = await ReceiveTcpMessageAsync();

        Assert.That(readResp[4], Is.EqualTo(0x00), "READ should succeed");
        ushort bytesRead = BitConverter.ToUInt16(readResp, 5);
        TestContext.Out.WriteLine($"Read {bytesRead} bytes");
        Assert.That(bytesRead, Is.GreaterThan(0), "Should read some data");

        // CLOSE
        var closeReq = CreateCloseFileRequest(_sessionId, NextSequence(), fileHandle);
        await SendTcpMessageAsync(closeReq);
        var closeResp = await ReceiveTcpMessageAsync();
        Assert.That(closeResp[4], Is.EqualTo(0x00), "CLOSE should succeed");

        TestContext.Out.WriteLine("✅ OPEN-READ-CLOSE cycle successful");
    }

    [Test, Order(21)]
    public async Task Test_21_OPEN_InvalidFile()
    {
        var openReq = CreateOpenFileRequest(_sessionId, NextSequence(), "/test/nonexistent.txt", 0x0001, 0x0000);
        await SendTcpMessageAsync(openReq);
        var openResp = await ReceiveTcpMessageAsync();

        byte status = openResp[4];
        Assert.That(status, Is.EqualTo(0x02), "Should return ENOENT (0x02)");
        TestContext.Out.WriteLine("✅ OPEN nonexistent file correctly rejected with ENOENT");
    }

    [Test, Order(22)]
    public async Task Test_22_READ_InvalidHandle()
    {
        var readReq = CreateReadRequest(_sessionId, NextSequence(), 0xFF, 256);
        await SendTcpMessageAsync(readReq);
        var readResp = await ReceiveTcpMessageAsync();

        byte status = readResp[4];
        Assert.That(status, Is.EqualTo(0x09), "Should return EBADF (0x09)");
        TestContext.Out.WriteLine("✅ READ invalid handle correctly rejected with EBADF");
    }

    [Test, Order(23)]
    public async Task Test_23_CLOSE_InvalidHandle()
    {
        var closeReq = CreateCloseFileRequest(_sessionId, NextSequence(), 0xFF);
        await SendTcpMessageAsync(closeReq);
        var closeResp = await ReceiveTcpMessageAsync();

        byte status = closeResp[4];
        Assert.That(status, Is.EqualTo(0x09), "Should return EBADF (0x09)");
        TestContext.Out.WriteLine("✅ CLOSE invalid handle correctly rejected with EBADF");
    }

    #endregion

    #region File Commands - STAT

    [Test, Order(30)]
    public async Task Test_30_STAT_ExistingFile()
    {
        var statReq = CreateStatRequest(_sessionId, NextSequence(), "/test/file1.txt");
        await SendTcpMessageAsync(statReq);
        var statResp = await ReceiveTcpMessageAsync();

        Assert.That(statResp[4], Is.EqualTo(0x00), "STAT should succeed");
        Assert.That(statResp.Length, Is.GreaterThanOrEqualTo(27), "STAT response should contain stat info");

        // Parse stat info (mode, uid, gid, size, atime, mtime, ctime)
        ushort mode = BitConverter.ToUInt16(statResp, 5);
        uint size = BitConverter.ToUInt32(statResp, 11);

        TestContext.Out.WriteLine($"File mode: 0x{mode:X4}, size: {size} bytes");
        Assert.That(size, Is.GreaterThan(0), "File should have non-zero size");

        TestContext.Out.WriteLine("✅ STAT successful");
    }

    [Test, Order(31)]
    public async Task Test_31_STAT_NonexistentFile()
    {
        var statReq = CreateStatRequest(_sessionId, NextSequence(), "/test/nonexistent.txt");
        await SendTcpMessageAsync(statReq);
        var statResp = await ReceiveTcpMessageAsync();

        byte status = statResp[4];
        Assert.That(status, Is.EqualTo(0x02), "Should return ENOENT (0x02)");
        TestContext.Out.WriteLine("✅ STAT nonexistent file correctly rejected with ENOENT");
    }

    #endregion

    #region File Commands - LSEEK

    [Test, Order(40)]
    public async Task Test_40_LSEEK_Success()
    {
        // Open file
        var openReq = CreateOpenFileRequest(_sessionId, NextSequence(), "/test/file1.txt", 0x0001, 0x0000);
        await SendTcpMessageAsync(openReq);
        var openResp = await ReceiveTcpMessageAsync();
        byte fileHandle = openResp[5];

        // LSEEK to position 10
        var lseekReq = CreateLseekRequest(_sessionId, NextSequence(), fileHandle, 0x00, 10); // SEEK_SET, offset 10
        await SendTcpMessageAsync(lseekReq);
        var lseekResp = await ReceiveTcpMessageAsync();

        Assert.That(lseekResp[4], Is.EqualTo(0x00), "LSEEK should succeed");
        TestContext.Out.WriteLine("✅ LSEEK successful");

        // Close
        var closeReq = CreateCloseFileRequest(_sessionId, NextSequence(), fileHandle);
        await SendTcpMessageAsync(closeReq);
        await ReceiveTcpMessageAsync();
    }

    [Test, Order(41)]
    public async Task Test_41_LSEEK_InvalidHandle()
    {
        var lseekReq = CreateLseekRequest(_sessionId, NextSequence(), 0xFF, 0x00, 0);
        await SendTcpMessageAsync(lseekReq);
        var lseekResp = await ReceiveTcpMessageAsync();

        byte status = lseekResp[4];
        Assert.That(status, Is.EqualTo(0x09), "Should return EBADF (0x09)");
        TestContext.Out.WriteLine("✅ LSEEK invalid handle correctly rejected with EBADF");
    }

    #endregion

    #region File Commands - WRITE

    [Test, Order(50)]
    public async Task Test_50_WRITE_Success()
    {
        string testFile = "/writable/writetest.txt";
        byte[] testData = Encoding.ASCII.GetBytes("Test write data");

        // OPEN for writing (O_WRONLY | O_CREAT | O_TRUNC)
        var openReq = CreateOpenFileRequest(_sessionId, NextSequence(), testFile, 0x0202, 0x01B6); // 0644 permissions
        await SendTcpMessageAsync(openReq);
        var openResp = await ReceiveTcpMessageAsync();

        Assert.That(openResp[4], Is.EqualTo(0x00), "OPEN for write should succeed");
        byte fileHandle = openResp[5];

        // WRITE data
        var writeReq = CreateWriteRequest(_sessionId, NextSequence(), fileHandle, testData);
        await SendTcpMessageAsync(writeReq);
        var writeResp = await ReceiveTcpMessageAsync();

        Assert.That(writeResp[4], Is.EqualTo(0x00), "WRITE should succeed");
        ushort bytesWritten = BitConverter.ToUInt16(writeResp, 5);
        Assert.That(bytesWritten, Is.EqualTo(testData.Length), "Should write all bytes");

        // CLOSE
        var closeReq = CreateCloseFileRequest(_sessionId, NextSequence(), fileHandle);
        await SendTcpMessageAsync(closeReq);
        await ReceiveTcpMessageAsync();

        TestContext.Out.WriteLine($"✅ WRITE successful - wrote {bytesWritten} bytes");
    }

    [Test, Order(51)]
    public async Task Test_51_WRITE_InvalidHandle()
    {
        byte[] testData = Encoding.ASCII.GetBytes("Test");
        var writeReq = CreateWriteRequest(_sessionId, NextSequence(), 0xFF, testData);
        await SendTcpMessageAsync(writeReq);
        var writeResp = await ReceiveTcpMessageAsync();

        byte status = writeResp[4];
        Assert.That(status, Is.EqualTo(0x09), "Should return EBADF (0x09)");
        TestContext.Out.WriteLine("✅ WRITE invalid handle correctly rejected with EBADF");
    }

    #endregion

    #region Directory Commands - MKDIR/RMDIR

    [Test, Order(60)]
    public async Task Test_60_MKDIR_Success()
    {
        string testDir = "/writable/testdir_mkdir";

        var mkdirReq = CreateMkdirRequest(_sessionId, NextSequence(), testDir, 0x01FF); // 0777 permissions
        await SendTcpMessageAsync(mkdirReq);
        var mkdirResp = await ReceiveTcpMessageAsync();

        Assert.That(mkdirResp[4], Is.EqualTo(0x00), "MKDIR should succeed");
        TestContext.Out.WriteLine("✅ MKDIR successful");

        // Cleanup - RMDIR
        var rmdirReq = CreateRmdirRequest(_sessionId, NextSequence(), testDir);
        await SendTcpMessageAsync(rmdirReq);
        await ReceiveTcpMessageAsync();
    }

    [Test, Order(61)]
    public async Task Test_61_MKDIR_AlreadyExists()
    {
        // Create directory first
        string testDir = "/writable/testdir_exists";
        var mkdirReq1 = CreateMkdirRequest(_sessionId, NextSequence(), testDir, 0x01FF);
        await SendTcpMessageAsync(mkdirReq1);
        await ReceiveTcpMessageAsync();

        // Try to create again
        var mkdirReq2 = CreateMkdirRequest(_sessionId, NextSequence(), testDir, 0x01FF);
        await SendTcpMessageAsync(mkdirReq2);
        var mkdirResp = await ReceiveTcpMessageAsync();

        byte status = mkdirResp[4];
        Assert.That(status, Is.Not.EqualTo(0x00), "Should fail - directory exists");
        TestContext.Out.WriteLine($"✅ MKDIR duplicate correctly rejected with status 0x{status:X2}");

        // Cleanup
        var rmdirReq = CreateRmdirRequest(_sessionId, NextSequence(), testDir);
        await SendTcpMessageAsync(rmdirReq);
        await ReceiveTcpMessageAsync();
    }

    [Test, Order(62)]
    public async Task Test_62_RMDIR_Success()
    {
        // Create directory
        string testDir = "/writable/testdir_rmdir";
        var mkdirReq = CreateMkdirRequest(_sessionId, NextSequence(), testDir, 0x01FF);
        await SendTcpMessageAsync(mkdirReq);
        await ReceiveTcpMessageAsync();

        // Remove directory
        var rmdirReq = CreateRmdirRequest(_sessionId, NextSequence(), testDir);
        await SendTcpMessageAsync(rmdirReq);
        var rmdirResp = await ReceiveTcpMessageAsync();

        Assert.That(rmdirResp[4], Is.EqualTo(0x00), "RMDIR should succeed");
        TestContext.Out.WriteLine("✅ RMDIR successful");
    }

    [Test, Order(63)]
    public async Task Test_63_RMDIR_NonexistentDirectory()
    {
        var rmdirReq = CreateRmdirRequest(_sessionId, NextSequence(), "/writable/nonexistent_dir");
        await SendTcpMessageAsync(rmdirReq);
        var rmdirResp = await ReceiveTcpMessageAsync();

        byte status = rmdirResp[4];
        Assert.That(status, Is.EqualTo(0x02), "Should return ENOENT (0x02)");
        TestContext.Out.WriteLine("✅ RMDIR nonexistent directory correctly rejected with ENOENT");
    }

    #endregion

    #region File Commands - UNLINK

    [Test, Order(70)]
    public async Task Test_70_UNLINK_Success()
    {
        string testFile = "/writable/unlinktest.txt";

        // Create file first
        var openReq = CreateOpenFileRequest(_sessionId, NextSequence(), testFile, 0x0202, 0x01B6);
        await SendTcpMessageAsync(openReq);
        var openResp = await ReceiveTcpMessageAsync();
        byte fileHandle = openResp[5];

        var closeReq = CreateCloseFileRequest(_sessionId, NextSequence(), fileHandle);
        await SendTcpMessageAsync(closeReq);
        await ReceiveTcpMessageAsync();

        // UNLINK file
        var unlinkReq = CreateUnlinkRequest(_sessionId, NextSequence(), testFile);
        await SendTcpMessageAsync(unlinkReq);
        var unlinkResp = await ReceiveTcpMessageAsync();

        Assert.That(unlinkResp[4], Is.EqualTo(0x00), "UNLINK should succeed");
        TestContext.Out.WriteLine("✅ UNLINK successful");
    }

    [Test, Order(71)]
    public async Task Test_71_UNLINK_NonexistentFile()
    {
        var unlinkReq = CreateUnlinkRequest(_sessionId, NextSequence(), "/writable/nonexistent.txt");
        await SendTcpMessageAsync(unlinkReq);
        var unlinkResp = await ReceiveTcpMessageAsync();

        byte status = unlinkResp[4];
        Assert.That(status, Is.EqualTo(0x02), "Should return ENOENT (0x02)");
        TestContext.Out.WriteLine("✅ UNLINK nonexistent file correctly rejected with ENOENT");
    }

    #endregion

    #region File Commands - CHMOD

    [Test, Order(80)]
    public async Task Test_80_CHMOD_Success()
    {
        string testFile = "/writable/chmodtest.txt";

        // Create file first
        var openReq = CreateOpenFileRequest(_sessionId, NextSequence(), testFile, 0x0202, 0x01B6);
        await SendTcpMessageAsync(openReq);
        var openResp = await ReceiveTcpMessageAsync();
        byte fileHandle = openResp[5];

        var closeReq = CreateCloseFileRequest(_sessionId, NextSequence(), fileHandle);
        await SendTcpMessageAsync(closeReq);
        await ReceiveTcpMessageAsync();

        // CHMOD to 0644
        var chmodReq = CreateChmodRequest(_sessionId, NextSequence(), testFile, 0x01A4); // 0644
        await SendTcpMessageAsync(chmodReq);
        var chmodResp = await ReceiveTcpMessageAsync();

        Assert.That(chmodResp[4], Is.EqualTo(0x00), "CHMOD should succeed");
        TestContext.Out.WriteLine("✅ CHMOD successful");

        // Cleanup
        var unlinkReq = CreateUnlinkRequest(_sessionId, NextSequence(), testFile);
        await SendTcpMessageAsync(unlinkReq);
        await ReceiveTcpMessageAsync();
    }

    [Test, Order(81)]
    public async Task Test_81_CHMOD_NonexistentFile()
    {
        var chmodReq = CreateChmodRequest(_sessionId, NextSequence(), "/writable/nonexistent.txt", 0x01FF);
        await SendTcpMessageAsync(chmodReq);
        var chmodResp = await ReceiveTcpMessageAsync();

        byte status = chmodResp[4];
        Assert.That(status, Is.EqualTo(0x02), "Should return ENOENT (0x02)");
        TestContext.Out.WriteLine("✅ CHMOD nonexistent file correctly rejected with ENOENT");
    }

    #endregion

    #region File Commands - RENAME

    [Test, Order(90)]
    public async Task Test_90_RENAME_Success()
    {
        string sourceFile = "/writable/rename_source.txt";
        string destFile = "/writable/rename_dest.txt";

        // Create source file
        var openReq = CreateOpenFileRequest(_sessionId, NextSequence(), sourceFile, 0x0202, 0x01B6);
        await SendTcpMessageAsync(openReq);
        var openResp = await ReceiveTcpMessageAsync();
        byte fileHandle = openResp[5];

        var closeReq = CreateCloseFileRequest(_sessionId, NextSequence(), fileHandle);
        await SendTcpMessageAsync(closeReq);
        await ReceiveTcpMessageAsync();

        // RENAME
        var renameReq = CreateRenameRequest(_sessionId, NextSequence(), sourceFile, destFile);
        await SendTcpMessageAsync(renameReq);
        var renameResp = await ReceiveTcpMessageAsync();

        Assert.That(renameResp[4], Is.EqualTo(0x00), "RENAME should succeed");
        TestContext.Out.WriteLine("✅ RENAME successful");

        // Cleanup
        var unlinkReq = CreateUnlinkRequest(_sessionId, NextSequence(), destFile);
        await SendTcpMessageAsync(unlinkReq);
        await ReceiveTcpMessageAsync();
    }

    [Test, Order(91)]
    public async Task Test_91_RENAME_NonexistentSource()
    {
        var renameReq = CreateRenameRequest(_sessionId, NextSequence(), "/writable/nonexistent.txt", "/writable/dest.txt");
        await SendTcpMessageAsync(renameReq);
        var renameResp = await ReceiveTcpMessageAsync();

        byte status = renameResp[4];
        Assert.That(status, Is.EqualTo(0x02), "Should return ENOENT (0x02)");
        TestContext.Out.WriteLine("✅ RENAME nonexistent source correctly rejected with ENOENT");
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Gets the next sequence number for commands on the shared TCP connection.
    /// Sequence numbers must increment for each command on the same session.
    /// </summary>
    private byte NextSequence()
    {
        return ++_sequenceNumber;
    }

    private async Task SendTcpMessageAsync(byte[] message)
    {
        if (_stream == null) throw new InvalidOperationException("Stream not initialized");
        await SendTcpMessageAsync(_stream, message);
    }

    private async Task SendTcpMessageAsync(NetworkStream stream, byte[] message)
    {
        // TCP: Send raw TNFS packet (NO length framing per TNFS spec)
        await stream.WriteAsync(message, 0, message.Length);
        await stream.FlushAsync();
    }

    private async Task<byte[]> ReceiveTcpMessageAsync()
    {
        if (_stream == null) throw new InvalidOperationException("Stream not initialized");
        return await ReceiveTcpMessageAsync(_stream);
    }

    private async Task<byte[]> ReceiveTcpMessageAsync(NetworkStream stream)
    {
        // TCP: Read raw TNFS packet (NO length framing per TNFS spec)
        // TNFS header is 4 bytes: SessionID(2) + Sequence(1) + Command(1)
        // Response includes: SessionID(2) + Sequence(1) + Command(1) + Status(1) + [Data...]

        // Read header first (5 bytes minimum for response)
        byte[] buffer = new byte[512]; // Max TNFS packet size
        int totalRead = 0;

        // Create cancellation token with timeout
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(TnfsTestConfig.CONNECTION_TIMEOUT_MS / 1000.0));

        // Read at least 5 bytes (header + status)
        while (totalRead < 5)
        {
            int read = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead, cts.Token);
            if (read == 0)
                throw new Exception("Connection closed while reading response");
            totalRead += read;

            // Give server time to send complete packet
            if (totalRead >= 5 && stream.DataAvailable == false)
                break;
        }

        // Read any remaining data if available
        while (stream.DataAvailable)
        {
            int read = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead, cts.Token);
            if (read == 0) break;
            totalRead += read;
        }

        byte[] message = new byte[totalRead];
        Array.Copy(buffer, message, totalRead);
        return message;
    }

    private byte[] CreateMountRequest(string path)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write((ushort)0x0000); // SessionID = 0 for MOUNT
        bw.Write((byte)0x00); // Sequence
        bw.Write((byte)0x00); // TNFS_MOUNT
        bw.Write((byte)0x01); // Version LSB
        bw.Write((byte)0x00); // Version MSB
        bw.Write(Encoding.ASCII.GetBytes(path));
        bw.Write((byte)0x00); // Null terminator

        return ms.ToArray();
    }

    private byte[] CreateUnmountRequest(ushort sessionId)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write((byte)0xFF); // Sequence
        bw.Write((byte)0x01); // TNFS_UMOUNT

        return ms.ToArray();
    }

    private byte[] CreateOpenDirRequest(ushort sessionId, byte sequence, string path)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write(sequence);
        bw.Write((byte)0x10); // TNFS_OPENDIR
        bw.Write(Encoding.ASCII.GetBytes(path));
        bw.Write((byte)0x00); // Null terminator

        return ms.ToArray();
    }

    private byte[] CreateReadDirRequest(ushort sessionId, byte sequence, byte handle)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write(sequence);
        bw.Write((byte)0x11); // TNFS_READDIR
        bw.Write(handle);

        return ms.ToArray();
    }

    private byte[] CreateCloseDirRequest(ushort sessionId, byte sequence, byte handle)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write(sequence);
        bw.Write((byte)0x12); // TNFS_CLOSEDIR
        bw.Write(handle);

        return ms.ToArray();
    }

    private byte[] CreateOpenFileRequest(ushort sessionId, byte sequence, string path, ushort flags, ushort mode)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write(sequence);
        bw.Write((byte)0x29); // TNFS_OPEN
        bw.Write(flags);
        bw.Write(mode);
        bw.Write(Encoding.ASCII.GetBytes(path));
        bw.Write((byte)0x00); // Null terminator

        return ms.ToArray();
    }

    private byte[] CreateReadRequest(ushort sessionId, byte sequence, byte handle, ushort length)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write(sequence);
        bw.Write((byte)0x21); // TNFS_READ
        bw.Write(handle);
        bw.Write(length);

        return ms.ToArray();
    }

    private byte[] CreateCloseFileRequest(ushort sessionId, byte sequence, byte handle)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write(sequence);
        bw.Write((byte)0x23); // TNFS_CLOSE
        bw.Write(handle);

        return ms.ToArray();
    }

    private byte[] CreateStatRequest(ushort sessionId, byte sequence, string path)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write(sequence);
        bw.Write((byte)0x24); // TNFS_STAT
        bw.Write(Encoding.ASCII.GetBytes(path));
        bw.Write((byte)0x00); // Null terminator

        return ms.ToArray();
    }

    private byte[] CreateLseekRequest(ushort sessionId, byte sequence, byte handle, byte whence, int offset)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write(sequence);
        bw.Write((byte)0x25); // TNFS_LSEEK
        bw.Write(handle);
        bw.Write(whence);
        bw.Write(offset);

        return ms.ToArray();
    }

    private byte[] CreateWriteRequest(ushort sessionId, byte sequence, byte handle, byte[] data)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write(sequence);
        bw.Write((byte)0x22); // TNFS_WRITE
        bw.Write(handle);
        bw.Write((ushort)data.Length); // Length of data
        bw.Write(data);

        return ms.ToArray();
    }

    private byte[] CreateMkdirRequest(ushort sessionId, byte sequence, string path, ushort mode)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write(sequence);
        bw.Write((byte)0x13); // TNFS_MKDIR
        bw.Write(mode);
        bw.Write(Encoding.ASCII.GetBytes(path));
        bw.Write((byte)0x00); // Null terminator

        return ms.ToArray();
    }

    private byte[] CreateRmdirRequest(ushort sessionId, byte sequence, string path)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write(sequence);
        bw.Write((byte)0x14); // TNFS_RMDIR
        bw.Write(Encoding.ASCII.GetBytes(path));
        bw.Write((byte)0x00); // Null terminator

        return ms.ToArray();
    }

    private byte[] CreateUnlinkRequest(ushort sessionId, byte sequence, string path)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write(sequence);
        bw.Write((byte)0x26); // TNFS_UNLINK
        bw.Write(Encoding.ASCII.GetBytes(path));
        bw.Write((byte)0x00); // Null terminator

        return ms.ToArray();
    }

    private byte[] CreateChmodRequest(ushort sessionId, byte sequence, string path, ushort mode)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write(sequence);
        bw.Write((byte)0x27); // TNFS_CHMOD
        bw.Write(mode);
        bw.Write(Encoding.ASCII.GetBytes(path));
        bw.Write((byte)0x00); // Null terminator

        return ms.ToArray();
    }

    private byte[] CreateRenameRequest(ushort sessionId, byte sequence, string sourcePath, string destPath)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(sessionId);
        bw.Write(sequence);
        bw.Write((byte)0x28); // TNFS_RENAME
        bw.Write(Encoding.ASCII.GetBytes(sourcePath));
        bw.Write((byte)0x00); // Null terminator
        bw.Write(Encoding.ASCII.GetBytes(destPath));
        bw.Write((byte)0x00); // Null terminator

        return ms.ToArray();
    }

    private string ParseNullTerminatedString(byte[] buffer, int offset)
    {
        int end = offset;
        while (end < buffer.Length && buffer[end] != 0)
            end++;
        return Encoding.ASCII.GetString(buffer, offset, end - offset);
    }

    private void ValidateResponse(byte[] response, int minLength, string operation)
    {
        if (response.Length < 5)
        {
            string hex = BitConverter.ToString(response);
            throw new Exception($"{operation} response too short: {response.Length} bytes (expected >=5). Response: {hex}");
        }

        byte status = response[4];
        if (status != 0x00)
        {
            string hex = BitConverter.ToString(response);
            string statusName = ((TnfsReturnCode)status).ToString();

            string hint = "";
            if (status == 0x02) // ENOENT
            {
                hint = $"\nHINT: Ensure C TNFSD server at {SERVER_HOST}:{SERVER_PORT} is serving directory: {_testRoot}";
                hint += $"\nHINT: Directory structure should exist on REMOTE server, not just local machine.";
            }

            throw new Exception($"{operation} failed with status 0x{status:X2} ({statusName}). Response: {hex}{hint}");
        }

        if (response.Length < minLength)
        {
            string hex = BitConverter.ToString(response);
            throw new Exception($"{operation} response too short: {response.Length} bytes (expected >={minLength}). Response: {hex}");
        }
    }

    #endregion
}
