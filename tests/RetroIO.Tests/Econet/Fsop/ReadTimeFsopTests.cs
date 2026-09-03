//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet.Fsop;

/// <summary>
/// Per-opcode protocol template: <c>ReadTime</c> (fsop 0x10). ReadTime is the migration template
/// because it touches no backend, so it shows the shape every per-opcode fixture follows — auth
/// behaviour, success reply, and a backend-call assertion — without disc-layout noise.
///
/// These assertions are migration-invariant: they must stay green through the FSOP strangler refactor
/// (legacy <c>MiscOpcodeHandler</c> today, <c>ReadTimeOperation</c> after migration). Add the deeper
/// per-opcode fixtures (Save/Load/GetObjectInfo/...) the same way as each opcode is migrated.
/// </summary>
[TestFixture]
[Category("Unit")]
internal sealed class ReadTimeFsopTests : FsopProtocolTestBase
{
    [Test]
    public void WithoutLogin_ReturnsWhoAreYou()
    {
        EconetMessage reply = Harness.Send(FileServerOpcodes.ReadTime);

        AssertWhoAreYou(reply);
    }

    [Test]
    public void LoggedIn_ReturnsSevenByteReply()
    {
        LoginAdmin();

        EconetMessage reply = Harness.Send(FileServerOpcodes.ReadTime);

        // PiFS 7-byte layout: [0, 0, day, monthYear, hour, min, sec] (see FileServerReply.ReadTime).
        Assert.That(reply.Payload, Has.Length.EqualTo(7));
        Assert.That(StatusOf(reply), Is.EqualTo(0x00));
    }

    [Test]
    public void LoggedIn_TouchesNoBackend()
    {
        LoginAdmin(); // clears the login's backend traffic

        Harness.Send(FileServerOpcodes.ReadTime);

        // ReadTime is a pure clock read — it must make no file-system calls at all.
        Assert.That(Backend.Calls, Is.Empty);
    }
}
