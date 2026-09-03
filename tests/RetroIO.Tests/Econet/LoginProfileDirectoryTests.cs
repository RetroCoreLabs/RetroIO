//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
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
using RetroIO.Identity;
using RetroIO.Tests.TNFS;

namespace RetroIO.Tests.Econet;

/// <summary>
/// End-to-end proof that the login path honours a user's persisted <c>econet.home</c> / <c>econet.lib</c>
/// preferences (the SETLIB/SETHOME wiring): a user WITH those set logs in with the CSD/library reported
/// by ReadUserEnv (0x15) pointing at the configured directories, while a preference pointing at a
/// missing directory falls back to the disc-layout default. The default-user path stays byte-identical
/// (pinned separately by <see cref="GoldenLoginEnvTests"/>).
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class LoginProfileDirectoryTests
{
    private MockFileSystemBackend _fs = null!;
    private IRetroIDP _idp = null!;
    private EconetUserDirectory _users = null!;
    private FileServerCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _fs = new MockFileSystemBackend();
        EconetSampleCatalogFixture.Seed(_fs);
        // Extra directories on disc 0 for the profile preferences to point at.
        _fs.AddDirectory("/econet-root/0RetroFS/HOMEDIR");
        _fs.AddDirectory("/econet-root/0RetroFS/LIBDIR");

        _users = EconetTestUsers.CreateDirectory(out _idp);
        DiscCatalog discs = new DiscCatalog(_fs, "/econet-root", "RetroFS");
        FileServerSessionManager sessions = new FileServerSessionManager(_fs, _users, discs, "/econet-root");
        FileServerHandleManager handles = new FileServerHandleManager();
        PathResolver paths = new PathResolver(_fs, discs, "/econet-root");
        BulkTransferManager bulk = new BulkTransferManager();

        FileServerContext context = new FileServerContext(
            _fs, _users, discs, sessions, handles, paths, bulk,
            new FileServerConfiguration(), new FileServerClock(),
            "/econet-root", "RetroFS", FileServerCommandHandler.DefaultVersionString);

        _handler = new FileServerCommandHandler(context, NullLogger<FileServerCommandHandler>.Instance);
    }

    [Test]
    public void UserWithHomeAndLib_ReadUserEnv_ReportsConfiguredLeaves()
    {
        CreateUser("HOMED", home: "$.HOMEDIR", lib: "$.LIBDIR");

        Login("HOMED");
        EconetMessage env = ReadUserEnv();

        Assert.That(CsdLeaf(env), Is.EqualTo("HOMEDIR"), "CSD leaf should follow econet.home");
        Assert.That(LibLeaf(env), Is.EqualTo("LIBDIR"), "library leaf should follow econet.lib");
    }

    [Test]
    public void UserWithMissingHome_ReadUserEnv_FallsBackToDiscLayoutDefault()
    {
        CreateUser("BADH", home: "$.NOSUCHDIR", lib: "$.LIBDIR");

        Login("BADH");
        EconetMessage env = ReadUserEnv();

        // Home resolution fails (dir absent) so the guard leaves the disc-layout default (discPath/USER).
        // Lib still points at the valid $.LIBDIR — the two overrides are independent.
        Assert.That(CsdLeaf(env), Is.EqualTo("USER"), "missing home must fall back to disc-layout default");
        Assert.That(LibLeaf(env), Is.EqualTo("LIBDIR"), "valid lib still applies");
    }

    // ---- *SETLIB / *SETHOME command round-trip ---------------------------------------------------

    [Test]
    public void SetLib_SelfService_TakesEffectAtNextLogin()
    {
        CreatePlainUser("SL");
        Login("SL");

        Assert.That(StatusOf(SendOsCli("SETLIB $.LIBDIR")), Is.EqualTo(0), "self *SETLIB should succeed");

        Login("SL"); // re-login: the persisted preference is read back by the login path
        Assert.That(LibLeaf(ReadUserEnv()), Is.EqualTo("LIBDIR"));
    }

    [Test]
    public void SetHome_NonAdmin_ReturnsInsufficientAccess()
    {
        CreatePlainUser("NA");
        Login("NA");

        // SETHOME is System-only; the dispatcher answers a non-admin with 0xBD before Execute.
        Assert.That(StatusOf(SendOsCli("SETHOME $.HOMEDIR")), Is.EqualTo(FileServerErrors.InsufficientAccess));
    }

    [Test]
    public void SetHome_SystSetsAnotherUser_TakesEffectAtThatUsersNextLogin()
    {
        CreatePlainUser("TG");
        Login("SYST");

        Assert.That(StatusOf(SendOsCli("SETHOME TG $.HOMEDIR")), Is.EqualTo(0), "SYST *SETHOME <user> <dir> should succeed");

        Login("TG");
        Assert.That(CsdLeaf(ReadUserEnv()), Is.EqualTo("HOMEDIR"));
    }

    [Test]
    public void SetLib_BadDirectory_ReturnsBadLibrary()
    {
        Login("SYST");

        Assert.That(StatusOf(SendOsCli("SETLIB $.NOSUCHDIR")), Is.EqualTo(FileServerErrors.BadLibrary));
    }

    [Test]
    public void SetLib_TwoArgFromNonAdmin_ReturnsBadParameters()
    {
        CreatePlainUser("NA2");
        Login("NA2");

        // A non-System user supplying a space (the two-argument form) is PiFS "Bad parameters" (0xFF).
        Assert.That(StatusOf(SendOsCli("SETLIB SOMEONE $.LIBDIR")), Is.EqualTo(0xFF));
    }

    [Test]
    public void SetLib_SystUnknownUser_ReturnsNoSuchUser()
    {
        Login("SYST");

        Assert.That(StatusOf(SendOsCli("SETLIB GHOST $.LIBDIR")), Is.EqualTo(0xFF));
    }

    [Test]
    public void SetLib_SystDiscInPath_ReturnsCantSpecifyDisc()
    {
        Login("SYST");

        // Disc rejection happens before the user lookup, so the named user need not exist.
        Assert.That(StatusOf(SendOsCli("SETLIB SOMEONE :DISC.LIB")), Is.EqualTo(0xFF));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static byte StatusOf(EconetMessage reply) => reply.Payload[1];

    private EconetMessage SendOsCli(string command) =>
        _handler.Handle(EconetFsTestHarness.CreateRequest(FileServerOpcodes.OsCli, Encoding.ASCII.GetBytes(command + "\r")))!;

    private void CreatePlainUser(string name)
    {
        RetroUser user = new RetroUser
        {
            UserId = System.Guid.NewGuid(),
            Username = name,
            DisplayName = name,
            IsActive = true,
            IsAdmin = false,
            AllowEconet = true,
            AllowTnfs = false,
        };
        user.Metadata[EconetMetadataKeys.AllowEmptyPassword] = "true";
        user.Metadata[EconetMetadataKeys.DiscMask] = "65535";
        _idp.CreateUserAsync(user).AsTask().GetAwaiter().GetResult();
        _idp.SaveAsync().AsTask().GetAwaiter().GetResult();
    }

    private void CreateUser(string name, string home, string lib)
    {
        RetroUser user = new RetroUser
        {
            UserId = System.Guid.NewGuid(),
            Username = name,
            DisplayName = name,
            IsActive = true,
            IsAdmin = false,
            AllowEconet = true,
            AllowTnfs = false,
        };
        user.Metadata[EconetMetadataKeys.AllowEmptyPassword] = "true";
        user.Metadata[EconetMetadataKeys.DiscMask] = "65535";
        user.Metadata[EconetMetadataKeys.Home] = home;
        user.Metadata[EconetMetadataKeys.Lib] = lib;
        _idp.CreateUserAsync(user).AsTask().GetAwaiter().GetResult();
        _idp.SaveAsync().AsTask().GetAwaiter().GetResult();
    }

    private void Login(string name)
    {
        EconetMessage reply = _handler.Handle(
            EconetFsTestHarness.CreateRequest(FileServerOpcodes.OsCli, Encoding.ASCII.GetBytes($"I AM {name} \r")))!;
        Assert.That(reply.Payload[1], Is.EqualTo(0), $"login for {name} should succeed");
    }

    private EconetMessage ReadUserEnv() =>
        _handler.Handle(EconetFsTestHarness.CreateLoggedInRequest(FileServerOpcodes.ReadUserEnv, []))!;

    // 0x15 layout: [0,0,16, disc[16], CSD leaf[10], lib leaf[10]].
    private static string CsdLeaf(EconetMessage env) => Encoding.ASCII.GetString(env.Payload, 19, 10).TrimEnd();
    private static string LibLeaf(EconetMessage env) => Encoding.ASCII.GetString(env.Payload, 29, 10).TrimEnd();
}
