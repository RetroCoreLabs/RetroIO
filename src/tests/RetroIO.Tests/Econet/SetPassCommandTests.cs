//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Covers <c>*SETPASS</c> — a System user resetting another user's password without the old one, and
/// the log redaction of its password argument.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class SetPassCommandTests
{
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new EconetFsTestHarness();
    }

    private static byte Status(EconetMessage reply) => reply.Payload[1];

    [Test]
    public void SetPass_WhenNotLoggedIn_ReturnsWhoAreYou()
    {
        Assert.That(Status(_harness.HandleOsCli("SETPASS USER SECRET\r")), Is.EqualTo(FileServerErrors.WhoAreYou));
    }

    [Test]
    public void SetPass_AsNonAdmin_ReturnsInsufficientAccess()
    {
        _harness.LoginAsUser();

        Assert.That(Status(_harness.HandleOsCli("SETPASS USER SECRET\r")), Is.EqualTo(FileServerErrors.InsufficientAccess));
    }

    [Test]
    public void SetPass_SetsNewPassword_TargetCanLogInWithIt()
    {
        _harness.LoginAsSyst();

        Assert.That(Status(_harness.HandleOsCli("SETPASS USER SECRET\r")), Is.EqualTo(0), "SETPASS should succeed");

        // The new password works, and a wrong one is rejected — proving the reset took effect.
        Assert.That(Status(_harness.HandleOsCli("I AM USER SECRET\r")), Is.EqualTo(0), "login with the new password");
        Assert.That(Status(_harness.HandleOsCli("I AM USER WRONGPASS\r")), Is.EqualTo(FileServerErrors.WrongPassword));
    }

    [Test]
    public void SetPass_UnknownUser_ReturnsError()
    {
        _harness.LoginAsSyst();

        Assert.That(Status(_harness.HandleOsCli("SETPASS GHOST SECRET\r")), Is.EqualTo(FileServerErrors.Unsupported));
    }

    [Test]
    public void SetPass_BadParameters_ReturnsError()
    {
        _harness.LoginAsSyst();

        // Only a username, no password.
        Assert.That(Status(_harness.HandleOsCli("SETPASS USER\r")), Is.EqualTo(FileServerErrors.Unsupported));
    }

    [Test]
    public void Redaction_MasksSetPassPassword_KeepsUsername()
    {
        // The username stays visible for audit; only the password token is masked.
        Assert.That(EconetLogRedaction.RedactCommandText("SETPASS FRED SECRET"), Is.EqualTo("SETPASS FRED ******"));
    }

    [Test]
    public void Redaction_MasksAbbreviatedSetPassPassword()
    {
        // Security-critical: an abbreviated *SETP must still redact.
        Assert.That(EconetLogRedaction.RedactCommandText("SETP FRED SECRET"), Is.EqualTo("SETP FRED ******"));
    }
}
