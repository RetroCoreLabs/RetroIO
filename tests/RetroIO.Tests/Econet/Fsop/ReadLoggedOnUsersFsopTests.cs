//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet.Fsop;

/// <summary>
/// Per-opcode protocol fixture for <c>ReadLoggedOnUsers</c> (fsop 0x0F). Migration guard for
/// <c>MiscOpcodeHandler</c> -> <c>ReadLoggedOnUsersOperation</c>.
/// </summary>
[TestFixture]
[Category("Unit")]
internal sealed class ReadLoggedOnUsersFsopTests : FsopProtocolTestBase
{
    [Test]
    public void WithoutLogin_ReturnsWhoAreYou()
    {
        // Request bytes: start index 0, count 8.
        AssertWhoAreYou(Harness.Send(FileServerOpcodes.ReadLoggedOnUsers, [0, 8]));
    }

    [Test]
    public void LoggedIn_ReturnsSuccessWithUserBlock()
    {
        LoginAdmin();

        EconetMessage reply = Harness.Send(FileServerOpcodes.ReadLoggedOnUsers, [0, 8]);

        // Success reply carries [0, 0, count, <variable per-user entries>]; the admin session itself is
        // logged on, so the block must extend past the 3-byte header.
        Assert.That(StatusOf(reply), Is.EqualTo(0x00));
        Assert.That(reply.Payload.Length, Is.GreaterThan(3), "expected at least one logged-on user entry");
    }
}
