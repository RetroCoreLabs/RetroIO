//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Validates SetBootOpt (fsop 0x16) against the PiFS reference (utilities/fs.c fs_set_bootopt):
/// updates the live session, persists to the user record (survives re-login), and rejects options &gt; 7.
/// This is the server side of the SHIFT-BREAK / !BOOT autoboot handshake (the boot option is reported
/// back in the login reply so the NFS ROM can LOAD/RUN/EXEC !BOOT).
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class BootOptionTests
{
    private const int LoginBootOptOffset = 5;

    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new EconetFsTestHarness();

    // NOTE: the boot option is NOT observable via ReadUserInfo (fsop 0x18) — PiFS 0x18 returns the user's
    // network location, not the boot option (utilities/fs.c:7622). The boot option is reported in the
    // login reply byte 5 (fs_login), which is what the NFS ROM reads for the SHIFT-BREAK handshake. These
    // tests therefore verify persistence/effect via a re-login.

    [Test]
    public void SetBootOpt_UpdatesAndIsReportedAtLogin()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] setReply = _harness.HandleLoggedIn(FileServerOpcodes.SetBootOpt, [2]).Payload;
        Assert.That(setReply[0], Is.EqualTo(0));
        Assert.That(setReply[1], Is.EqualTo(0));

        _harness.HandleOsCli("BYE\r");
        byte[] login = _harness.HandleOsCli("I AM SYST \r").Payload;
        Assert.That(login[LoginBootOptOffset], Is.EqualTo(2), "RUN boot option reported at login");
    }

    [Test]
    public void SetBootOpt_PersistsAcrossReLogin()
    {
        byte[] firstLogin = _harness.HandleOsCli("I AM SYST \r").Payload;
        Assert.That(firstLogin[LoginBootOptOffset], Is.EqualTo(0), "default boot option is off");

        _harness.HandleLoggedIn(FileServerOpcodes.SetBootOpt, [3]);
        _harness.HandleOsCli("BYE\r");

        byte[] secondLogin = _harness.HandleOsCli("I AM SYST \r").Payload;
        Assert.That(secondLogin[0], Is.EqualTo(FileServerOpcodes.LoginSuccessReturnCode));
        Assert.That(secondLogin[LoginBootOptOffset], Is.EqualTo(3), "persisted EXEC boot option reported at login");
    }

    [Test]
    public void SetBootOpt_Zero_TurnsAutobootOff()
    {
        _harness.HandleOsCli("I AM SYST \r");
        _harness.HandleLoggedIn(FileServerOpcodes.SetBootOpt, [2]);

        _harness.HandleLoggedIn(FileServerOpcodes.SetBootOpt, [0]);

        _harness.HandleOsCli("BYE\r");
        byte[] login = _harness.HandleOsCli("I AM SYST \r").Payload;
        Assert.That(login[LoginBootOptOffset], Is.EqualTo(0), "autoboot turned off");
    }

    [Test]
    public void SetBootOpt_AboveSeven_ReturnsBadOption()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] reply = _harness.HandleLoggedIn(FileServerOpcodes.SetBootOpt, [8]).Payload;

        Assert.That(reply[1], Is.EqualTo(FileServerErrors.InsufficientAccess));
    }

    [Test]
    public void SetBootOpt_WithoutLogin_ReturnsWhoAreYou()
    {
        byte[] reply = _harness.HandleLoggedIn(FileServerOpcodes.SetBootOpt, [2]).Payload;

        Assert.That(reply[1], Is.EqualTo(FileServerErrors.WhoAreYou));
    }
}
