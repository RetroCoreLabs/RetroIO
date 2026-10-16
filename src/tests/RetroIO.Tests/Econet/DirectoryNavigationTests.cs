//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Validates DNFS-style <c>*DIR</c> / <c>*LIB</c> directory navigation over OSCLI (fsop 0x00).
/// Reply return codes and the resulting <c>read-user-environment</c> leaf names are matched against
/// the PiFS reference (utilities/fs.c: *DIR returns 0x07, *LIB returns 0x09, plus the new handle).
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class DirectoryNavigationTests
{
    private const string SubDirName = "GAMES";
    private const string SubDirUnix = EconetSampleCatalogFixture.UserDirectory + "/" + SubDirName;

    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new EconetFsTestHarness();
        _harness.FileSystem.AddDirectory(SubDirUnix);
        _harness.FileSystem.AddFile(SubDirUnix + "/INVADERS", Encoding.ASCII.GetBytes("game\r"));
    }

    /// <summary>
    /// REGRESSION: the disc root must report the Acorn name "$", never the HOST folder name.
    /// </summary>
    /// <remarks>
    /// Found live in BeebEm 2026-07-17: "*DIR :RetroFS" then "*CAT" reported the directory as
    /// "0RetroFS" — PiFS's on-disk "disc index + disc name" folder, leaked to the BBC as though it
    /// were an Acorn directory name. PiFS names the tail "$" whenever npath == 0, i.e. at the disc
    /// root (fs.c:10388). The host layout must never be visible on the wire.
    /// </remarks>
    [Test]
    public void Dir_ToDiscRoot_ReportsDollar_NotTheHostFolderName()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] payload = _harness.HandleOsCli("DIR :RetroFS\r").Payload;
        Assert.That(payload[0], Is.EqualTo(0x07), "':DISC' alone is the disc root and must succeed");

        byte[] env = _harness.HandleLoggedIn(FileServerOpcodes.ReadUserEnv, []).Payload;
        string csdLeaf = Encoding.ASCII.GetString(env, 19, 10).TrimEnd();

        Assert.That(csdLeaf, Is.EqualTo("$"), "disc root is '$' (PiFS npath == 0)");
        Assert.That(csdLeaf, Is.Not.EqualTo("0RetroFS"), "the host folder name must never reach the client");
    }

    /// <summary>
    /// REGRESSION: "*DIR :" (bare colon, no disc name) must fail and leave the CSD untouched.
    /// </summary>
    /// <remarks>
    /// Found live in BeebEm 2026-07-17. The bare ":" was accepted and stored as the CSD, resolving to
    /// the disc CONTAINER above the user's root and trapping the session — ParentAcorn's "$" clamp
    /// cannot fire on a path with no '$' in it, so "*DIR ^" could not escape. PiFS rejects it: its
    /// disc-name regex requires 1-16 chars (fs.c:3409), so the match fails and fs.c:2399 returns
    /// FS_PATH_ERR_NODISC; the CSD is only updated after a SUCCESSFUL normalize (fs.c:10365).
    /// </remarks>
    [Test]
    public void DirBareColon_IsRejected_AndLeavesCsdUnchanged()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] before = _harness.HandleLoggedIn(FileServerOpcodes.ReadUserEnv, []).Payload;
        string leafBefore = Encoding.ASCII.GetString(before, 19, 10).TrimEnd();

        byte[] payload = _harness.HandleOsCli("DIR :\r").Payload;
        Assert.That(payload[0], Is.EqualTo(0), "error replies start with 0x00");
        Assert.That(payload[1], Is.EqualTo(FileServerErrors.NotFound), "bare ':' has no disc name");

        byte[] after = _harness.HandleLoggedIn(FileServerOpcodes.ReadUserEnv, []).Payload;
        string leafAfter = Encoding.ASCII.GetString(after, 19, 10).TrimEnd();

        Assert.That(leafAfter, Is.EqualTo(leafBefore), "a failed *DIR must not move the CSD");
        Assert.That(leafAfter, Is.EqualTo("USER"), "still in the user's home");
    }

    [Test]
    public void Dir_ToSubdirectory_ReturnsChangedDirectoryReturnCode()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] payload = _harness.HandleOsCli($"DIR {SubDirName}\r").Payload;

        Assert.That(payload[0], Is.EqualTo(0x07), "PiFS *DIR return code");
        Assert.That(payload[1], Is.EqualTo(0));
        Assert.That(payload[2], Is.EqualTo(AcornFileHandles.WireCwd), "new CSD handle (bit-encoded CWD)");
    }

    [Test]
    public void Dir_ToSubdirectory_UpdatesReadUserEnvCsdLeaf()
    {
        _harness.HandleOsCli("I AM SYST \r");
        _harness.HandleOsCli($"DIR {SubDirName}\r");

        byte[] env = _harness.HandleLoggedIn(FileServerOpcodes.ReadUserEnv, []).Payload;

        Assert.That(env, Has.Length.EqualTo(39));
        string csdLeaf = Encoding.ASCII.GetString(env, 19, 10).TrimEnd();
        Assert.That(csdLeaf, Is.EqualTo(SubDirName));
    }

    [Test]
    public void Dir_Home_AfterSubdirectory_RestoresRootLeaf()
    {
        _harness.HandleOsCli("I AM SYST \r");
        _harness.HandleOsCli($"DIR {SubDirName}\r");

        byte[] payload = _harness.HandleOsCli("DIR\r").Payload;
        Assert.That(payload[0], Is.EqualTo(0x07));

        byte[] env = _harness.HandleLoggedIn(FileServerOpcodes.ReadUserEnv, []).Payload;
        string csdLeaf = Encoding.ASCII.GetString(env, 19, 10).TrimEnd();
        // PiFS fs.c:10388 names the CSD tail "$" ONLY at the disc root (npath == 0); the user's home
        // is $.USER, so its leaf is "USER". Re-baselined 2026-07-17 when RetroFS moved to PiFS parity
        // (previously home was reported as "$", which made the disc root leak its host folder name).
        Assert.That(csdLeaf, Is.EqualTo("USER"), "empty *DIR returns to the user root ($.USER -> leaf USER)");
    }

    [Test]
    public void DirCaret_FromSubdirectory_AscendsToParent()
    {
        _harness.HandleOsCli("I AM SYST \r");
        _harness.HandleOsCli($"DIR {SubDirName}\r");

        byte[] payload = _harness.HandleOsCli("DIR^\r").Payload;
        Assert.That(payload[0], Is.EqualTo(0x07));

        byte[] env = _harness.HandleLoggedIn(FileServerOpcodes.ReadUserEnv, []).Payload;
        string csdLeaf = Encoding.ASCII.GetString(env, 19, 10).TrimEnd();
        // Parent of $.USER.GAMES is $.USER, whose PiFS leaf is "USER" (fs.c:10388 - "$" is the disc
        // root only). Re-baselined 2026-07-17 with the move to PiFS naming parity.
        Assert.That(csdLeaf, Is.EqualTo("USER"), "parent of $.USER.GAMES is the user home ($.USER)");
    }

    [Test]
    public void Dir_NonExistentDirectory_ReturnsNotFound()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] payload = _harness.HandleOsCli("DIR NOSUCH\r").Payload;

        Assert.That(payload[1], Is.EqualTo(FileServerErrors.NotFound));
    }

    [Test]
    public void Dir_WithoutLogin_ReturnsWhoAreYou()
    {
        byte[] payload = _harness.HandleOsCli($"DIR {SubDirName}\r").Payload;

        Assert.That(payload[1], Is.EqualTo(FileServerErrors.WhoAreYou));
    }

    [Test]
    public void Lib_ToSubdirectory_ReturnsChangedLibraryReturnCode()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] payload = _harness.HandleOsCli($"LIB {SubDirName}\r").Payload;

        Assert.That(payload[0], Is.EqualTo(0x09), "PiFS *LIB return code");
        Assert.That(payload[1], Is.EqualTo(0));
        Assert.That(payload[2], Is.EqualTo(AcornFileHandles.WireLib), "new library handle (bit-encoded LIB)");
    }

    [Test]
    public void Lib_ToSubdirectory_UpdatesReadUserEnvLibraryLeaf()
    {
        _harness.HandleOsCli("I AM SYST \r");
        _harness.HandleOsCli($"LIB {SubDirName}\r");

        byte[] env = _harness.HandleLoggedIn(FileServerOpcodes.ReadUserEnv, []).Payload;

        string libLeaf = Encoding.ASCII.GetString(env, 29, 10).TrimEnd();
        Assert.That(libLeaf, Is.EqualTo(SubDirName));
    }

    [Test]
    public void Lib_NonExistentDirectory_ReturnsNotFound()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] payload = _harness.HandleOsCli("LIB NOSUCH\r").Payload;

        Assert.That(payload[1], Is.EqualTo(FileServerErrors.NotFound));
    }

    [Test]
    public void Cat_AfterDirIntoSubdirectory_ListsSubdirectoryEntries()
    {
        _harness.HandleOsCli("I AM SYST \r");
        _harness.HandleOsCli($"DIR {SubDirName}\r");

        // Plain *CAT redirects to Examine "." which now resolves to the new CSD.
        byte[] examine = _harness.HandleLoggedIn(
            FileServerOpcodes.Examine,
            Encoding.ASCII.GetBytes(".\r")).Payload;

        Assert.That(examine[2], Is.EqualTo(1), "GAMES contains a single entry (INVADERS)");
        string first = Encoding.ASCII.GetString(examine, 4, 10).TrimEnd();
        Assert.That(first, Is.EqualTo("INVADERS"));
    }
}
