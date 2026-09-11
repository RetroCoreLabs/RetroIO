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
/// Per-opcode protocol fixture for <c>SetBootOpt</c> (fsop 0x16). Migration guard for
/// <c>MiscOpcodeHandler</c> -> <c>SetBootOptOperation</c>. Covers auth, a valid option, and the
/// >7 rejection.
/// </summary>
[TestFixture]
[Category("Unit")]
internal sealed class SetBootOptFsopTests : FsopProtocolTestBase
{
    [Test]
    public void WithoutLogin_ReturnsWhoAreYou()
    {
        AssertWhoAreYou(Harness.Send(FileServerOpcodes.SetBootOpt, [3]));
    }

    [Test]
    public void ValidOption_ReturnsSuccess()
    {
        LoginAdmin();

        EconetMessage reply = Harness.Send(FileServerOpcodes.SetBootOpt, [3]);

        Assert.That(StatusOf(reply), Is.EqualTo(0x00));
    }

    [Test]
    public void OptionAboveSeven_ReturnsInsufficientAccess()
    {
        LoginAdmin();

        // PiFS fs_set_bootopt rejects options above 7 with 0xBD.
        EconetMessage reply = Harness.Send(FileServerOpcodes.SetBootOpt, [8]);

        AssertInsufficientAccess(reply);
    }
}
