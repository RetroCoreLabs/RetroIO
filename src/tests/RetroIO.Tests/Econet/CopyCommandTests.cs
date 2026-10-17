//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Covers <c>*COPY</c> — a previously missing server-side file copy.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class CopyCommandTests
{
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new EconetFsTestHarness();
        _harness.HandleOsCli("I AM SYST \r");
        _harness.FileSystem.AddFile(EconetSampleCatalogFixture.UserDirectory + "/SOURCE", Encoding.ASCII.GetBytes("hello world"));
    }

    [Test]
    public void Copy_DuplicatesFile_AndReturnsSuccess()
    {
        byte[] payload = _harness.HandleOsCli("COPY SOURCE DEST\r").Payload;

        Assert.That(payload[0], Is.EqualTo(0), "success reply starts 0x00");
        Assert.That(payload[1], Is.EqualTo(0), "no error code");
        Assert.That(_harness.FileSystem.FileExists(EconetSampleCatalogFixture.UserDirectory + "/DEST"), Is.True,
            "destination file must exist after copy");
    }

    [Test]
    public void Copy_MissingSource_ReturnsNotFound()
    {
        byte[] payload = _harness.HandleOsCli("COPY NOSUCH DEST\r").Payload;

        Assert.That(payload[0], Is.EqualTo(0));
        Assert.That(payload[1], Is.EqualTo(FileServerErrors.NotFound));
    }

    [Test]
    public void Copy_MissingArgument_ReturnsBadCommand()
    {
        byte[] payload = _harness.HandleOsCli("COPY SOURCE\r").Payload;

        Assert.That(payload[0], Is.EqualTo(0));
        Assert.That(payload[1], Is.EqualTo(FileServerErrors.BadCommand));
    }

    [Test]
    public void Copy_Abbreviated_Works()
    {
        // Minimum abbreviation is 2, so "*CO." reaches COPY.
        byte[] payload = _harness.HandleOsCli("CO.SOURCE DEST2\r").Payload;

        Assert.That(payload[1], Is.EqualTo(0), "abbreviated *CO. copies");
        Assert.That(_harness.FileSystem.FileExists(EconetSampleCatalogFixture.UserDirectory + "/DEST2"), Is.True);
    }

    [Test]
    public void Copy_DoesNotCollideWithCatOrCdirOrCreate()
    {
        // Guards the registration order: "*CO." must select COPY, not CAT/CDIR/CREATE.
        byte[] payload = _harness.HandleOsCli("CO.SOURCE DEST3\r").Payload;
        Assert.That(payload[1], Is.EqualTo(0));
        Assert.That(_harness.FileSystem.FileExists(EconetSampleCatalogFixture.UserDirectory + "/DEST3"), Is.True);
    }
}
