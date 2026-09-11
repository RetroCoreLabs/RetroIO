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
/// Per-opcode protocol fixture for the random-access pair <c>GetRandomAccessInfo</c> (0x0C) and
/// <c>SetRandomAccessInfo</c> (0x0D). Migration guard for <c>MiscOpcodeHandler</c> ->
/// <c>GetRandomAccessInfoOperation</c>/<c>SetRandomAccessInfoOperation</c>.
/// </summary>
/// <remarks>
/// TODO (deeper coverage): open a channel, set its cursor, then read the cursor/extent back and assert
/// the 24-bit value. Covered here: auth + the error paths (unknown channel, short parameters).
/// </remarks>
[TestFixture]
[Category("Unit")]
internal sealed class RandomAccessInfoFsopTests : FsopProtocolTestBase
{
    [Test]
    public void GetRandomAccessInfo_WithoutLogin_ReturnsWhoAreYou()
    {
        AssertWhoAreYou(Harness.Send(FileServerOpcodes.GetRandomAccessInfo, [/* handle */ 5, /* function */ 0]));
    }

    [Test]
    public void GetRandomAccessInfo_UnknownChannel_ReturnsBadChannel()
    {
        LoginAdmin();

        EconetMessage reply = Harness.Send(FileServerOpcodes.GetRandomAccessInfo, [5, 0]);

        AssertErrorCode(reply, FileServerErrors.BadChannel);
    }

    [Test]
    public void SetRandomAccessInfo_WithoutLogin_ReturnsWhoAreYou()
    {
        AssertWhoAreYou(Harness.Send(FileServerOpcodes.SetRandomAccessInfo, [5, 0, 0, 0, 0]));
    }

    [Test]
    public void SetRandomAccessInfo_ShortParameters_ReturnsBadCommand()
    {
        LoginAdmin();

        // Needs 5 bytes (handle + 32-bit cursor); 3 is malformed.
        EconetMessage reply = Harness.Send(FileServerOpcodes.SetRandomAccessInfo, [5, 0, 0]);

        AssertErrorCode(reply, FileServerErrors.BadCommand);
    }

    [Test]
    public void SetRandomAccessInfo_UnknownChannel_ReturnsBadChannel()
    {
        LoginAdmin();

        EconetMessage reply = Harness.Send(FileServerOpcodes.SetRandomAccessInfo, [5, 0, 0, 0, 0]);

        AssertErrorCode(reply, FileServerErrors.BadChannel);
    }
}
