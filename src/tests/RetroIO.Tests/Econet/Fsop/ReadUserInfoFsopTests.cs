//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet.Fsop;

/// <summary>
/// Per-opcode protocol fixture for <c>ReadUserInfo</c> (fsop 0x18). Migration guard for
/// <c>MiscOpcodeHandler</c> -> <c>ReadUserInfoOperation</c>. Covers auth, a self-lookup (admin flag),
/// and the not-logged-on rejection.
/// </summary>
[TestFixture]
[Category("Unit")]
internal sealed class ReadUserInfoFsopTests : FsopProtocolTestBase
{
    [Test]
    public void WithoutLogin_ReturnsWhoAreYou()
    {
        AssertWhoAreYou(Harness.Send(FileServerOpcodes.ReadUserInfo, Encoding.ASCII.GetBytes("SYST\r")));
    }

    [Test]
    public void LoggedOnAdmin_ReturnsSystemFlagAndLocation()
    {
        LoginAdmin();

        EconetMessage reply = Harness.Send(FileServerOpcodes.ReadUserInfo, Encoding.ASCII.GetBytes("SYST\r"));

        // PiFS layout: [0, 0, flag, station, net]; flag = 0x40 for a System user.
        Assert.That(StatusOf(reply), Is.EqualTo(0x00));
        Assert.That(reply.Payload[2], Is.EqualTo(0x40), "System-user flag");
        Assert.That(reply.Payload[3], Is.EqualTo(1), "client station (harness uses 1)");
        Assert.That(reply.Payload[4], Is.EqualTo(1), "client net (harness uses 1)");
    }

    [Test]
    public void UnknownUser_ReturnsNotLoggedOn()
    {
        LoginAdmin();

        EconetMessage reply = Harness.Send(FileServerOpcodes.ReadUserInfo, Encoding.ASCII.GetBytes("GHOST\r"));

        // 0xBC "No such user or not logged on".
        AssertErrorCode(reply, 0xBC);
    }
}
