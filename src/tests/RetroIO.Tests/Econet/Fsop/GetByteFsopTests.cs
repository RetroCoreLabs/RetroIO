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
/// Per-opcode protocol fixture for <c>GetByte</c> (fsop 0x08). Migration guard for
/// <c>MiscOpcodeHandler</c> -> <c>GetByteOperation</c>.
/// </summary>
/// <remarks>
/// TODO (deeper coverage): open a channel (fsop 0x06), then assert byte-by-byte reads, the EOF flag,
/// and retransmit (control-bit) behaviour. Covered here: auth + unknown channel.
/// </remarks>
[TestFixture]
[Category("Unit")]
internal sealed class GetByteFsopTests : FsopProtocolTestBase
{
    [Test]
    public void WithoutLogin_ReturnsWhoAreYou()
    {
        AssertWhoAreYou(Harness.Send(FileServerOpcodes.GetByte));
    }

    [Test]
    public void LoggedIn_NoOpenChannel_ReturnsBadChannel()
    {
        LoginAdmin();

        // Nothing has been opened, so the URD handle resolves to no channel.
        EconetMessage reply = Harness.Send(FileServerOpcodes.GetByte);

        AssertErrorCode(reply, FileServerErrors.BadChannel);
    }
}
