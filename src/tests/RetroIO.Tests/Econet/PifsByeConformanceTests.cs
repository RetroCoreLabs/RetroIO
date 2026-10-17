//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

/// <summary>
/// PiFS-aligned BYE matrix (utilities/fs.c: OSCLI case 0 + opcode 0x17).
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class PifsByeConformanceTests
{
    private EconetFsTestHarness _harness = null!;
    private DnfsClientSimulator _client = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new EconetFsTestHarness();
        _client = new DnfsClientSimulator(_harness);
    }

    [Test]
    public void OsCliBye_WhenLoggedIn_ReturnsZeroZero()
    {
        _client.OsCli("I AM SYST \r");
        DnfsReply bye = _client.OsCli("BYE\r");

        Assert.That(bye.Payload, Is.EqualTo(new byte[] { 0, 0 }));
        Assert.That(_client.IsLoggedIn, Is.False);
    }

    [Test]
    public void OsCliBye_WhenNotLoggedIn_ReturnsWhoAreYou()
    {
        DnfsReply bye = _client.OsCli("BYE\r");

        Assert.That(bye.ErrorCode, Is.EqualTo(FileServerErrors.WhoAreYou));
        Assert.That(_client.IsLoggedIn, Is.False);
    }

    [Test]
    public void OpcodeBye_WhenLoggedIn_ReturnsZeroZero()
    {
        _client.OsCli("I AM SYST \r");
        DnfsReply bye = _client.Opcode(FileServerOpcodes.Bye, []);

        Assert.That(bye.Payload, Is.EqualTo(new byte[] { 0, 0 }));
    }

    [Test]
    public void OpcodeBye_WhenNotLoggedIn_ReturnsWhoAreYou()
    {
        DnfsReply bye = _client.Opcode(FileServerOpcodes.Bye, []);

        Assert.That(bye.ErrorCode, Is.EqualTo(FileServerErrors.WhoAreYou));
    }

    [Test]
    public void OsCliBye_AndOpcodeBye_WhenLoggedIn_UseSameSuccessReply()
    {
        _client.OsCli("I AM SYST \r");
        DnfsReply osCliBye = _client.OsCli("BYE\r");
        Assert.That(osCliBye.Payload, Is.EqualTo(new byte[] { 0, 0 }));

        _client.OsCli("I AM SYST \r");
        DnfsReply opcodeBye = _client.Opcode(FileServerOpcodes.Bye, []);
        Assert.That(opcodeBye.Payload, Is.EqualTo(osCliBye.Payload));
    }
}
