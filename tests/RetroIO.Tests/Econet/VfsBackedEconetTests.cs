//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Storage.Core.VFS;
using RetroIO.Protocols.Econet.FileServer;
using RetroIO.Protocols.Econet.FileServer.Bulk;
using RetroIO.Protocols.Econet.FileServer.Configuration;
using RetroIO.Protocols.Econet.FileServer.Discs;
using RetroIO.Protocols.Econet.FileServer.Handles;
using RetroIO.Protocols.Econet.FileServer.Paths;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Storage.Providers;

namespace RetroIO.Tests.Econet;

/// <summary>
/// End-to-end proof that the Econet file server is transparent over a <see cref="VfsFileSystemBackend"/>:
/// the whole stack (disc catalogue, path resolver, login, ReadUserEnv, GetObjectInfo) runs against a
/// real <c>native</c> VFS mount instead of the local/mock backend, and produces the expected replies.
/// The logical Econet root (<c>/econet-root</c>) is mapped by the adapter onto a real temp directory
/// through the mount, so this exercises the host-path→URN translation under real fsops.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class VfsBackedEconetTests
{
    private const string EconetRoot = "/econet-root";

    private string _diskRoot = null!;   // the real dir the native mount points at (== logical /econet-root)
    private string _cacheDir = null!;
    private VfsCore _vfs = null!;
    private FileServerCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        // Real on-disk catalogue: <diskRoot>/0RetroFS/USER/DATA — mirrors the mock sample layout so the
        // disc enumerates as "RetroFS" and the default home resolves to .../USER (leaf "USER").
        _diskRoot = Path.Combine(Path.GetTempPath(), "retrofs-vfseconet-" + Guid.NewGuid().ToString("N"));
        _cacheDir = _diskRoot + "-cache"; // OUTSIDE the mount so it isn't seen as a disc
        Directory.CreateDirectory(Path.Combine(_diskRoot, "0RetroFS", "USER"));
        File.WriteAllText(Path.Combine(_diskRoot, "0RetroFS", "USER", "DATA"), "hello vfs");

        _vfs = new VfsCore(enablePersistence: false);
        _vfs.RegisterProvider(new NativeVolumeProvider());
        _vfs.Mount("native", _diskRoot, "vol0", readOnly: false);

        // The adapter maps the logical /econet-root onto the native mount (whose root is _diskRoot).
        VfsFileSystemBackend backend =
            new VfsFileSystemBackend(_vfs, "native", "vol0", hostRoot: EconetRoot, tempCacheDir: _cacheDir);

        EconetUserDirectory users = EconetTestUsers.CreateDirectory(out _);
        DiscCatalog discs = new DiscCatalog(backend, EconetRoot, "RetroFS");
        FileServerSessionManager sessions = new FileServerSessionManager(backend, users, discs, EconetRoot);
        FileServerHandleManager handles = new FileServerHandleManager();
        PathResolver paths = new PathResolver(backend, discs, EconetRoot);
        BulkTransferManager bulk = new BulkTransferManager();

        FileServerContext context = new FileServerContext(
            backend, users, discs, sessions, handles, paths, bulk,
            new FileServerConfiguration(), new FileServerClock(),
            EconetRoot, "RetroFS", FileServerCommandHandler.DefaultVersionString);

        _handler = new FileServerCommandHandler(context, NullLogger<FileServerCommandHandler>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _vfs?.Dispose();
        try { Directory.Delete(_diskRoot, recursive: true); } catch { }
        try { Directory.Delete(_cacheDir, recursive: true); } catch { }
    }

    private EconetMessage OsCli(string command) =>
        _handler.Handle(EconetFsTestHarness.CreateRequest(FileServerOpcodes.OsCli, Encoding.ASCII.GetBytes(command)))!;

    private EconetMessage LoggedIn(byte opcode, byte[] p) =>
        _handler.Handle(EconetFsTestHarness.CreateLoggedInRequest(opcode, p))!;

    [Test]
    public void Login_OverVfsBackend_Succeeds()
    {
        EconetMessage reply = OsCli("I AM SYST \r");

        Assert.That(reply.Payload[0], Is.EqualTo(FileServerOpcodes.LoginSuccessReturnCode));
        Assert.That(reply.Payload[1], Is.EqualTo(0));
    }

    [Test]
    public void ReadUserEnv_OverVfsBackend_ReportsDiscAndHomeLeaf()
    {
        OsCli("I AM SYST \r");

        EconetMessage env = LoggedIn(FileServerOpcodes.ReadUserEnv, Array.Empty<byte>());

        // 0x15: [0,0,16, disc[16], CSD leaf[10], lib leaf[10]]. Disc "RetroFS", home leaf "USER" -- the
        // disc catalogue enumeration + home resolution + catalogue naming all went through the adapter.
        Assert.That(env.Payload, Has.Length.EqualTo(39));
        Assert.That(Encoding.ASCII.GetString(env.Payload, 3, 16).TrimEnd(), Is.EqualTo("RetroFS"));
        Assert.That(Encoding.ASCII.GetString(env.Payload, 19, 10).TrimEnd(), Is.EqualTo("USER"));
    }
}
