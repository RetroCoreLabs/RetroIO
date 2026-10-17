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
/// Per-opcode protocol fixture for <c>ReadUserEnv</c> (fsop 0x15). Migration guard for
/// <c>MiscOpcodeHandler</c> -> <c>ReadUserEnvOperation</c>.
/// </summary>
[TestFixture]
[Category("Unit")]
internal sealed class ReadUserEnvFsopTests : FsopProtocolTestBase
{
    [Test]
    public void WithoutLogin_ReturnsWhoAreYou()
    {
        AssertWhoAreYou(Harness.Send(FileServerOpcodes.ReadUserEnv));
    }

    [Test]
    public void LoggedIn_ReturnsThirtyNineByteReply()
    {
        LoginAdmin();

        EconetMessage reply = Harness.Send(FileServerOpcodes.ReadUserEnv);

        // PiFS layout: 00 00 + disc-name-length(16) + disc[16] + CSD leaf[10] + lib leaf[10] = 39 bytes.
        Assert.That(reply.Payload, Has.Length.EqualTo(39));
        Assert.That(StatusOf(reply), Is.EqualTo(0x00));
        Assert.That(reply.Payload[2], Is.EqualTo(16), "disc-name-length field");
    }
}
