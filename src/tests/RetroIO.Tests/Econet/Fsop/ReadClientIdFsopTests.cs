//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet.Fsop;

/// <summary>
/// Per-opcode protocol fixture for <c>ReadClientId</c> (fsop 0x20). Migration-invariant guard for the
/// move from <c>MiscOpcodeHandler</c> to <c>ReadClientIdOperation</c>.
/// </summary>
[TestFixture]
[Category("Unit")]
internal sealed class ReadClientIdFsopTests : FsopProtocolTestBase
{
    [Test]
    public void WithoutLogin_ReturnsWhoAreYou()
    {
        EconetMessage reply = Harness.Send(FileServerOpcodes.ReadClientId);

        AssertWhoAreYou(reply);
    }

    [Test]
    public void LoggedIn_ReturnsOwnUsernameCrTerminated()
    {
        LoginAdmin(); // logs in as SYST

        EconetMessage reply = Harness.Send(FileServerOpcodes.ReadClientId);

        // Raw username + 0x0D, NO [0,0] status prefix (PiFS fs.c:11482).
        Assert.That(Encoding.ASCII.GetString(reply.Payload), Is.EqualTo("SYST\r"));
    }

    [Test]
    public void LoggedIn_TouchesNoBackend()
    {
        LoginAdmin();

        Harness.Send(FileServerOpcodes.ReadClientId);

        // Just formats the session username — no file-system access.
        Assert.That(Backend.Calls, Is.Empty);
    }
}
