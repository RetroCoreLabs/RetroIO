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
/// Per-opcode protocol fixture for <c>SetRtc</c> (fsop 0x1C). Migration guard for
/// <c>MiscOpcodeHandler</c> -> <c>SetRtcOperation</c>. This is the first opcode exercising the
/// dispatcher's <c>RequiresSystem</c> gate, so it asserts the admin-vs-non-admin split explicitly.
/// </summary>
[TestFixture]
[Category("Unit")]
internal sealed class SetRtcFsopTests : FsopProtocolTestBase
{
    // [minute, hour, day, month, yearByte] — yearByte = (year-1900) << 1. 2026 -> 126 << 1 = 252.
    private static readonly byte[] ValidTime = [0, 12, 18, 7, 252];

    [Test]
    public void WithoutLogin_ReturnsWhoAreYou()
    {
        AssertWhoAreYou(Harness.Send(FileServerOpcodes.SetRtc, ValidTime));
    }

    [Test]
    public void AsNormalUser_ReturnsInsufficientAccess()
    {
        LoginNormalUser();

        AssertInsufficientAccess(Harness.Send(FileServerOpcodes.SetRtc, ValidTime));
    }

    [Test]
    public void AsAdmin_ValidTime_ReturnsSuccess()
    {
        LoginAdmin();

        EconetMessage reply = Harness.Send(FileServerOpcodes.SetRtc, ValidTime);

        Assert.That(StatusOf(reply), Is.EqualTo(0x00));
    }

    [Test]
    public void AsAdmin_BadTime_ReturnsBadCommand()
    {
        LoginAdmin();

        // Month 13 is out of range -> ArgumentOutOfRangeException -> 0xFE "Bad time".
        EconetMessage reply = Harness.Send(FileServerOpcodes.SetRtc, [0, 12, 18, 13, 252]);

        AssertErrorCode(reply, FileServerErrors.BadCommand);
    }
}
