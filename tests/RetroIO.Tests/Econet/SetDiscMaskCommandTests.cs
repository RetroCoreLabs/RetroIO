//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Covers <c>*DISCMASK</c> / <c>*DISKMASK</c> — a System user setting a user's visible-disc bitmap.
/// The seeded USER account starts at mask 65535 (0xFFFF, "ALL"), so changes are tested against that.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class SetDiscMaskCommandTests
{
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new EconetFsTestHarness();
    }

    private static byte Status(EconetMessage reply) => reply.Payload[1];

    [Test]
    public void DiscMask_WhenNotLoggedIn_ReturnsWhoAreYou()
    {
        Assert.That(Status(_harness.HandleOsCli("DISCMASK USER NONE\r")), Is.EqualTo(FileServerErrors.WhoAreYou));
    }

    [Test]
    public void DiscMask_AsNonAdmin_ReturnsInsufficientAccess()
    {
        _harness.LoginAsUser();

        Assert.That(Status(_harness.HandleOsCli("DISCMASK USER NONE\r")), Is.EqualTo(FileServerErrors.InsufficientAccess));
    }

    [Test]
    public void DiscMask_SetNone_ThenAgain_PersistsAndRejectsUnchanged()
    {
        _harness.LoginAsSyst();

        // USER starts ALL (0xFFFF); NONE (0x0000) is a change -> success.
        Assert.That(Status(_harness.HandleOsCli("DISCMASK USER NONE\r")), Is.EqualTo(0));
        // Repeating NONE is now unchanged -> error, proving the first write persisted.
        Assert.That(Status(_harness.HandleOsCli("DISCMASK USER NONE\r")), Is.EqualTo(FileServerErrors.Unsupported));
    }

    [Test]
    public void DiscMask_AlreadyAll_ReturnsUnchanged()
    {
        _harness.LoginAsSyst();

        // USER is already ALL (0xFFFF) -> no change.
        Assert.That(Status(_harness.HandleOsCli("DISCMASK USER ALL\r")), Is.EqualTo(FileServerErrors.Unsupported));
    }

    [Test]
    public void DiscMask_HexValue_Changes()
    {
        _harness.LoginAsSyst();

        // 0x000F != 0xFFFF -> change.
        Assert.That(Status(_harness.HandleOsCli("DISCMASK USER 000F\r")), Is.EqualTo(0));
    }

    [Test]
    public void DiscMask_BadParameters_ReturnsError()
    {
        _harness.LoginAsSyst();

        // Only one token (no disc spec).
        Assert.That(Status(_harness.HandleOsCli("DISCMASK USER\r")), Is.EqualTo(FileServerErrors.Unsupported));
    }

    [Test]
    public void DiscMask_UnknownUser_ReturnsError()
    {
        _harness.LoginAsSyst();

        Assert.That(Status(_harness.HandleOsCli("DISCMASK GHOST ALL\r")), Is.EqualTo(FileServerErrors.Unsupported));
    }

    [Test]
    public void DiscMask_Abbreviated_DISCM_Works()
    {
        _harness.LoginAsSyst();

        // Minimum abbreviation is 5, so "*DISCM" must reach it.
        Assert.That(Status(_harness.HandleOsCli("DISCM USER 0001\r")), Is.EqualTo(0));
    }
}
