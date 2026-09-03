//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Covers <c>*CDIR</c>, which was missing from OSCLI even though the CreateDir operation existed.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class CdirCommandTests
{
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new EconetFsTestHarness();
        _harness.HandleOsCli("I AM SYST \r");
    }

    [Test]
    public void Cdir_CreatesDirectory_AndReturnsSuccess()
    {
        byte[] payload = _harness.HandleOsCli("CDIR NEWDIR\r").Payload;

        Assert.That(payload[0], Is.EqualTo(0), "success reply starts 0x00");
        Assert.That(payload[1], Is.EqualTo(0), "success reply has no error code");
        Assert.That(_harness.FileSystem.DirectoryExists(EconetSampleCatalogFixture.UserDirectory + "/NEWDIR"), Is.True,
            "the directory must actually be created in the backend");
    }

    [Test]
    public void Cdir_Abbreviated_Works()
    {
        // Minimum abbreviation is 2, so "*CD." must reach CDIR.
        byte[] payload = _harness.HandleOsCli("CD.ABBREV\r").Payload;

        Assert.That(payload[1], Is.EqualTo(0), "abbreviated *CD. creates the directory");
        Assert.That(_harness.FileSystem.DirectoryExists(EconetSampleCatalogFixture.UserDirectory + "/ABBREV"), Is.True);
    }

    [Test]
    public void Cdir_AbsoluteDollarPath_CreatesAtDiscRoot_NotLiteralDollar()
    {
        // Regression: "*CDIR $.ROOTDIR" used to create a literal "$" directory under the CWD
        // (CurrentDirectoryUnix + "$/ROOTDIR") instead of resolving "$" to the disc root.
        byte[] payload = _harness.HandleOsCli("CDIR $.ROOTDIR\r").Payload;

        Assert.That(payload[1], Is.EqualTo(0), "absolute *CDIR $.X should succeed");
        Assert.That(_harness.FileSystem.DirectoryExists("/econet-root/0RetroFS/ROOTDIR"), Is.True,
            "the directory must be created at the disc root");
        Assert.That(_harness.FileSystem.DirectoryExists("/econet-root/0RetroFS/USER/$"), Is.False,
            "must NOT create a literal '$' directory under the current directory");
    }

    [Test]
    public void Cdir_WhenAlreadyExists_ReturnsError()
    {
        _harness.FileSystem.AddDirectory(EconetSampleCatalogFixture.UserDirectory + "/EXISTING");

        byte[] payload = _harness.HandleOsCli("CDIR EXISTING\r").Payload;

        Assert.That(payload[0], Is.EqualTo(0), "error reply starts 0x00");
        Assert.That(payload[1], Is.EqualTo(FileServerErrors.BadCommand), "creating an existing name is rejected");
    }

    [Test]
    public void Cdir_WithNoArgument_ReturnsBadCommand()
    {
        byte[] payload = _harness.HandleOsCli("CDIR\r").Payload;

        Assert.That(payload[0], Is.EqualTo(0));
        Assert.That(payload[1], Is.EqualTo(FileServerErrors.BadCommand));
    }

    [Test]
    public void Cdir_WhenNotLoggedIn_ReturnsWhoAreYou()
    {
        EconetFsTestHarness fresh = new EconetFsTestHarness();
        byte[] payload = fresh.HandleOsCli("CDIR NEWDIR\r").Payload;

        Assert.That(payload[1], Is.EqualTo(FileServerErrors.WhoAreYou));
    }
}
