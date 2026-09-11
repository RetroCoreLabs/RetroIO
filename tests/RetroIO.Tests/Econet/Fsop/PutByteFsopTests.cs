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
/// Per-opcode protocol fixture for <c>PutByte</c> (fsop 0x09). Migration guard for
/// <c>MiscOpcodeHandler</c> -> <c>PutByteOperation</c>.
/// </summary>
/// <remarks>
/// TODO (deeper coverage): open a writable channel, write bytes, and assert they land at the cursor
/// with correct retransmit handling. Covered here: auth + unknown channel.
/// </remarks>
[TestFixture]
[Category("Unit")]
internal sealed class PutByteFsopTests : FsopProtocolTestBase
{
    [Test]
    public void WithoutLogin_ReturnsWhoAreYou()
    {
        AssertWhoAreYou(Harness.Send(FileServerOpcodes.PutByte));
    }

    [Test]
    public void LoggedIn_NoOpenChannel_ReturnsBadChannel()
    {
        LoginAdmin();

        EconetMessage reply = Harness.Send(FileServerOpcodes.PutByte);

        AssertErrorCode(reply, FileServerErrors.BadChannel);
    }
}
