//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Tnfs.Client;
using RetroIO.Protocols.Tnfs.Protocol;

namespace RetroIO.Tests.TNFS;

/// <summary>
/// High-level integration tests for TnfsClient against reference C TNFSD server.
///
/// Covers every TNFS command TnfsClient exposes a public method for: MOUNT, UMOUNT, OPENDIR,
/// READDIR, CLOSEDIR, MKDIR, RMDIR, OPEN, READ, WRITE, CLOSE, LSEEK, STAT, UNLINK, CHMOD, RENAME.
/// OPENDIRX and READDIRX are exercised indirectly — OpenDir always issues OPENDIRX under the hood
/// (see TnfsClient.OpenDir) and ReadDir tries READDIRX first with a READDIR fallback (see
/// TnfsClient.ReadDir) — Test_10_OpenDir_Success/Test_11_ReadDir_Success cover them at the wire
/// protocol level even though they're not separately-named client methods.
///
/// NOT covered, and cannot be until TnfsClient itself grows the corresponding methods (this is a
/// client feature gap, not a test gap — there is nothing here to call):
///   - TELLDIR / SEEKDIR (directory-position save/restore; no TnfsClient method exists)
///   - OPENFILE_OLD (legacy pre-extended OPEN variant; TnfsClient only ever issues the current OPEN)
/// This doc comment previously claimed "ALL TNFS commands" were tested here, which was not true —
/// corrected rather than papered over with tests that would call nothing real.
///
/// SERVER REQUIREMENTS:
/// ===================
///
/// 1. C TNFSD Server Configuration:
///    - Running on: See TnfsTestConfig.REMOTE_SERVER_ADDRESS:REMOTE_SERVER_PORT
///    - TCP transport: ENABLED
///    - Server root: See TnfsTestConfig.REMOTE_SERVER_ROOT
///
/// 2. Test Setup:
///    - Tests will automatically create required directory structure on LOCAL machine
///    - YOU MUST manually ensure the C server has matching structure:
///      {SERVER_ROOT}/test/           (read-only test files)
///      {SERVER_ROOT}/test/subdir/    (subdirectory with files)
///      {SERVER_ROOT}/writable/       (read-write test area)
///
/// 3. Start C TNFSD Server:
///    tnfsd {SERVER_ROOT} --port {PORT} --tcp
///
/// 4. Run Tests:
///    dotnet test --filter ClassName=TnfsClientIntegrationTests
///
/// </summary>
[TestFixture]
[Category("Manual")]
[Category("ExternalTNFSD")]
[Category("ClientIntegration")]
[Explicit("Requires manually started C TNFSD server (see TnfsTestConfig) with TCP transport")]
public class TnfsClientIntegrationTests
{
    private static readonly string SERVER_HOST = TnfsTestConfig.REMOTE_SERVER_ADDRESS;
    private const int SERVER_PORT = TnfsTestConfig.REMOTE_SERVER_PORT;
    private TnfsClient? _client;
    private string _testRoot = TnfsTestConfig.REMOTE_SERVER_ROOT;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Console.WriteLine($"Using test root: {_testRoot}");
        Console.WriteLine($"Target server: {SERVER_HOST}:{SERVER_PORT}");
        Console.WriteLine();
        Console.WriteLine("WARNING: You must manually ensure test files exist on the REMOTE server!");
        Console.WriteLine($"         Expected structure on {SERVER_HOST}:");
        Console.WriteLine($"         {_testRoot}/test/file1.txt");
        Console.WriteLine($"         {_testRoot}/test/file2.txt");
        Console.WriteLine($"         {_testRoot}/test/subdir/nested.txt");
        Console.WriteLine($"         {_testRoot}/writable/ (writable directory)");
        Console.WriteLine();
    }

    [SetUp]
    public void SetUp()
    {
        // Create fresh client for each test
        _client = new TnfsClient(SERVER_HOST, SERVER_PORT, useTcp: true);
    }

    [TearDown]
    public void TearDown()
    {
        // Unmount and disconnect after each test
        if (_client != null && _client.IsMounted)
        {
            _client.Unmount();
        }
        _client?.Dispose();
        _client = null;
    }

    // ====================================================================
    // SESSION TESTS
    // ====================================================================

    [Test, Order(1)]
    public void Test_01_Mount_Success()
    {
        var result = _client!.Mount("/");

        Assert.That(result.Success, Is.True, $"Mount failed: {result.ErrorMessage}");
        Assert.That(_client.IsMounted, Is.True, "Client should be mounted");

        Console.WriteLine($"✓ Mounted successfully. Session ID: {_client.SessionId:X4}");
    }

    [Test, Order(2)]
    public void Test_02_Unmount_Success()
    {
        // Mount first
        var mountResult = _client!.Mount("/");
        Assert.That(mountResult.Success, Is.True);

        // Unmount
        var unmountResult = _client.Unmount();

        Assert.That(unmountResult.Success, Is.True, $"Unmount failed: {unmountResult.ErrorMessage}");
        Assert.That(_client.IsMounted, Is.False, "Client should not be mounted");

        Console.WriteLine("✓ Unmounted successfully");
    }

    // ====================================================================
    // DIRECTORY OPERATION TESTS
    // ====================================================================

    /// <summary>
    /// Also covers OPENDIRX — TnfsClient.OpenDir always issues OPENDIRX on the wire (see its doc
    /// comment), there is no separate "plain OPENDIR" code path to test independently.
    /// </summary>
    [Test, Order(10)]
    public void Test_10_OpenDir_Success()
    {
        _client!.Mount("/");

        var result = _client.OpenDir("/test");

        Assert.That(result.Success, Is.True, $"OpenDir failed: {result.ErrorMessage}");
        Assert.That(result.Data, Is.GreaterThanOrEqualTo(0), "Directory handle should be valid (>= 0)");

        Console.WriteLine($"✓ Opened directory /test. Handle: {result.Data}");

        _client.CloseDir(result.Data);
    }

    /// <summary>
    /// Also covers READDIRX — TnfsClient.ReadDir tries READDIRX first and only falls back to plain
    /// READDIR if the server rejects it (see its doc comment); against a modern TNFSD server this
    /// test exercises the READDIRX path.
    /// </summary>
    [Test, Order(11)]
    public void Test_11_ReadDir_Success()
    {
        _client!.Mount("/");
        var openResult = _client.OpenDir("/test");
        Assert.That(openResult.Success, Is.True);

        byte handle = openResult.Data;
        int entriesRead = 0;

        // Read all entries
        while (true)
        {
            var readResult = _client.ReadDir(handle);

            // EOF is returned as SUCCESS with null data
            if (!readResult.Success || readResult.Data == null)
            {
                Console.WriteLine($"✓ Reached end of directory after {entriesRead} entries");
                break;
            }

            entriesRead++;
            Console.WriteLine($"  Entry {entriesRead}: {readResult.Data}");
        }

        Assert.That(entriesRead, Is.GreaterThan(0), "Should read at least one entry");

        _client.CloseDir(handle);
    }

    [Test, Order(12)]
    public void Test_12_CloseDir_Success()
    {
        _client!.Mount("/");
        var openResult = _client.OpenDir("/test");
        Assert.That(openResult.Success, Is.True);

        var closeResult = _client.CloseDir(openResult.Data);

        Assert.That(closeResult.Success, Is.True,
            $"CloseDir failed: {closeResult.ErrorMessage}");

        Console.WriteLine($"✓ Closed directory handle {openResult.Data}");
    }

    [Test, Order(13)]
    public void Test_13_MkDir_Success()
    {
        _client!.Mount("/");

        string dirName = $"/writable/testdir_{DateTime.Now.Ticks}";
        var result = _client.MkDir(dirName);

        Assert.That(result.Success, Is.True,
            $"MkDir failed: {result.ErrorMessage}");

        Console.WriteLine($"✓ Created directory: {dirName}");

        // Cleanup
        _client.RmDir(dirName);
    }

    [Test, Order(14)]
    public void Test_14_RmDir_Success()
    {
        _client!.Mount("/");

        // Create directory first
        string dirName = $"/writable/testdir_{DateTime.Now.Ticks}";
        var createResult = _client.MkDir(dirName);
        Assert.That(createResult.Success, Is.True);

        // Remove it
        var result = _client.RmDir(dirName);

        Assert.That(result.Success, Is.True,
            $"RmDir failed: {result.ErrorMessage}");

        Console.WriteLine($"✓ Removed directory: {dirName}");
    }

    // ====================================================================
    // FILE I/O TESTS
    // ====================================================================

    [Test, Order(20)]
    public void Test_20_Open_ForRead_Success()
    {
        _client!.Mount("/");

        var result = _client.Open("/test/file1.txt", TnfsOpenFlags.O_RDONLY);

        Assert.That(result.Success, Is.True,
            $"Open failed: {result.ErrorMessage}");
        Assert.That(result.Data, Is.GreaterThanOrEqualTo(0), "File handle should be valid (>= 0)");

        Console.WriteLine($"✓ Opened /test/file1.txt for reading. Handle: {result.Data}");

        _client.Close(result.Data);
    }

    [Test, Order(21)]
    public void Test_21_Read_Success()
    {
        _client!.Mount("/");
        var openResult = _client.Open("/test/file1.txt", TnfsOpenFlags.O_RDONLY);
        Assert.That(openResult.Success, Is.True);

        ushort handle = openResult.Data;
        var result = _client.Read(handle, 100);

        Assert.That(result.Success, Is.True,
            $"Read failed: {result.ErrorMessage}");
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data.Length, Is.GreaterThan(0), "Should read some data");

        string content = Encoding.ASCII.GetString(result.Data);
        Console.WriteLine($"✓ Read {result.Data.Length} bytes: \"{content.Substring(0, Math.Min(40, content.Length))}...\"");

        _client.Close(handle);
    }

    [Test, Order(22)]
    public void Test_22_Write_Success()
    {
        _client!.Mount("/");

        // Create new file for writing
        string fileName = $"/writable/testfile_{DateTime.Now.Ticks}.txt";
        var openResult = _client.Open(fileName,
            TnfsOpenFlags.O_WRONLY | TnfsOpenFlags.O_CREAT | TnfsOpenFlags.O_TRUNC);
        Assert.That(openResult.Success, Is.True);

        ushort handle = openResult.Data;
        byte[] data = Encoding.ASCII.GetBytes("Test write data");

        var result = _client.Write(handle, data);

        Assert.That(result.Success, Is.True,
            $"Write failed: {result.ErrorMessage}");
        Assert.That(result.Data, Is.EqualTo((ushort)data.Length),
            "Should write all bytes");

        Console.WriteLine($"✓ Wrote {result.Data} bytes to {fileName}");

        _client.Close(handle);
        _client.Unlink(fileName); // Cleanup
    }

    [Test, Order(23)]
    public void Test_23_Close_Success()
    {
        _client!.Mount("/");
        var openResult = _client.Open("/test/file1.txt", TnfsOpenFlags.O_RDONLY);
        Assert.That(openResult.Success, Is.True);

        var result = _client.Close(openResult.Data);

        Assert.That(result.Success, Is.True,
            $"Close failed: {result.ErrorMessage}");

        Console.WriteLine($"✓ Closed file handle {openResult.Data}");
    }

    [Test, Order(24)]
    public void Test_24_Lseek_Success()
    {
        _client!.Mount("/");
        var openResult = _client.Open("/test/file1.txt", TnfsOpenFlags.O_RDONLY);
        Assert.That(openResult.Success, Is.True);

        ushort handle = openResult.Data;

        // Seek to position 10
        var result = _client.Lseek(handle, 10, (byte)TnfsSeekOrigin.SeekSet);

        Assert.That(result.Success, Is.True,
            $"Lseek failed: {result.ErrorMessage}");

        Console.WriteLine($"✓ Seeked to position 10 in file");

        _client.Close(handle);
    }

    // ====================================================================
    // FILE OPERATION TESTS
    // ====================================================================

    [Test, Order(30)]
    public void Test_30_Stat_Success()
    {
        _client!.Mount("/");

        var result = _client.Stat("/test/file1.txt");

        Assert.That(result.Success, Is.True,
            $"Stat failed: {result.ErrorMessage}");
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data.Size, Is.GreaterThan(0), "File should have size");

        Console.WriteLine($"✓ Stat /test/file1.txt:");
        Console.WriteLine($"  Size: {result.Data.Size} bytes");
        Console.WriteLine($"  Mode: 0x{result.Data.Permissions:X4}");
    }

    [Test, Order(31)]
    public void Test_31_Unlink_Success()
    {
        _client!.Mount("/");

        // Create file first
        string fileName = $"/writable/deletetest_{DateTime.Now.Ticks}.txt";
        var createResult = _client.Open(fileName,
            TnfsOpenFlags.O_WRONLY | TnfsOpenFlags.O_CREAT);
        Assert.That(createResult.Success, Is.True);
        _client.Close(createResult.Data);

        // Delete it
        var result = _client.Unlink(fileName);

        Assert.That(result.Success, Is.True,
            $"Unlink failed: {result.ErrorMessage}");

        Console.WriteLine($"✓ Deleted file: {fileName}");
    }

    [Test, Order(32)]
    [Ignore("CHMOD not implemented in TNFS server (empty function stub)")]
    public void Test_32_Chmod_Success()
    {
        _client!.Mount("/");

        // Create file first
        string fileName = $"/writable/chmodtest_{DateTime.Now.Ticks}.txt";
        var createResult = _client.Open(fileName,
            TnfsOpenFlags.O_WRONLY | TnfsOpenFlags.O_CREAT);
        Assert.That(createResult.Success, Is.True);
        _client.Close(createResult.Data);

        // Change permissions
        ushort newMode = 0x01A4; // 0644 octal
        var result = _client.Chmod(fileName, newMode);

        Assert.That(result.Success, Is.True,
            $"Chmod failed: {result.ErrorMessage}");

        Console.WriteLine($"✓ Changed permissions of {fileName} to 0x{newMode:X4}");

        _client.Unlink(fileName); // Cleanup
    }

    [Test, Order(33)]
    public void Test_33_Rename_Success()
    {
        _client!.Mount("/");

        // Create file first
        string oldName = $"/writable/oldname_{DateTime.Now.Ticks}.txt";
        string newName = $"/writable/newname_{DateTime.Now.Ticks}.txt";

        var createResult = _client.Open(oldName,
            TnfsOpenFlags.O_WRONLY | TnfsOpenFlags.O_CREAT);
        Assert.That(createResult.Success, Is.True);
        _client.Close(createResult.Data);

        // Rename it
        var result = _client.Rename(oldName, newName);

        Assert.That(result.Success, Is.True,
            $"Rename failed: {result.ErrorMessage}");

        Console.WriteLine($"✓ Renamed {oldName} to {newName}");

        _client.Unlink(newName); // Cleanup
    }

    // ====================================================================
    // INTEGRATION TESTS (Multiple Operations)
    // ====================================================================

    [Test, Order(100)]
    public void Test_100_Complete_File_Lifecycle()
    {
        Console.WriteLine("Testing complete file lifecycle:");

        _client!.Mount("/");
        string fileName = $"/writable/lifecycle_{DateTime.Now.Ticks}.txt";

        // 1. Create file
        var createResult = _client.Open(fileName,
            TnfsOpenFlags.O_WRONLY | TnfsOpenFlags.O_CREAT | TnfsOpenFlags.O_TRUNC);
        Assert.That(createResult.Success, Is.True);
        Console.WriteLine($"  1. Created {fileName}");

        // 2. Write data
        byte[] data = Encoding.ASCII.GetBytes("Lifecycle test content");
        var writeResult = _client.Write(createResult.Data, data);
        Assert.That(writeResult.Success, Is.True);
        Console.WriteLine($"  2. Wrote {writeResult.Data} bytes");

        // 3. Close file
        _client.Close(createResult.Data);
        Console.WriteLine("  3. Closed file");

        // 4. Stat file
        var statResult = _client.Stat(fileName);
        Assert.That(statResult.Success, Is.True);
        Assert.That(statResult.Data, Is.Not.Null);
        Assert.That(statResult.Data!.Size, Is.EqualTo(data.Length));
        Console.WriteLine($"  4. Stat: size={statResult.Data.Size} bytes");

        // 5. Open for reading
        var readOpenResult = _client.Open(fileName, TnfsOpenFlags.O_RDONLY);
        Assert.That(readOpenResult.Success, Is.True);
        Console.WriteLine("  5. Opened for reading");

        // 6. Read data
        var readResult = _client.Read(readOpenResult.Data, 100);
        Assert.That(readResult.Success, Is.True);
        Assert.That(readResult.Data, Is.Not.Null);
        string content = Encoding.ASCII.GetString(readResult.Data!);
        Assert.That(content, Is.EqualTo("Lifecycle test content"));
        Console.WriteLine($"  6. Read: \"{content}\"");

        // 7. Close file
        _client.Close(readOpenResult.Data);
        Console.WriteLine("  7. Closed file");

        // 8. Delete file
        var deleteResult = _client.Unlink(fileName);
        Assert.That(deleteResult.Success, Is.True);
        Console.WriteLine($"  8. Deleted {fileName}");

        Console.WriteLine("✓ Complete lifecycle test passed");
    }

    [Test, Order(101)]
    public void Test_101_Complete_Directory_Lifecycle()
    {
        Console.WriteLine("Testing complete directory lifecycle:");

        _client!.Mount("/");
        string dirName = $"/writable/dirtest_{DateTime.Now.Ticks}";

        // 1. Create directory
        var createResult = _client.MkDir(dirName);
        Assert.That(createResult.Success, Is.True);
        Console.WriteLine($"  1. Created {dirName}");

        // 2. Create file in directory
        string fileName = $"{dirName}/testfile.txt";
        var createFileResult = _client.Open(fileName,
            TnfsOpenFlags.O_WRONLY | TnfsOpenFlags.O_CREAT);
        Assert.That(createFileResult.Success, Is.True);
        _client.Close(createFileResult.Data);
        Console.WriteLine($"  2. Created {fileName}");

        // 3. List directory
        var openDirResult = _client.OpenDir(dirName);
        Assert.That(openDirResult.Success, Is.True);
        Console.WriteLine($"  3. Opened directory for reading");

        int entries = 0;
        while (true)
        {
            var readResult = _client.ReadDir(openDirResult.Data);
            // EOF is returned as SUCCESS with null data
            if (!readResult.Success || readResult.Data == null) break;
            entries++;
            Console.WriteLine($"     Entry: {readResult.Data}");
        }

        _client.CloseDir(openDirResult.Data);
        Assert.That(entries, Is.GreaterThan(0));
        Console.WriteLine($"  4. Listed {entries} entries");

        // 5. Delete file
        _client.Unlink(fileName);
        Console.WriteLine($"  5. Deleted {fileName}");

        // 6. Remove directory
        var removeDirResult = _client.RmDir(dirName);
        Assert.That(removeDirResult.Success, Is.True);
        Console.WriteLine($"  6. Removed {dirName}");

        Console.WriteLine("✓ Complete directory lifecycle test passed");
    }
}
