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
/// Per-opcode protocol fixture for <c>EndOfFile</c> (fsop 0x11). Migration-invariant guard for the move
/// from <c>MiscOpcodeHandler</c> to <c>EndOfFileOperation</c>.
/// </summary>
/// <remarks>
/// TODO (deeper coverage): drive Open (fsop 0x06) to get a live channel, read to the end, then assert
/// the 0x80 EOF flag / 0x00 not-at-EOF flag. Covered lightly here (auth + bad channel) until the file
/// I/O opcodes get their own fixtures.
/// </remarks>
[TestFixture]
[Category("Unit")]
internal sealed class EndOfFileFsopTests : FsopProtocolTestBase
{
    [Test]
    public void WithoutLogin_ReturnsWhoAreYou()
    {
        EconetMessage reply = Harness.Send(FileServerOpcodes.EndOfFile, [/* handle */ 5]);

        AssertWhoAreYou(reply);
    }

    [Test]
    public void LoggedIn_UnknownHandle_ReturnsBadChannel()
    {
        LoginAdmin();

        // No channel has ever been opened, so handle 5 is unknown.
        EconetMessage reply = Harness.Send(FileServerOpcodes.EndOfFile, [5]);

        AssertErrorCode(reply, FileServerErrors.BadChannel);
    }
}
