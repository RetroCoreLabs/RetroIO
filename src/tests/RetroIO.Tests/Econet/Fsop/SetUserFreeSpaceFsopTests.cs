//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet.Fsop;

/// <summary>
/// Per-opcode protocol fixture for <c>SetUserFreeSpace</c> (fsop 0x1F). Migration guard for
/// <c>MiscOpcodeHandler</c> -> <c>SetUserFreeSpaceOperation</c>. System-privilege only.
/// </summary>
[TestFixture]
[Category("Unit")]
internal sealed class SetUserFreeSpaceFsopTests : FsopProtocolTestBase
{
    [Test]
    public void WithoutLogin_ReturnsWhoAreYou()
    {
        AssertWhoAreYou(Harness.Send(FileServerOpcodes.SetUserFreeSpace, [0, 0, 0, 0]));
    }

    [Test]
    public void AsNormalUser_ReturnsInsufficientAccess()
    {
        LoginNormalUser();

        AssertInsufficientAccess(Harness.Send(FileServerOpcodes.SetUserFreeSpace, [0, 0, 0, 0]));
    }

    [Test]
    public void AsAdmin_ReturnsSuccess()
    {
        LoginAdmin();

        EconetMessage reply = Harness.Send(FileServerOpcodes.SetUserFreeSpace, [0, 0, 0, 0]);

        Assert.That(StatusOf(reply), Is.EqualTo(0x00));
    }
}
